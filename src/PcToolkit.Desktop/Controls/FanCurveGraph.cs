using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PcToolkit.Core.Overclock;

namespace PcToolkit.Desktop.Controls;

/// <summary>Кривая вентилятора: температура (°C) по горизонтали, скорость (%) по вертикали, с сеткой и подписями.</summary>
public sealed class FanCurveGraph : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<FanPoint>), typeof(FanCurveGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x2C, 0x38)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x84)));
    private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)));
    private static readonly Brush AreaBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0x8D, 0xFF)));
    private static readonly Typeface Font = new("Segoe UI");

    public IReadOnlyList<FanPoint>? Points
    {
        get => (IReadOnlyList<FanPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        const double left = 34, bottom = 18, top = 6, right = 8;
        double width = ActualWidth - left - right;
        double height = ActualHeight - top - bottom;
        if (width < 20 || height < 20)
            return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridPen = new Pen(GridBrush, 1);
        gridPen.Freeze();

        Point Map(double temperature, double speed) =>
            new(left + temperature / 100 * width, top + height - speed / 100 * height);

        for (int v = 0; v <= 100; v += 25)
        {
            Point a = Map(0, v);
            dc.DrawLine(gridPen, a, Map(100, v));
            var label = new FormattedText($"{v} %", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, LabelBrush, dpi);
            dc.DrawText(label, new Point(left - label.Width - 4, a.Y - label.Height / 2));

            Point b = Map(v, 0);
            dc.DrawLine(gridPen, b, Map(v, 100));
            var t = new FormattedText($"{v}°", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, LabelBrush, dpi);
            dc.DrawText(t, new Point(Math.Min(b.X - t.Width / 2, left + width - t.Width), top + height + 3));
        }

        IReadOnlyList<FanPoint>? points = Points;
        if (points is not { Count: > 0 })
            return;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (StreamGeometryContext l = line.Open())
        using (StreamGeometryContext f = area.Open())
        {
            // До первой точки скорость постоянна, после последней — тоже.
            Point start = Map(0, points[0].Speed);
            l.BeginFigure(start, false, false);
            f.BeginFigure(Map(0, 0), true, true);
            f.LineTo(start, false, false);
            foreach (FanPoint p in points)
            {
                Point mapped = Map(Math.Clamp(p.Temperature, 0, 100), Math.Clamp(p.Speed, 0, 100));
                l.LineTo(mapped, true, false);
                f.LineTo(mapped, false, false);
            }

            Point end = Map(100, Math.Clamp(points[^1].Speed, 0, 100));
            l.LineTo(end, true, false);
            f.LineTo(end, false, false);
            f.LineTo(Map(100, 0), false, false);
        }

        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(AreaBrush, null, area);
        var pen = new Pen(LineBrush, 2);
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
        foreach (FanPoint p in points)
            dc.DrawEllipse(LineBrush, null, Map(Math.Clamp(p.Temperature, 0, 100), Math.Clamp(p.Speed, 0, 100)), 3.5, 3.5);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
