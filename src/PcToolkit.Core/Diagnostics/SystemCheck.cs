using PcToolkit.Core.Platform;
using PcToolkit.Core.Sensors;

namespace PcToolkit.Core.Diagnostics;

/// <summary>Порядок важен: по нему результаты сортируются, самые серьёзные сверху.</summary>
public enum FindingSeverity
{
    Critical,
    Warning,
    Info,
    Good,
}

public sealed record Finding(FindingSeverity Severity, string Title, string Detail)
{
    /// <summary>Экран с подробностями: <see cref="FindingAreas"/>. null — перейти некуда.</summary>
    public string? Area { get; init; }

    /// <summary>Совет, а не проблема (XMP, старый драйвер): показывается отдельно и не влияет на итог проверки.</summary>
    public bool IsTip { get; init; }
}

/// <summary>Куда ведёт «Подробнее» у результата проверки.</summary>
public static class FindingAreas
{
    public const string Disks = "disks";
    public const string Crashes = "crashes";
    public const string Drivers = "drivers";
    public const string Memory = "memory";
    public const string Space = "space";
}

/// <summary>
/// Быстрая проверка состояния компьютера по текущему снимку датчиков.
/// Только чтение: ничего в системе не меняет.
/// </summary>
public static class SystemCheck
{
    public static IReadOnlyList<Finding> Run(HardwareSnapshot snapshot, EnvironmentStatus environment)
    {
        var findings = new List<Finding>();
        KeyMetrics metrics = KeyMetrics.From(snapshot);

        CheckEnvironment(findings, environment);

        if (snapshot.Devices.Count == 0)
        {
            findings.Add(new Finding(FindingSeverity.Warning,
                "Датчики не найдены",
                "Данные ещё не получены или библиотека датчиков не смогла открыть устройства. Подробности в журнале приложения."));
        }

        if (metrics.CpuTemperature is { } cpuTemperature)
        {
            AddTemperature(findings, "процессора", cpuTemperature.Value, Thresholds.CpuWarning, Thresholds.CpuCritical,
                "Проверьте кулер процессора, термопасту и пыль в радиаторе.");
        }
        else if (metrics.CpuName is not null)
        {
            findings.Add(new Finding(FindingSeverity.Info,
                "Температура процессора недоступна",
                environment.HasFullSensors
                    ? "Обычно это значит, что драйвер PawnIO не установлен или не поддерживает этот процессор."
                    : "Обычно она появляется после запуска приложения от имени администратора."));
        }

        if (metrics.GpuTemperature is { } gpuTemperature)
        {
            AddTemperature(findings, "видеокарты", gpuTemperature.Value, Thresholds.GpuWarning, Thresholds.GpuCritical,
                "Проверьте вентиляторы видеокарты и продуваемость корпуса.");
        }

        if (metrics.GpuHotSpot is { } hotSpot && hotSpot.Value >= Thresholds.GpuHotSpotWarning)
        {
            findings.Add(new Finding(FindingSeverity.Warning,
                $"Горячая точка видеокарты: {hotSpot.Value:0} °C",
                "Большая разница между температурой ядра и горячей точкой часто говорит о высохшей термопасте."));
        }

        foreach (DeviceSnapshot device in snapshot.Devices.Where(d => d.Kind == DeviceKind.Storage))
        {
            MetricValue? temperature = KeyMetrics.StorageTemperature(device);
            if (temperature is null)
                continue;

            HealthState state = Thresholds.Classify(temperature.Value, Thresholds.StorageWarning, Thresholds.StorageCritical);
            if (state is HealthState.Warning or HealthState.Critical)
            {
                findings.Add(new Finding(
                    state == HealthState.Critical ? FindingSeverity.Critical : FindingSeverity.Warning,
                    $"Накопитель греется: {temperature.Value:0} °C",
                    $"{device.Name}. При перегреве SSD снижает скорость. Помогает обдув или радиатор для NVMe."));
            }
        }

        if (metrics.MemoryLoad is { } memoryLoad && memoryLoad.Value >= Thresholds.MemoryLoadWarning)
        {
            findings.Add(new Finding(FindingSeverity.Warning,
                $"Оперативная память занята на {memoryLoad.Value:0} %",
                "Закройте ненужные программы. Если так постоянно, памяти может не хватать для ваших задач."));
        }

        // Место на дисках проверяет фоновый монитор (HealthFindings.FromDriveSpace): разделы могут отвечать секундами.
        if (!findings.Any(f => f.Severity is FindingSeverity.Critical or FindingSeverity.Warning))
        {
            findings.Add(new Finding(FindingSeverity.Good,
                "Серьёзных проблем не найдено",
                "Температуры и загрузка памяти в норме на момент проверки."));
        }

        return findings.OrderBy(f => f.Severity).ToList();
    }

    private static void CheckEnvironment(List<Finding> findings, EnvironmentStatus environment)
    {
        if (!environment.HasFullSensors)
        {
            findings.Add(new Finding(FindingSeverity.Info,
                "Приложение запущено без прав администратора",
                "Часть датчиков недоступна: температуры процессора, датчики материнской платы и SMART дисков. " +
                "Установите службу PC Toolkit (Настройки), и запускать от имени администратора больше не понадобится."));
        }

        if (!environment.PawnIoInstalled)
        {
            findings.Add(new Finding(FindingSeverity.Warning,
                "Не установлен драйвер PawnIO",
                "Через него читаются температуры процессора и датчики материнской платы. Установите его кнопкой «Установить драйвер» на главном экране."));
        }
        else if (environment.PawnIoOutdated)
        {
            findings.Add(new Finding(FindingSeverity.Warning,
                $"Устаревший драйвер PawnIO ({environment.PawnIoVersion})",
                "Нужна версия 2.0 или новее. Обновите его кнопкой «Установить драйвер» на главном экране."));
        }
    }

    private static void AddTemperature(List<Finding> findings, string what, float value, float warning, float critical, string advice)
    {
        HealthState state = Thresholds.Classify(value, warning, critical);
        if (state == HealthState.Critical)
            findings.Add(new Finding(FindingSeverity.Critical, $"Очень высокая температура {what}: {value:0} °C", advice));
        else if (state == HealthState.Warning)
            findings.Add(new Finding(FindingSeverity.Warning, $"Высокая температура {what}: {value:0} °C", advice));
    }
}
