using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using PcToolkit.Core;
using PcToolkit.Core.Benchmarks;
using PcToolkit.Core.Diagnostics;
using PcToolkit.Core.Platform;
using PcToolkit.Core.Sensors;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Строка «Последние замеры FPS» на главной.</summary>
public sealed record HomeRecordingRow(string Game, string When, string Average, string Low, string Profile, bool WithProfile);

/// <summary>Строка «Последние тесты» на главной.</summary>
public sealed record HomeTestRow(string What, string Result, string When);

/// <summary>Быстрое действие на главной: значок, название, пояснение, куда ведёт.</summary>
public sealed class HomeActionViewModel
{
    public HomeActionViewModel(string title, string text, Geometry icon, DeviceColors colors, ICommand command)
    {
        Title = title;
        Text = text;
        Icon = icon;
        Colors = colors;
        Command = command;
    }

    public string Title { get; }

    public string Text { get; }

    public Geometry Icon { get; }

    public DeviceColors Colors { get; }

    public ICommand Command { get; }
}

/// <summary>
/// Главный экран: итог проверки системы, плитки устройств, что с играми сейчас и последние замеры,
/// быстрые действия и последние тесты.
/// </summary>
public sealed class HomeViewModel : ObservableObject
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly GamesViewModel _games;
    private readonly BenchmarkHistory _benchmarks;
    private EnvironmentStatus _environment;
    private HardwareSnapshot _latest = HardwareSnapshot.Empty;
    private IReadOnlyList<Finding> _problems = Array.Empty<Finding>();
    private IReadOnlyList<Finding> _tips = Array.Empty<Finding>();
    private readonly Dictionary<string, IReadOnlyList<Finding>> _extraFindings = new();
    private IReadOnlyList<HomeRecordingRow> _recordings = Array.Empty<HomeRecordingRow>();
    private IReadOnlyList<HomeTestRow> _tests = Array.Empty<HomeTestRow>();
    private string _statusTitle = "Проверка запустится, как только придут данные датчиков";
    private string _statusDetail = "Проверка только читает данные и ничего не меняет.";
    private FindingSeverity _statusSeverity = FindingSeverity.Info;
    private string _subtitle = "";
    private string _gameTitle = "";
    private string _gameDetail = "";
    private bool _autoCheckDone;
    private bool _showGpuTuningNotice;
    private DateTime _lastSubtitle = DateTime.Now;
    private DateTime _lastSideInfo = DateTime.Now;

    public HomeViewModel(EnvironmentStatus environment, ICommand restartAsAdminCommand, ICommand installDriverCommand,
                         ICommand installServiceCommand, GamesViewModel games, BenchmarkHistory benchmarks,
                         Action<object> navigate, object optimization, object cleanup, object tests, object apps)
    {
        _environment = environment;
        _games = games;
        _benchmarks = benchmarks;
        InstallServiceCommand = installServiceCommand;
        RestartAsAdminCommand = restartAsAdminCommand;
        InstallDriverCommand = installDriverCommand;
        RunCheckCommand = new RelayCommand(RunCheck);
        OpenGamesCommand = new RelayCommand(() =>
        {
            _games.IsRecordingsTab = true;
            navigate(_games);
        });
        OpenMonitoringCommand = new RelayCommand(() => navigate(MonitoringPage!));
        OpenAreaCommand = new RelayCommand(p =>
        {
            if (p is Finding { Area: { } area })
                OpenArea?.Invoke(area);
        });

        Actions = new[]
        {
            new HomeActionViewModel("Оптимизация", "Подобрать настройки Windows по трём вопросам", Icons.Optimize, DevicePalette.Cpu,
                new RelayCommand(() => navigate(optimization))),
            new HomeActionViewModel("Очистка", "Временные файлы, кэши браузеров и шейдеров", Icons.Cleanup, DevicePalette.Ssd,
                new RelayCommand(() => navigate(cleanup))),
            new HomeActionViewModel("Тест стабильности", "Процессор, видеокарта и память под нагрузкой", Icons.Tests, DevicePalette.Gpu,
                new RelayCommand(() => navigate(tests))),
            new HomeActionViewModel("Удалить программу", "Вместе с папками и записями реестра", Icons.Apps, DevicePalette.Ram,
                new RelayCommand(() => navigate(apps))),
        };

        UpdateSubtitle();
        RefreshSideInfo();
    }

    /// <summary>Страница мониторинга — для перехода по щелчку на плитку.</summary>
    public object? MonitoringPage { get; set; }

    public DeviceTiles Tiles { get; } = new();

    public IReadOnlyList<HomeActionViewModel> Actions { get; }

    public ICommand RunCheckCommand { get; }

    public ICommand RestartAsAdminCommand { get; }

    public ICommand InstallDriverCommand { get; }

    public ICommand InstallServiceCommand { get; }

    public ICommand OpenGamesCommand { get; }

    public ICommand OpenMonitoringCommand { get; }

    /// <summary>«Подробнее» у результата проверки: параметр — <see cref="Finding"/>.</summary>
    public ICommand OpenAreaCommand { get; }

    /// <summary>Переход к экрану по <see cref="Finding.Area"/> (задаёт главная модель).</summary>
    public Action<string>? OpenArea { get; set; }

    public ICommand RecordFpsCommand => _games.ToggleRecordingCommand;

    /// <summary>Раздел «Игры»: состояние записи FPS для подписи кнопки.</summary>
    public GamesViewModel Games => _games;

    /// <summary>Виден ли экран: пока скрыт, плитки не пересчитываются.</summary>
    public bool IsPageVisible { get; set; }

    public bool ShowAdminBanner => !_environment.HasFullSensors;

    /// <summary>В видеокарте остались настройки из прежнего экрана «Разгон».</summary>
    public bool ShowGpuTuningNotice
    {
        get => _showGpuTuningNotice;
        private set => SetProperty(ref _showGpuTuningNotice, value);
    }

    /// <summary>Открывает предпросмотр возврата заводских настроек видеокарты.</summary>
    public ICommand? RevertGpuTuningCommand { get; init; }

    public void SetGpuTuningNotice(bool show) => ShowGpuTuningNotice = show;

    public bool ShowPawnIoBanner => !_environment.PawnIoInstalled || _environment.PawnIoOutdated;

    public string PawnIoBannerTitle => _environment.PawnIoOutdated
        ? $"Драйвер PawnIO устарел ({_environment.PawnIoVersion})"
        : "Не установлен драйвер PawnIO";

    public string PawnIoBannerText => _environment.IsElevated
        ? "Через него читаются температуры процессора и датчики материнской платы. Установщик уже встроен в приложение, " +
          "после установки приложение перезапустится."
        : "Через него читаются температуры процессора и датчики материнской платы. Установщик уже встроен в приложение: " +
          "Windows один раз попросит разрешение администратора, затем приложение перезапустится с полным набором датчиков.";

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    /// <summary>Самое серьёзное из найденного: от него цвет карточки проверки.</summary>
    public FindingSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set => SetProperty(ref _statusSeverity, value);
    }

    /// <summary>Предупреждения и проблемы (без «всё хорошо»).</summary>
    public IReadOnlyList<Finding> Problems
    {
        get => _problems;
        private set
        {
            if (SetProperty(ref _problems, value))
                OnPropertyChanged(nameof(HasProblems));
        }
    }

    public bool HasProblems => _problems.Count > 0;

    /// <summary>Советы: XMP, один модуль памяти, старый драйвер видеокарты.</summary>
    public IReadOnlyList<Finding> Tips
    {
        get => _tips;
        private set
        {
            if (SetProperty(ref _tips, value))
                OnPropertyChanged(nameof(HasTips));
        }
    }

    public bool HasTips => _tips.Count > 0;

    /// <summary>
    /// Результаты фоновых проверок (диски, сбои, драйверы, память) для «Проверки системы».
    /// <paramref name="source"/> — откуда: новые результаты того же источника заменяют прежние.
    /// </summary>
    public void SetFindings(string source, IReadOnlyList<Finding> findings)
    {
        _extraFindings[source] = findings;
        if (_autoCheckDone)
            RunCheck();
    }

    public string PawnIoChip => _environment.PawnIoVersion is { } version ? $"Драйвер PawnIO {version}" : "PawnIO не установлен";

    public string AccessChip => _environment.IsElevated
        ? "Права администратора"
        : _environment.ServiceConnected ? "Служба PC Toolkit работает" : "Обычные права";

    public string GameTitle
    {
        get => _gameTitle;
        private set
        {
            if (SetProperty(ref _gameTitle, value))
                OnPropertyChanged(nameof(HasRunningGame));
        }
    }

    public string GameDetail
    {
        get => _gameDetail;
        private set => SetProperty(ref _gameDetail, value);
    }

    public bool HasRunningGame => _gameTitle.Length > 0;

    public IReadOnlyList<HomeRecordingRow> Recordings
    {
        get => _recordings;
        private set
        {
            if (SetProperty(ref _recordings, value))
                OnPropertyChanged(nameof(HasRecordings));
        }
    }

    public bool HasRecordings => _recordings.Count > 0;

    public IReadOnlyList<HomeTestRow> Tests
    {
        get => _tests;
        private set
        {
            if (SetProperty(ref _tests, value))
                OnPropertyChanged(nameof(HasTests));
        }
    }

    public bool HasTests => _tests.Count > 0;

    /// <summary>Обновляет баннеры после установки драйвера или смены прав.</summary>
    public void SetEnvironment(EnvironmentStatus environment)
    {
        _environment = environment;
        OnPropertyChanged(nameof(ShowAdminBanner));
        OnPropertyChanged(nameof(ShowPawnIoBanner));
        OnPropertyChanged(nameof(PawnIoBannerTitle));
        OnPropertyChanged(nameof(PawnIoBannerText));
        OnPropertyChanged(nameof(PawnIoChip));
        OnPropertyChanged(nameof(AccessChip));
    }

    public void Update(HardwareSnapshot snapshot, SensorHistory history)
    {
        _latest = snapshot;
        if (!_autoCheckDone && snapshot.Devices.Count > 0)
        {
            _autoCheckDone = true;
            RunCheck();
        }

        if (!IsPageVisible)
            return;

        Tiles.Update(snapshot, history, _environment.HasFullSensors);
        // Время работы — раз в 30 секунд, игры и замеры — раз в 3 секунды (снимки бывают до 4 раз в секунду).
        DateTime now = snapshot.Timestamp;
        if (now - _lastSubtitle >= TimeSpan.FromSeconds(30))
        {
            _lastSubtitle = now;
            UpdateSubtitle();
        }

        if (now - _lastSideInfo >= TimeSpan.FromSeconds(3))
        {
            _lastSideInfo = now;
            RefreshSideInfo();
        }
    }

    /// <summary>Игры, замеры и тесты: при показе экрана и раз в несколько секунд.</summary>
    public void RefreshSideInfo()
    {
        GameItemViewModel? running = _games.Games
            .Where(g => g.IsRunning)
            .OrderByDescending(g => g.HasProfile)
            .FirstOrDefault();
        if (running is null)
        {
            GameTitle = "";
            GameDetail = "";
        }
        else
        {
            GameTitle = running.Name;
            GameDetail = running.HasProfile
                ? running.ProfileSummary.Replace("Профиль: ", "Профиль включён: ")
                : "Профиль выключен — ничего не меняется";
        }

        Recordings = _games.Recordings
            .Take(5)
            .Select(r => new HomeRecordingRow(
                r.Title,
                r.Recording.At.LocalDateTime.ToString("d MMM, HH:mm", Russian),
                r.Recording.AverageFps.ToString(r.Recording.AverageFps >= 100 ? "0" : "0.#", Russian),
                r.Recording.Low1Fps.ToString(r.Recording.Low1Fps >= 100 ? "0" : "0.#", Russian),
                r.ProfileActive ? "с профилем" : "без профиля",
                r.ProfileActive))
            .ToList();

        var tests = new List<HomeTestRow>();
        if (_benchmarks.Gpu.OrderByDescending(r => r.At).FirstOrDefault() is { } gpu)
            tests.Add(new HomeTestRow("Видеокарта",
                (gpu.Score is int score ? $"{score:N0} баллов · " : "") + $"{gpu.Gflops:N0} GFLOPS",
                gpu.At.LocalDateTime.ToString("d MMM", Russian)));
        if (_benchmarks.Cpu.OrderByDescending(r => r.At).FirstOrDefault() is { } cpu)
            tests.Add(new HomeTestRow("Процессор",
                cpu.Score is { } s ? $"{s.MultiThread:N0} баллов (1 поток: {s.SingleThread:N0})" : "замер устарел — пройдите заново",
                cpu.At.LocalDateTime.ToString("d MMM", Russian)));
        if (_benchmarks.Memory.OrderByDescending(r => r.At).FirstOrDefault() is { } memory)
            tests.Add(new HomeTestRow("Память",
                $"чтение {memory.Read.ToString("0.0", Russian)} ГБ/с · задержка {memory.Latency:0} нс",
                memory.At.LocalDateTime.ToString("d MMM", Russian)));
        if (_benchmarks.Disk.OrderByDescending(r => r.At).FirstOrDefault() is { } disk && disk.Results.Count > 0)
            tests.Add(new HomeTestRow("Диск " + disk.Drive,
                $"чтение {disk.Results[0].ReadMBps:N0} МБ/с · запись {disk.Results[0].WriteMBps:N0} МБ/с",
                disk.At.LocalDateTime.ToString("d MMM", Russian)));
        Tests = tests;
    }

    private void RunCheck()
    {
        List<Finding> findings = SystemCheck.Run(_latest, _environment)
            .Concat(_extraFindings.Values.SelectMany(f => f))
            .ToList();
        Problems = findings.Where(f => !f.IsTip && f.Severity is FindingSeverity.Critical or FindingSeverity.Warning)
                           .OrderBy(f => f.Severity)
                           .ToList();
        Tips = findings.Where(f => f.IsTip).ToList();
        List<Finding> checks = findings.Where(f => !f.IsTip).ToList();
        FindingSeverity worst = checks.Count == 0 ? FindingSeverity.Good : checks.Min(f => f.Severity);
        StatusSeverity = worst;
        if (Problems.Count == 0)
        {
            var checkedParts = new List<string> { "температуры", "загрузка", "память" };
            if (_extraFindings.ContainsKey("space"))
                checkedParts.Add("место на дисках");
            if (_extraFindings.ContainsKey("disks"))
                checkedParts.Add("здоровье дисков");
            if (_extraFindings.ContainsKey("crashes"))
                checkedParts.Add("сбои за неделю");
            if (_extraFindings.ContainsKey("drivers"))
                checkedParts.Add("драйверы");
            StatusTitle = "Серьёзных проблем не найдено";
            StatusDetail = $"Проверка в {DateTime.Now:HH:mm}: {string.Join(", ", checkedParts)} — в норме.";
        }
        else
        {
            StatusTitle = Problems.Count == 1
                ? Problems[0].Title
                : $"Найдено {Problems.Count} {Formatting.Plural(Problems.Count, "замечание", "замечания", "замечаний")}";
            StatusDetail = $"Проверка в {DateTime.Now:HH:mm}. Подробности ниже — проверка только читает данные и ничего не меняет.";
        }
    }

    private void UpdateSubtitle() =>
        Subtitle = $"{Environment.MachineName} · {OsInfo.FriendlyName} · работает {Formatting.Uptime(OsInfo.Uptime)}";
}
