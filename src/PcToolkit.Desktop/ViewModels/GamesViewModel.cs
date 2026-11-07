using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PcToolkit.Core.Games;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Settings;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>
/// Экран «Игры»: список игр и профили, оверлей с FPS, замеры FPS «до и после».
/// Работает и когда окно скрыто в трее: следит за запуском игр, применяет профили, показывает оверлей.
/// </summary>
public sealed class GamesViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly Dispatcher _dispatcher;
    private readonly GameStore _store;
    private readonly GameSessionManager _sessions;
    private readonly FrameMonitor _monitor;
    private readonly FpsRecordingStore _recordingStore;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _recordingTimer;
    private readonly HashSet<string> _profileOverlayGames = new();
    private readonly HashSet<string> _pendingProfileRecordings = new();
    private Dictionary<string, HashSet<int>> _gameProcesses = new();
    private volatile List<GameProfile> _profileSnapshot = new();
    private volatile List<GameEntry> _gameSnapshot = new();
    private FrameMonitorState? _pendingState;
    private int _stateScheduled;
    private int _stateCounter;
    private string _tab = "games";
    private bool _pageVisible;
    private bool _monitorForOverlay;
    private bool _monitorForRecording;
    private bool _monitorForPage;
    private bool _overlayEnabled;
    private bool _overlaySuppressed;
    private FpsRecorder? _recorder;
    private string? _recordingGameId;
    private int _recordingLimitSeconds;
    private DateTime? _startRequestedAt;
    private bool _loaded;
    private bool _isLoading;
    private string _searchText = "";
    private GameItemViewModel? _selectedGame;
    private string _frameStatusText = "Счётчик кадров запускается, когда нужен: при включённом оверлее или записи.";
    private bool _frameSourceProblem;
    private string _recordingStatus = "";
    private int _recordingSeconds;
    private bool _closeToTray;
    private bool _autostart;
    private string _hotkeyWarning = "";
    private ComparisonData? _comparison;

    public GamesViewModel(IFrameSource frameSource, Dispatcher dispatcher, ICommand installServiceCommand, bool frameSourceAvailable)
    {
        _dispatcher = dispatcher;
        _store = GameStore.Load();
        _sessions = new GameSessionManager();
        _sessions.GameStarted += (_, game) => _dispatcher.InvokeAsync(() => OnGameStarted(game));
        _sessions.GameStopped += (_, game) => _dispatcher.InvokeAsync(() => OnGameStopped(game));
        _sessions.GameChanged += (_, game) => _dispatcher.InvokeAsync(() => OnGameChanged(game));
        _monitor = new FrameMonitor(frameSource);
        _monitor.Updated += OnFrameState;
        _recordingStore = FpsRecordingStore.Load();
        InstallServiceCommand = installServiceCommand;
        CanCountFrames = frameSourceAvailable;
        if (!frameSourceAvailable)
        {
            _frameSourceProblem = true;
            _frameStatusText = "Счётчику кадров нужна служба PC Toolkit (или запуск от имени администратора): только так Windows " +
                               "сообщает о кадрах других программ. Профили игр работают и без неё.";
        }

        AppSettings settings = AppSettings.Load();
        _overlayEnabled = settings.OverlayEnabled;
        _recordingSeconds = settings.RecordingSeconds;
        _closeToTray = settings.CloseToTray;
        _autostart = Autostart.IsEnabled;
        Overlay = new OverlayViewModel
        {
            Corner = settings.OverlayCorner,
            ShowSensors = settings.OverlayShowSensors,
            ShowGraph = settings.OverlayShowGraph,
        };

        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = item => item is GameItemViewModel game
                                   && (_searchText.Length == 0 || game.Name.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase));

        foreach (FpsRecording recording in _recordingStore.Recordings)
            Recordings.Add(new RecordingItemViewModel(recording, this));

        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !_isLoading);
        AddGameCommand = new RelayCommand(AddGameFromFile);
        RemoveGameCommand = new RelayCommand(RemoveSelectedGame, () => _selectedGame is { IsCustom: true });
        ToggleOverlayCommand = new RelayCommand(ToggleOverlay);
        ToggleRecordingCommand = new RelayCommand(ToggleRecording, () => CanCountFrames || _recorder is not null);
        ClearComparisonCommand = new RelayCommand(ClearComparison);

        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordingTimer.Tick += (_, _) => OnRecordingTick();

        PublishProfiles();
        UpdateMonitorUsage();
        _ = Task.Run(WatchLoop);
        // Список игр нужен и без открытого экрана: по нему запись FPS узнаёт игру.
        _dispatcher.InvokeAsync(() => _ = LoadAsync(), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Уведомление для трея: «замер сохранён» и т. п.</summary>
    public event EventHandler<string>? Notification;

    /// <summary>Изменилось состояние оверлея или записи (для меню трея).</summary>
    public event EventHandler? StateChanged;

    public OverlayViewModel Overlay { get; }

    public ObservableCollection<GameItemViewModel> Games { get; } = new();

    public ICollectionView GamesView { get; }

    public ObservableCollection<PresenterViewModel> Presenters { get; } = new();

    public ObservableCollection<RecordingItemViewModel> Recordings { get; } = new();

    public ICommand InstallServiceCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand AddGameCommand { get; }

    public ICommand RemoveGameCommand { get; }

    public ICommand ToggleOverlayCommand { get; }

    public ICommand ToggleRecordingCommand { get; }

    public ICommand ClearComparisonCommand { get; }

    public bool CanCountFrames { get; }

    // ----- Вкладки -----

    public bool IsGamesTab { get => _tab == "games"; set => SetTab("games", value); }

    public bool IsOverlayTab { get => _tab == "overlay"; set => SetTab("overlay", value); }

    public bool IsRecordingsTab { get => _tab == "recordings"; set => SetTab("recordings", value); }

    // ----- Счётчик кадров -----

    public string FrameStatusText
    {
        get => _frameStatusText;
        private set => SetProperty(ref _frameStatusText, value);
    }

    public bool FrameSourceProblem
    {
        get => _frameSourceProblem;
        private set => SetProperty(ref _frameSourceProblem, value);
    }

    // ----- Игры и профили -----

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                RelayCommand.Refresh();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value?.Trim() ?? ""))
                GamesView.Refresh();
        }
    }

    public GameItemViewModel? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetProperty(ref _selectedGame, value))
            {
                OnPropertyChanged(nameof(HasSelectedGame));
                RelayCommand.Refresh();
            }
        }
    }

    public bool HasSelectedGame => _selectedGame is not null;

    public string GamesSummary
    {
        get
        {
            int profiles = _store.Profiles.Count(p => p.Enabled);
            return Games.Count == 0
                ? (_isLoading ? "Ищу установленные игры…" : "Игры не найдены — добавьте игру вручную")
                : $"Игр: {Games.Count} · с профилем: {profiles}";
        }
    }

    internal GameStore Store => _store;

    // ----- Оверлей -----

    /// <summary>Оверлей во всех играх (настройка).</summary>
    public bool OverlayEnabled
    {
        get => _overlayEnabled;
        set
        {
            if (!SetProperty(ref _overlayEnabled, value))
                return;
            _overlaySuppressed = false;
            AppSettings.Update(s => s.OverlayEnabled = value);
            UpdateMonitorUsage();
        }
    }

    /// <summary>Оверлей сейчас работает (включён настройкой или профилем запущенной игры).</summary>
    public bool OverlayActive => !_overlaySuppressed && (_overlayEnabled || _profileOverlayGames.Count > 0);

    public bool IsTopLeft { get => Overlay.Corner == "TopLeft"; set => SetCorner("TopLeft", value); }

    public bool IsTopRight { get => Overlay.Corner == "TopRight"; set => SetCorner("TopRight", value); }

    public bool IsBottomLeft { get => Overlay.Corner == "BottomLeft"; set => SetCorner("BottomLeft", value); }

    public bool IsBottomRight { get => Overlay.Corner == "BottomRight"; set => SetCorner("BottomRight", value); }

    public bool OverlayShowSensors
    {
        get => Overlay.ShowSensors;
        set
        {
            Overlay.ShowSensors = value;
            AppSettings.Update(s => s.OverlayShowSensors = value);
            OnPropertyChanged();
        }
    }

    public bool OverlayShowGraph
    {
        get => Overlay.ShowGraph;
        set
        {
            Overlay.ShowGraph = value;
            AppSettings.Update(s => s.OverlayShowGraph = value);
            OnPropertyChanged();
        }
    }

    public string HotkeyWarning
    {
        get => _hotkeyWarning;
        set
        {
            if (SetProperty(ref _hotkeyWarning, value))
                OnPropertyChanged(nameof(HasHotkeyWarning));
        }
    }

    public bool HasHotkeyWarning => _hotkeyWarning.Length > 0;

    public bool HasPresenters => Presenters.Count > 0;

    // ----- Работа в фоне -----

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (SetProperty(ref _closeToTray, value))
                AppSettings.Update(s => s.CloseToTray = value);
        }
    }

    /// <summary>Запускать вместе с Windows (в трее).</summary>
    public bool StartWithWindows
    {
        get => _autostart;
        set
        {
            if (_autostart == value)
                return;
            if (Autostart.Set(value))
                _autostart = value;
            OnPropertyChanged();
        }
    }

    // ----- Запись -----

    public bool IsRecording => _recorder is not null;

    public string RecordingButtonText => _recorder is null ? "Начать запись" : "Остановить и сохранить";

    public string RecordingStatus
    {
        get => _recordingStatus;
        private set => SetProperty(ref _recordingStatus, value);
    }

    public bool IsDuration30 { get => _recordingSeconds == 30; set => SetDuration(30, value); }

    public bool IsDuration60 { get => _recordingSeconds == 60; set => SetDuration(60, value); }

    public bool IsDuration120 { get => _recordingSeconds == 120; set => SetDuration(120, value); }

    public bool IsDurationUnlimited { get => _recordingSeconds == 0; set => SetDuration(0, value); }

    public bool HasRecordings => Recordings.Count > 0;

    public ComparisonData? Comparison
    {
        get => _comparison;
        private set
        {
            if (SetProperty(ref _comparison, value))
            {
                OnPropertyChanged(nameof(HasComparison));
                OnPropertyChanged(nameof(HasSingleRecording));
                OnPropertyChanged(nameof(HasNoSelection));
            }
        }
    }

    public bool HasComparison => _comparison is { B: not null };

    public bool HasSingleRecording => _comparison is { B: null };

    public bool HasNoSelection => _comparison is null;

    // ===================================================================

    /// <summary>Экран открыт или закрыт: пока открыта вкладка оверлея или замеров, список «кто выводит кадры» живой.</summary>
    public void SetPageVisible(bool visible)
    {
        _pageVisible = visible;
        UpdateMonitorUsage();
        if (visible && !_loaded && !_isLoading)
            _ = LoadAsync();
    }

    /// <summary>Данные датчиков раз в секунду: для оверлея и записи.</summary>
    public void Update(HardwareSnapshot snapshot)
    {
        if (!OverlayActive && _recorder is null)
            return;

        KeyMetrics metrics = KeyMetrics.From(snapshot);
        Overlay.UpdateSensors(metrics);
        _recorder?.OnSensors(metrics.CpuLoad?.Value, metrics.GpuLoad?.Value, metrics.CpuTemperature?.Value,
            metrics.GpuTemperature?.Value, metrics.GpuPower?.Value);
    }

    /// <summary>Ctrl+Shift+F10 и меню трея.</summary>
    public void ToggleOverlay()
    {
        if (OverlayActive)
        {
            if (_profileOverlayGames.Count > 0)
                _overlaySuppressed = true;
            if (_overlayEnabled)
            {
                _overlayEnabled = false;
                AppSettings.Update(s => s.OverlayEnabled = false);
            }
        }
        else if (_profileOverlayGames.Count > 0 && _overlaySuppressed)
        {
            _overlaySuppressed = false;
        }
        else
        {
            _overlayEnabled = true;
            _overlaySuppressed = false;
            AppSettings.Update(s => s.OverlayEnabled = true);
        }

        OnPropertyChanged(nameof(OverlayEnabled));
        UpdateMonitorUsage();
    }

    /// <summary>Ctrl+Shift+F11, кнопка и меню трея: начать или остановить запись FPS.</summary>
    public void ToggleRecording()
    {
        if (_recorder is not null)
        {
            StopRecording();
            return;
        }

        if (!CanCountFrames)
        {
            RecordingStatus = "Счётчик кадров недоступен: установите службу PC Toolkit.";
            return;
        }

        // Если счётчик уже работает, игра известна сразу; иначе он запустится, и запись начнётся с первым ответом.
        if (_monitor.Last is { } state && ChooseRecordingTarget(state) is int pid && (_monitorForOverlay || _monitorForPage))
        {
            StartRecording(pid, FindGameId(pid), automatic: false);
            return;
        }

        _startRequestedAt = DateTime.Now;
        RecordingStatus = "Ищу игру, которая выводит кадры…";
        UpdateMonitorUsage();
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_recorder is not null)
            StopRecording();
        _sessions.RevertAll();
        _monitor.Updated -= OnFrameState;
        _monitor.Dispose();
    }

    // ----- Список игр -----

    private async Task LoadAsync()
    {
        if (_isLoading)
            return;

        IsLoading = true;
        OnPropertyChanged(nameof(GamesSummary));
        try
        {
            IReadOnlyList<GameEntry> found = await Task.Run(GameLibrary.Discover);
            var all = found.ToList();
            foreach (GameEntry custom in _store.CustomGames)
            {
                if (all.All(g => g.Id != custom.Id))
                    all.Add(custom);
            }

            // Профиль есть, а игру больше не находит (переустановлена в другую папку и т. п.) — показываем по профилю.
            foreach (GameProfile profile in _store.Profiles)
            {
                if (all.All(g => g.Id != profile.GameId))
                {
                    all.Add(new GameEntry
                    {
                        Id = profile.GameId,
                        Name = profile.Name,
                        Source = "Профиль",
                        InstallDir = profile.InstallDir,
                        Executable = profile.Executable,
                    });
                }
            }

            string? selectedId = _selectedGame?.Entry.Id;
            Games.Clear();
            foreach (GameEntry game in all.OrderBy(g => _store.Find(g.Id) is { Enabled: true } ? 0 : 1)
                                          .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new GameItemViewModel(game, this);
                item.IsRunning = _gameProcesses.ContainsKey(game.Id);
                Games.Add(item);
            }

            _gameSnapshot = all;
            _loaded = true;
            SelectedGame = Games.FirstOrDefault(g => g.Entry.Id == selectedId);
            AppLog.Info($"Найдено игр: {Games.Count} (с профилем: {_store.Profiles.Count(p => p.Enabled)}).");
            _ = LoadIconsAsync(Games.ToList());
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось найти игры.", ex);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(GamesSummary));
        }
    }

    private async Task LoadIconsAsync(List<GameItemViewModel> items)
    {
        foreach (GameItemViewModel item in items)
        {
            string exe = item.Entry.Executable;
            if (exe.Length == 0)
                continue;
            ImageSource? icon = await Task.Run(() => AppIcons.LoadExecutable(exe));
            if (icon is not null)
                item.Icon = icon;
        }
    }

    private void AddGameFromFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите exe-файл игры",
            Filter = "Программы (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
            AddCustomGame(GameLibrary.FromExecutable(dialog.FileName));
    }

    /// <summary>Добавляет игру по процессу, который выводит кадры.</summary>
    internal void AddPresenterAsGame(PresenterViewModel presenter)
    {
        string? path = ProcessInfo.ImagePath(presenter.ProcessId);
        if (path is null)
        {
            MessageBox.Show("Windows не отдала путь к программе этого процесса.", "PC Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AddCustomGame(GameLibrary.FromExecutable(path));
        IsGamesTab = true;
    }

    private void AddCustomGame(GameEntry game)
    {
        GameItemViewModel? existing = Games.FirstOrDefault(g => g.Entry.Id == game.Id
            || (g.Entry.Executable.Length > 0 && string.Equals(g.Entry.Executable, game.Executable, StringComparison.OrdinalIgnoreCase)));
        if (existing is null)
        {
            _store.CustomGames.Add(game);
            _store.Save();
            existing = new GameItemViewModel(game, this);
            Games.Insert(0, existing);
            _gameSnapshot = Games.Select(g => g.Entry).ToList();
            existing.Icon = AppIcons.LoadExecutable(game.Executable);
            AppLog.Info($"Игра добавлена вручную: {game.Name} ({game.Executable}).");
        }

        SearchText = "";
        OnPropertyChanged(nameof(SearchText));
        SelectedGame = existing;
        OnPropertyChanged(nameof(GamesSummary));
    }

    private void RemoveSelectedGame()
    {
        if (_selectedGame is not { IsCustom: true } item)
            return;

        _store.CustomGames.RemoveAll(g => g.Id == item.Entry.Id);
        _store.Profiles.RemoveAll(p => p.GameId == item.Entry.Id);
        _store.Save();
        Games.Remove(item);
        _gameSnapshot = Games.Select(g => g.Entry).ToList();
        SelectedGame = null;
        PublishProfiles();
        OnPropertyChanged(nameof(GamesSummary));
    }

    // ----- Профили -----

    internal void SetProfileEnabled(GameItemViewModel item, bool enabled)
    {
        GameProfile? profile = _store.Find(item.Entry.Id);
        if (profile is null)
        {
            if (!enabled)
                return;
            profile = GameProfile.For(item.Entry);
            _store.Profiles.Add(profile);
        }
        else
        {
            profile.Enabled = enabled;
        }

        _store.Save();
        PublishProfiles();
        AppLog.Info($"Профиль «{profile.Name}» {(enabled ? "включён" : "выключен")} ({profile.Summary}).");
        item.Refresh();
        OnPropertyChanged(nameof(GamesSummary));
    }

    internal void UpdateProfile(GameItemViewModel item, Action<GameProfile> change)
    {
        GameProfile? profile = _store.Find(item.Entry.Id);
        if (profile is null)
            return;

        change(profile);
        _store.Save();
        PublishProfiles();
        item.Refresh();
    }

    internal RunningGame? SessionFor(string gameId) => _sessions.Running.FirstOrDefault(r => r.Profile.GameId == gameId);

    private void PublishProfiles()
    {
        // Копии для фонового потока: он не должен видеть профиль посреди изменения.
        _profileSnapshot = _store.Profiles.Select(p => new GameProfile
        {
            GameId = p.GameId,
            Name = p.Name,
            InstallDir = p.InstallDir,
            Executable = p.Executable,
            Enabled = p.Enabled,
            PowerPlan = p.PowerPlan,
            HighPriority = p.HighPriority,
            Overlay = p.Overlay,
            RecordFps = p.RecordFps,
        }).ToList();
    }

    /// <summary>Раз в 2 секунды: какие игры запущены; применить или откатить профили.</summary>
    private async Task WatchLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                try
                {
                    IReadOnlyList<RunningProcess> processes = ProcessInfo.List();
                    List<GameProfile> profiles = _profileSnapshot;
                    _sessions.Update(profiles, processes);

                    var running = new Dictionary<string, HashSet<int>>();
                    foreach (GameProfile profile in profiles)
                        AddMatches(running, profile.GameId, profile, processes);
                    foreach (GameEntry game in _gameSnapshot)
                    {
                        if (!running.ContainsKey(game.Id))
                            AddMatches(running, game.Id, GameProfile.For(game), processes);
                    }

                    await _dispatcher.InvokeAsync(() => ApplyRunning(running));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Error("Ошибка при проверке запущенных игр.", ex);
                }
            }
            while (await timer.WaitForNextTickAsync(_cts.Token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void AddMatches(Dictionary<string, HashSet<int>> running, string id, GameProfile probe, IReadOnlyList<RunningProcess> processes)
    {
        foreach (RunningProcess process in processes)
        {
            if (!probe.Matches(process))
                continue;
            if (!running.TryGetValue(id, out HashSet<int>? pids))
                running[id] = pids = new HashSet<int>();
            pids.Add(process.Id);
        }
    }

    private void ApplyRunning(Dictionary<string, HashSet<int>> running)
    {
        _gameProcesses = running;
        foreach (GameItemViewModel item in Games)
        {
            item.IsRunning = running.ContainsKey(item.Entry.Id);
            item.RefreshStatus();
        }
    }

    private void OnGameStarted(RunningGame game)
    {
        GameProfile profile = game.Profile;
        if (profile.Overlay)
        {
            _profileOverlayGames.Add(profile.GameId);
            _overlaySuppressed = false;
        }

        if (profile.RecordFps && CanCountFrames)
            _pendingProfileRecordings.Add(profile.GameId);

        UpdateMonitorUsage();
        Games.FirstOrDefault(g => g.Entry.Id == profile.GameId)?.RefreshStatus();
        Notification?.Invoke(this, $"{profile.Name}: профиль применён");
    }

    /// <summary>Профиль запущенной игры изменили: оверлей и запись по профилю следуют за ним сразу.</summary>
    private void OnGameChanged(RunningGame game)
    {
        GameProfile profile = game.Profile;
        if (profile.Overlay)
        {
            if (_profileOverlayGames.Add(profile.GameId))
                _overlaySuppressed = false;
        }
        else
        {
            _profileOverlayGames.Remove(profile.GameId);
        }

        if (profile.RecordFps)
        {
            if (CanCountFrames && _recordingGameId != profile.GameId)
                _pendingProfileRecordings.Add(profile.GameId);
        }
        else
        {
            _pendingProfileRecordings.Remove(profile.GameId);
            if (_recorder is not null && _recordingGameId == profile.GameId)
                StopRecording();
        }

        UpdateMonitorUsage();
        Games.FirstOrDefault(g => g.Entry.Id == profile.GameId)?.RefreshStatus();
    }

    private void OnGameStopped(RunningGame game)
    {
        string id = game.Profile.GameId;
        _profileOverlayGames.Remove(id);
        _pendingProfileRecordings.Remove(id);
        if (_recorder is not null && _recordingGameId == id)
            StopRecording();

        UpdateMonitorUsage();
        Games.FirstOrDefault(g => g.Entry.Id == id)?.RefreshStatus();
    }

    // ----- Счётчик кадров -----

    private void OnFrameState(object? sender, FrameMonitorState state)
    {
        Interlocked.Exchange(ref _pendingState, state);
        if (Interlocked.Exchange(ref _stateScheduled, 1) == 1)
            return;

        _dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _stateScheduled, 0);
            if (Interlocked.Exchange(ref _pendingState, null) is { } latest)
                ApplyFrameState(latest);
        }, DispatcherPriority.Background);
    }

    private void ApplyFrameState(FrameMonitorState state)
    {
        if (state.Error is { } error)
        {
            FrameSourceProblem = true;
            FrameStatusText = $"Счётчик кадров недоступен: {error}.";
        }
        else
        {
            FrameSourceProblem = false;
            FrameStatusText = $"Счётчик кадров работает ({_monitor.SourceName}).";
        }

        // Оверлей виден, только когда на переднем плане игра, чьи кадры считаются: игра из списка,
        // записываемый процесс или любое окно на весь экран. В браузере и обычных окнах его нет.
        Overlay.UpdateFrames(state);
        bool visible = false;
        if (OverlayActive && state.ProcessId is int target)
        {
            int? foreground = ProcessInfo.ForegroundProcessId();
            bool isGame = IsKnownGame(target) || _recorder?.ProcessId == target || ProcessInfo.IsForegroundFullscreen();
            visible = (foreground is null || foreground == target) && isGame;
        }

        Overlay.IsVisible = visible;

        // Запись по профилю начинается, как только процесс игры начал выводить кадры.
        if (_recorder is null && _pendingProfileRecordings.Count > 0)
        {
            foreach (string gameId in _pendingProfileRecordings.ToList())
            {
                if (_gameProcesses.TryGetValue(gameId, out HashSet<int>? pids)
                    && state.Presenters.FirstOrDefault(p => pids.Contains(p.ProcessId)) is { } presenter)
                {
                    _pendingProfileRecordings.Remove(gameId);
                    StartRecording(presenter.ProcessId, gameId, automatic: true);
                    break;
                }
            }
        }

        // Запись, запрошенная кнопкой или клавишей, пока счётчик ещё не работал.
        if (_startRequestedAt is DateTime requested && _recorder is null)
        {
            if (ChooseRecordingTarget(state) is int pid)
            {
                _startRequestedAt = null;
                StartRecording(pid, FindGameId(pid), automatic: false);
            }
            else if (DateTime.Now - requested > TimeSpan.FromSeconds(6) || state.Error is not null)
            {
                _startRequestedAt = null;
                RecordingStatus = state.Error is not null
                    ? "Запись не началась: счётчик кадров недоступен."
                    : "Ни одна игра сейчас не выводит кадры. Запустите игру и нажмите Ctrl+Shift+F11 прямо в ней.";
                UpdateMonitorUsage();
            }
        }

        if (_recorder is not null)
            UpdateRecordingStatus(state);

        // Список «кто выводит кадры» — раз в секунду, и только когда его видно.
        if (_pageVisible && _tab == "overlay" && ++_stateCounter % 4 == 1)
            UpdatePresenters(state.Presenters);
    }

    private void UpdatePresenters(IReadOnlyList<FramePresenter> presenters)
    {
        var known = new HashSet<int>(_gameProcesses.Values.SelectMany(p => p));
        Presenters.Clear();
        foreach (FramePresenter presenter in presenters.Take(12))
        {
            string name = ProcessInfo.Name(presenter.ProcessId) ?? $"PID {presenter.ProcessId}";
            if (string.Equals(name, "dwm", StringComparison.OrdinalIgnoreCase))
                continue; // сам рабочий стол Windows
            Presenters.Add(new PresenterViewModel(presenter, name, known.Contains(presenter.ProcessId), this));
        }

        OnPropertyChanged(nameof(HasPresenters));
    }

    private void UpdateMonitorUsage()
    {
        bool wantRecording = _recorder is not null || _pendingProfileRecordings.Count > 0 || _startRequestedAt is not null;
        bool wantPage = _pageVisible && _tab is "overlay" or "recordings";
        Use(ref _monitorForOverlay, OverlayActive && CanCountFrames);
        Use(ref _monitorForRecording, wantRecording && CanCountFrames);
        Use(ref _monitorForPage, wantPage && CanCountFrames && _pageVisible);
        if (!OverlayActive)
            Overlay.IsVisible = false;

        OnPropertyChanged(nameof(OverlayActive));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Use(ref bool flag, bool value)
    {
        if (flag == value)
            return;

        flag = value;
        if (value)
            _monitor.Acquire();
        else
            _monitor.Release();
    }

    // ----- Запись FPS -----

    /// <summary>Чью запись начать: игру на переднем плане, иначе известную игру, иначе самый «быстрый» процесс.</summary>
    private int? ChooseRecordingTarget(FrameMonitorState state)
    {
        if (state.ProcessId is int target && state.Fps > 0)
            return target;

        var known = new HashSet<int>(_gameProcesses.Values.SelectMany(p => p));
        FramePresenter? game = state.Presenters.FirstOrDefault(p => known.Contains(p.ProcessId) && p.Fps > 0);
        return game?.ProcessId ?? state.Presenters.FirstOrDefault(p => p.Fps >= 20)?.ProcessId;
    }

    private bool IsKnownGame(int processId) => _gameProcesses.Values.Any(pids => pids.Contains(processId));

    private string? FindGameId(int processId) =>
        _gameProcesses.FirstOrDefault(p => p.Value.Contains(processId)).Key;

    private void StartRecording(int processId, string? gameId, bool automatic)
    {
        string processName = ProcessInfo.Name(processId) ?? $"PID {processId}";
        GameEntry? game = gameId is null ? null : _gameSnapshot.FirstOrDefault(g => g.Id == gameId);
        RunningGame? session = gameId is null ? null : SessionFor(gameId);
        _recorder = new FpsRecorder(processId, game?.Name ?? session?.Profile.Name ?? processName, gameId ?? "", processName,
            session is not null, session?.Profile.Summary ?? "");
        _recordingGameId = automatic ? gameId : null;
        _recordingLimitSeconds = automatic ? 0 : _recordingSeconds;
        _monitor.PinnedProcessId = processId;
        _monitor.AddListener(_recorder);
        _recordingTimer.Start();
        Overlay.IsRecording = true;
        Overlay.RecordingText = "REC 0:00";
        RecordingStatus = $"Запись: {_recorder.GameName}…";
        AppLog.Info($"Запись FPS: {_recorder.GameName} ({processName}, PID {processId}){(automatic ? " по профилю" : "")}.");
        OnRecordingChanged();
        UpdateMonitorUsage();
    }

    private void StopRecording()
    {
        FpsRecorder? recorder = _recorder;
        if (recorder is null)
            return;

        _recorder = null;
        _recordingGameId = null;
        _monitor.RemoveListener(recorder);
        _monitor.PinnedProcessId = null;
        _recordingTimer.Stop();
        Overlay.IsRecording = false;

        FpsRecording? result = recorder.Finish();
        if (result is null)
        {
            RecordingStatus = "Запись слишком короткая (меньше 5 секунд кадров) — не сохранена.";
        }
        else
        {
            _recordingStore.Add(result);
            Recordings.Insert(0, new RecordingItemViewModel(result, this));
            OnPropertyChanged(nameof(HasRecordings));
            string text = $"{result.GameName}: {Fps(result.AverageFps)} FPS, 1% low {Fps(result.Low1Fps)}";
            RecordingStatus = "Замер сохранён — " + text;
            Notification?.Invoke(this, "Замер FPS сохранён: " + text);
            AppLog.Info($"Замер FPS сохранён: {text}, {result.Seconds:0} с, кадров {result.Frames}.");
        }

        OnRecordingChanged();
        UpdateMonitorUsage();
    }

    private void OnRecordingTick()
    {
        if (_recorder is not { } recorder)
            return;

        if (_recordingLimitSeconds > 0 && recorder.RecordedSeconds >= _recordingLimitSeconds)
        {
            StopRecording();
            return;
        }

        if (ProcessInfo.Name(recorder.ProcessId) is null)
        {
            AppLog.Info("Процесс игры завершился — запись FPS остановлена.");
            StopRecording();
            return;
        }

        Overlay.RecordingText = "REC " + Duration(recorder.RecordedSeconds);
    }

    private void UpdateRecordingStatus(FrameMonitorState state)
    {
        if (_recorder is not { } recorder)
            return;

        string limit = _recordingLimitSeconds > 0 ? $" из {Duration(_recordingLimitSeconds)}" : "";
        string fps = state.ProcessId != recorder.ProcessId ? " · нет кадров"
            : !state.InForeground ? " · пауза: игра не на экране, кадры не пишутся"
            : state.Fps > 0 ? $" · {Fps(state.Fps)} FPS"
            : " · нет кадров";
        RecordingStatus = $"Запись: {recorder.GameName} · {Duration(recorder.RecordedSeconds)}{limit}{fps}";
    }

    private void OnRecordingChanged()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingButtonText));
        RelayCommand.Refresh();
    }

    internal void DeleteRecording(RecordingItemViewModel item)
    {
        _recordingStore.Remove(item.Recording.Id);
        Recordings.Remove(item);
        OnPropertyChanged(nameof(HasRecordings));
        UpdateComparison();
    }

    /// <summary>Отмечено не больше двух замеров: при третьей отметке снимается самая старая.</summary>
    internal void OnCompareChanged(RecordingItemViewModel item)
    {
        if (item.IsCompared)
        {
            var marked = Recordings.Where(r => r.IsCompared && r != item).OrderBy(r => r.MarkedAt).ToList();
            while (marked.Count >= 2)
            {
                marked[0].SetComparedSilently(false);
                marked.RemoveAt(0);
            }
        }

        UpdateComparison();
    }

    private void ClearComparison()
    {
        foreach (RecordingItemViewModel item in Recordings.Where(r => r.IsCompared))
            item.SetComparedSilently(false);
        UpdateComparison();
    }

    private void UpdateComparison()
    {
        var marked = Recordings.Where(r => r.IsCompared).Select(r => r.Recording).OrderBy(r => r.At).ToList();
        Comparison = marked.Count switch
        {
            0 => null,
            1 => new ComparisonData(marked[0], null),
            _ => new ComparisonData(marked[0], marked[1]),
        };
    }

    private void SetTab(string tab, bool value)
    {
        if (!value || _tab == tab)
            return;

        _tab = tab;
        OnPropertyChanged(nameof(IsGamesTab));
        OnPropertyChanged(nameof(IsOverlayTab));
        OnPropertyChanged(nameof(IsRecordingsTab));
        UpdateMonitorUsage();
    }

    private void SetCorner(string corner, bool value)
    {
        if (!value || Overlay.Corner == corner)
            return;

        Overlay.Corner = corner;
        AppSettings.Update(s => s.OverlayCorner = corner);
        OnPropertyChanged(nameof(IsTopLeft));
        OnPropertyChanged(nameof(IsTopRight));
        OnPropertyChanged(nameof(IsBottomLeft));
        OnPropertyChanged(nameof(IsBottomRight));
    }

    private void SetDuration(int seconds, bool value)
    {
        if (!value || _recordingSeconds == seconds)
            return;

        _recordingSeconds = seconds;
        AppSettings.Update(s => s.RecordingSeconds = seconds);
        OnPropertyChanged(nameof(IsDuration30));
        OnPropertyChanged(nameof(IsDuration60));
        OnPropertyChanged(nameof(IsDuration120));
        OnPropertyChanged(nameof(IsDurationUnlimited));
    }

    internal static string Fps(double value) => value.ToString(value < 100 ? "0.#" : "0", Russian);

    internal static string Duration(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", Russian) : time.ToString(@"m\:ss", Russian);
    }
}

/// <summary>Игра в списке и её профиль.</summary>
public sealed class GameItemViewModel : ObservableObject
{
    private readonly GamesViewModel _owner;
    private ImageSource? _icon;
    private bool _isRunning;

    public GameItemViewModel(GameEntry entry, GamesViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    public GameEntry Entry { get; }

    public string Name => Entry.Name;

    public string Source => Entry.Source;

    public string InstallDir => Entry.InstallDir;

    public bool IsCustom => Entry.Id.StartsWith("manual:", StringComparison.Ordinal);

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
                OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    private GameProfile? Profile => _owner.Store.Find(Entry.Id);

    public bool HasProfile => Profile is { Enabled: true };

    public bool ProfileEnabled
    {
        get => Profile is { Enabled: true };
        set => _owner.SetProfileEnabled(this, value);
    }

    public bool PowerPlan
    {
        get => Profile?.PowerPlan ?? true;
        set => _owner.UpdateProfile(this, p => p.PowerPlan = value);
    }

    public bool HighPriority
    {
        get => Profile?.HighPriority ?? false;
        set => _owner.UpdateProfile(this, p => p.HighPriority = value);
    }

    public bool Overlay
    {
        get => Profile?.Overlay ?? true;
        set => _owner.UpdateProfile(this, p => p.Overlay = value);
    }

    public bool RecordFps
    {
        get => Profile?.RecordFps ?? false;
        set => _owner.UpdateProfile(this, p => p.RecordFps = value);
    }

    public string ProfileSummary => Profile is { Enabled: true } profile ? "Профиль: " + profile.Summary : "";

    /// <summary>Сейчас: запущена ли игра и что сделал профиль.</summary>
    public string StatusText
    {
        get
        {
            RunningGame? session = _owner.SessionFor(Entry.Id);
            if (session is not null)
            {
                string since = session.StartedAt.ToString("HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
                return $"Игра запущена в {since} ({session.MainProcessName}). Профиль применён:\n• " + string.Join("\n• ", session.Applied);
            }

            if (_isRunning)
                return HasProfile
                    ? "Игра запущена. Профиль применится в течение пары секунд."
                    : "Игра запущена. Профиль выключен — ничего не меняется.";

            return HasProfile ? "Игра не запущена. Профиль сработает при следующем запуске." : "";
        }
    }

    public bool HasStatus => StatusText.Length > 0;

    public void Refresh()
    {
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(ProfileEnabled));
        OnPropertyChanged(nameof(PowerPlan));
        OnPropertyChanged(nameof(HighPriority));
        OnPropertyChanged(nameof(Overlay));
        OnPropertyChanged(nameof(RecordFps));
        OnPropertyChanged(nameof(ProfileSummary));
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
    }
}

/// <summary>Процесс, который сейчас выводит кадры.</summary>
public sealed class PresenterViewModel
{
    public PresenterViewModel(FramePresenter presenter, string name, bool isKnownGame, GamesViewModel owner)
    {
        ProcessId = presenter.ProcessId;
        Name = name;
        FpsText = GamesViewModel.Fps(presenter.Fps) + " FPS";
        Api = presenter.Api;
        IsKnownGame = isKnownGame;
        AddCommand = new RelayCommand(() => owner.AddPresenterAsGame(this));
    }

    public int ProcessId { get; }

    public string Name { get; }

    public string FpsText { get; }

    public string Api { get; }

    public bool IsKnownGame { get; }

    public bool CanAdd => !IsKnownGame;

    public ICommand AddCommand { get; }
}

/// <summary>Сохранённый замер в списке.</summary>
public sealed class RecordingItemViewModel : ObservableObject
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly GamesViewModel _owner;
    private bool _isCompared;

    public RecordingItemViewModel(FpsRecording recording, GamesViewModel owner)
    {
        Recording = recording;
        _owner = owner;
        DeleteCommand = new RelayCommand(() => owner.DeleteRecording(this));
    }

    public FpsRecording Recording { get; }

    public string Title => Recording.GameName;

    public string DateText => Recording.At.LocalDateTime.ToString("d MMM, HH:mm", Russian) + " · " + GamesViewModel.Duration(Recording.Seconds);

    public string SummaryText =>
        $"{GamesViewModel.Fps(Recording.AverageFps)} FPS  ·  1% low {GamesViewModel.Fps(Recording.Low1Fps)}  ·  0,1% low {GamesViewModel.Fps(Recording.Low01Fps)}";

    public string ProfileText => Recording.ProfileActive ? "с профилем" : "без профиля";

    public bool ProfileActive => Recording.ProfileActive;

    public DateTime MarkedAt { get; private set; }

    public bool IsCompared
    {
        get => _isCompared;
        set
        {
            if (!SetProperty(ref _isCompared, value))
                return;
            MarkedAt = DateTime.Now;
            _owner.OnCompareChanged(this);
        }
    }

    public ICommand DeleteCommand { get; }

    internal void SetComparedSilently(bool value)
    {
        _isCompared = value;
        OnPropertyChanged(nameof(IsCompared));
    }
}

/// <summary>Один замер (B = null) или сравнение двух: A — более ранний, B — более поздний.</summary>
public sealed class ComparisonData
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public ComparisonData(FpsRecording a, FpsRecording? b)
    {
        A = a;
        B = b;
        Rows = BuildRows(a, b);
    }

    public FpsRecording A { get; }

    public FpsRecording? B { get; }

    public string TitleA => $"{A.GameName} · {A.At.LocalDateTime.ToString("d MMM, HH:mm", Russian)} · {(A.ProfileActive ? "с профилем" : "без профиля")}";

    public string TitleB => B is null ? "" : $"{B.GameName} · {B.At.LocalDateTime.ToString("d MMM, HH:mm", Russian)} · {(B.ProfileActive ? "с профилем" : "без профиля")}";

    public IReadOnlyList<double> TimelineA => A.FpsTimeline;

    public IReadOnlyList<double> TimelineB => B?.FpsTimeline ?? (IReadOnlyList<double>)Array.Empty<double>();

    public IReadOnlyList<ComparisonRow> Rows { get; }

    public string Verdict
    {
        get
        {
            if (B is null)
                return A.ProfileText.Length > 0 ? "Профиль во время замера: " + A.ProfileText : "";

            double avg = Change(A.AverageFps, B.AverageFps);
            double low = Change(A.Low1Fps, B.Low1Fps);
            string Describe(double change) => Math.Abs(change) < 3 ? "почти не изменился" : change > 0 ? $"вырос на {change:0.#} %" : $"упал на {-change:0.#} %";
            return $"Во втором замере средний FPS {Describe(avg)}, 1% low {Describe(low)}. " +
                   "Разница меньше 3 % обычно в пределах погрешности: сцены в игре никогда не совпадают полностью.";
        }
    }

    private static double Change(double before, double after) => before > 0 ? (after - before) / before * 100 : 0;

    private static IReadOnlyList<ComparisonRow> BuildRows(FpsRecording a, FpsRecording? b)
    {
        var rows = new List<ComparisonRow>
        {
            Row("Средний FPS", a.AverageFps, b?.AverageFps, higherIsBetter: true, v => GamesViewModel.Fps(v)),
            Row("1% low", a.Low1Fps, b?.Low1Fps, higherIsBetter: true, v => GamesViewModel.Fps(v)),
            Row("0,1% low", a.Low01Fps, b?.Low01Fps, higherIsBetter: true, v => GamesViewModel.Fps(v)),
            Row("Время кадра, среднее", a.AverageFrameMs, b?.AverageFrameMs, higherIsBetter: false, v => v.ToString("0.0", Russian) + " мс"),
            Row("Время кадра, 99%", a.P99FrameMs, b?.P99FrameMs, higherIsBetter: false, v => v.ToString("0.0", Russian) + " мс"),
            Row("Кадров дольше 50 мс", a.LongFrames, b?.LongFrames, higherIsBetter: false, v => v.ToString("0", Russian)),
            Row("Длительность", a.Seconds, b?.Seconds, null, v => GamesViewModel.Duration(v)),
        };

        if (a.CpuLoad is not null || b?.CpuLoad is not null)
            rows.Add(Row("Загрузка CPU", a.CpuLoad, b?.CpuLoad, null, v => v.ToString("0", Russian) + " %"));
        if (a.GpuLoad is not null || b?.GpuLoad is not null)
            rows.Add(Row("Загрузка GPU", a.GpuLoad, b?.GpuLoad, null, v => v.ToString("0", Russian) + " %"));
        if (a.GpuTemperatureMax is not null || b?.GpuTemperatureMax is not null)
            rows.Add(Row("Температура GPU, макс.", a.GpuTemperatureMax, b?.GpuTemperatureMax, false, v => v.ToString("0", Russian) + " °C"));
        if (a.CpuTemperatureMax is not null || b?.CpuTemperatureMax is not null)
            rows.Add(Row("Температура CPU, макс.", a.CpuTemperatureMax, b?.CpuTemperatureMax, false, v => v.ToString("0", Russian) + " °C"));
        if (a.GpuPower is not null || b?.GpuPower is not null)
            rows.Add(Row("Мощность GPU, средняя", a.GpuPower, b?.GpuPower, null, v => v.ToString("0", Russian) + " Вт"));
        return rows;
    }

    private static ComparisonRow Row(string name, double? a, double? b, bool? higherIsBetter, Func<double, string> format)
    {
        string aText = a is double av ? format(av) : "—";
        string bText = b is double bv ? format(bv) : "—";
        string delta = "";
        string kind = "Same";
        if (a is double x && b is double y && x > 0)
        {
            double change = (y - x) / x * 100;
            delta = (change >= 0 ? "+" : "−") + Math.Abs(change).ToString("0.#", Russian) + " %";
            if (higherIsBetter is bool higher && Math.Abs(change) >= 3)
                kind = (change > 0) == higher ? "Better" : "Worse";
        }

        return new ComparisonRow(name, aText, bText, delta, kind);
    }
}

/// <summary>Строка сравнения. Kind: Better, Worse, Same — для цвета разницы.</summary>
public sealed record ComparisonRow(string Metric, string A, string B, string Delta, string Kind);
