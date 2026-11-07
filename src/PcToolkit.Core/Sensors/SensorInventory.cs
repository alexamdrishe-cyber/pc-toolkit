using System.Globalization;
using System.Text;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Sensors;

/// <summary>
/// Список всех устройств и датчиков текстом: %LocalAppData%\PcToolkit\sensors.txt.
/// Пишется при запуске и при смене набора датчиков — чтобы было видно, что программа видит на этом компьютере
/// (пригодится и для разбора проблем с конкретным железом).
/// </summary>
public static class SensorInventory
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "sensors.txt");

    public static string Format(HardwareSnapshot snapshot)
    {
        var text = new StringBuilder();
        text.AppendLine($"Снимок {snapshot.Timestamp:yyyy-MM-dd HH:mm:ss} · устройств: {snapshot.Devices.Count} · датчиков: {snapshot.SensorCount}");
        foreach (DeviceSnapshot device in snapshot.Devices)
        {
            text.AppendLine();
            text.AppendLine($"[{device.Kind}] {device.Name}  ({device.Id})");
            foreach (SensorReading sensor in device.Sensors)
            {
                string value = sensor.Value is float v ? v.ToString("0.###", CultureInfo.InvariantCulture) : "—";
                string min = sensor.Min is float mn ? mn.ToString("0.###", CultureInfo.InvariantCulture) : "—";
                string max = sensor.Max is float mx ? mx.ToString("0.###", CultureInfo.InvariantCulture) : "—";
                text.AppendLine($"  {sensor.Kind,-12} {sensor.Name,-40} {value,10}  [{min} … {max}]  {sensor.Id}");
            }
        }

        return text.ToString();
    }

    public static void Write(HardwareSnapshot snapshot, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Format(snapshot));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось записать список датчиков.", ex);
        }
    }
}
