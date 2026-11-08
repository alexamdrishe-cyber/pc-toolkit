using PcToolkit.Core.Fans;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Overclock;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Service;

namespace PcToolkit.Desktop.Services;

/// <summary>
/// Управление вентиляторами, пока программа запущена (в том числе свёрнутой в трей). Раз в секунду считает обороты
/// по кривым и отправляет их: через службу — «в аренду» на несколько секунд (программа закрылась или зависла —
/// служба сама вернёт вентиляторы BIOS), без службы с правами администратора — в свою библиотеку датчиков.
/// Видеокарте AMD своя кривая (если она выбрана) записывается в драйвер; в «Тихо» видеокарта остаётся на его
/// автоматике. Выход из программы или выключение управления — всё возвращается BIOS и драйверу видеокарты.
/// </summary>
public sealed class FanService : IDisposable
{
    private const int LeaseMs = 6000;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    private readonly FanController _controller = new();
    private readonly ServiceClient? _service;
    private readonly LhmSensorSource? _local;
    private readonly bool _canControlBoard;
    private readonly AmdGpuFans _amd = new();
    private readonly object _tickLock = new();
    private readonly Dictionary<string, (double Duty, DateTime Until)> _overrides = new();
    private readonly Timer _timer;
    private volatile HardwareSnapshot? _latest;
    private bool _controlling;
    private string? _gpuApplied;
    private bool _amdProbed;
    private bool _disposed;
    private int _conflictCountdown;
    private string _conflictsLogged = "";

    /// <param name="service">Служба, если программа работает через неё (без прав администратора).</param>
    /// <param name="local">Своя библиотека датчиков, если программа запущена с правами администратора без службы.</param>
    public FanService(FanSettings settings, ServiceClient? service, LhmSensorSource? local, bool elevated)
    {
        Settings = settings;
        _service = service;
        _local = elevated ? local : null;
        _canControlBoard = service is not null || _local is not null;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Настройки, которые меняет окно (только под <see cref="SettingsLock"/>).</summary>
    public FanSettings Settings { get; }

    /// <summary>Замок для изменений <see cref="Settings"/>: поток управления берёт под ним копию раз в секунду.</summary>
    public object SettingsLock { get; } = new();

    /// <summary>Вентиляторы материнской платы, контроллеров и видеокарт NVIDIA из последнего снимка.</summary>
    public IReadOnlyList<FanChannel> Channels { get; private set; } = Array.Empty<FanChannel>();

    /// <summary>Последние заданные обороты: Id → % (null — вентилятором управляет BIOS).</summary>
    public IReadOnlyDictionary<string, double?> Duties { get; private set; } = new Dictionary<string, double?>();

    public double? CpuTemperature { get; private set; }

    public double? GpuTemperature { get; private set; }

    /// <summary>Горячая точка видеокарты: по ней драйвер AMD крутит вентиляторы по кривой.</summary>
    public double? GpuHotSpot { get; private set; }

    /// <summary>Обороты вентиляторов видеокарты AMD (тахометр из библиотеки датчиков).</summary>
    public double? GpuFanRpm { get; private set; }

    public bool Overheat => _controller.Overheat;

    /// <summary>Почему нельзя управлять вентиляторами материнской платы (null — можно).</summary>
    public string? BoardProblem { get; private set; }

    /// <summary>Видеокарта AMD с кривой вентиляторов (null — нет или драйвер не даёт).</summary>
    public GpuTuningInfo? AmdGpu { get; private set; }

    public string? AmdProblem { get; private set; }

    /// <summary>Запущенные программы, которые тоже умеют управлять вентиляторами (проверка раз в 10 секунд).</summary>
    public IReadOnlyList<string> Conflicts { get; private set; } = Array.Empty<string>();

    /// <summary>Как драйвер видеокарты принял кривую (точки после округления к его шагам).</summary>
    public IReadOnlyList<FanPoint> AmdCurve { get; private set; } = Array.Empty<FanPoint>();

    /// <summary>После каждого шага (в фоновом потоке).</summary>
    public event Action? Updated;

    public void Start()
    {
        // Кривая видеокарты осталась от упавшего запуска, а управление выключено — возвращаем драйверу.
        if (!Settings.Enabled && _amd.HasSavedState)
            _ = Task.Run(_amd.Restore);
        _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void OnSnapshot(HardwareSnapshot snapshot) => _latest = snapshot;

    /// <summary>Настройки поменялись (режим, кривая, роли): сразу к новой цели, без плавного перехода.</summary>
    public void SettingsChanged()
    {
        lock (SettingsLock)
            Settings.Save();
        lock (_tickLock)
        {
            _controller.Reset();
            _gpuApplied = null;
        }

        _ = Task.Run(Tick);
    }

    /// <summary>Временно задать вентилятору обороты (определить, какой это, калибровка). Защита от перегрева действует.</summary>
    public void Override(string id, double duty, TimeSpan duration)
    {
        lock (_tickLock)
            _overrides[id] = (Math.Clamp(duty, 0, 100), DateTime.Now + duration);
        _ = Task.Run(Tick);
    }

    public void ClearOverride(string id)
    {
        lock (_tickLock)
            _overrides.Remove(id);
        _ = Task.Run(Tick);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Dispose();
        lock (_tickLock)
        {
            ReleaseBoard();
            if (_amd.HasSavedState)
                _amd.Restore();
        }
    }

    private void Tick()
    {
        if (_disposed)
            return;

        CheckConflicts();
        lock (_tickLock)
        {
            if (_disposed)
                return;

            HardwareSnapshot? snapshot = _latest;
            if (snapshot is null)
                return;

            KeyMetrics metrics = KeyMetrics.From(snapshot);
            CpuTemperature = metrics.CpuTemperature?.Value;
            GpuTemperature = metrics.GpuTemperature?.Value;
            GpuHotSpot = metrics.GpuHotSpot?.Value;
            GpuFanRpm = snapshot.Devices.FirstOrDefault(d => d.Kind == DeviceKind.GpuAmd)?.Sensors
                .FirstOrDefault(s => s.Kind == SensorKind.Fan)?.Value;
            Channels = _canControlBoard ? FanChannels.FromSnapshot(snapshot) : Array.Empty<FanChannel>();
            if (!_canControlBoard)
                BoardProblem = "для вентиляторов материнской платы нужна служба PC Toolkit (Настройки) или запуск от имени администратора";
            else if (Channels.Count == 0)
                BoardProblem = "библиотека датчиков не нашла на материнской плате вентиляторов, которыми можно управлять";
            else if (BoardProblem is not null && !BoardProblem.StartsWith("Служба", StringComparison.Ordinal))
                BoardProblem = null;

            FanSettings settings;
            lock (SettingsLock)
                settings = Settings.Clone();

            try
            {
                TickBoard(snapshot, settings);
                TickAmd(settings);
            }
            catch (Exception ex)
            {
                AppLog.Error("Ошибка управления вентиляторами.", ex);
            }
        }

        try
        {
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка в обработчике вентиляторов.", ex);
        }
    }

    /// <summary>Раз в 10 секунд — не запущена ли другая программа управления вентиляторами (MSI Afterburner, FanControl…).</summary>
    private void CheckConflicts()
    {
        if (Interlocked.Decrement(ref _conflictCountdown) > 0)
            return;

        _conflictCountdown = 10;
        IReadOnlyList<string> conflicts = FanConflicts.Running();
        Conflicts = conflicts;
        string text = string.Join(", ", conflicts);
        if (text != _conflictsLogged)
        {
            _conflictsLogged = text;
            if (text.Length > 0)
                AppLog.Info("Вентиляторами может управлять и другая программа: " + text + ".");
        }
    }

    private void TickBoard(HardwareSnapshot snapshot, FanSettings settings)
    {
        // Данные датчиков устарели (служба или источник зависли): не продлеваем обороты — служба вернёт их BIOS.
        if (DateTime.Now - snapshot.Timestamp > StaleAfter)
            return;

        var duties = new Dictionary<string, double?>(_controller.Tick(settings, Channels, CpuTemperature, GpuTemperature, GpuHotSpot));
        DateTime now = DateTime.Now;
        foreach (string id in _overrides.Where(o => o.Value.Until <= now).Select(o => o.Key).ToList())
            _overrides.Remove(id);
        foreach ((string id, (double duty, _)) in _overrides)
        {
            if (Channels.Any(c => c.Id == id))
                duties[id] = _controller.Overheat ? 100 : duty;
        }

        bool active = duties.Values.Any(d => d is not null);
        Duties = duties;
        if (!active && !_controlling)
            return;

        Send(duties);
        _controlling = active;
    }

    private void Send(IReadOnlyDictionary<string, double?> duties)
    {
        if (_service is not null)
        {
            ServiceResponse? response = _service.SetFans(duties, LeaseMs);
            BoardProblem = response switch
            {
                null => "Служба PC Toolkit не отвечает",
                { Ok: false, Error: { } error } when error.Contains("Неизвестная команда", StringComparison.Ordinal) =>
                    "Служба PC Toolkit устарела — обновите её в «Настройках»",
                { Ok: false } => "Служба отказалась: " + response.Error,
                _ => null,
            };
        }
        else
        {
            _local?.SetControls(duties);
        }
    }

    private void ReleaseBoard()
    {
        if (!_controlling)
            return;

        _controlling = false;
        if (_service is not null)
            _service.ReleaseFans();
        else
            _local?.ReleaseControls();
        AppLog.Info("Вентиляторы возвращены BIOS.");
    }

    private void TickAmd(FanSettings settings)
    {
        if (!_amdProbed)
        {
            _amdProbed = true;
            AmdGpu = AmdGpuFans.Probe(out string? reason);
            AmdProblem = AmdGpu is null ? reason : null;
        }

        if (AmdGpu is null)
            return;

        // Управление выключено или видеокарта на автоматике драйвера — вернуть то, что было до программы.
        if (!settings.Enabled || settings.Active.GpuByDriver)
        {
            if (_amd.HasSavedState)
            {
                _amd.Restore();
                AmdCurve = Array.Empty<FanPoint>();
            }

            _gpuApplied = null;
            return;
        }

        FanTemplate template = settings.Active;
        string wanted = template.Gpu + "|" + template.GpuZeroRpm;
        if (wanted == _gpuApplied)
            return;

        try
        {
            AmdCurve = _amd.Apply(template.Gpu, template.GpuZeroRpm);
            AmdProblem = null;
        }
        catch (AdlxException ex)
        {
            AmdProblem = "драйвер не принял кривую: " + ex.Message;
            AppLog.Error("Не удалось записать кривую вентиляторов видеокарты.", ex);
        }

        _gpuApplied = wanted;
    }
}
