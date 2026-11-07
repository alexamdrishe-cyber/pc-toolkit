namespace PcToolkit.Core.Changes;

/// <summary>Одно запланированное изменение: параметр, текущее и новое значение.</summary>
public sealed record PlannedChange(
    string TweakId,
    string TweakTitle,
    ChangeDefinition Definition,
    ISettingTarget Target,
    ValueSnapshot Before,
    ValueSnapshot After)
{
    /// <summary>Для отмены: какую запись журнала отменяет это изменение.</summary>
    public Guid? RevertsEntryId { get; init; }

    public string BeforeText => Target.Describe(Before);

    public string AfterText => Target.Describe(After);
}

/// <summary>
/// План изменений для предпросмотра. Пока в <see cref="Problems"/> что-то есть (конфликт, нет прав),
/// план выполнить нельзя.
/// </summary>
public sealed class ChangePlan
{
    public ChangePlan(ChangeSetKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    public ChangeSetKind Kind { get; }

    public string Title { get; }

    public List<PlannedChange> Items { get; } = new();

    /// <summary>Причины, по которым план выполнить нельзя: конфликты, нет прав администратора.</summary>
    public List<string> Problems { get; } = new();

    /// <summary>Что не будет изменено и почему: уже нужное значение, параметр недоступен и т. п.</summary>
    public List<string> Skipped { get; } = new();

    /// <summary>Записи журнала, которые при отмене просто освобождаются без записи в систему.</summary>
    public List<ChangeEntry> Released { get; } = new();

    public RestartRequirement Restart { get; set; }

    public bool CanExecute => Problems.Count == 0 && (Items.Count > 0 || Released.Count > 0);
}

/// <summary>Итог выполнения плана.</summary>
public sealed record ChangeResult(bool Success, string Message, ChangeSet? Set)
{
    public static ChangeResult Fail(string message, ChangeSet? set = null) => new(false, message, set);
}
