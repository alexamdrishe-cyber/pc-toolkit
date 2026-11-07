using PcToolkit.Core.Logging;
using PcToolkit.Core.Tweaks;

namespace PcToolkit.Core.Changes;

public enum TweakState
{
    NotApplied,
    Applied,
    AppliedOutside,
    Partial,
    NotApplicable,
    Unknown,
}

/// <summary>Состояние настройки на этом компьютере.</summary>
public sealed record TweakStatus(TweakState State, string? Reason, bool CanRevert);

/// <summary>
/// Единственное место, которое меняет систему. Модули (оптимизация, а позже очистка и профили игр)
/// только просят движок построить план и выполнить его.
///
/// Правила:
/// 1. Перед изменением строится план с предпросмотром «было → станет» и проверкой конфликтов.
/// 2. Набор изменений сначала записывается в журнал как «начат», затем параметры меняются по одному
///    с проверкой; при ошибке уже сделанное откатывается.
/// 3. Владелец параметра — настройка из последней не отменённой записи журнала.
///    Другая настройка не может изменить этот параметр, пока владелец не отменён.
/// 4. Отмена возвращает значение, которое было до изменения, и только если параметр с тех пор никто не трогал.
/// </summary>
public sealed class ChangeEngine
{
    private readonly ChangeJournal _journal;
    private readonly ITargetFactory _factory;
    private readonly Func<bool> _isElevated;
    private readonly int _windowsBuild;
    private readonly object _gate = new();

    public ChangeEngine(ChangeJournal journal, ITargetFactory factory, Func<bool> isElevated, int? windowsBuild = null)
    {
        _journal = journal;
        _factory = factory;
        _isElevated = isElevated;
        _windowsBuild = windowsBuild ?? Environment.OSVersion.Version.Build;
    }

    public ChangeJournal Journal => _journal;

    public int WindowsBuild => _windowsBuild;

    /// <summary>Применённые и не отменённые записи журнала по ключу параметра (кто сейчас владеет параметром).</summary>
    public IReadOnlyDictionary<string, ChangeEntry> ActiveEntries()
    {
        lock (_gate)
        {
            return CollectActiveEntries();
        }
    }

    public TweakStatus GetStatus(TweakDefinition tweak)
    {
        if (!tweak.IsApplicableToBuild(_windowsBuild))
            return new TweakStatus(TweakState.NotApplicable, "не подходит для этой версии Windows", false);

        Dictionary<string, ChangeEntry> active;
        lock (_gate)
        {
            active = CollectActiveEntries();
        }

        int checkedCount = 0;
        int matching = 0;
        bool ownedByTweak = false;
        string? unavailableReason = null;

        foreach (ChangeDefinition definition in tweak.Changes)
        {
            try
            {
                ResolvedChange change = _factory.Resolve(definition);
                if (!change.Target.IsAvailable(out string? reason))
                {
                    unavailableReason ??= reason;
                    continue;
                }

                checkedCount++;
                if (change.Target.Read().SameAs(change.Desired))
                    matching++;

                if (active.TryGetValue(change.Target.Key, out ChangeEntry? owner) && owner.TweakId == tweak.Id)
                    ownedByTweak = true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось проверить состояние настройки «{tweak.Id}».", ex);
                return new TweakStatus(TweakState.Unknown, ex.Message, ownedByTweak);
            }
        }

        if (checkedCount == 0)
            return new TweakStatus(TweakState.NotApplicable, unavailableReason ?? "недоступна на этом компьютере", false);

        TweakState state = matching == checkedCount
            ? ownedByTweak ? TweakState.Applied : TweakState.AppliedOutside
            : matching == 0 ? TweakState.NotApplied : TweakState.Partial;

        return new TweakStatus(state, null, ownedByTweak);
    }

    public ChangePlan PlanApply(IReadOnlyCollection<TweakDefinition> tweaks)
    {
        string title = tweaks.Count == 1 ? $"Применить «{tweaks.First().Title}»" : $"Применить настройки ({tweaks.Count})";
        var plan = new ChangePlan(ChangeSetKind.Apply, title);

        lock (_gate)
        {
            Dictionary<string, ChangeEntry> active = CollectActiveEntries();
            var desiredByKey = new Dictionary<string, (TweakDefinition Tweak, ValueSnapshot Desired)>(StringComparer.OrdinalIgnoreCase);
            var byId = tweaks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            var reportedPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool elevated = _isElevated();

            foreach (TweakDefinition tweak in tweaks)
            {
                foreach (string otherId in tweak.ConflictsWith)
                {
                    if (!byId.TryGetValue(otherId, out TweakDefinition? other))
                        continue;

                    // Несовместимость может быть указана только у одной из настроек; сообщаем о паре один раз.
                    string pair = string.Compare(tweak.Id, other.Id, StringComparison.OrdinalIgnoreCase) < 0
                        ? $"{tweak.Id}|{other.Id}"
                        : $"{other.Id}|{tweak.Id}";
                    if (reportedPairs.Add(pair))
                        plan.Problems.Add($"«{tweak.Title}» и «{other.Title}» несовместимы: выберите что-то одно.");
                }

                if (!tweak.IsApplicableToBuild(_windowsBuild))
                {
                    plan.Skipped.Add($"«{tweak.Title}»: не подходит для этой версии Windows.");
                    continue;
                }

                bool adminProblemReported = false;
                bool tweakHasChanges = false;

                foreach (ChangeDefinition definition in tweak.Changes)
                {
                    ResolvedChange change;
                    try
                    {
                        change = _factory.Resolve(definition);
                    }
                    catch (Exception ex)
                    {
                        plan.Problems.Add($"«{tweak.Title}»: ошибка в описании настройки ({ex.Message}).");
                        continue;
                    }

                    ISettingTarget target = change.Target;
                    if (!target.IsAvailable(out string? reason))
                    {
                        plan.Skipped.Add($"«{tweak.Title}»: {reason}.");
                        continue;
                    }

                    if (target.RequiresAdmin && !elevated)
                    {
                        if (!adminProblemReported)
                            plan.Problems.Add($"«{tweak.Title}» меняет системные параметры: нужны права администратора.");
                        adminProblemReported = true;
                        continue;
                    }

                    if (desiredByKey.TryGetValue(target.Key, out var previous))
                    {
                        if (!previous.Desired.SameAs(change.Desired))
                        {
                            plan.Problems.Add(
                                $"«{tweak.Title}» и «{previous.Tweak.Title}» по-разному меняют один параметр: {target.DisplayName}.");
                        }

                        continue;
                    }

                    desiredByKey[target.Key] = (tweak, change.Desired);

                    if (active.TryGetValue(target.Key, out ChangeEntry? owner)
                        && !string.Equals(owner.TweakId, tweak.Id, StringComparison.OrdinalIgnoreCase)
                        && !owner.After.SameAs(change.Desired))
                    {
                        plan.Problems.Add(
                            $"Параметр {target.DisplayName} уже изменён настройкой «{owner.TweakTitle}». Сначала верните её.");
                        continue;
                    }

                    ValueSnapshot current;
                    try
                    {
                        current = target.Read();
                    }
                    catch (Exception ex)
                    {
                        plan.Problems.Add($"Не удалось прочитать {target.DisplayName}: {ex.Message}");
                        continue;
                    }

                    if (current.SameAs(change.Desired))
                    {
                        plan.Skipped.Add($"«{tweak.Title}»: {target.DisplayName} уже имеет нужное значение.");
                        continue;
                    }

                    plan.Items.Add(new PlannedChange(tweak.Id, tweak.Title, definition, target, current, change.Desired));
                    tweakHasChanges = true;
                }

                if (tweakHasChanges)
                    plan.Restart = RestartRequirementText.Max(plan.Restart, tweak.Restart);
            }
        }

        return plan;
    }

    /// <summary>План отмены настройки, применённой через приложение.</summary>
    public ChangePlan PlanRevertTweak(TweakDefinition tweak)
    {
        var plan = new ChangePlan(ChangeSetKind.Revert, $"Вернуть «{tweak.Title}»");
        lock (_gate)
        {
            foreach (ChangeEntry entry in CollectActiveEntries().Values.Where(e =>
                         string.Equals(e.TweakId, tweak.Id, StringComparison.OrdinalIgnoreCase)))
            {
                AddRevertItem(plan, entry);
            }
        }

        plan.Restart = tweak.Restart;
        if (plan.Items.Count == 0 && plan.Released.Count == 0 && plan.Problems.Count == 0)
            plan.Skipped.Add("Возвращать нечего: эта настройка не применялась через приложение.");

        return plan;
    }

    /// <summary>План отмены целого набора изменений из журнала.</summary>
    public ChangePlan PlanRevertSet(Guid setId)
    {
        lock (_gate)
        {
            ChangeSet? set = _journal.Sets.FirstOrDefault(s => s.Id == setId);
            var plan = new ChangePlan(ChangeSetKind.Revert, set is null ? "Отмена" : $"Отмена: {set.Title}");
            if (set is null || set.Kind != ChangeSetKind.Apply || set.Status != ChangeSetStatus.Applied)
            {
                plan.Problems.Add("Этот набор изменений нельзя отменить.");
                return plan;
            }

            foreach (ChangeEntry entry in set.Entries.Where(e => e.Done && e.RevertedBySetId is null))
                AddRevertItem(plan, entry);

            plan.Restart = set.Restart;
            if (plan.Items.Count == 0 && plan.Released.Count == 0 && plan.Problems.Count == 0)
                plan.Skipped.Add("Все изменения этого набора уже отменены.");

            return plan;
        }
    }

    /// <summary>
    /// План отмены всех действующих изменений одного типа параметров (например, всех настроек видеокарты),
    /// из каких бы наборов они ни были.
    /// </summary>
    public ChangePlan PlanRevertType(string targetType, string title)
    {
        lock (_gate)
        {
            var plan = new ChangePlan(ChangeSetKind.Revert, title);
            foreach (ChangeEntry entry in CollectActiveEntries().Values
                         .Where(e => string.Equals(e.Target.Type, targetType, StringComparison.OrdinalIgnoreCase)))
                AddRevertItem(plan, entry);

            if (plan.Items.Count == 0 && plan.Released.Count == 0 && plan.Problems.Count == 0)
                plan.Skipped.Add("Возвращать нечего: таких изменений, сделанных приложением, нет.");

            return plan;
        }
    }

    public ChangeResult Execute(ChangePlan plan)
    {
        lock (_gate)
        {
            if (!plan.CanExecute)
                return ChangeResult.Fail(plan.Problems.Count > 0 ? string.Join(Environment.NewLine, plan.Problems) : "Нечего менять.");

            // Значения могли измениться между предпросмотром и нажатием «Применить».
            foreach (PlannedChange item in plan.Items)
            {
                ValueSnapshot current;
                try
                {
                    current = item.Target.Read();
                }
                catch (Exception ex)
                {
                    return ChangeResult.Fail($"Не удалось прочитать {item.Target.DisplayName}: {ex.Message}");
                }

                if (!current.SameAs(item.Before))
                {
                    return ChangeResult.Fail(
                        $"Параметр {item.Target.DisplayName} изменился после предпросмотра. Откройте предпросмотр заново.");
                }
            }

            var set = new ChangeSet
            {
                Kind = plan.Kind,
                Title = plan.Title,
                Status = ChangeSetStatus.Pending,
                Restart = plan.Restart,
                Entries = plan.Items.Select(item => new ChangeEntry
                {
                    TweakId = item.TweakId,
                    TweakTitle = item.TweakTitle,
                    TargetKey = item.Target.Key,
                    TargetDisplay = item.Target.DisplayName,
                    Target = item.Definition,
                    Before = item.Before,
                    After = item.After,
                    BeforeText = item.BeforeText,
                    AfterText = item.AfterText,
                    RevertsEntryId = item.RevertsEntryId,
                }).ToList(),
            };

            // Сначала журнал: если приложение упадёт посреди применения, при запуске изменения будут отменены.
            _journal.Add(set);
            AppLog.Info($"Начато: {set.Title} ({set.Entries.Count} изм.).");

            for (int i = 0; i < plan.Items.Count; i++)
            {
                PlannedChange item = plan.Items[i];
                ChangeEntry entry = set.Entries[i];
                try
                {
                    item.Target.Write(item.After);
                    ValueSnapshot check = item.Target.Read();
                    if (!check.SameAs(item.After))
                    {
                        throw new InvalidOperationException(
                            $"после записи значение {item.Target.Describe(check)}, а ожидалось {item.AfterText}");
                    }

                    entry.Done = true;
                    _journal.Save();
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Ошибка при изменении {item.Target.DisplayName}. Откатываю набор «{set.Title}».", ex);
                    RestoreFailedItem(item);
                    string rollbackProblems = RollBack(set);
                    set.Status = ChangeSetStatus.RolledBack;
                    set.Error = $"{item.Target.DisplayName}: {ex.Message}{rollbackProblems}";
                    _journal.Save();
                    return ChangeResult.Fail(
                        $"Не удалось изменить {item.Target.DisplayName}: {ex.Message}. Уже сделанные изменения отменены.{rollbackProblems}",
                        set);
                }
            }

            if (plan.Kind == ChangeSetKind.Revert)
                MarkReverted(set, plan);

            set.Status = ChangeSetStatus.Applied;
            _journal.Save();
            AppLog.Info($"Готово: {set.Title}.");

            string message = set.Entries.Count == 0
                ? "Готово."
                : $"Изменено параметров: {set.Entries.Count}.";
            if (set.Restart != RestartRequirement.None)
                message += $" Чтобы изменения вступили в силу, {RestartRequirementText.Describe(set.Restart)}.";

            return new ChangeResult(true, message, set);
        }
    }

    /// <summary>
    /// Откатывает наборы, применение которых прервалось (приложение закрылось или упало).
    /// Возвращает число откатанных наборов и описание проблем, если они были.
    /// </summary>
    public (int Recovered, string? Problems) RecoverInterrupted()
    {
        lock (_gate)
        {
            int recovered = 0;
            var problems = new List<string>();
            bool elevated = _isElevated();

            foreach (ChangeSet set in _journal.Sets.Where(s => s.Status == ChangeSetStatus.Pending).ToList())
            {
                bool needsAdmin = set.Entries.Any(e => e.Done && SafeRequiresAdmin(e));
                if (needsAdmin && !elevated)
                {
                    problems.Add($"«{set.Title}»: применение было прервано; чтобы отменить его, запустите приложение от имени администратора.");
                    continue;
                }

                string rollbackProblems = RollBack(set);
                set.Status = ChangeSetStatus.RolledBack;
                set.Error = "Применение было прервано (приложение закрылось или упало), сделанные изменения отменены." + rollbackProblems;
                recovered++;
                AppLog.Warn($"Восстановление после сбоя: набор «{set.Title}» откатан.{rollbackProblems}");
            }

            if (recovered > 0)
                _journal.Save();

            return (recovered, problems.Count > 0 ? string.Join(Environment.NewLine, problems) : null);
        }
    }

    private Dictionary<string, ChangeEntry> CollectActiveEntries()
    {
        var result = new Dictionary<string, ChangeEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ChangeSet set in _journal.Sets
                     .Where(s => s.Kind == ChangeSetKind.Apply && s.Status == ChangeSetStatus.Applied)
                     .OrderBy(s => s.CreatedAt))
        {
            foreach (ChangeEntry entry in set.Entries.Where(e => e.Done && e.RevertedBySetId is null))
                result[entry.TargetKey] = entry;
        }

        return result;
    }

    private void AddRevertItem(ChangePlan plan, ChangeEntry entry)
    {
        ISettingTarget target;
        ValueSnapshot current;
        try
        {
            target = _factory.Resolve(entry.Target).Target;
            if (target.RequiresAdmin && !_isElevated())
            {
                plan.Problems.Add($"Чтобы вернуть {entry.TargetDisplay}, нужны права администратора.");
                return;
            }

            current = target.Read();
        }
        catch (Exception ex)
        {
            plan.Problems.Add($"Не удалось прочитать {entry.TargetDisplay}: {ex.Message}");
            return;
        }

        if (current.SameAs(entry.Before))
        {
            plan.Skipped.Add($"{entry.TargetDisplay}: уже имеет прежнее значение.");
            plan.Released.Add(entry);
            return;
        }

        if (!current.SameAs(entry.After))
        {
            // Кто-то поменял параметр после нас: не затираем чужое изменение, просто перестаём им владеть.
            plan.Skipped.Add(
                $"{entry.TargetDisplay}: значение изменено вне приложения (сейчас {target.Describe(current)}), оставляю как есть.");
            plan.Released.Add(entry);
            return;
        }

        plan.Items.Add(new PlannedChange(entry.TweakId, entry.TweakTitle, entry.Target, target, current, entry.Before)
        {
            RevertsEntryId = entry.Id,
        });
    }

    private void MarkReverted(ChangeSet revertSet, ChangePlan plan)
    {
        DateTime now = DateTime.Now;
        var originals = new List<ChangeEntry>(plan.Released);
        foreach (ChangeEntry entry in revertSet.Entries)
        {
            if (entry.RevertsEntryId is Guid id && _journal.FindEntry(id) is { } original)
                originals.Add(original);
        }

        foreach (ChangeEntry original in originals)
        {
            // Освобождаем и более ранние записи той же настройки для того же параметра.
            foreach (ChangeEntry entry in _journal.Sets
                         .Where(s => s.Kind == ChangeSetKind.Apply)
                         .SelectMany(s => s.Entries)
                         .Where(e => e.RevertedBySetId is null
                                     && string.Equals(e.TargetKey, original.TargetKey, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(e.TweakId, original.TweakId, StringComparison.OrdinalIgnoreCase)))
            {
                entry.RevertedBySetId = revertSet.Id;
                entry.RevertedAt = now;
            }
        }
    }

    /// <summary>Возвращает прежние значения для уже сделанных изменений набора, от последнего к первому.</summary>
    private string RollBack(ChangeSet set)
    {
        var problems = new List<string>();
        foreach (ChangeEntry entry in Enumerable.Reverse(set.Entries).Where(e => e.Done))
        {
            try
            {
                ISettingTarget target = _factory.Resolve(entry.Target).Target;
                ValueSnapshot current = target.Read();
                if (current.SameAs(entry.After))
                {
                    target.Write(entry.Before);
                    entry.Done = false;
                }
                else if (current.SameAs(entry.Before))
                {
                    entry.Done = false;
                }
                else
                {
                    problems.Add($"{entry.TargetDisplay} изменён вне приложения, оставлен как есть");
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{entry.TargetDisplay}: {ex.Message}");
                AppLog.Error($"Не удалось откатить {entry.TargetDisplay}.", ex);
            }
        }

        return problems.Count > 0 ? " Не удалось откатить: " + string.Join("; ", problems) + "." : "";
    }

    /// <summary>Параметр, на котором случилась ошибка, мог записаться частично: возвращаем ему прежнее значение.</summary>
    private static void RestoreFailedItem(PlannedChange item)
    {
        try
        {
            if (!item.Target.Read().SameAs(item.Before))
                item.Target.Write(item.Before);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Не удалось вернуть {item.Target.DisplayName} после ошибки.", ex);
        }
    }

    private bool SafeRequiresAdmin(ChangeEntry entry)
    {
        try
        {
            return _factory.Resolve(entry.Target).Target.RequiresAdmin;
        }
        catch
        {
            return true;
        }
    }
}
