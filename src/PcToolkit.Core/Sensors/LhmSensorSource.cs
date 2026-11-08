using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Sensors;

/// <summary>
/// Читает датчики через LibreHardwareMonitor в отдельном фоновом потоке.
/// Вся работа с библиотекой идёт только в этом потоке: объект Computer не потокобезопасен.
/// Медленные и редко меняющиеся устройства (накопители со SMART, блоки питания, USB-контроллеры, батарея)
/// опрашиваются не чаще раза в секунду, даже если остальные — 4 раза в секунду.
/// </summary>
public sealed class LhmSensorSource : ISensorSource
{
    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _wake = new(false);
    private long _intervalTicks;
    private DateTime _lastSlowUpdate = DateTime.MinValue;
    private bool _slowTick;
    private int _timedCycles;
    private long _timedTicks;
    private readonly Dictionary<string, long> _hardwareCost = new();
    private readonly HashSet<string> _reportedProblems = new();
    private readonly Dictionary<SensorType, SensorKind> _sensorKinds = new();
    private readonly Dictionary<HardwareType, DeviceKind> _deviceKinds = new();
    private Thread? _thread;
    private volatile SensorSourceState _state = SensorSourceState.Stopped;
    private volatile string? _lastError;
    private bool _disposed;

    // Управление вентиляторами: команды копятся здесь и выполняются в потоке датчиков (Computer не потокобезопасен).
    private readonly object _controlLock = new();
    private Dictionary<string, float?>? _pendingControls;
    private bool _releaseControls;
    private readonly HashSet<string> _controlled = new();

    public LhmSensorSource(TimeSpan? interval = null)
    {
        Interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public TimeSpan Interval
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));
        set
        {
            long ticks = Math.Max(value.Ticks, MinInterval.Ticks);
            if (Interlocked.Exchange(ref _intervalTicks, ticks) == ticks)
                return;

            try
            {
                _wake.Set(); // не ждать до конца прежней, более долгой паузы
            }
            catch (ObjectDisposedException)
            {
                // Источник уже остановлен.
            }
        }
    }

    public event EventHandler<HardwareSnapshot>? SnapshotReady;

    /// <summary>
    /// Обороты вентиляторов: Id управления → % (null — отдать вентилятор BIOS). Выполняется в потоке датчиков
    /// перед ближайшим опросом; при остановке источника все вентиляторы возвращаются BIOS.
    /// </summary>
    public void SetControls(IReadOnlyDictionary<string, double?> duties)
    {
        lock (_controlLock)
        {
            _pendingControls ??= new Dictionary<string, float?>();
            foreach ((string id, double? duty) in duties)
                _pendingControls[id] = duty is double d ? (float)Math.Clamp(d, 0, 100) : null;
        }

        Wake();
    }

    /// <summary>Вернуть все вентиляторы, которыми управляла программа, BIOS.</summary>
    public void ReleaseControls()
    {
        lock (_controlLock)
        {
            _pendingControls = null;
            _releaseControls = true;
        }

        Wake();
    }

    private void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Источник уже остановлен.
        }
    }

    public event EventHandler? StateChanged;

    public SensorSourceState State => _state;

    public string? LastError => _lastError;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
            return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PcToolkit.Sensors",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();

        bool stopped = _thread is null || !_thread.IsAlive || _thread.Join(TimeSpan.FromSeconds(5));
        if (stopped)
        {
            _cts.Dispose();
            _wake.Dispose();
        }
        else
            AppLog.Warn("Поток датчиков не завершился за 5 секунд.");
    }

    private void Run()
    {
        CancellationToken token = _cts.Token;
        SetState(SensorSourceState.Starting);

        var computer = new Computer();
        var openTimer = Stopwatch.StartNew();
        Queue<(string Name, Action Enable)> pending = Groups(computer);

        try
        {
            AppLog.Info("LibreHardwareMonitor: запуск…");
            computer.Open();
            // Первые данные — сразу после процессора; остальные группы включаются по одной между опросами,
            // чтобы медленная (память через SMBus у службы — до 9 секунд) не задерживала всё остальное.
            EnableNext(pending);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            AppLog.Error("Не удалось запустить LibreHardwareMonitor.", ex);
            TryClose(computer);
            SetState(SensorSourceState.Failed);
            return;
        }

        SetState(SensorSourceState.Running);

        try
        {
            var cycleTimer = new Stopwatch();
            while (!token.IsCancellationRequested)
            {
                cycleTimer.Restart();

                ApplyControls(computer, releaseAll: false);

                HardwareSnapshot? snapshot = null;
                try
                {
                    snapshot = Collect(computer);
                }
                catch (Exception ex)
                {
                    ReportOnce("collect", "Ошибка при чтении датчиков.", ex);
                }

                if (snapshot is not null)
                {
                    try
                    {
                        SnapshotReady?.Invoke(this, snapshot);
                    }
                    catch (Exception ex)
                    {
                        ReportOnce("handler", "Ошибка в обработчике данных датчиков.", ex);
                    }
                }

                if (pending.Count > 0)
                {
                    EnableNext(pending);
                    if (pending.Count == 0)
                        AppLog.Info($"LibreHardwareMonitor запущен за {openTimer.ElapsedMilliseconds} мс, устройств: {computer.Hardware.Count}.");
                    continue; // сразу снять показания с новой группы
                }

                ReportCycleCost(cycleTimer.Elapsed);
                TimeSpan delay = Interval - cycleTimer.Elapsed;
                if (delay < TimeSpan.FromMilliseconds(50))
                    delay = TimeSpan.FromMilliseconds(50);

                WaitHandle.WaitAny(new[] { token.WaitHandle, _wake }, delay);
            }
        }
        finally
        {
            ApplyControls(computer, releaseAll: true);
            TryClose(computer);
            SetState(SensorSourceState.Stopped);
        }
    }

    /// <summary>Выполняет накопившиеся команды вентиляторам (только в потоке датчиков).</summary>
    private void ApplyControls(Computer computer, bool releaseAll)
    {
        Dictionary<string, float?>? pending;
        lock (_controlLock)
        {
            pending = _pendingControls;
            _pendingControls = null;
            releaseAll |= _releaseControls;
            _releaseControls = false;
        }

        if (pending is null && !(releaseAll && _controlled.Count > 0))
            return;

        foreach (ISensor sensor in ControlSensors(computer))
        {
            string id = sensor.Identifier.ToString();
            try
            {
                if (releaseAll)
                {
                    if (_controlled.Remove(id))
                    {
                        sensor.Control.SetDefault();
                        AppLog.Info($"Вентилятор «{sensor.Name}» ({sensor.Hardware.Name}) возвращён BIOS.");
                    }

                    continue;
                }

                if (pending is null || !pending.TryGetValue(id, out float? duty))
                    continue;

                if (duty is float value)
                {
                    sensor.Control.SetSoftware(value);
                    if (_controlled.Add(id))
                        AppLog.Info($"Вентилятор «{sensor.Name}» ({sensor.Hardware.Name}): обороты задаёт PC Toolkit.");
                }
                else if (_controlled.Remove(id))
                {
                    sensor.Control.SetDefault();
                    AppLog.Info($"Вентилятор «{sensor.Name}» ({sensor.Hardware.Name}) возвращён BIOS.");
                }
            }
            catch (Exception ex)
            {
                ReportOnce("control:" + id, $"Не удалось управлять вентилятором «{sensor.Name}».", ex);
            }
        }
    }

    private static IEnumerable<ISensor> ControlSensors(Computer computer)
    {
        foreach (IHardware hardware in computer.Hardware)
        {
            foreach (ISensor sensor in SensorsOf(hardware))
            {
                if (sensor.SensorType == SensorType.Control && sensor.Control is not null)
                    yield return sensor;
            }
        }
    }

    private static IEnumerable<ISensor> SensorsOf(IHardware hardware)
    {
        foreach (ISensor sensor in hardware.Sensors)
            yield return sensor;
        foreach (IHardware sub in hardware.SubHardware)
        {
            foreach (ISensor sensor in SensorsOf(sub))
                yield return sensor;
        }
    }

    /// <summary>
    /// Группы датчиков включаются по одной, и перед каждой в журнал пишется строка: если на каком-то компьютере
    /// библиотека уронит процесс (ошибка в машинном коде не ловится), последняя строка журнала покажет, на чём.
    /// Обычная ошибка в одной группе не лишает остальных датчиков. Процессор — первым: от него зависит видеоядро Intel.
    /// Память — последней: модули памяти читаются через SMBus, это самое долгое.
    /// </summary>
    private static Queue<(string Name, Action Enable)> Groups(Computer computer) => new(new (string, Action)[]
    {
        ("процессор", () => computer.IsCpuEnabled = true),
        ("видеокарты", () => computer.IsGpuEnabled = true),
        ("материнская плата", () => computer.IsMotherboardEnabled = true),
        ("накопители", () => computer.IsStorageEnabled = true),
        ("сеть", () => computer.IsNetworkEnabled = true),
        ("контроллеры вентиляторов и подсветки", () => computer.IsControllerEnabled = true),
        ("блоки питания", () => computer.IsPsuEnabled = true),
        ("батарея", () => computer.IsBatteryEnabled = true),
        ("память", () => computer.IsMemoryEnabled = true),
    });

    private static void EnableNext(Queue<(string Name, Action Enable)> pending)
    {
        (string name, Action enable) = pending.Dequeue();
        AppLog.Info($"LibreHardwareMonitor: {name}…");
        try
        {
            enable();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"LibreHardwareMonitor: «{name}» не запустились, остальные датчики работают.", ex);
        }
    }

    /// <summary>Сколько в среднем занимает один опрос — один раз в журнал, чтобы видеть цену частого опроса.</summary>
    private void ReportCycleCost(TimeSpan elapsed)
    {
        if (_timedCycles < 0)
            return;

        // Первые циклы пропускаем: там прогрев библиотеки и JIT.
        if (++_timedCycles <= 5)
            return;

        _timedTicks += elapsed.Ticks;
        if (_timedCycles < 45)
            return;

        double average = TimeSpan.FromTicks(_timedTicks / 40).TotalMilliseconds;
        string slowest = string.Join(", ", _hardwareCost
            .OrderByDescending(pair => pair.Value)
            .Take(4)
            .Select(pair => $"{pair.Key} {Stopwatch.GetElapsedTime(0, pair.Value / 40).TotalMilliseconds:0.#}"));
        AppLog.Info($"LibreHardwareMonitor: опрос датчиков занимает в среднем {average:0} мс (интервал {Interval.TotalMilliseconds:0} мс); " +
                    $"дольше всего, мс: {slowest}.");
        _timedCycles = -1;
        _hardwareCost.Clear();
    }

    private HardwareSnapshot Collect(Computer computer)
    {
        DateTime now = DateTime.Now;
        _slowTick = now - _lastSlowUpdate >= SlowInterval - TimeSpan.FromMilliseconds(50);
        if (_slowTick)
            _lastSlowUpdate = now;

        var devices = new List<DeviceSnapshot>();
        foreach (IHardware hardware in computer.Hardware)
            AddHardware(hardware, devices);

        return new HardwareSnapshot(DateTime.Now, devices);
    }

    private void AddHardware(IHardware hardware, List<DeviceSnapshot> devices)
    {
        try
        {
            // Между редкими опросами медленное устройство отдаёт прежние значения.
            if (_slowTick || !IsSlow(hardware.HardwareType))
            {
                long started = Stopwatch.GetTimestamp();
                hardware.Update();
                if (_timedCycles is > 5 and < 45)
                {
                    string key = hardware.Name.Trim();
                    _hardwareCost[key] = _hardwareCost.GetValueOrDefault(key) + (Stopwatch.GetTimestamp() - started);
                }
            }
        }
        catch (Exception ex)
        {
            ReportOnce("update:" + hardware.Identifier, $"Не удалось обновить устройство «{hardware.Name}».", ex);
        }

        var sensors = new List<SensorReading>(hardware.Sensors.Length);
        foreach (ISensor sensor in hardware.Sensors)
        {
            sensors.Add(new SensorReading(
                sensor.Identifier.ToString(),
                sensor.Name,
                MapSensorKind(sensor.SensorType),
                Clean(sensor.Value),
                Clean(sensor.Min),
                Clean(sensor.Max)));
        }

        if (sensors.Count > 0)
        {
            devices.Add(new DeviceSnapshot(
                hardware.Identifier.ToString(),
                hardware.Name,
                MapDeviceKind(hardware.HardwareType),
                sensors));
        }

        // Вложенные устройства (например, микросхема SuperIO на материнской плате) обновляются отдельно.
        foreach (IHardware subHardware in hardware.SubHardware)
            AddHardware(subHardware, devices);
    }

    private static bool IsSlow(HardwareType type) => type.ToString() is "Storage" or "Psu" or "Cooler" or "Battery";

    private static float? Clean(float? value) =>
        value is float number && (float.IsNaN(number) || float.IsInfinity(number)) ? null : value;

    // Сопоставление по имени, а не по числовому значению: так код не ломается,
    // если в новой версии библиотеки добавят или переставят элементы enum.
    private SensorKind MapSensorKind(SensorType type)
    {
        if (!_sensorKinds.TryGetValue(type, out SensorKind kind))
        {
            kind = Enum.TryParse(type.ToString(), out SensorKind parsed) ? parsed : SensorKind.Other;
            _sensorKinds[type] = kind;
        }

        return kind;
    }

    private DeviceKind MapDeviceKind(HardwareType type)
    {
        if (!_deviceKinds.TryGetValue(type, out DeviceKind kind))
        {
            kind = type.ToString() switch
            {
                "Cpu" => DeviceKind.Cpu,
                "GpuNvidia" => DeviceKind.GpuNvidia,
                "GpuAmd" => DeviceKind.GpuAmd,
                "GpuIntel" => DeviceKind.GpuIntel,
                "Memory" => DeviceKind.Memory,
                "Motherboard" or "SuperIO" or "EmbeddedController" => DeviceKind.Motherboard,
                "Storage" => DeviceKind.Storage,
                "Network" => DeviceKind.Network,
                "Cooler" => DeviceKind.Cooler,
                "Psu" => DeviceKind.Psu,
                "Battery" => DeviceKind.Battery,
                _ => DeviceKind.Other,
            };
            _deviceKinds[type] = kind;
        }

        return kind;
    }

    /// <summary>Пишет ошибку в журнал один раз, чтобы не засорять его каждую секунду.</summary>
    private void ReportOnce(string key, string message, Exception exception)
    {
        if (_reportedProblems.Add(key))
            AppLog.Warn(message, exception);
    }

    private void SetState(SensorSourceState state)
    {
        _state = state;
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка в обработчике смены состояния датчиков.", ex);
        }
    }

    private static void TryClose(Computer computer)
    {
        try
        {
            computer.Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ошибка при закрытии LibreHardwareMonitor.", ex);
        }
    }
}
