namespace PcToolkit.Core.Overlay;

/// <summary>Прямоугольник в пикселях экрана.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>Перевод места оверлея (<see cref="OverlayPosition"/>) в пиксели монитора и обратно.</summary>
public static class OverlayPlacement
{
    /// <summary>Левый верхний угол окна оверлея размером width×height на мониторе. Окно не выходит за край экрана.</summary>
    public static (int X, int Y) Place(PixelRect monitor, int width, int height, OverlayPosition position)
    {
        double dx = position.X * monitor.Width;
        double dy = position.Y * monitor.Height;
        double x = position.IsRight ? monitor.Right - width - dx
            : position.IsCenter ? monitor.Left + (monitor.Width - width) / 2.0 + dx
            : monitor.Left + dx;
        double y = position.IsBottom ? monitor.Bottom - height - dy : monitor.Top + dy;

        x = Math.Clamp(x, monitor.Left, Math.Max(monitor.Left, monitor.Right - width));
        y = Math.Clamp(y, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - height));
        return ((int)Math.Round(x), (int)Math.Round(y));
    }

    /// <summary>
    /// Место окна, которое перетащили мышью. Отсчёт — от ближайшего угла или середины края: если оверлей
    /// стоит у правого края, он и дальше будет прижат к нему, даже когда станет шире.
    /// </summary>
    public static OverlayPosition FromWindow(PixelRect monitor, PixelRect window)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0)
            return new OverlayPosition();

        double centerX = (window.Left + window.Right) / 2.0 - monitor.Left;
        double centerY = (window.Top + window.Bottom) / 2.0 - monitor.Top;
        string horizontal = centerX < monitor.Width / 3.0 ? "Left" : centerX > monitor.Width * 2 / 3.0 ? "Right" : "Center";
        string vertical = centerY < monitor.Height / 2.0 ? "Top" : "Bottom";

        double x = horizontal switch
        {
            "Left" => window.Left - monitor.Left,
            "Right" => monitor.Right - window.Right,
            _ => centerX - monitor.Width / 2.0,
        };
        double y = vertical == "Top" ? window.Top - monitor.Top : monitor.Bottom - window.Bottom;

        var position = new OverlayPosition
        {
            Anchor = vertical + horizontal,
            X = Math.Round(x / monitor.Width, 4),
            Y = Math.Round(y / monitor.Height, 4),
        };
        return position.Normalize();
    }
}
