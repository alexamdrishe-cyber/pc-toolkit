using System.IO;
using System.Windows.Threading;
using PcToolkit.Core;
using PcToolkit.Core.Diagnostics;
using PcToolkit.Core.Inventory;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Settings;
using PcToolkit.Desktop.ViewModels;

namespace PcToolkit.Desktop.Services;

/// <summary>
/// Фоновые проверки здоровья компьютера, пока приложение открыто (в том числе в трее):
/// журнал сбоев — при запуске и раз в 30 минут, диски — при запуске и раз в 6 часов, драйверы и память — при запуске,
/// место на дисках — раз в 30 минут, перегрев — по данным датчиков. Итоги попадают в «Проверку системы» на главной,
/// важное — уведомлением в трее. Только чтение: ничего не меняет.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DiskInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan HeatDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeatCooldown = TimeSpan.FromMinutes(30);

    // Порог уведомления выше порога «Проверки системы»: многие процессоры (Ryzen 7000, Core 13–14-го поколения)
    // под полной нагрузкой штатно работают у 95 °C, и уведомлять об этом — только мешать.
    private const float CpuHeatLimit = 98;
    private const float GpuHeatLimit = 93;
    private const double LowSpacePercent = 5;

    private readonly HomeViewModel _home;
    private readonly DiskHealthViewModel _disks;
    private readonly DriversViewModel _drivers;
    private readonly OperationGate _gate;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _lowSpaceNotified = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _firstRun;
    private DateTime _lastDiskCheck = DateTime.MinValue;
    private DateTime? _cpuHotSince;
    private DateTime? _gpuHotSince;
    private DateTime _lastHeatNotice = DateTime.MinValue;
    private bool _running;

    public HealthMonitor(HomeViewModel home, DiskHealthViewModel disks, DriversViewModel drivers, OperationGate gate)
    {
        _home = home;
        _disks = disks;
        _drivers = drivers;
        _gate = gate;

        _disks.ReportsChanged += (_, _) => _home.SetFindings("disks", HealthFindings.FromDisks(_disks.Reports).ToList());
        _disks.News += news =>
        {
            foreach (Core.Disks.DiskHealthNews item in news)
                Notify(HealthFindings.DiskNotification(item));
        };
        _drivers.ReportChanged += (_, _) =>
        {
            if (_drivers.Report is { } report)
                _home.SetFindings("drivers", HealthFindings.FromDrivers(report).ToList());
        };

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += (_, _) => _ = RunAsync(startup: false);
    }

    /// <summary>Что-то стоит показать пользователю уведомлением.</summary>
    public event Action<HealthNotification>? Notification;

    /// <summary>Первая проверка — через <paramref name="delay"/> после запуска (чтобы не мешать запуску), дальше — по расписанию.</summary>
    public void Start(TimeSpan delay)
    {
        _firstRun = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        _firstRun.Tick += (_, _) =>
        {
            _firstRun.Stop();
            _ = RunAsync(startup: true);
            _timer.Start();
        };
        _firstRun.Start();
    }

    /// <summary>Перегрев по данным датчиков: вызывается раз в секунду.</summary>
    public void OnSnapshot(HardwareSnapshot snapshot)
    {
        // Во время теста нагрузка и нагрев ожидаемы, а у тестов свои пределы температуры.
        if (_gate.Current is not null)
        {
            _cpuHotSince = _gpuHotSince = null;
            return;
        }

        KeyMetrics metrics = KeyMetrics.From(snapshot);
        CheckHeat(metrics.CpuTemperature?.Value, CpuHeatLimit, ref _cpuHotSince, snapshot.Timestamp,
            "Процессор перегревается", "Проверьте кулер процессора, термопасту и пыль в радиаторе.");
        CheckHeat(metrics.GpuTemperature?.Value, GpuHeatLimit, ref _gpuHotSince, snapshot.Timestamp,
            "Видеокарта перегревается", "Проверьте вентиляторы видеокарты и продуваемость корпуса.");
    }

    public void Dispose()
    {
        _firstRun?.Stop();
        _timer.Stop();
    }

    private async Task RunAsync(bool startup)
    {
        if (_running)
            return;

        _running = true;
        try
        {
            DateTime now = DateTime.Now;
            CrashReport week = await Task.Run(() => CrashesViewModel.Read(7, now));
            _home.SetFindings("crashes", HealthFindings.FromCrashes(week).ToList());

            // О сбоях сообщаем один раз: сравниваем с моментом прошлой проверки (он хранится в настройках).
            // При самом первом запуске старые сбои не повод для уведомления — они и так видны на главной.
            DateTime? since = AppSettings.Load().LastCrashCheck;
            if (since is { } previous && HealthFindings.CrashNotification(week, previous) is { } notification)
                Notify(notification);
            AppSettings.Update(s => s.LastCrashCheck = now);

            CheckDiskSpace();

            if (startup || now - _lastDiskCheck >= DiskInterval)
            {
                _lastDiskCheck = now;
                await _disks.RefreshAsync();
            }

            if (startup)
            {
                await _drivers.RefreshAsync();
                MemoryConfig? memory = await Task.Run(SystemInfoProvider.ReadMemoryConfig);
                _home.SetFindings("memory", HealthFindings.FromMemory(memory).ToList());
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Фоновая проверка здоровья компьютера не удалась.", ex);
        }
        finally
        {
            _running = false;
        }
    }

    private void CheckHeat(float? temperature, float limit, ref DateTime? hotSince, DateTime now, string title, string advice)
    {
        if (temperature is not float t || t < limit)
        {
            hotSince = null;
            return;
        }

        hotSince ??= now;
        if (now - hotSince < HeatDuration || now - _lastHeatNotice < HeatCooldown)
            return;

        _lastHeatNotice = now;
        Notify(new HealthNotification($"{title}: {t:0} °C", $"Температура держится выше {limit:0} °C. {advice}", "monitoring"));
    }

    private void CheckDiskSpace()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return;
        }

        foreach (DriveInfo drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0)
                    continue;

                double free = 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
                string name = drive.Name.TrimEnd('\\');
                if (free >= LowSpacePercent)
                {
                    _lowSpaceNotified.Remove(name);
                    continue;
                }

                if (!_lowSpaceNotified.Add(name))
                    continue;

                Notify(new HealthNotification($"Почти нет места на диске {name}",
                    $"Свободно {Formatting.Bytes((ulong)drive.AvailableFreeSpace)} ({free:0} %). Windows и игры работают хуже, когда диск заполнен. " +
                    "Что занимает место — «Диски» → «Место», освободить — «Очистка».", "space"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Диск отключили во время проверки.
            }
        }
    }

    private void Notify(HealthNotification notification)
    {
        AppLog.Info($"Уведомление: {notification.Title}. {notification.Text}");
        if (AppSettings.Load().ProblemNotifications)
            Notification?.Invoke(notification);
    }
}
