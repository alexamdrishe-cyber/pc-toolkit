using System.Windows.Media;

namespace PcToolkit.Desktop.Services;

/// <summary>Контурные значки 24×24 для меню и карточек (рисуются линией цвета текста).</summary>
public static class Icons
{
    public static Geometry Home { get; } = Parse("M4 10.5 12 4l8 6.5V20h-5v-6H9v6H4z");
    public static Geometry Monitoring { get; } = Parse("M3 12h4l3-7 4 14 3-7h4");
    public static Geometry System { get; } = Parse("M8 8h8v8H8z M10 3v3 M14 3v3 M10 18v3 M14 18v3 M3 10h3 M3 14h3 M18 10h3 M18 14h3");
    public static Geometry Games { get; } = Parse("M7 6h10a5 5 0 0 1 4.9 6l-.8 3.8a2.8 2.8 0 0 1-4.8 1.3L14.5 15h-5l-1.8 2.1a2.8 2.8 0 0 1-4.8-1.3L2.1 12A5 5 0 0 1 7 6z M7 9.5v3 M5.5 11h3 M15.5 10h.01 M17.5 12h.01");
    public static Geometry Tests { get; } = Parse("M4.2 17a8.5 8.5 0 1 1 15.6 0 M12 13.5l4-4");
    public static Geometry Overclock { get; } = Parse("M13 3 5 14h6l-1 7 8-11h-6z");
    public static Geometry Optimize { get; } = Parse("M4 7h9 M17 7h3 M4 12h3 M11 12h9 M4 17h11 M19 17h1 M15 5v4 M9 10v4 M17 15v4");
    public static Geometry Cleanup { get; } = Parse("M12 4v3 M12 17v3 M4 12h3 M17 12h3 M6.5 6.5l2 2 M15.5 15.5l2 2 M6.5 17.5l2-2 M15.5 8.5l2-2");
    public static Geometry Disk { get; } = Parse("M12 3a9 9 0 1 0 9 9h-9z M15 3.6A9 9 0 0 1 20.4 9H15z");
    public static Geometry Apps { get; } = Parse("M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h6v6h-6z");
    public static Geometry Journal { get; } = Parse("M3.5 12a8.5 8.5 0 1 0 2.6-6.1 M3.5 4.5v4h4 M12 8v4.5l3 2");
    public static Geometry Settings { get; } = Parse("M12 15.2a3.2 3.2 0 1 0 0-6.4 3.2 3.2 0 0 0 0 6.4z M19 13.5l1.8 1.1-1.8 3.2-2-.7a7 7 0 0 1-1.9 1.1l-.4 2.1h-3.6l-.4-2.1a7 7 0 0 1-1.9-1.1l-2 .7-1.8-3.2 1.6-1.4a7 7 0 0 1 0-2.4L5.1 9.4l1.8-3.2 2 .7a7 7 0 0 1 1.9-1.1l.4-2.1h3.6l.4 2.1a7 7 0 0 1 1.9 1.1l2-.7 1.8 3.2-1.6 1.4a7 7 0 0 1 0 2.4z");
    public static Geometry Search { get; } = Parse("M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14z M20 20l-4-4");
    public static Geometry Collapse { get; } = Parse("M4 5h16v14H4z M9 5v14 M15.5 9.5 13 12l2.5 2.5");
    public static Geometry Expand { get; } = Parse("M4 5h16v14H4z M9 5v14 M13 9.5l2.5 2.5L13 14.5");
    public static Geometry Logo { get; } = Parse("M3 12h4l3-7 4 14 3-7h4");
    public static Geometry Check { get; } = Parse("M5 12.5l4.5 4.5L19 7.5");
    public static Geometry Warning { get; } = Parse("M12 4 2.5 20h19z M12 10v4.5 M12 17.3v.01");
    public static Geometry Chevron { get; } = Parse("M9 6l6 6-6 6");
    public static Geometry Pin { get; } = Parse("M9 3.5h6l-1 5.5 3.5 3.5v2h-11v-2L10 9z M12 14.5V21");
    public static Geometry Close { get; } = Parse("M6 6l12 12 M18 6 6 18");
    public static Geometry Folder { get; } = Parse("M3.5 6.5h6l2 2h9v10h-17z");
    public static Geometry Record { get; } = Parse("M12 6a6 6 0 1 0 0 12 6 6 0 0 0 0-12z");

    private static Geometry Parse(string data)
    {
        Geometry geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
