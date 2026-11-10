using System.Windows.Media;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Цвета устройства: линия графика, вторая линия (светлее), метка CPU/GPU/RAM.</summary>
public sealed class DeviceColors
{
    public DeviceColors(string tag, Color accent, Color secondary, Color tagBackground, Color tagForeground)
    {
        Tag = tag;
        Accent = accent;
        Secondary = secondary;
        AccentBrush = Freeze(new SolidColorBrush(accent));
        SecondaryBrush = Freeze(new SolidColorBrush(secondary));
        // Метка — полупрозрачный оттенок цвета устройства: одинаково читается в тёмной и светлой теме.
        TagBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B)));
        TagForeground = Freeze(new SolidColorBrush(accent));
    }

    public string Tag { get; }

    public Color Accent { get; }

    public Color Secondary { get; }

    public Brush AccentBrush { get; }

    public Brush SecondaryBrush { get; }

    public Brush TagBackground { get; }

    public Brush TagForeground { get; }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Единые цвета устройств во всём приложении: процессор синий, видеокарта оранжевая, память фиолетовая,
/// диски бирюзовые. Синий с оранжевым различаются и по яркости, а не только по оттенку.
/// </summary>
public static class DevicePalette
{
    public static DeviceColors Cpu { get; } = new("CPU", Rgb(0x4C, 0x8D, 0xFF), Rgb(0xC9, 0xD7, 0xF2), Rgb(0x1B, 0x2A, 0x47), Rgb(0x8F, 0xB5, 0xFF));

    public static DeviceColors Gpu { get; } = new("GPU", Rgb(0xF0, 0x8A, 0x4B), Rgb(0xF7, 0xC5, 0x9F), Rgb(0x3A, 0x26, 0x18), Rgb(0xF6, 0xA5, 0x74));

    public static DeviceColors Ram { get; } = new("RAM", Rgb(0xA7, 0x8B, 0xFA), Rgb(0xDD, 0xD3, 0xFE), Rgb(0x2C, 0x24, 0x47), Rgb(0xC4, 0xB5, 0xFD));

    public static DeviceColors Ssd { get; } = new("SSD", Rgb(0x2D, 0xD4, 0xBF), Rgb(0xA7, 0xF3, 0xE4), Rgb(0x12, 0x36, 0x34), Rgb(0x5E, 0xEA, 0xD4));

    public static DeviceColors Net { get; } = new("NET", Rgb(0x8F, 0xB0, 0xCC), Rgb(0xD3, 0xE2, 0xEE), Rgb(0x1E, 0x2A, 0x33), Rgb(0xA5, 0xC8, 0xE4));

    public static DeviceColors Board { get; } = new("MB", Rgb(0x9A, 0xA3, 0xB5), Rgb(0xD7, 0xDC, 0xE5), Rgb(0x23, 0x28, 0x33), Rgb(0xC9, 0xCF, 0xDA));

    /// <summary>Цвета для линий, закреплённых на общем графике: синий, оранжевый, бирюзовый, фиолетовый.</summary>
    public static IReadOnlyList<Color> Pins { get; } = new[] { Cpu.Accent, Gpu.Accent, Ssd.Accent, Ram.Accent };

    public static DeviceColors For(string tag) => tag switch
    {
        "CPU" => Cpu,
        "GPU" => Gpu,
        "RAM" => Ram,
        "SSD" or "DISK" => Ssd,
        "NET" => Net,
        _ => Board,
    };

    public static DeviceColors For(Core.Sensors.DeviceKind kind) => kind switch
    {
        Core.Sensors.DeviceKind.Cpu => Cpu,
        Core.Sensors.DeviceKind.GpuAmd or Core.Sensors.DeviceKind.GpuNvidia or Core.Sensors.DeviceKind.GpuIntel => Gpu,
        Core.Sensors.DeviceKind.Memory => Ram,
        Core.Sensors.DeviceKind.Storage => Ssd,
        Core.Sensors.DeviceKind.Network => Net,
        _ => Board,
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
