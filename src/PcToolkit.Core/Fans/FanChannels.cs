using PcToolkit.Core.Sensors;

namespace PcToolkit.Core.Fans;

/// <summary>Какие вентиляторы видны в снимке датчиков и какие у них обороты.</summary>
public static class FanChannels
{
    /// <summary>
    /// Управляемые вентиляторы: материнская плата, контроллеры и видеокарты NVIDIA. Видеокарта AMD управляется
    /// через свой драйвер (ADLX), поэтому её управление из библиотеки датчиков не берётся.
    /// </summary>
    public static IReadOnlyList<FanChannel> FromSnapshot(HardwareSnapshot snapshot)
    {
        var channels = new List<FanChannel>();
        foreach (DeviceSnapshot device in snapshot.Devices)
        {
            FanChannelKind? kind = device.Kind switch
            {
                DeviceKind.Motherboard => FanChannelKind.Motherboard,
                DeviceKind.Cooler => FanChannelKind.Controller,
                DeviceKind.GpuNvidia => FanChannelKind.GpuNvidia,
                _ => null,
            };
            if (kind is null)
                continue;

            foreach (SensorReading control in device.Sensors.Where(s => s.Kind == SensorKind.Control))
            {
                string fanId = FanIdFor(control.Id);
                SensorReading? fan = device.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Fan && s.Id == fanId);
                channels.Add(new FanChannel(control.Id, control.Name, device.Name, kind.Value, fan?.Value, control.Value));
            }
        }

        return channels;
    }

    /// <summary>«…/control/2» → «…/fan/2»: у библиотеки датчиков номер управления совпадает с номером тахометра.</summary>
    public static string FanIdFor(string controlId)
    {
        int at = controlId.LastIndexOf("/control/", StringComparison.Ordinal);
        return at < 0 ? controlId : controlId[..at] + "/fan/" + controlId[(at + "/control/".Length)..];
    }

    /// <summary>«Fan #2» → «Вентилятор 2», «GPU Fan» → «Вентилятор видеокарты».</summary>
    public static string DisplayName(FanChannel channel)
    {
        string name = channel.Name.Trim();
        if (channel.Kind == FanChannelKind.GpuNvidia)
            return name.Contains('#') || name.Any(char.IsDigit) ? "Видеокарта: " + name.Replace("GPU Fan", "вентилятор") : "Вентиляторы видеокарты";
        if (name.StartsWith("Fan #", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[5..].Trim(), out int number))
            return $"Вентилятор {number}";
        if (name.StartsWith("Fan Control #", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[13..].Trim(), out int control))
            return $"Вентилятор {control}";
        return name;
    }
}
