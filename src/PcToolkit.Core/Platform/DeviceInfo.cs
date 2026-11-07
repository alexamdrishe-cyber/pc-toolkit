using System.Management;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Platform;

/// <summary>Сведения о типе компьютера, нужные для рекомендаций.</summary>
public static class DeviceInfo
{
    private static readonly Lazy<bool> Laptop = new(DetectLaptop);

    /// <summary>Есть ли у компьютера батарея (ноутбук, планшет).</summary>
    public static bool IsLaptop => Laptop.Value;

    private static bool DetectLaptop()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery");
            using ManagementObjectCollection batteries = searcher.Get();
            return batteries.Count > 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось определить, ноутбук ли это.", ex);
            return false;
        }
    }
}
