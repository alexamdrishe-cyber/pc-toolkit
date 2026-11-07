using System.Windows;
using System.Windows.Media;
using PcToolkit.Core.Sensors;

namespace PcToolkit.Desktop.Controls;

/// <summary>
/// Крошечный график в строке таблицы: только линия, шкала по своим минимуму и максимуму.
/// По ширине — <see cref="WindowSeconds"/> секунд, последние значения прижаты к правому краю.
/// </summary>
public sealed class MiniSpark : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(MiniSpark),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(MiniSpark),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Шаг между значениями, секунды.</summary>
    public static readonly DependencyProperty SampleSecondsProperty = DependencyProperty.Register(
        nameof(SampleSeconds), typeof(double), typeof(MiniSpark),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(double), typeof(MiniSpark),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Сквозной номер первого значения (HistoryWindow.FirstIndex): к нему привязаны группы точек.</summary>
    public static readonly DependencyProperty FirstIndexProperty = DependencyProperty.Register(
        nameof(FirstIndex), typeof(long), typeof(MiniSpark),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public double SampleSeconds { get => (double)GetValue(SampleSecondsProperty); set => SetValue(SampleSecondsProperty, value); }

    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }

    public long FirstIndex { get => (long)GetValue(FirstIndexProperty); set => SetValue(FirstIndexProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        IReadOnlyList<double>? values = Values;
        double width = ActualWidth;
        double height = ActualHeight;
        if (values is null || values.Count < 2 || width < 4 || height < 4)
            return;

        double sample = Math.Max(0.01, SampleSeconds);
        double window = Math.Max(sample * 2, WindowSeconds);
        int count = Math.Min(values.Count, (int)Math.Floor(window / sample) + 1);
        int offset = values.Count - count;
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = offset; i < values.Count; i++)
        {
            double v = values[i];
            if (!double.IsFinite(v))
                continue;
            lo = Math.Min(lo, v);
            hi = Math.Max(hi, v);
        }

        if (lo > hi)
            return;

        // Почти ровная линия не должна выглядеть как пила из-за шума в последнем знаке.
        double span = Math.Max(hi - lo, Math.Max(Math.Abs(hi) * 0.05, 1e-6));
        double middle = (hi + lo) / 2;
        lo = middle - span / 2;
        double right = width - 1;
        double stepPx = (width - 1) * sample / window;
        // Не больше точки на пиксель: соседние значения усредняются. Границы групп — по сквозному номеру
        // значения (ChartGroups), как в LineChart: иначе линия перестраивается при каждом снимке и дёргается.
        int group = ChartGroups.Size(sample, (width - 1) / window);
        List<Point> points = ChartGroups.Average(values, offset, FirstIndex, group)
            .Select(p => new Point(right - p.StepsFromEnd * stepPx, height - 1.5 - (p.Value - lo) / span * (height - 3)))
            .ToList();

        if (points.Count < 2)
            return;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Count; i++)
                ctx.LineTo(points[i], true, true);
        }

        geometry.Freeze();
        var pen = new Pen(Stroke, 1.3) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}

/// <summary>Столбики по ядрам или потокам: высота — доля от шкалы (0…1).</summary>
public sealed class BarStrip : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(BarStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(BarStrip),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(BarStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        IReadOnlyList<double>? values = Values;
        if (values is null || values.Count == 0 || ActualWidth < 4)
            return;

        double gap = values.Count > 8 ? 2 : 3;
        double barWidth = (ActualWidth - gap * (values.Count - 1)) / values.Count;
        if (barWidth <= 0)
            return;

        for (int i = 0; i < values.Count; i++)
        {
            double x = i * (barWidth + gap);
            if (Track is not null)
                dc.DrawRoundedRectangle(Track, null, new Rect(x, 0, barWidth, ActualHeight), 1.5, 1.5);

            double fraction = double.IsFinite(values[i]) ? Math.Clamp(values[i], 0.06, 1) : 0.06;
            double h = Math.Max(2, ActualHeight * fraction);
            dc.DrawRoundedRectangle(Fill, null, new Rect(x, ActualHeight - h, barWidth, h), 1.5, 1.5);
        }
    }
}
