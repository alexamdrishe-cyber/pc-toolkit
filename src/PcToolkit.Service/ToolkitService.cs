using System.ServiceProcess;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Fans;
using PcToolkit.Core.Games;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Service;
using PcToolkit.Core.Tweaks;

namespace PcToolkit.Service;

/// <summary>Служба Windows: только запускает и останавливает <see cref="ServiceHost"/>.</summary>
public sealed class ToolkitService : ServiceBase
{
    private ServiceHost? _host;

    public ToolkitService()
    {
        ServiceName = ServiceProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        _host = new ServiceHost();
        _host.Start();
    }

    protected override void OnStop()
    {
        _host?.Dispose();
        _host = null;
    }

    protected override void OnShutdown() => OnStop();
}

/// <summary>
/// Всё, что делает служба: читает датчики с правами SYSTEM и отвечает приложению через канал.
/// Ничего не меняет по своей инициативе.
/// </summary>
public sealed class ServiceHost : IDisposable
{
    /// <summary>Пока приложение не спрашивает датчики, служба опрашивает их редко.</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(2);

    private readonly LhmSensorSource _sensors = new(IdleInterval);
    private long _lastSensorsRequest;
    private readonly SystemTargetFactory _factory = new();
    private readonly object _writeLock = new();
    // Счётчик кадров запускается по первому запросу приложения и останавливается через 30 секунд без запросов.
    private readonly FrameCaptureHost _frames = new("PcToolkit Frames (service)");
    private PrivilegedChangeValidator? _validator;
    private PipeServer? _server;
    private volatile HardwareSnapshot? _latest;

    // Вентиляторы: обороты действуют до этого момента (Environment.TickCount64), потом — обратно BIOS.
    private readonly object _fansLock = new();
    private long _fansLeaseUntil;
    private Timer? _fansWatchdog;

    public void Start()
    {
        AppLog.Info($"Служба PC Toolkit запускается (протокол {ServiceProtocol.Version}, сборка {ServiceProtocol.BuildId}).");

        _validator = new PrivilegedChangeValidator(TweakCatalog.LoadEmbedded(_factory), _factory);
        AppLog.Info($"Разрешённых системных параметров: {_validator.AllowedCount}.");

        _sensors.SnapshotReady += (_, snapshot) =>
        {
            _latest = snapshot;
            // Приложение закрыто (15 секунд без запросов) — обратно на редкий опрос.
            if (_sensors.Interval < IdleInterval && Environment.TickCount64 - Interlocked.Read(ref _lastSensorsRequest) > 15_000)
                _sensors.Interval = IdleInterval;
        };
        _sensors.Start();

        _server = new PipeServer(Handle);
        _server.Start();
        _fansWatchdog = new Timer(_ => CheckFansLease(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        _fansWatchdog?.Dispose();
        _server?.Dispose();
        _frames.Dispose();
        _sensors.Dispose();
        AppLog.Info("Служба PC Toolkit остановлена.");
    }

    private ServiceResponse Handle(ServiceRequest request)
    {
        switch (request.Op)
        {
            case ServiceOps.Hello:
                return new ServiceResponse { Ok = true, Version = ServiceProtocol.Version, BuildId = ServiceProtocol.BuildId };

            case ServiceOps.Sensors:
                // Частоту задаёт приложение (1–4 раза в секунду); приложение старой версии её не передаёт — раз в секунду.
                Interlocked.Exchange(ref _lastSensorsRequest, Environment.TickCount64);
                _sensors.Interval = TimeSpan.FromMilliseconds(Math.Clamp(request.IntervalMs ?? 1000, 200, 5000));
                return new ServiceResponse
                {
                    Ok = true,
                    Snapshot = _latest,
                    SensorState = _sensors.State,
                    SensorError = _sensors.LastError,
                };

            case ServiceOps.Write:
                return Write(request);

            case ServiceOps.Frames:
                return Frames(request);

            case ServiceOps.Disks:
                return Disks();

            case ServiceOps.Fans:
                return Fans(request);

            case ServiceOps.Startup:
                return Startup(request);

            default:
                return ServiceResponse.Fail($"Неизвестная команда «{request.Op}».");
        }
    }

    /// <summary>
    /// Обороты от приложения. Принимаются только для вентиляторов материнской платы, контроллеров и видеокарт NVIDIA,
    /// не ниже <see cref="FanSettings.AbsoluteMinDuty"/> %; при перегреве — 100 %, что бы ни просили.
    /// </summary>
    /// <summary>Только раздел StartupApproved в HKLM и только состояние записи — ничего другого эта команда не пишет.</summary>
    private static ServiceResponse Startup(ServiceRequest request)
    {
        if (request.StartupKey is not { } key || !PcToolkit.Core.Startup.StartupApproved.IsValidSubKey(key))
            return ServiceResponse.Fail("Неизвестный раздел автозагрузки.");
        if (string.IsNullOrWhiteSpace(request.StartupName))
            return ServiceResponse.Fail("Не указано имя записи автозагрузки.");

        try
        {
            PcToolkit.Core.Startup.StartupApproved.Write(Microsoft.Win32.RegistryHive.LocalMachine, key, request.StartupName, request.StartupEnabled);
            return new ServiceResponse { Ok = true, Version = ServiceProtocol.Version };
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return ServiceResponse.Fail(ex.Message);
        }
    }

    private ServiceResponse Fans(ServiceRequest request)
    {
        if (request.ReleaseFans)
        {
            lock (_fansLock)
                _fansLeaseUntil = 0;
            _sensors.ReleaseControls();
            return new ServiceResponse { Ok = true };
        }

        if (request.FanDuties is null)
            return ServiceResponse.Fail("Нет оборотов вентиляторов.");

        HardwareSnapshot? latest = _latest;
        var allowed = latest is null
            ? new HashSet<string>()
            : FanChannels.FromSnapshot(latest).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        KeyMetrics? metrics = latest is null ? null : KeyMetrics.From(latest);
        bool hot = metrics?.CpuTemperature?.Value >= FanController.OverheatOn || metrics?.GpuTemperature?.Value >= FanController.OverheatOn;

        var duties = new Dictionary<string, double?>();
        foreach ((string id, double? duty) in request.FanDuties)
        {
            if (!allowed.Contains(id))
                continue;
            duties[id] = duty is double value ? (hot ? 100 : Math.Clamp(value, FanSettings.AbsoluteMinDuty, 100)) : null;
        }

        _sensors.SetControls(duties);
        lock (_fansLock)
            _fansLeaseUntil = duties.Values.Any(d => d is not null) ? Environment.TickCount64 + Math.Clamp(request.LeaseMs ?? 5000, 2000, 30000) : 0;
        return new ServiceResponse { Ok = true };
    }

    /// <summary>Приложение перестало продлевать обороты (закрыто, зависло, упало) — вентиляторы обратно BIOS.</summary>
    private void CheckFansLease()
    {
        lock (_fansLock)
        {
            if (_fansLeaseUntil == 0 || Environment.TickCount64 < _fansLeaseUntil)
                return;
            _fansLeaseUntil = 0;
        }

        AppLog.Info("Приложение перестало управлять вентиляторами — возвращаю их BIOS.");
        _sensors.ReleaseControls();
    }

    private readonly object _disksLock = new();

    private ServiceResponse Disks()
    {
        // Только чтение: сведения о дисках и SMART. Два запроса подряд не опрашивают диски одновременно.
        lock (_disksLock)
        {
            try
            {
                return new ServiceResponse { Ok = true, Disks = DiskHealthReader.ReadAll() };
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось прочитать SMART дисков.", ex);
                return ServiceResponse.Fail(ex.Message);
            }
        }
    }

    private ServiceResponse Frames(ServiceRequest request)
    {
        try
        {
            FrameQueryResult result = _frames.Query(request.ProcessId, request.Since ?? 0);
            return new ServiceResponse
            {
                Ok = true,
                Presenters = result.Presenters.ToList(),
                Frames = result.Frames,
                Now = result.Now,
            };
        }
        catch (FrameCaptureException ex)
        {
            return ServiceResponse.Fail(ex.Message);
        }
    }

    private ServiceResponse Write(ServiceRequest request)
    {
        if (request.Change is null || request.Value is null || _validator is null)
            return ServiceResponse.Fail("Неполный запрос на изменение.");

        if (!_validator.IsAllowed(request.Change, request.Value, out ResolvedChange? resolved, out string? reason))
        {
            AppLog.Warn($"Отклонён запрос на изменение: {reason}.");
            return ServiceResponse.Fail("Служба отказалась: " + reason + ".");
        }

        lock (_writeLock)
        {
            try
            {
                resolved!.Target.Write(request.Value);
                ValueSnapshot after = resolved.Target.Read();
                if (!after.SameAs(request.Value))
                    return ServiceResponse.Fail($"После записи «{resolved.Target.DisplayName}» значение не совпало с ожидаемым.");

                AppLog.Info($"Изменено по запросу приложения: {resolved.Target.DisplayName} = {resolved.Target.Describe(request.Value)}.");
                return new ServiceResponse { Ok = true };
            }
            catch (Exception ex)
            {
                AppLog.Error($"Ошибка записи {resolved!.Target.DisplayName}.", ex);
                return ServiceResponse.Fail(ex.Message);
            }
        }
    }
}
