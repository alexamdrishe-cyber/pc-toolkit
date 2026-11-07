namespace PcToolkit.Core.Diagnostics;

public enum HealthState
{
    Unknown,
    Normal,
    Warning,
    Critical,
}

/// <summary>
/// Ориентировочные пороги для предупреждений. Это не паспортные пределы конкретного железа,
/// а общие значения, при которых стоит обратить внимание на охлаждение.
/// </summary>
public static class Thresholds
{
    public const float CpuWarning = 85;
    public const float CpuCritical = 95;

    public const float GpuWarning = 85;
    public const float GpuCritical = 95;
    public const float GpuHotSpotWarning = 105;

    public const float StorageWarning = 65;
    public const float StorageCritical = 75;

    public const float MemoryLoadWarning = 90;

    public const double DiskFreeWarningPercent = 10;

    public static HealthState Classify(float? value, float warning, float critical)
    {
        if (value is not float v)
            return HealthState.Unknown;

        if (v >= critical)
            return HealthState.Critical;

        return v >= warning ? HealthState.Warning : HealthState.Normal;
    }
}
