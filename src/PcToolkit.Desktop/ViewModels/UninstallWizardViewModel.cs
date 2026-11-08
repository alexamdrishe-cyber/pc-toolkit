using System.IO;
using System.Windows.Input;
using PcToolkit.Core;
using PcToolkit.Core.Apps;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Platform;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Приложение в мастере: что с ним произошло.</summary>
public sealed class WizardAppViewModel : ObservableObject
{
    private string _status;
    private string _statusKind = "";

    public WizardAppViewModel(InstalledApp app)
    {
        App = app;
        UninstallerMissing = app.Kind == AppKind.Desktop && !Uninstaller.UninstallerExists(app);
        _status = app.Kind == AppKind.Store
            ? "Удалится сразу, без окон. Можно будет вернуть."
            : UninstallerMissing
                ? "Деинсталлятор не найден: будут убраны только запись в списке и следы."
                : "Откроется деинсталлятор программы.";
    }

    public InstalledApp App { get; }

    public string Name => App.Name;

    public bool UninstallerMissing { get; }

    public UninstallOutcome? Outcome { get; set; }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>good | warning | critical | info | ""</summary>
    public string StatusKind
    {
        get => _statusKind;
        set => SetProperty(ref _statusKind, value);
    }
}

/// <summary>Найденный след с галочкой.</summary>
public sealed class LeftoverItemViewModel : ObservableObject
{
    private bool _isSelected;

    public LeftoverItemViewModel(string appName, Leftover item)
    {
        AppName = appName;
        Item = item;
        _isSelected = true;
    }

    public string AppName { get; }

    public Leftover Item { get; }

    public string Path
    {
        get
        {
            string path = Item.Kind == LeftoverKind.RegistryValue ? $"{Item.Path}  →  «{Item.ValueName}»" : Item.Path;
            return Item.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue && Item.View == Microsoft.Win32.RegistryView.Registry32
                ? path + "  (32-битная часть)"
                : path;
        }
    }

    public string KindText => Item.Kind switch
    {
        LeftoverKind.Folder => "Папка",
        LeftoverKind.Shortcut => "Ярлык",
        LeftoverKind.RegistryValue => "Автозапуск",
        LeftoverKind.ScheduledTask => "Задание",
        _ => "Реестр",
    };

    public string Details
    {
        get
        {
            var parts = new List<string> { Item.Reason };
            if (Item.SizeBytes > 0)
                parts.Add(Formatting.Bytes((ulong)Item.SizeBytes));
            if (Item.NeedsAdmin)
                parts.Add("нужны права администратора");
            return string.Join(" · ", parts);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>Мастер удаления: подтверждение → деинсталляторы по очереди → найденные следы → итог.</summary>
public sealed class UninstallWizardViewModel : ObservableObject
{
    private readonly RemovedStoreApps _removed;
    private readonly Func<bool, Task> _close;
    private readonly CancellationTokenSource _cancel = new();
    private string _step = "confirm";
    private bool _searchLeftovers = true;
    private bool _userDone;
    private bool _stopAfterCurrent;
    private bool _changed;
    private string _currentText = "";
    private string _statusText = "";
    private string _summary = "";
    private string _backupFolder = "";
    private IReadOnlyList<LeftoverItemViewModel> _leftovers = Array.Empty<LeftoverItemViewModel>();
    private readonly bool _orphans;

    /// <summary>
    /// Мастер поиска остатков уже удалённых программ: сразу ищет, потом тот же шаг «следы» и итог.
    /// Запуск поиска — <see cref="FindOrphansAsync"/>.
    /// </summary>
    public static UninstallWizardViewModel ForOrphans(RemovedStoreApps removed, Func<bool, Task> close) =>
        new(Array.Empty<InstalledApp>(), removed, close, orphans: true);

    public UninstallWizardViewModel(IReadOnlyList<InstalledApp> apps, RemovedStoreApps removed, Func<bool, Task> close)
        : this(apps, removed, close, orphans: false)
    {
    }

    private UninstallWizardViewModel(IReadOnlyList<InstalledApp> apps, RemovedStoreApps removed, Func<bool, Task> close, bool orphans)
    {
        _removed = removed;
        _close = close;
        _orphans = orphans;
        Apps = apps.Select(a => new WizardAppViewModel(a)).ToList();

        StartCommand = new RelayCommand(() => _ = RunAsync(), () => IsConfirm);
        CancelCommand = new RelayCommand(() => _ = _close(false), () => IsConfirm);
        ContinueCommand = new RelayCommand(() => _userDone = true, () => IsWorking);
        StopAfterCurrentCommand = new RelayCommand(() =>
        {
            _stopAfterCurrent = true;
            StatusText = "Остановлюсь после текущего приложения.";
        }, () => IsWorking && !_stopAfterCurrent);
        RemoveLeftoversCommand = new RelayCommand(() => _ = RemoveLeftoversAsync(), () => IsLeftovers);
        SkipLeftoversCommand = new RelayCommand(() => Finish("Следы оставлены как есть."), () => IsLeftovers);
        DoneCommand = new RelayCommand(() => _ = _close(_changed), () => IsDone);
        OpenBackupCommand = new RelayCommand(() => ShellService.OpenFolder(_backupFolder), () => _backupFolder.Length > 0 && Directory.Exists(_backupFolder));
    }

    public IReadOnlyList<WizardAppViewModel> Apps { get; }

    public string Title => Apps.Count == 1 ? $"Удалить «{Apps[0].Name}»?" : $"Удалить приложения: {Apps.Count}?";

    public bool HasDesktopApps => Apps.Any(a => a.App.Kind == AppKind.Desktop);

    public bool HasStoreApps => Apps.Any(a => a.App.Kind == AppKind.Store);

    public bool SearchLeftovers
    {
        get => _searchLeftovers;
        set => SetProperty(ref _searchLeftovers, value);
    }

    public bool IsConfirm => _step == "confirm";

    public bool IsWorking => _step == "working";

    public bool IsLeftovers => _step == "leftovers";

    public bool IsDone => _step == "done";

    public bool ShowApps => !IsLeftovers && Apps.Count > 0;

    /// <summary>Идёт удаление программ (а не поиск остатков): показываются подсказки про окно деинсталлятора.</summary>
    public bool IsUninstalling => IsWorking && !_orphans;

    public string LeftoversTitle => _orphans ? "Найдены остатки удалённых программ" : "Найдены следы";

    public string CurrentText
    {
        get => _currentText;
        private set => SetProperty(ref _currentText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public IReadOnlyList<LeftoverItemViewModel> Leftovers
    {
        get => _leftovers;
        private set => SetProperty(ref _leftovers, value);
    }

    public string LeftoversCaption
    {
        get
        {
            long size = Leftovers.Where(l => l.Item.Kind == LeftoverKind.Folder).Sum(l => l.Item.SizeBytes);
            string intro = _orphans
                ? "То, что осталось от программ, удалённых раньше: ярлыки, автозапуск и задания, которые ведут на несуществующие файлы, " +
                  "записи в списке программ без самой программы и пустые папки. "
                : "";
            return intro + $"Найдено: {Leftovers.Count}" + (size > 0 ? $", папки занимают {Formatting.Bytes((ulong)size)}" : "") +
                   ". Папки и ярлыки уйдут в Корзину, записи реестра и задания — после резервной копии. Снимите галочку с того, что хотите оставить.";
        }
    }

    public ICommand StartCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand ContinueCommand { get; }

    public ICommand StopAfterCurrentCommand { get; }

    public ICommand RemoveLeftoversCommand { get; }

    public ICommand SkipLeftoversCommand { get; }

    public ICommand DoneCommand { get; }

    public ICommand OpenBackupCommand { get; }

    public bool HasBackup => _backupFolder.Length > 0 && Directory.Exists(_backupFolder);

    private void SetStep(string step)
    {
        _step = step;
        OnPropertyChanged(nameof(IsConfirm));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(IsLeftovers));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(ShowApps));
        OnPropertyChanged(nameof(IsUninstalling));
        RelayCommand.Refresh();
    }

    /// <summary>Поиск остатков уже удалённых программ (режим <see cref="ForOrphans"/>).</summary>
    public async Task FindOrphansAsync()
    {
        SetStep("working");
        CurrentText = "Ищу остатки удалённых программ";
        bool elevated = EnvironmentStatus.CurrentProcessElevated;
        var progress = new Progress<string>(s => StatusText = s);
        IReadOnlyList<Orphan> orphans;
        try
        {
            orphans = await Task.Run(() => OrphanScanner.Find(InstalledAppsProvider.ListDesktop(), elevated, progress));
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось найти остатки удалённых программ.", ex);
            Finish("Не удалось выполнить поиск: " + ex.Message);
            return;
        }

        StatusText = "";
        CurrentText = "";
        if (orphans.Count == 0)
        {
            Finish("Ничего не найдено: ярлыков, автозапуска и заданий от удалённых программ нет, записей без программ и пустых папок тоже.");
            return;
        }

        Leftovers = orphans.OrderBy(o => o.Item.Kind).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
                           .Select(o => new LeftoverItemViewModel(o.Name, o.Item)).ToList();
        OnPropertyChanged(nameof(LeftoversCaption));
        SetStep("leftovers");
    }

    private async Task RunAsync()
    {
        SetStep("working");
        bool elevated = EnvironmentStatus.CurrentProcessElevated;
        var found = new List<LeftoverItemViewModel>();
        for (int i = 0; i < Apps.Count; i++)
        {
            if (_stopAfterCurrent)
            {
                foreach (WizardAppViewModel rest in Apps.Skip(i))
                {
                    rest.Status = "Пропущено.";
                    rest.StatusKind = "info";
                }

                break;
            }

            WizardAppViewModel item = Apps[i];
            CurrentText = Apps.Count > 1 ? $"Удаляю {i + 1} из {Apps.Count}: {item.Name}" : $"Удаляю {item.Name}";
            item.Status = "Удаляется…";
            item.StatusKind = "info";
            _userDone = false;

            try
            {
                if (item.App.Kind == AppKind.Store)
                {
                    StatusText = "Удаляю приложение Microsoft Store…";
                    string? error = await StoreAppsProvider.RemoveAsync(item.App, _cancel.Token);
                    if (error is null)
                    {
                        _removed.Add(item.App);
                        _changed = true;
                        item.Outcome = UninstallOutcome.Removed;
                        item.Status = "Удалено. Вернуть можно на экране «Приложения».";
                        item.StatusKind = "good";
                    }
                    else
                    {
                        item.Outcome = UninstallOutcome.Failed;
                        item.Status = "Не удалось: " + error;
                        item.StatusKind = "critical";
                    }

                    continue;
                }

                StatusText = item.UninstallerMissing
                    ? "Деинсталлятора нет — ищу запись и следы."
                    : "Открыт деинсталлятор программы. Пройдите его до конца; если он попросит перезагрузку — выберите «позже».";
                var progress = new Progress<string>(s => StatusText = s);
                UninstallOutcome outcome = item.UninstallerMissing
                    ? UninstallOutcome.UninstallerMissing
                    : await Uninstaller.RunAsync(item.App, progress, () => _userDone, _cancel.Token);
                item.Outcome = outcome;

                (item.Status, item.StatusKind) = outcome switch
                {
                    UninstallOutcome.Removed => ("Удалено.", "good"),
                    UninstallOutcome.UninstallerMissing => ("Деинсталлятора нет: можно убрать запись и следы.", "warning"),
                    UninstallOutcome.StillInstalled => ("Программа осталась: удаление отменили, или оно закончится после перезагрузки.", "warning"),
                    UninstallOutcome.Canceled => ("Отменено: не выданы права администратора.", "warning"),
                    _ => ("Не удалось запустить деинсталлятор.", "critical"),
                };
                if (outcome == UninstallOutcome.Removed)
                    _changed = true;

                if (SearchLeftovers && outcome is UninstallOutcome.Removed or UninstallOutcome.UninstallerMissing)
                {
                    StatusText = "Ищу оставшиеся папки, ярлыки и записи реестра…";
                    InstalledApp app = item.App;
                    bool includeEntry = outcome == UninstallOutcome.UninstallerMissing;
                    IReadOnlyList<Leftover> leftovers = await Task.Run(() =>
                    {
                        IReadOnlyList<InstalledApp> others = InstalledAppsProvider.ListDesktop();
                        return LeftoverScanner.Find(app, others, includeEntry, elevated);
                    });
                    found.AddRange(leftovers.Select(l => new LeftoverItemViewModel(app.Name, l)));
                    if (leftovers.Count > 0)
                        item.Status += $" Найдено следов: {leftovers.Count}.";
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = "Прервано.";
                item.StatusKind = "warning";
                break;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Ошибка удаления «{item.Name}».", ex);
                item.Status = "Ошибка: " + ex.Message;
                item.StatusKind = "critical";
            }
        }

        StatusText = "";
        CurrentText = "";
        if (found.Count > 0)
        {
            Leftovers = found;
            OnPropertyChanged(nameof(LeftoversCaption));
            SetStep("leftovers");
        }
        else
        {
            Finish(SearchLeftovers && HasDesktopApps && Apps.Any(a => a.Outcome == UninstallOutcome.Removed)
                ? "Следов не найдено — деинсталлятор убрал всё сам."
                : "");
        }
    }

    private async Task RemoveLeftoversAsync()
    {
        var selected = Leftovers.Where(l => l.IsSelected).Select(l => l.Item).ToList();
        if (selected.Count == 0)
        {
            Finish("Следы оставлены как есть.");
            return;
        }

        SetStep("working");
        CurrentText = "Удаляю следы";
        StatusText = "Папки и ярлыки — в Корзину, ключи реестра — с резервной копией.";
        _backupFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PcToolkit", "backups", "apps", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));

        var results = new List<LeftoverResult>();
        var userItems = selected.Where(i => !i.NeedsAdmin).ToList();
        var adminItems = selected.Where(i => i.NeedsAdmin).ToList();
        results.AddRange(await Task.Run(() => LeftoverRemover.Remove(userItems, _backupFolder)));

        if (adminItems.Count > 0)
        {
            StatusText = "Часть следов лежит в системных папках — Windows попросит права администратора.";
            string request = Path.Combine(Path.GetTempPath(), $"pctoolkit-leftovers-{Guid.NewGuid():N}.json");
            try
            {
                LeftoverRemover.WriteRequest(request, adminItems, _backupFolder);
                bool ok = await ServiceInstaller.RunElevatedAsync($"{LaunchArguments.RemoveLeftovers} \"{request}\"");
                IReadOnlyList<LeftoverResult> elevated = LeftoverRemover.ReadResults(request);
                results.AddRange(elevated.Count > 0
                    ? elevated
                    : adminItems.Select(i => new LeftoverResult(i, false, ok ? "нет ответа" : "права администратора не выданы")));
            }
            finally
            {
                TryDelete(request);
                TryDelete(request + ".result.json");
            }
        }

        int removed = results.Count(r => r.Removed);
        long freed = results.Where(r => r.Removed && r.Item.Kind == LeftoverKind.Folder).Sum(r => r.Item.SizeBytes);
        var failed = results.Where(r => !r.Removed).ToList();
        string text = $"Убрано следов: {removed} из {results.Count}" + (freed > 0 ? $", освобождено {Formatting.Bytes((ulong)freed)}" : "") + ".";
        if (failed.Count > 0)
            text += " Не удалось: " + string.Join("; ", failed.Take(3).Select(f => $"{Path.GetFileName(f.Item.Path)} — {f.Error}")) +
                    (failed.Count > 3 ? $" и ещё {failed.Count - 3}" : "") + ".";
        text += " Папки и ярлыки можно вернуть из Корзины";
        if (results.Any(r => r.Removed && r.Item.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue))
            text += ", записи реестра — двойным щелчком по .reg-файлу в папке резервных копий";
        if (results.Any(r => r.Removed && r.Item.Kind == LeftoverKind.ScheduledTask))
            text += ", задания — через «Импортировать задачу» в Планировщике заданий (XML-файл в той же папке)";
        text += ".";
        _changed = true;
        Finish(text);
    }

    private void Finish(string summary)
    {
        Summary = summary;
        OnPropertyChanged(nameof(HasBackup));
        SetStep("done");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
