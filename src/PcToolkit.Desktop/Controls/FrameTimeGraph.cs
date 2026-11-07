using System.Windows;
using System.Windows.Media;

namespace PcToolkit.Desktop.Controls;

/// <summary>
/// Маленький график времени кадра для оверлея: ровная линия — плавная картинка, пики — рывки.
/// Шкала от 0 до max(33 мс, самый долгий кадр), пунктир — 16,7 мс (60 FPS).
/// </summary>
public sealed class FrameTimeGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(FrameTimeGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(FrameTimeGraph),
        new FrameworkPropertyMetadata(Brushes.LightGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen GuidePen = CreateGuidePen();

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 2 || height < 2)
            return;

        IReadOnlyList<double>? values = Values;
        double max = 1000.0 / 30;
        if (values is { Count: > 0 })
            max = Math.Max(max, values.Max() * 1.1);

        double guideY = height - 1000.0 / 60 / max * height;
        dc.DrawLine(GuidePen, new Point(0, guideY), new Point(width, guideY));

        if (values is not { Count: > 1 })
            return;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            double step = width / (values.Count - 1);
            for (int i = 0; i < values.Count; i++)
            {
                var point = new Point(i * step, height - Math.Min(values[i], max) / max * height);
                if (i == 0)
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                else
                    context.LineTo(point, isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        var pen = new Pen(Stroke, 1.3);
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Pen CreateGuidePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }
}
