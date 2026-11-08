using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PcToolkit.Core.Fans;

namespace PcToolkit.Desktop.Controls;

/// <summary>
/// Кривая «температура → обороты»: температура по горизонтали (20–100 °C), обороты по вертикали (0–100 %).
/// В ручном режиме точки перетаскиваются мышью; отметка показывает текущую температуру и обороты на ней.
/// </summary>
public sealed class FanCurveEditor : FrameworkElement
{
    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve), typeof(FanCurve), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
        nameof(IsEditable), typeof(bool), typeof(FanCurveEditor), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentTemperatureProperty = DependencyProperty.Register(
        nameof(CurrentTemperature), typeof(double), typeof(FanCurveEditor), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(Color.FromRgb(0x4C, 0x8D, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    private const double MinT = FanCurve.MinTemperature;
    private const double MaxT = FanCurve.MaxTemperature;
    private const double Left = 40;
    private const double Right = 12;
    private const double Top = 12;
    private const double Bottom = 24;
    private const double HitRadius = 11;

    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x29, 0x34)));
    private static readonly Brush GridStrongBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x33, 0x40)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x83, 0x96)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xF1)));
    private static readonly Brush BubbleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1C, 0x22, 0x2C)));
    private static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private int _hover = -1;
    private int _drag = -1;

    public FanCurveEditor()
    {
        Focusable = false;
        ClipToBounds = false;
    }

    public FanCurve? Curve
    {
        get => (FanCurve?)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    /// <summary>Текущая температура датчика, по которому идёт кривая (NaN — неизвестна).</summary>
    public double CurrentTemperature
    {
        get => (double)GetValue(CurrentTemperatureProperty);
        set => SetValue(CurrentTemperatureProperty, value);
    }

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private Rect Plot => new(Left, Top, Math.Max(1, ActualWidth - Left - Right), Math.Max(1, ActualHeight - Top - Bottom));

    private Point Map(double temperature, double duty)
    {
        Rect plot = Plot;
        return new Point(plot.Left + (Math.Clamp(temperature, MinT, MaxT) - MinT) / (MaxT - MinT) * plot.Width,
                         plot.Bottom - Math.Clamp(duty, 0, 100) / 100 * plot.Height);
    }

    private (double Temperature, double Duty) Unmap(Point point)
    {
        Rect plot = Plot;
        return (MinT + (point.X - plot.Left) / plot.Width * (MaxT - MinT), (plot.Bottom - point.Y) / plot.Height * 100);
    }

    protected override void OnRender(DrawingContext dc)
    {
        Rect plot = Plot;
        if (plot.Width < 40 || plot.Height < 30)
            return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // ловить мышь по всей площади

        var grid = new Pen(GridBrush, 1);
        var gridStrong = new Pen(GridStrongBrush, 1);
        grid.Freeze();
        gridStrong.Freeze();
        for (int duty = 0; duty <= 100; duty += 25)
        {
            Point a = Map(MinT, duty);
            dc.DrawLine(duty == 0 ? gridStrong : grid, Snap(a), Snap(Map(MaxT, duty)));
            Text(dc, $"{duty} %", new Point(plot.Left - 6, a.Y), LabelBrush, 10.5, dpi, alignRight: true, middle: true);
        }

        for (int t = (int)MinT; t <= MaxT; t += 10)
        {
            Point a = Map(t, 0);
            dc.DrawLine(t % 20 == 0 ? gridStrong : grid, Snap(a), Snap(Map(t, 100)));
            if (t % 20 == 0)
                Text(dc, $"{t}°", new Point(a.X, plot.Bottom + 5), LabelBrush, 10.5, dpi, center: true);
        }

        FanCurve? curve = Curve;
        if (curve is null)
            return;

        Color accent = Accent;
        IReadOnlyList<CurvePoint> points = curve.Points;
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (StreamGeometryContext l = line.Open())
        using (StreamGeometryContext f = area.Open())
        {
            Point start = Map(MinT, points[0].Duty);
            l.BeginFigure(start, false, false);
            f.BeginFigure(Map(MinT, 0), true, true);
            f.LineTo(start, false, false);
            foreach (CurvePoint p in points)
            {
                Point mapped = Map(p.Temperature, p.Duty);
                l.LineTo(mapped, true, true);
                f.LineTo(mapped, false, false);
            }

            Point end = Map(MaxT, points[^1].Duty);
            l.LineTo(end, true, false);
            f.LineTo(end, false, false);
            f.LineTo(Map(MaxT, 0), false, false);
        }

        line.Freeze();
        area.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B), Color.FromArgb(0x08, accent.R, accent.G, accent.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        var pen = new Pen(new SolidColorBrush(accent), 2.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        // Текущая температура: вертикальная отметка и точка на кривой.
        double current = CurrentTemperature;
        if (!double.IsNaN(current))
        {
            double duty = curve.DutyAt(current);
            Point at = Map(current, duty);
            var marker = new Pen(TextBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            marker.Freeze();
            dc.DrawLine(marker, Snap(new Point(at.X, plot.Top)), Snap(new Point(at.X, plot.Bottom)));
            dc.DrawEllipse(TextBrush, new Pen(new SolidColorBrush(accent), 3), at, 5, 5);
            Bubble(dc, $"{current:0}° → {duty:0} %", new Point(at.X, plot.Top), dpi, accent, above: false);
        }

        var dotStroke = new Pen(new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x11)), 2);
        dotStroke.Freeze();
        var dotFill = new SolidColorBrush(accent);
        dotFill.Freeze();
        for (int i = 0; i < points.Count; i++)
        {
            Point mapped = Map(points[i].Temperature, points[i].Duty);
            bool active = IsEditable && (i == _hover || i == _drag);
            double radius = IsEditable ? (active ? 7.5 : 6) : 3.5;
            dc.DrawEllipse(active ? TextBrush : dotFill, dotStroke, mapped, radius, radius);
            if (active)
                Bubble(dc, $"{points[i].Temperature:0}° → {points[i].Duty:0} %", mapped, dpi, accent, above: true);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsEditable || Curve is null)
            return;

        Point position = e.GetPosition(this);
        if (_drag >= 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            (double temperature, double duty) = Unmap(position);
            FanCurve moved = Curve.WithPoint(_drag, temperature, duty);
            if (!moved.SameAs(Curve))
                SetCurrentValue(CurveProperty, moved); // привязка остаётся, новое значение уходит в модель
            return;
        }

        int hover = HitTest(position);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover >= 0 ? Cursors.Hand : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEditable || Curve is null)
            return;

        int hit = HitTest(e.GetPosition(this));
        if (hit < 0)
            return;

        _drag = hit;
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag < 0)
            return;

        _drag = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover >= 0 && _drag < 0)
        {
            _hover = -1;
            Cursor = null;
            InvalidateVisual();
        }
    }

    private int HitTest(Point position)
    {
        if (Curve is null)
            return -1;

        int best = -1;
        double bestDistance = HitRadius;
        for (int i = 0; i < Curve.Points.Count; i++)
        {
            Point p = Map(Curve.Points[i].Temperature, Curve.Points[i].Duty);
            double distance = (p - position).Length;
            if (distance <= bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    private void Bubble(DrawingContext dc, string text, Point anchor, double dpi, Color accent, bool above)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, BoldFont, 11.5, TextBrush, dpi);
        double width = formatted.Width + 14;
        double height = formatted.Height + 6;
        double x = Math.Clamp(anchor.X - width / 2, 0, Math.Max(0, ActualWidth - width));
        double y = above ? anchor.Y - height - 10 : anchor.Y + 2;
        if (above && y < 0)
            y = anchor.Y + 12;
        var rect = new Rect(x, y, width, height);
        var border = new Pen(new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B)), 1);
        border.Freeze();
        dc.DrawRoundedRectangle(BubbleBrush, border, rect, 6, 6);
        dc.DrawText(formatted, new Point(x + 7, y + 3));
    }

    private static void Text(DrawingContext dc, string text, Point at, Brush brush, double size, double dpi,
                             bool alignRight = false, bool center = false, bool middle = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush, dpi);
        double x = alignRight ? at.X - formatted.Width : center ? at.X - formatted.Width / 2 : at.X;
        double y = middle ? at.Y - formatted.Height / 2 : at.Y;
        dc.DrawText(formatted, new Point(x, y));
    }

    private static Point Snap(Point point) => new(Math.Round(point.X) + 0.5, Math.Round(point.Y) + 0.5);

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
