using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PcToolkit.Core;
using PcToolkit.Core.Benchmarks;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Cleanup;
using PcToolkit.Core.Diagnostics;
using PcToolkit.Core.Fans;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Games;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Overclock;
using PcToolkit.Core.Platform;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Service;
using PcToolkit.Core.Settings;
using PcToolkit.Core.Tweaks;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>
/// Главная модель окна: меню, текущая страница, строка состояния, установка драйвера.
/// Получает снимки датчиков в фоновом потоке и передаёт их страницам в потоке интерфейса.
/// </summary>
public sealed class MainViewModel : ObservableObject, IChangePresenter, IOverlayHost, IDisposable
{
    private readonly ISensorSource _source;
    private readonly ServiceClient? _service;
    private readonly ChangeEngine _engine;
    private object? _preview;
    private readonly OperationGate _gate = new();
    private readonly Dispatcher _dispatcher;
    // 30 минут при опросе раз в секунду: столько показывает самое длинное окно на экране «Графики».
    // 30 минут при опросе 4 раза в секунду (при более редком — дольше).
    private readonly SensorHistory _history = new(capacity: 7300);
    private DateTime _lastSlowUpdate = DateTime.MinValue;
    private bool _isSidebarCollapsed;
    private bool _isSearchOpen;
    private string _searchQuery = "";
    private EnvironmentStatus _envStatus;
    private NavItemViewModel? _selectedNav;
    private object? _currentPage;
    private string _statusText = "Запуск датчиков…";
    private SensorSourceState _sourceState = SensorSourceState.Starting;
    private HardwareSnapshot? _queuedSnapshot;
    private int _applyScheduled;
    private bool _installingDriver;
    private bool _disposed;
    private int _inventorySignature;
    private int _applyCount;
    private long _applyTicks;
    private readonly HashSet<object> _visited = new();
    private readonly HealthMonitor _health;

    public MainViewModel(ISensorSource source, Dispatcher dispatcher, ServiceClient? service = null, ServiceInfo? serviceInfo = null)
    {
        _source = source;
        _dispatcher = dispatcher;
        _service = service;
        var steps = new StartupSteps();
        EnvironmentStatus detected = EnvironmentStatus.Detect();
        // Служба нужна, только когда у самого приложения нет прав администратора.
        _envStatus = detected with { ServiceConnected = service is not null && !detected.IsElevated };

        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        InstallDriverCommand = new RelayCommand(InstallDriver, () => !_installingDriver);
        ReinstallDriverCommand = new RelayCommand(ReinstallDriver, () => !_installingDriver);
        OpenLogsCommand = new RelayCommand(() => ShellService.OpenFolder(AppLog.LogDirectory));

        Settings = new SettingsViewModel(serviceInfo, _envStatus.IsElevated, _envStatus.ServiceConnected);
        Monitoring = new MonitoringViewModel { HasFullSensors = _envStatus.HasFullSensors };
        _source.Interval = Settings.SensorInterval;
        Monitoring.SetInterval(Settings.SensorInterval);
        Settings.SensorIntervalChanged += interval =>
        {
            _source.Interval = interval;
            Monitoring.SetInterval(interval);
        };
        SystemInfo = new SystemInfoViewModel();
        steps.Done("настройки, мониторинг, система");

        // Движок изменений — единственное место, которое меняет систему.
        // Параметры, которым нужны права администратора, без прав записываются через службу.
        var factory = new ServiceRoutingTargetFactory(
            new SystemTargetFactory(),
            () => _envStatus.IsElevated,
            () => _envStatus.ServiceConnected ? _service : null);
        _engine = new ChangeEngine(ChangeJournal.Load(), factory, () => _envStatus.IsElevated);
        TweakCatalog catalog = TweakCatalog.LoadEmbedded(factory);
        Optimization = new OptimizationViewModel(_engine, catalog, factory, this, _envStatus.HasFullSensors, RestartAsAdminCommand);
        Journal = new JournalViewModel(_engine, this);
        steps.Done("оптимизация и журнал");
        Cleanup = new CleanupViewModel(CleanupCatalog.LoadEmbedded(), CleanupHistory.Load(), _gate, this,
                                       _envStatus.IsElevated, RestartAsAdminCommand);
        steps.Done("очистка");
        Apps = new AppsViewModel(_gate, this);
        steps.Done("приложения");
        BenchmarkHistory benchmarkHistory = BenchmarkHistory.Load();
        Benchmarks = new BenchmarksViewModel(_gate, benchmarkHistory);

        // SMART: напрямую с правами администратора, иначе через службу.
        var diskHealth = new DiskHealthViewModel(
            () => DiskHealthSource.Read(_envStatus.IsElevated, _envStatus.ServiceConnected ? _service : null),
            DiskHealthHistory.Load(), Settings.InstallCommand, RestartAsAdminCommand);
        Disks = new DisksViewModel(diskHealth, new DiskTestViewModel(_gate, benchmarkHistory),
                                   new DiskAnalyzerViewModel(_envStatus.IsElevated, RestartAsAdminCommand));
        steps.Done("тесты и диски");
        Crashes = new CrashesViewModel(() => Disks.Health.Reports
            .GroupBy(r => r.Disk.Number)
            .ToDictionary(g => g.Key, g => g.First().Title),
            () => _envStatus.ServiceConnected ? _service : null, _envStatus.IsElevated);
        Drivers = new DriversViewModel(ReinstallDriverCommand);
        Startup = new StartupViewModel(() => _envStatus.ServiceConnected ? _service : null, _envStatus.IsElevated);
        Network = new NetworkViewModel();
        _fanService = new FanService(FanSettings.Load(), _envStatus.ServiceConnected ? _service : null, source as LhmSensorSource, _envStatus.IsElevated);
        Fans = new FansViewModel(_fanService, dispatcher);

        // Кадры других программ Windows сообщает только администратору: напрямую или через службу.
        IFrameSource frames = _envStatus.IsElevated
            ? new LocalFrameSource()
            : _service is not null
                ? new ServiceFrameSource(_service)
                : new UnavailableFrameSource("нужна служба PC Toolkit или запуск от имени администратора");
        Games = new GamesViewModel(frames, dispatcher, Settings.InstallCommand, frames is not UnavailableFrameSource);
        steps.Done("игры");

        Home = new HomeViewModel(_envStatus, RestartAsAdminCommand, InstallDriverCommand, Settings.InstallCommand,
                                 Games, benchmarkHistory, Navigate, Optimization, Cleanup, Benchmarks, Apps)
        {
            MonitoringPage = Monitoring,
            RevertGpuTuningCommand = new RelayCommand(RevertGpuTuning),
            OpenArea = OpenArea,
        };
        steps.Done("главная");
        _health = new HealthMonitor(Home, Disks.Health, Drivers, _gate);
        _health.Notification += n => HealthNotification?.Invoke(n);

        var home = new NavItemViewModel("Главная", Icons.Home, Home, "сводка, проверка системы, последние замеры");
        NavGroups = new[]
        {
            new NavGroupViewModel("Обзор", new[]
            {
                home,
                new NavItemViewModel("Мониторинг", Icons.Monitoring, Monitoring, "датчики, температуры, графики, частоты, вентиляторы, запись CSV"),
                new NavItemViewModel("Вентиляторы", Icons.Fan, Fans,
                    "вентиляторы, кулеры, обороты, тихо, холодно, кривая, шум, корпусные вентиляторы, помпа, калибровка"),
                new NavItemViewModel("Система", Icons.System, SystemInfo, "процессор, материнская плата, BIOS, память, диски, отчёт"),
                new NavItemViewModel("Диски", Icons.Disk, Disks,
                    "здоровье дисков, SMART, износ и ресурс SSD, битые секторы, скорость диска, место на диске, крупные папки и файлы"),
                new NavItemViewModel("Сбои", Icons.Crash, Crashes,
                    "синий экран, BSOD, внезапные выключения и перезагрузки, вылеты игр, ошибки драйвера видеокарты, WHEA, журнал событий"),
            }),
            new NavGroupViewModel("Игры и скорость", new[]
            {
                new NavItemViewModel("Игры", Icons.Games, Games, "профили игр, оверлей FPS, замеры FPS до и после"),
                new NavItemViewModel("Тесты", Icons.Tests, Benchmarks, "стресс-тест, стабильность, бенчмарк, процессор, видеокарта, память"),
                new NavItemViewModel("Сеть", Icons.Network, Network, "пинг, джиттер, потери пакетов, Wi-Fi, сигнал, DNS, лаги в играх"),
            }),
            new NavGroupViewModel("Обслуживание", new[]
            {
                new NavItemViewModel("Оптимизация", Icons.Optimize, Optimization, "настройки Windows, твики, конфиденциальность, питание"),
                new NavItemViewModel("Автозагрузка", Icons.Startup, Startup, "что запускается вместе с Windows, медленное включение компьютера"),
                new NavItemViewModel("Очистка", Icons.Cleanup, Cleanup, "временные файлы, кэш браузеров, кэш шейдеров, корзина"),
                new NavItemViewModel("Приложения", Icons.Apps, Apps, "удалить программу, следы, приложения Microsoft Store"),
                new NavItemViewModel("Драйверы", Icons.Driver, Drivers,
                    "драйвер не установлен, неизвестное устройство, драйвер видеокарты, чипсет, диспетчер устройств"),
            }),
        };
        BottomNav = new[]
        {
            new NavItemViewModel("Журнал и откат", Icons.Journal, Journal, "история изменений, отменить, вернуть как было"),
            new NavItemViewModel("Настройки", Icons.Settings, Settings, "служба, автозапуск, трей, драйвер PawnIO"),
        };

        NavigateCommand = new RelayCommand(p =>
        {
            if (p is NavItemViewModel item)
                SelectedNav = item;
        });
        _isSidebarCollapsed = AppSettings.Load().SidebarCollapsed;
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarCollapsed = !IsSidebarCollapsed);
        OpenSearchCommand = new RelayCommand(OpenSearch);
        CloseSearchCommand = new RelayCommand(() => IsSearchOpen = false);
        RunSearchCommand = new RelayCommand(p => RunSearch(p as SearchResultViewModel));

        SelectedNav = home;
        steps.Done("меню и первый экран");

        _source.SnapshotReady += (_, snapshot) => _fanService.OnSnapshot(snapshot);
        _source.SnapshotReady += OnSnapshotReady;
        _source.StateChanged += OnSourceStateChanged;
    }

    public EnvironmentStatus EnvStatus => _envStatus;

    public HomeViewModel Home { get; }

    public MonitoringViewModel Monitoring { get; }

    public SystemInfoViewModel SystemInfo { get; }

    public OptimizationViewModel Optimization { get; }

    public JournalViewModel Journal { get; }

    public CleanupViewModel Cleanup { get; }

    public DisksViewModel Disks { get; }

    public CrashesViewModel Crashes { get; }

    public DriversViewModel Drivers { get; }

    public StartupViewModel Startup { get; }

    public NetworkViewModel Network { get; }

    public FansViewModel Fans { get; }

    private readonly FanService _fanService;

    public AppsViewModel Apps { get; }

    public BenchmarksViewModel Benchmarks { get; }

    public GamesViewModel Games { get; }

    public void ShowOverlay(object overlay) => Preview = overlay;

    public void CloseOverlay() => Preview = null;

    /// <summary>Открытое окно предпросмотра изменений (поверх любого экрана) или null.</summary>
    public object? Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public IReadOnlyList<NavGroupViewModel> NavGroups { get; }

    /// <summary>Пункты внизу меню: «Журнал и откат», «Настройки».</summary>
    public IReadOnlyList<NavItemViewModel> BottomNav { get; }

    public IEnumerable<NavItemViewModel> AllNavItems => NavGroups.SelectMany(g => g.Items).Concat(BottomNav);

    public ICommand NavigateCommand { get; }

    public ICommand ToggleSidebarCommand { get; }

    public ICommand OpenSearchCommand { get; }

    public ICommand CloseSearchCommand { get; }

    public ICommand RunSearchCommand { get; }

    /// <summary>Меню свёрнуто в полоску со значками.</summary>
    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set
        {
            if (SetProperty(ref _isSidebarCollapsed, value))
            {
                OnPropertyChanged(nameof(IsSidebarExpanded));
                AppSettings.Update(s => s.SidebarCollapsed = value);
            }
        }
    }

    public bool IsSidebarExpanded => !_isSidebarCollapsed;

    /// <summary>Открыт поиск по программе (Ctrl+K).</summary>
    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set => SetProperty(ref _isSearchOpen, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value ?? ""))
                UpdateSearchResults();
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<SearchResultViewModel> SearchResults { get; } = new();

    /// <summary>Переход на страницу из других экранов (например, быстрые действия на главной).</summary>
    /// <summary>Уведомление о проблеме для трея: сбой, перегрев, диск, место.</summary>
    public event Action<HealthNotification>? HealthNotification;

    /// <summary>Открывает экран с подробностями по <see cref="Finding.Area"/> или уведомлению.</summary>
    public void OpenArea(string area)
    {
        switch (area)
        {
            case FindingAreas.Disks:
                Disks.ShowTab("health");
                Navigate(Disks);
                break;
            case "space":
                Disks.ShowTab("space");
                Navigate(Disks);
                break;
            case FindingAreas.Crashes:
                Navigate(Crashes);
                break;
            case FindingAreas.Drivers:
                Navigate(Drivers);
                break;
            case FindingAreas.Memory:
                Benchmarks.IsMemoryTab = true;
                Navigate(Benchmarks);
                break;
            case "monitoring":
                Navigate(Monitoring);
                break;
        }
    }

    public void Navigate(object page)
    {
        NavItemViewModel? item = AllNavItems.FirstOrDefault(i => ReferenceEquals(i.Page, page));
        if (item is not null)
            SelectedNav = item;
    }

    /// <summary>Стрелки в поиске: какая строка выделена.</summary>
    public void MoveSearchHighlight(int delta)
    {
        if (SearchResults.Count == 0)
            return;

        int index = SearchResults.ToList().FindIndex(r => r.IsHighlighted);
        int next = Math.Clamp(index + delta, 0, SearchResults.Count - 1);
        for (int i = 0; i < SearchResults.Count; i++)
            SearchResults[i].IsHighlighted = i == next;
    }

    public void RunHighlightedSearch() => RunSearch(SearchResults.FirstOrDefault(r => r.IsHighlighted) ?? SearchResults.FirstOrDefault());

    private void OpenSearch()
    {
        SearchQuery = "";
        UpdateSearchResults();
        IsSearchOpen = true;
    }

    private void RunSearch(SearchResultViewModel? result)
    {
        if (result is null)
            return;

        IsSearchOpen = false;
        result.Run();
    }

    private void UpdateSearchResults()
    {
        string query = _searchQuery.Trim();
        SearchResults.Clear();
        foreach (NavItemViewModel item in AllNavItems)
        {
            if (query.Length == 0
                || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || item.Keywords.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                NavItemViewModel target = item;
                SearchResults.Add(new SearchResultViewModel(item.Title, item.Keywords, item.Icon, () => SelectedNav = target));
            }
        }

        if (query.Length >= 2)
        {
            SearchResults.Add(new SearchResultViewModel($"Найти датчик «{query}»", "Мониторинг → все датчики", Icons.Search, () =>
            {
                Navigate(Monitoring);
                Monitoring.ShowSensor(query);
            }));
        }

        if (SearchResults.Count > 0)
            SearchResults[0].IsHighlighted = true;
    }

    public ICommand RestartAsAdminCommand { get; }

    public ICommand InstallDriverCommand { get; }

    /// <summary>Переустановить PawnIO, когда он числится установленным, но не работает.</summary>
    public ICommand ReinstallDriverCommand { get; }

    public ICommand OpenLogsCommand { get; }

    public string VersionText =>
        "версия " + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    /// <summary>Заголовок карточки состояния внизу меню.</summary>
    public string ServiceStatusText => _sourceState switch
    {
        SensorSourceState.Failed => "Датчики недоступны",
        SensorSourceState.Starting => "Датчики запускаются",
        SensorSourceState.Stopped => "Датчики остановлены",
        _ => _envStatus.IsElevated ? "Права администратора" : _envStatus.ServiceConnected ? "Служба работает" : "Обычные права",
    };

    /// <summary>Подсказка к карточке состояния: права, драйвер, последнее обновление.</summary>
    public string StatusTooltip => $"{ElevationText} · {PawnIoText}";

    public string ElevationText => _envStatus.IsElevated
        ? "Права администратора"
        : _envStatus.ServiceConnected ? "Через службу PC Toolkit" : "Обычные права";

    public SettingsViewModel Settings { get; }

    public string PawnIoText => _envStatus.PawnIoVersion is { } version
        ? $"PawnIO {version}"
        : "PawnIO не установлен";

    public NavItemViewModel? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (SetProperty(ref _selectedNav, value) && value is not null)
            {
                foreach (NavItemViewModel item in AllNavItems)
                    item.IsSelected = ReferenceEquals(item, value);

                MeasureFirstVisit(value);
                CurrentPage = value.Page;
                Games.SetPageVisible(value.Page == Games);
                Monitoring.IsPageVisible = value.Page == Monitoring;
                Home.IsPageVisible = value.Page == Home;
                if (value.Page == Home)
                    Home.RefreshSideInfo();
                if (value.Page is CleanupViewModel cleanup)
                    cleanup.EnsureScanned();
                else if (value.Page is DisksViewModel disks)
                    disks.OnShown();
                else if (value.Page is CrashesViewModel crashes)
                    crashes.EnsureLoaded();
                else if (value.Page is DriversViewModel drivers)
                    drivers.EnsureLoaded();
                else if (value.Page is NetworkViewModel network)
                    network.EnsureLoaded();
                else if (value.Page is FansViewModel fans)
                    fans.OnShown();
                else if (value.Page is AppsViewModel apps)
                    apps.EnsureLoaded();
                else if (value.Page is StartupViewModel startup)
                    startup.OnShown();
            }
        }
    }

    public object? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public SensorSourceState SourceState
    {
        get => _sourceState;
        private set
        {
            if (SetProperty(ref _sourceState, value))
                OnPropertyChanged(nameof(ServiceStatusText));
        }
    }

    private bool NeedsDriver => !_envStatus.PawnIoInstalled || _envStatus.PawnIoOutdated;

    /// <summary>
    /// Запускает датчики. Если приложение перезапущено с правами администратора ради установки драйвера,
    /// сначала ставит драйвер: тогда LibreHardwareMonitor сразу откроется уже с ним.
    /// </summary>
    public async Task StartAsync(bool installDriverFirst, bool repairDriverFirst = false)
    {
        AppLog.Info($"Права администратора: {_envStatus.IsElevated}; PawnIO: {_envStatus.PawnIoVersion?.ToString() ?? "не установлен"}.");

        try
        {
            if (repairDriverFirst && _envStatus.IsElevated)
                await RepairDriverAsync();
            else if (installDriverFirst && _envStatus.IsElevated && NeedsDriver)
                await InstallDriverAsync();
        }
        catch (Exception ex)
        {
            // Что бы ни случилось с установкой, датчики должны запуститься.
            AppLog.Error("Ошибка при установке драйвера во время запуска.", ex);
        }

        _source.Start();
        _fanService.Start();
        SystemInfo.EnsureLoaded();
        // Диски, сбои, драйверы и память — в фоне, когда запуск уже позади.
        _health.Start(TimeSpan.FromSeconds(12));
        RecoverInterruptedChanges();
        RecoverInterruptedGpuCheck();
        Journal.Refresh();
        UpdateGpuTuningNotice();
        _ = Optimization.RefreshAsync();

#if DEBUG
        if (NeedsDriver)
            _ = Task.Run(() => AppLog.Info("Самопроверка: " + PawnIoInstaller.Verify().Message));
#endif

        if (!installDriverFirst && !repairDriverFirst)
            OfferDriverOnce();
    }

    public void ShowPlan(ChangePlan plan, bool offerRestorePoint, Action<ChangeResult?>? completed = null)
    {
        Preview = new ChangePreviewViewModel(plan, _engine, _gate, offerRestorePoint, result =>
        {
            OnPreviewClosed(result);
            completed?.Invoke(result);
        });
    }

    private void OnPreviewClosed(ChangeResult? result)
    {
        Preview = null;
        if (result?.Success == true)
            Optimization.OnApplied();

        Journal.Refresh();
        _ = Optimization.RefreshAsync();
        UpdateGpuTuningNotice();
    }

    /// <summary>
    /// Разгона в программе больше нет. Если настройки видеокарты, применённые прежней версией, всё ещё действуют,
    /// главная предлагает вернуть заводские (через обычный предпросмотр «было → станет»).
    /// </summary>
    private void UpdateGpuTuningNotice()
    {
        bool active;
        try
        {
            active = GpuTuningRecovery.HasActiveTuning(_engine);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось проверить настройки видеокарты в журнале.", ex);
            active = false;
        }

        if (!active)
        {
            Home.SetGpuTuningNotice(false);
            return;
        }

        // Видеокарту могли заменить: тогда вернуть нечего, и напоминание только мешало бы.
        _ = Task.Run(() => AdlxGpuTuning.Shared(out _) is not null).ContinueWith(
            t => _dispatcher.InvokeAsync(() => Home.SetGpuTuningNotice(t.Status == TaskStatus.RanToCompletion && t.Result)),
            TaskScheduler.Default);
    }

    private void RevertGpuTuning() => ShowPlan(GpuTuningRecovery.PlanRevertAll(_engine), offerRestorePoint: false);

    /// <summary>Если прошлая проверка разгона или автоподбор оборвались (зависание, перезагрузка), возвращаем настройки видеокарты.</summary>
    private void RecoverInterruptedGpuCheck()
    {
        string? message = GpuTuningRecovery.RecoverInterrupted(_engine);
        if (message is null)
            return;

        Journal.Refresh();
        _dispatcher.InvokeAsync(() => ShowMessage(message, MessageBoxButton.OK, MessageBoxImage.Warning), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Если прошлый запуск оборвался посреди применения изменений, откатываем их.</summary>
    private void RecoverInterruptedChanges()
    {
        try
        {
            (int recovered, string? problems) = _engine.RecoverInterrupted();
            if (recovered > 0 || problems is not null)
            {
                string text = recovered > 0
                    ? $"Прошлое применение настроек было прервано. Сделанные изменения отменены (наборов: {recovered}). Подробности в журнале."
                    : "";
                if (problems is not null)
                    text += (text.Length > 0 ? "\n\n" : "") + problems;

                _dispatcher.InvokeAsync(
                    () => ShowMessage(text, MessageBoxButton.OK, MessageBoxImage.Warning),
                    DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка при восстановлении после прерванного применения.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        // Вентиляторы — первыми: вернуть BIOS и драйверу видеокарты, пока служба, датчики и библиотека AMD ещё работают.
        _fanService.Dispose();
        _source.SnapshotReady -= OnSnapshotReady;
        _source.StateChanged -= OnSourceStateChanged;
        Monitoring.Dispose();
        Benchmarks.Shutdown();
        Disks.Shutdown();
        Games.Dispose();
        _health.Dispose();
        AdlxGpuTuning.ShutdownShared();
        _source.Dispose();
        _service?.Dispose();
    }

    /// <summary>При первом запуске один раз предлагает установить драйвер, если его нет.</summary>
    private void OfferDriverOnce()
    {
        if (!NeedsDriver)
            return;

        AppSettings settings = AppSettings.Load();
        if (settings.DriverOfferShown)
            return;

        settings.DriverOfferShown = true;
        settings.Save();

        // Показываем после того, как окно отрисуется.
        _dispatcher.InvokeAsync(() =>
        {
            MessageBoxResult answer = ShowMessage(
                "Для температур процессора и датчиков материнской платы нужен драйвер PawnIO.\n\n" +
                "Это бесплатный драйвер с открытым исходным кодом и цифровой подписью автора; " +
                "его же использует LibreHardwareMonitor. Установщик уже встроен в приложение.\n\n" +
                "Установить сейчас? Windows попросит разрешение администратора.\n" +
                "Позже это можно сделать кнопкой на главном экране.",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes)
                InstallDriver();
        }, DispatcherPriority.ApplicationIdle);
    }

    private async void InstallDriver()
    {
        try
        {
            if (_installingDriver)
                return;

            if (!_envStatus.IsElevated)
            {
                // Один запрос UAC: новая копия с правами администратора сама поставит драйвер и запустит датчики.
                if (ShellService.RestartAsAdministrator(LaunchArguments.InstallDriver))
                    App.Quit();
                return;
            }

            // Уже с правами администратора: ставим и перезапускаемся, чтобы датчики открылись с драйвером.
            if (await InstallDriverAsync() && ShellService.RestartApplication())
                App.Quit();
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка при установке драйвера.", ex);
        }
    }

    private async void ReinstallDriver()
    {
        try
        {
            if (_installingDriver)
                return;

            if (!_envStatus.IsElevated)
            {
                // Один запрос UAC: копия с правами администратора переустановит драйвер и сразу запустит датчики.
                if (ShellService.RestartAsAdministrator(LaunchArguments.RepairDriver))
                    App.Quit();
                return;
            }

            if (await RepairDriverAsync() && ShellService.RestartApplication())
                App.Quit();
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка при переустановке драйвера.", ex);
        }
    }

    /// <summary>Удалить и заново поставить PawnIO, затем перезапустить службу, чтобы её датчики открылись с драйвером.</summary>
    private async Task<bool> RepairDriverAsync()
    {
        if (_installingDriver)
            return false;

        _installingDriver = true;
        RelayCommand.Refresh();
        StatusText = "Переустанавливаю драйвер PawnIO…";

        try
        {
            PawnIoInstallResult result = await Task.Run(PawnIoInstaller.Repair);
            if (result.Success)
                await Task.Run(ServiceInstaller.RestartIfInstalled);

            UpdateEnvironment(EnvironmentStatus.Detect() with { ServiceConnected = _envStatus.ServiceConnected });
            if (result.Success)
            {
                AppLog.Info($"Драйвер PawnIO {_envStatus.PawnIoVersion} переустановлен.");
                StatusText = "Драйвер PawnIO переустановлен";
                return true;
            }

            AppLog.Warn("Драйвер PawnIO не переустановлен: " + result.Message);
            StatusText = "Драйвер PawnIO не переустановлен";
            ShowMessage($"{result.Message}\n\nПодробности в журнале приложения.", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            _installingDriver = false;
            RelayCommand.Refresh();
        }
    }

    private async Task<bool> InstallDriverAsync()
    {
        if (_installingDriver)
            return false;

        _installingDriver = true;
        RelayCommand.Refresh();
        StatusText = "Устанавливаю драйвер PawnIO…";

        try
        {
            PawnIoInstallResult result = await Task.Run(() => PawnIoInstaller.Install());
            UpdateEnvironment(EnvironmentStatus.Detect() with { ServiceConnected = _envStatus.ServiceConnected });

            if (!NeedsDriver)
            {
                AppLog.Info($"Драйвер PawnIO {_envStatus.PawnIoVersion} установлен.");
                StatusText = $"Драйвер PawnIO {_envStatus.PawnIoVersion} установлен";
                return true;
            }

            string message = result.Success
                ? "Установщик завершился, но драйвер PawnIO не появился в системе."
                : result.Message;
            AppLog.Warn("Драйвер PawnIO не установлен: " + message);
            StatusText = "Драйвер PawnIO не установлен";
            ShowMessage($"{message}\n\nПодробности в журнале приложения.", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            _installingDriver = false;
            RelayCommand.Refresh();
        }
    }

    private void UpdateEnvironment(EnvironmentStatus status)
    {
        _envStatus = status;
        OnPropertyChanged(nameof(EnvStatus));
        OnPropertyChanged(nameof(ElevationText));
        OnPropertyChanged(nameof(ServiceStatusText));
        OnPropertyChanged(nameof(StatusTooltip));
        OnPropertyChanged(nameof(PawnIoText));
        Home.SetEnvironment(status);
    }

    private static MessageBoxResult ShowMessage(string text, MessageBoxButton buttons, MessageBoxImage image) =>
        Application.Current?.MainWindow is { IsVisible: true } owner
            ? MessageBox.Show(owner, text, "PC Toolkit", buttons, image)
            : MessageBox.Show(text, "PC Toolkit", buttons, image);

    // Вызывается в фоновом потоке датчиков. Если интерфейс не успел обработать прошлый снимок,
    // старый просто заменяется новым: очередь не растёт.
    private void OnSnapshotReady(object? sender, HardwareSnapshot snapshot)
    {
        Interlocked.Exchange(ref _queuedSnapshot, snapshot);
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 1)
            return;

        _dispatcher.InvokeAsync(ApplyQueuedSnapshot, DispatcherPriority.Background);
    }

    private void ApplyQueuedSnapshot()
    {
        Interlocked.Exchange(ref _applyScheduled, 0);
        HardwareSnapshot? snapshot = Interlocked.Exchange(ref _queuedSnapshot, null);
        if (snapshot is null || _disposed)
            return;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка при обновлении интерфейса данными датчиков.", ex);
        }

        MeasureApply(started);
    }

    private void ApplySnapshot(HardwareSnapshot snapshot)
    {
        if (_history.Count == 0)
            AppLog.Info($"Первые данные датчиков на экране через {App.SinceStartMs} мс после запуска.");

        // Каждый снимок (до 4 раз в секунду): графики и цифры мониторинга и главной.
        _history.Add(snapshot);
        Home.Update(snapshot, _history);
        Monitoring.Update(snapshot, _history);

        // Остальное — раз в секунду, как было рассчитано: графики тестов считают точки секундами,
        // оверлей и запись FPS усредняют по секундам, строку состояния часто менять незачем.
        if (snapshot.Timestamp - _lastSlowUpdate < TimeSpan.FromMilliseconds(950))
            return;
        _lastSlowUpdate = snapshot.Timestamp;

        WriteInventoryIfChanged(snapshot);
        // Фактическая частота: служба старой версии отдаёт снимки раз в секунду, что бы ни выбрали.
        Monitoring.SetInterval(TimeSpan.FromSeconds(_history.RecentSampleSeconds));
        Benchmarks.Update(snapshot);
        _health.OnSnapshot(snapshot);
        Games.Update(snapshot);

        if (!_installingDriver)
        {
            int devices = snapshot.Devices.Count;
            int sensors = snapshot.SensorCount;
            StatusText = $"{devices} {Formatting.Plural(devices, "устройство", "устройства", "устройств")} · " +
                         $"{sensors} {Formatting.Plural(sensors, "датчик", "датчика", "датчиков")}";
        }
    }

    /// <summary>Сколько интерфейс тратит на один снимок — один раз в журнал (снимков до 4 в секунду).</summary>
    private void MeasureApply(long started)
    {
        if (_applyCount < 0)
            return;

        // Первые снимки пропускаем: там прогрев (JIT, построение таблицы).
        if (++_applyCount <= 20)
            return;

        _applyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        if (_applyCount < 100)
            return;

        double average = System.Diagnostics.Stopwatch.GetElapsedTime(0, _applyTicks / 80).TotalMilliseconds;
        AppLog.Info($"Интерфейс: обработка снимка датчиков в среднем {average:0.0} мс (открыт раздел «{_selectedNav?.Title}»).");
        _applyCount = -1;
    }

    /// <summary>Первое открытие раздела: время до того, как он нарисован, — в журнал.</summary>
    private void MeasureFirstVisit(NavItemViewModel item)
    {
        if (!_visited.Add(item.Page))
            return;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _dispatcher.BeginInvoke(() =>
            AppLog.Info($"Раздел «{item.Title}» впервые открыт за {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} мс."),
            DispatcherPriority.ContextIdle);
    }

    /// <summary>Список датчиков в sensors.txt: при первом снимке и когда набор устройств меняется.</summary>
    private void WriteInventoryIfChanged(HardwareSnapshot snapshot)
    {
        if (snapshot.Devices.Count == 0)
            return;

        int signature = snapshot.Devices.Count * 1000 + snapshot.SensorCount;
        if (signature == _inventorySignature)
            return;

        _inventorySignature = signature;
        _ = Task.Run(() => SensorInventory.Write(snapshot));
    }

    private void OnSourceStateChanged(object? sender, EventArgs e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            SourceState = _source.State;
            if (_source.State == SensorSourceState.Failed)
                StatusText = $"Датчики недоступны: {_source.LastError}. Подробности в журнале.";
        });
    }

    private void RestartAsAdmin()
    {
        if (ShellService.RestartAsAdministrator())
            App.Quit();
    }
}
