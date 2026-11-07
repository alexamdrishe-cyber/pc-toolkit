namespace PcToolkit.Core.Sensors;

/// <summary>
/// Тип устройства. Свой enum, а не тип из LibreHardwareMonitor: так интерфейс не зависит от библиотеки
/// и данные можно будет передавать от службы Windows (этап 2).
/// </summary>
public enum DeviceKind
{
    Cpu,
    GpuNvidia,
    GpuAmd,
    GpuIntel,
    Memory,
    Motherboard,
    Storage,
    Network,
    Cooler,
    Psu,
    Battery,
    Other,
}

/// <summary>Тип показания. Имена совпадают с SensorType из LibreHardwareMonitor.</summary>
public enum SensorKind
{
    Temperature,
    Load,
    Clock,
    Power,
    Voltage,
    Current,
    Fan,
    Control,
    Level,
    Data,
    SmallData,
    Throughput,
    Frequency,
    Flow,
    Factor,
    TimeSpan,
    Timing,
    Energy,
    Noise,
    Conductivity,
    Humidity,
    Other,
}

/// <summary>Одно показание датчика в момент снимка.</summary>
public sealed record SensorReading(string Id, string Name, SensorKind Kind, float? Value, float? Min, float? Max);

/// <summary>Устройство и все его показания.</summary>
public sealed record DeviceSnapshot(string Id, string Name, DeviceKind Kind, IReadOnlyList<SensorReading> Sensors)
{
    public bool IsGpu => Kind is DeviceKind.GpuNvidia or DeviceKind.GpuAmd or DeviceKind.GpuIntel;
}

/// <summary>Снимок всех датчиков компьютера. Неизменяемый, его можно безопасно передавать между потоками.</summary>
public sealed record HardwareSnapshot(DateTime Timestamp, IReadOnlyList<DeviceSnapshot> Devices)
{
    public static HardwareSnapshot Empty { get; } = new(DateTime.MinValue, Array.Empty<DeviceSnapshot>());

    public int SensorCount => Devices.Sum(device => device.Sensors.Count);
}
