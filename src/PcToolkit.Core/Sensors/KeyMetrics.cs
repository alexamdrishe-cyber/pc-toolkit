namespace PcToolkit.Core.Sensors;

/// <summary>Показание, выбранное как главное для карточки: значение и сенсор, из которого оно взято.</summary>
public sealed record MetricValue(string SensorId, string DeviceName, float Value);

/// <summary>
/// Главные показатели для сводки: температура и нагрузка CPU и GPU, память, накопители.
/// Имена сенсоров взяты из LibreHardwareMonitor 0.9.6; если точного имени нет, берётся разумная замена.
/// </summary>
public sealed class KeyMetrics
{
    public string? CpuName { get; private set; }
    public MetricValue? CpuTemperature { get; private set; }
    public MetricValue? CpuLoad { get; private set; }
    public MetricValue? CpuPower { get; private set; }
    public float? CpuClock { get; private set; }

    public string? GpuName { get; private set; }
    public MetricValue? GpuTemperature { get; private set; }
    public MetricValue? GpuHotSpot { get; private set; }
    public MetricValue? GpuLoad { get; private set; }
    public MetricValue? GpuPower { get; private set; }
    /// <summary>Частота ядра видеокарты, МГц.</summary>
    public MetricValue? GpuClock { get; private set; }
    /// <summary>Занятая видеопамять, МБ.</summary>
    public MetricValue? GpuMemoryUsed { get; private set; }
    /// <summary>Объём видеопамяти, МБ.</summary>
    public MetricValue? GpuMemoryTotal { get; private set; }

    public MetricValue? MemoryLoad { get; private set; }
    /// <summary>Занятая оперативная память, ГБ.</summary>
    public MetricValue? MemoryUsed { get; private set; }
    /// <summary>Свободная оперативная память, ГБ.</summary>
    public MetricValue? MemoryAvailable { get; private set; }

    public int StorageCount { get; private set; }
    public MetricValue? HottestStorage { get; private set; }
    public MetricValue? BusiestStorage { get; private set; }

    public static KeyMetrics From(HardwareSnapshot snapshot)
    {
        var metrics = new KeyMetrics();
        metrics.ReadCpu(snapshot);
        metrics.ReadGpu(snapshot);
        metrics.ReadMemory(snapshot);
        metrics.ReadStorage(snapshot);
        return metrics;
    }

    /// <summary>
    /// Температура накопителя. У NVMe это «Composite Temperature» (общая температура по стандарту NVMe),
    /// у SATA — «Temperature». У NVMe есть ещё «Warning Temperature» и «Critical Temperature»:
    /// это не измерения, а пороги, заданные производителем, поэтому они никогда не выбираются.
    /// «Temperature #N» — отдельные точки внутри диска (часто горячая точка контроллера), берутся только в крайнем случае.
    /// </summary>
    public static MetricValue? StorageTemperature(DeviceSnapshot device) =>
        Pick(device, SensorKind.Temperature, "Composite Temperature", "Temperature")
        ?? Max(device, SensorKind.Temperature, sensor => !IsThreshold(sensor.Name));

    /// <summary>Порог, а не измерение: «Warning Temperature», «Critical Temperature» и т. п.</summary>
    public static bool IsThreshold(string sensorName) =>
        sensorName.Contains("Warning", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("Critical", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("Limit", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("Distance", StringComparison.OrdinalIgnoreCase);

    private void ReadCpu(HardwareSnapshot snapshot)
    {
        DeviceSnapshot? cpu = snapshot.Devices.FirstOrDefault(d => d.Kind == DeviceKind.Cpu);
        if (cpu is null)
            return;

        CpuName = cpu.Name;
        CpuTemperature =
            Pick(cpu, SensorKind.Temperature,
                "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core (Tdie)", "CCDs Max (Tdie)", "Core Max", "Core Average")
            ?? Max(cpu, SensorKind.Temperature, s => !IsThreshold(s.Name));
        CpuLoad = Pick(cpu, SensorKind.Load, "CPU Total");

        // Без драйвера PawnIO мощность процессора приходит нулём: работающий процессор не потребляет 0 Вт.
        MetricValue? power = Pick(cpu, SensorKind.Power, "CPU Package", "Package");
        CpuPower = power is { Value: > 0 } ? power : null;

        MetricValue? averageClock = Pick(cpu, SensorKind.Clock, "Cores (Average)");
        if (averageClock is { Value: > 0 })
        {
            CpuClock = averageClock.Value;
        }
        else
        {
            List<float> cores = cpu.Sensors
                .Where(s => s.Kind == SensorKind.Clock && s.Value > 0 && s.Name.Contains("Core #", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Value!.Value)
                .ToList();
            if (cores.Count > 0)
                CpuClock = cores.Average();
        }
    }

    private void ReadGpu(HardwareSnapshot snapshot)
    {
        // Дискретная карта важнее встроенной: сначала NVIDIA, потом AMD, потом Intel,
        // а среди одинаковых — та, у которой больше видеопамяти.
        DeviceSnapshot? gpu = snapshot.Devices
            .Where(d => d.IsGpu)
            .OrderBy(d => d.Kind switch
            {
                DeviceKind.GpuNvidia => 0,
                DeviceKind.GpuAmd => 1,
                _ => 2,
            })
            .ThenByDescending(d => TotalVideoMemory(d)?.Value ?? 0)
            .FirstOrDefault();
        if (gpu is null)
            return;

        GpuName = gpu.Name;
        GpuTemperature = Pick(gpu, SensorKind.Temperature, "GPU Core") ?? Max(gpu, SensorKind.Temperature, s => !IsThreshold(s.Name));
        GpuHotSpot = Pick(gpu, SensorKind.Temperature, "GPU Hot Spot");
        GpuLoad = Pick(gpu, SensorKind.Load, "GPU Core", "D3D 3D") ?? Max(gpu, SensorKind.Load);
        GpuPower = Pick(gpu, SensorKind.Power, "GPU Package", "GPU Power", "GPU PPT", "GPU Core");
        GpuClock = Pick(gpu, SensorKind.Clock, "GPU Core", "GPU Shader");
        GpuMemoryUsed = Pick(gpu, SensorKind.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        GpuMemoryTotal = TotalVideoMemory(gpu);
    }

    private void ReadMemory(HardwareSnapshot snapshot)
    {
        // У LibreHardwareMonitor два устройства памяти: «Total Memory» и «Virtual Memory». Нужно первое.
        DeviceSnapshot? memory = snapshot.Devices.FirstOrDefault(d =>
            d.Kind == DeviceKind.Memory && !d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        if (memory is null)
            return;

        MemoryLoad = Pick(memory, SensorKind.Load, "Memory");
        MemoryUsed = Pick(memory, SensorKind.Data, "Memory Used");
        MemoryAvailable = Pick(memory, SensorKind.Data, "Memory Available");
    }

    private void ReadStorage(HardwareSnapshot snapshot)
    {
        foreach (DeviceSnapshot device in snapshot.Devices.Where(d => d.Kind == DeviceKind.Storage))
        {
            StorageCount++;

            MetricValue? temperature = StorageTemperature(device);
            if (temperature is not null && (HottestStorage is null || temperature.Value > HottestStorage.Value))
                HottestStorage = temperature;

            MetricValue? activity = Pick(device, SensorKind.Load, "Total Activity");
            if (activity is not null && (BusiestStorage is null || activity.Value > BusiestStorage.Value))
                BusiestStorage = activity;
        }
    }

    private static MetricValue? TotalVideoMemory(DeviceSnapshot gpu) =>
        Pick(gpu, SensorKind.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");

    /// <summary>
    /// Можно ли верить значению. Без прав администратора и драйвера PawnIO библиотека
    /// отдаёт по многим датчикам ноль вместо «нет данных»: температура 0 °C и частота 0 МГц
    /// у работающего компьютера невозможны, поэтому такие значения считаем отсутствующими.
    /// </summary>
    public static bool IsPlausible(SensorKind kind, float value) => kind switch
    {
        SensorKind.Temperature or SensorKind.Clock => value > 0,
        _ => true,
    };

    private static MetricValue? Pick(DeviceSnapshot device, SensorKind kind, params string[] names)
    {
        foreach (string name in names)
        {
            foreach (SensorReading sensor in device.Sensors)
            {
                if (sensor.Kind == kind
                    && sensor.Value is float value
                    && IsPlausible(kind, value)
                    && string.Equals(sensor.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return new MetricValue(sensor.Id, device.Name, value);
                }
            }
        }

        return null;
    }

    private static MetricValue? Max(DeviceSnapshot device, SensorKind kind, Func<SensorReading, bool>? filter = null)
    {
        MetricValue? best = null;
        foreach (SensorReading sensor in device.Sensors)
        {
            if (sensor.Kind != kind || sensor.Value is not float value || !IsPlausible(kind, value))
                continue;

            if (filter is not null && !filter(sensor))
                continue;

            if (best is null || value > best.Value)
                best = new MetricValue(sensor.Id, device.Name, value);
        }

        return best;
    }
}
