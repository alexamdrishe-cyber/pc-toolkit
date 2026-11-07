using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PcToolkit.Core;
using PcToolkit.Core.Apps;
using PcToolkit.Core.Logging;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Строка списка приложений.</summary>
public sealed class AppItemViewModel : ObservableObject
{
    private bool _isSelected;
    private ImageSource? _icon;
    private long? _size;

    public AppItemViewModel(InstalledApp app, Action<AppItemViewModel> uninstall, Action selectionChanged)
    {
        App = app;
        _size = app.SizeBytes;
        Advice = app.Kind == AppKind.Store ? StoreAppAdvice.For(app.PackageName) : new AppAdvice(AdviceLevel.None, "");
        UninstallerMissing = app.Kind == AppKind.Desktop && !Uninstaller.UninstallerExists(app);
        UninstallCommand = new RelayCommand(() => uninstall(this), () => CanUninstall);
        _selectionChanged = selectionChanged;
    }

    private readonly Action _selectionChanged;

    public InstalledApp App { get; }

    public string Name => App.Name;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (App.Publisher.Length > 0)
                parts.Add(App.Publisher);
            if (App.Version.Length > 0)
                parts.Add(App.Version);
            parts.Add(App.Kind == AppKind.Store ? "Microsoft Store" : App.ForAllUsers ? "для всех пользователей" : "только для вас");
            return string.Join(" · ", parts);
        }
    }

    public string DateText => App.InstallDate is { } d ? d.ToString("dd.MM.yyyy") : "";

    public long SizeBytes => _size ?? 0;

    public string SizeText => _size is > 0 ? Formatting.Bytes((ulong)_size.Value) : "";

    public AppAdvice Advice { get; }

    public bool HasAdvice => Advice.Level != AdviceLevel.None;

    public string AdviceText => Advice.Level switch
    {
        AdviceLevel.Remove => "Можно удалить",
        AdviceLevel.Optional => "На ваше усмотрение",
        AdviceLevel.Keep => "Лучше оставить",
        AdviceLevel.Blocked => "Не удаляется",
        _ => "",
    };

    /// <summary>good | info | warning | muted</summary>
    public string AdviceKind => Advice.Level switch
    {
        AdviceLevel.Remove => "good",
        AdviceLevel.Optional => "info",
        AdviceLevel.Keep => "warning",
        _ => "muted",
    };

    public string AdviceNote => Advice.Note;

    public bool UninstallerMissing { get; }

    public bool CanUninstall => Advice.Level != AdviceLevel.Blocked;

    public ICommand UninstallCommand { get; }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
                OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => Icon is not null;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanUninstall)
                value = false;
            if (SetProperty(ref _isSelected, value))
                _selectionChanged();
        }
    }

    public void SetSize(long bytes)
    {
        _size = bytes;
        App.SizeBytes = bytes;
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeBytes));
    }
}

/// <summary>Удалённое приложение Store с кнопкой «Вернуть».</summary>
public sealed class RemovedAppViewModel
{
    public RemovedAppViewModel(RemovedStoreApp app, Func<RemovedAppViewModel, Task> restore)
    {
        Source = app;
        RestoreCommand = new RelayCommand(() => _ = restore(this));
    }

    public RemovedStoreApp Source { get; }

    public string Name => Source.Name;

    public string DateText => $"удалено {Source.RemovedAt.LocalDateTime:d MMMM, HH:mm}";

    public ICommand RestoreCommand { get; }
}

/// <summary>Экран «Приложения»: список программ и приложений Store, удаление с поиском следов.</summary>
public sealed class AppsViewModel : ObservableObject
{
    private readonly OperationGate _gate;
    private readonly IOverlayHost _host;
    private readonly RemovedStoreApps _removed = RemovedStoreApps.Load();
    private List<AppItemViewModel> _all = new();
    private IReadOnlyList<AppItemViewModel> _visible = Array.Empty<AppItemViewModel>();
    private IReadOnlyList<RemovedAppViewModel> _removedRows = Array.Empty<RemovedAppViewModel>();
    private string _searchText = "";
    private string _filter = "all";
    private string _sort = "name";
    private bool _isLoading;
    private bool _loadedOnce;
    private string _message = "";

    public AppsViewModel(OperationGate gate, IOverlayHost host)
    {
        _gate = gate;
        _host = host;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !IsLoading);
        UninstallSelectedCommand = new RelayCommand(() => StartUninstall(_all.Where(a => a.IsSelected).ToList()), () => SelectedCount > 0);
        ClearSelectionCommand = new RelayCommand(() =>
        {
            foreach (AppItemViewModel item in _all)
                item.IsSelected = false;
        }, () => SelectedCount > 0);
        SelectRecommendedCommand = new RelayCommand(() =>
        {
            foreach (AppItemViewModel item in _all.Where(a => a.Advice.Level == AdviceLevel.Remove))
                item.IsSelected = true;
        }, () => _all.Any(a => a.Advice.Level == AdviceLevel.Remove));
        RefreshRemoved();
    }

    public ICommand RefreshCommand { get; }

    public ICommand UninstallSelectedCommand { get; }

    public ICommand ClearSelectionCommand { get; }

    public ICommand SelectRecommendedCommand { get; }

    public IReadOnlyList<AppItemViewModel> VisibleApps
    {
        get => _visible;
        private set
        {
            if (SetProperty(ref _visible, value))
                OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => !IsLoading && VisibleApps.Count == 0;

    public IReadOnlyList<RemovedAppViewModel> RemovedApps
    {
        get => _removedRows;
        private set
        {
            if (SetProperty(ref _removedRows, value))
                OnPropertyChanged(nameof(HasRemovedApps));
        }
    }

    public bool HasRemovedApps => RemovedApps.Count > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                RelayCommand.Refresh();
            }
        }
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message.Length > 0;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilter();
        }
    }

    public bool FilterAll { get => _filter == "all"; set => SetFilter("all", value); }

    public bool FilterDesktop { get => _filter == "desktop"; set => SetFilter("desktop", value); }

    public bool FilterStore { get => _filter == "store"; set => SetFilter("store", value); }

    public bool FilterRecommended { get => _filter == "recommended"; set => SetFilter("recommended", value); }

    public string AllCaption => $"Все ({_all.Count})";

    public string DesktopCaption => $"Программы ({_all.Count(a => a.App.Kind == AppKind.Desktop)})";

    public string StoreCaption => $"Microsoft Store ({_all.Count(a => a.App.Kind == AppKind.Store)})";

    public string RecommendedCaption => $"Можно удалить ({_all.Count(a => a.Advice.Level == AdviceLevel.Remove)})";

    public bool SortByName { get => _sort == "name"; set => SetSort("name", value); }

    public bool SortBySize { get => _sort == "size"; set => SetSort("size", value); }

    public bool SortByDate { get => _sort == "date"; set => SetSort("date", value); }

    public int SelectedCount => _all.Count(a => a.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText
    {
        get
        {
            var selected = _all.Where(a => a.IsSelected).ToList();
            long size = selected.Sum(a => a.SizeBytes);
            return $"Выбрано: {selected.Count}" + (size > 0 ? $" · около {Formatting.Bytes((ulong)size)}" : "");
        }
    }

    /// <summary>Вызывается при открытии экрана: список загружается один раз, дальше — по кнопке «Обновить».</summary>
    public void EnsureLoaded()
    {
        if (!_loadedOnce)
            _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (IsLoading)
            return;

        _loadedOnce = true;
        IsLoading = true;
        Message = "";
        try
        {
            Task<IReadOnlyList<InstalledApp>> desktop = Task.Run(InstalledAppsProvider.ListDesktop);
            Task<IReadOnlyList<InstalledApp>> store = StoreAppsProvider.ListAsync();
            IReadOnlyList<InstalledApp> desktopApps = await desktop;
            IReadOnlyList<InstalledApp> storeApps;
            try
            {
                storeApps = await store;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось получить приложения Microsoft Store.", ex);
                storeApps = Array.Empty<InstalledApp>();
                Message = "Список приложений Microsoft Store недоступен: PowerShell не ответил. Программы показаны полностью.";
            }

            _all = desktopApps.Concat(storeApps)
                .Select(a => new AppItemViewModel(a, item => StartUninstall(new[] { item }), OnSelectionChanged))
                .ToList();
            OnCountsChanged();
            ApplyFilter();
            AppLog.Info($"Список приложений: программ {desktopApps.Count}, приложений Store {storeApps.Count}.");
            _ = LoadDetailsAsync(_all.ToList());
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось получить список приложений.", ex);
            Message = "Не удалось получить список приложений: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Значки и размеры приложений Store — в фоне, чтобы список появился сразу.</summary>
    private static async Task LoadDetailsAsync(IReadOnlyList<AppItemViewModel> items)
    {
        foreach (AppItemViewModel[] chunk in items.Chunk(16))
        {
            var icons = await Task.Run(() => chunk.Select(i => AppIcons.Load(i.App)).ToArray());
            for (int i = 0; i < chunk.Length; i++)
                chunk[i].Icon = icons[i];
        }

        foreach (AppItemViewModel item in items.Where(i => i.App.Kind == AppKind.Store && i.App.InstallLocation.Length > 3))
        {
            long size = await Task.Run(() => LeftoverScanner.FolderSize(item.App.InstallLocation));
            if (size > 0)
                item.SetSize(size);
        }
    }

    private void StartUninstall(IReadOnlyList<AppItemViewModel> items)
    {
        var apps = items.Where(i => i.CanUninstall).ToList();
        if (apps.Count == 0)
            return;

        IDisposable? slot = _gate.TryEnter("Удаление приложений", out string? busy);
        if (slot is null)
        {
            Message = $"Сейчас нельзя: выполняется «{busy}».";
            return;
        }

        var wizard = new UninstallWizardViewModel(apps.Select(a => a.App).ToList(), _removed, async changed =>
        {
            slot.Dispose();
            _host.CloseOverlay();
            RefreshRemoved();
            if (changed)
                await LoadAsync();
        });
        _host.ShowOverlay(wizard);
    }

    private async Task RestoreAsync(RemovedAppViewModel row)
    {
        InstalledApp app = row.Source.ToApp();
        Message = $"Возвращаю «{app.Name}»…";
        bool restored = false;
        try
        {
            restored = await StoreAppsProvider.RestoreAsync(app);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось вернуть {app.Name}.", ex);
        }

        if (restored)
        {
            _removed.Remove(app.PackageFamilyName);
            RefreshRemoved();
            Message = $"«{app.Name}» снова установлено.";
            await LoadAsync();
            return;
        }

        // Файлов пакета на диске уже нет — открываем страницу в магазине.
        Message = $"Файлы «{app.Name}» уже удалены с диска — открываю страницу в Microsoft Store, установите оттуда.";
        ShellService.OpenUrl(StoreAppsProvider.StorePageUri(app));
        _removed.Remove(app.PackageFamilyName);
        RefreshRemoved();
    }

    private void RefreshRemoved() =>
        RemovedApps = _removed.Items.Select(r => new RemovedAppViewModel(r, RestoreAsync)).ToList();

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        RelayCommand.Refresh();
    }

    private void OnCountsChanged()
    {
        OnPropertyChanged(nameof(AllCaption));
        OnPropertyChanged(nameof(DesktopCaption));
        OnPropertyChanged(nameof(StoreCaption));
        OnPropertyChanged(nameof(RecommendedCaption));
        OnSelectionChanged();
    }

    private void SetFilter(string filter, bool value)
    {
        if (!value || _filter == filter)
            return;
        _filter = filter;
        OnPropertyChanged(nameof(FilterAll));
        OnPropertyChanged(nameof(FilterDesktop));
        OnPropertyChanged(nameof(FilterStore));
        OnPropertyChanged(nameof(FilterRecommended));
        ApplyFilter();
    }

    private void SetSort(string sort, bool value)
    {
        if (!value || _sort == sort)
            return;
        _sort = sort;
        OnPropertyChanged(nameof(SortByName));
        OnPropertyChanged(nameof(SortBySize));
        OnPropertyChanged(nameof(SortByDate));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<AppItemViewModel> items = _all;
        items = _filter switch
        {
            "desktop" => items.Where(a => a.App.Kind == AppKind.Desktop),
            "store" => items.Where(a => a.App.Kind == AppKind.Store),
            "recommended" => items.Where(a => a.Advice.Level == AdviceLevel.Remove),
            _ => items,
        };

        string query = SearchText.Trim();
        if (query.Length > 0)
            items = items.Where(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                     || a.App.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        items = _sort switch
        {
            "size" => items.OrderByDescending(a => a.SizeBytes).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "date" => items.OrderByDescending(a => a.App.InstallDate ?? DateTime.MinValue).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        VisibleApps = items.ToList();
    }
}
