using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PcToolkit.Desktop.Controls;

/// <summary>
/// Линейный график без сторонних библиотек: сетка, подписи значений с единицами, шкала времени,
/// заливка под линией и подсказка со значением при наведении мыши.
/// Два вида: полный (<see cref="ShowAxes"/> = true) и компактный для маленьких карточек.
/// Время: при <see cref="Capacity"/> &gt; 0 — «скользящее окно» (последнее значение у правого края, подписи «−2 мин … сейчас»),
/// при 0 — от начала записи (подписи «0:00 … 1:00»), точки растягиваются на всю ширину.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly int[] TimeSteps = { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400 };

    public static readonly DependencyProperty ValuesProperty = Register(nameof(Values), typeof(IReadOnlyList<double>), null);
    public static readonly DependencyProperty StrokeProperty = Register(nameof(Stroke), typeof(Brush), null);
    public static readonly DependencyProperty FillProperty = Register(nameof(Fill), typeof(Brush), null);
    public static readonly DependencyProperty GridBrushProperty = Register(nameof(GridBrush), typeof(Brush), null);
    public static readonly DependencyProperty StrokeThicknessProperty = Register(nameof(StrokeThickness), typeof(double), 2.0);

    /// <summary>Сколько точек помещается по ширине (скользящее окно). 0 — время от начала, точки на всю ширину.</summary>
    public static readonly DependencyProperty CapacityProperty = Register(nameof(Capacity), typeof(int), 0);

    /// <summary>Минимальный диапазон по вертикали, чтобы колебания в доли градуса не выглядели как скачки.</summary>
    public static readonly DependencyProperty MinimumRangeProperty = Register(nameof(MinimumRange), typeof(double), 2.0);

    /// <summary>Единица значений для подписей: «°C», «МГц», «%». Для «%» шкала всегда 0–100.</summary>
    public static readonly DependencyProperty UnitProperty = Register(nameof(Unit), typeof(string), "");

    /// <summary>Сколько секунд между точками.</summary>
    public static readonly DependencyProperty SampleSecondsProperty = Register(nameof(SampleSeconds), typeof(double), 1.0);

    /// <summary>Полный вид с осями; false — компактный (сетка и подписи границ внутри графика).</summary>
    public static readonly DependencyProperty ShowAxesProperty = Register(nameof(ShowAxes), typeof(bool), true);

    /// <summary>Текст на пустом графике.</summary>
    public static readonly DependencyProperty EmptyTextProperty = Register(nameof(EmptyText), typeof(string), "");

    private Point? _hover;

    public Sparkline()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Заливка под линией. Если не задана — градиент цвета линии.</summary>
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    public double MinimumRange { get => (double)GetValue(MinimumRangeProperty); set => SetValue(MinimumRangeProperty, value); }

    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    public double SampleSeconds { get => (double)GetValue(SampleSecondsProperty); set => SetValue(SampleSecondsProperty, value); }

    public bool ShowAxes { get => (bool)GetValue(ShowAxesProperty); set => SetValue(ShowAxesProperty, value); }

    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 24 || height < 16)
            return;

        // Прозрачный фон — чтобы подсказка работала по всей площади графика.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        IReadOnlyList<double> values = Values ?? Array.Empty<double>();
        int count = values.Count;
        bool axes = ShowAxes;
        double fontSize = axes ? 11 : 10;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Brush gridBrush = GridBrush ?? Resource("LineBrush", Color.FromRgb(0x26, 0x2C, 0x38));
        Brush labelBrush = Resource("TextMutedBrush", Color.FromRgb(0x6A, 0x72, 0x84));
        Brush stroke = Stroke ?? Resource("AccentBrush", Color.FromRgb(0x4C, 0x8D, 0xFF));
        Color strokeColor = stroke is SolidColorBrush solid ? solid.Color : Color.FromRgb(0x4C, 0x8D, 0xFF);

        FormattedText Text(string text, Brush brush, double size = 0) =>
            new(text, Russian, FlowDirection.LeftToRight, typeface, size > 0 ? size : fontSize, brush, pixelsPerDip);

        // ---- Вертикальная шкала ----
        (double lo, double hi) = ValueRange(values);
        double plotHeightGuess = axes ? height - fontSize - 14 : height - 4;
        int targetTicks = axes ? Math.Clamp((int)(plotHeightGuess / 28), 2, 6) : 3;
        double step = NiceStep((hi - lo) / Math.Max(1, targetTicks - 1));
        double niceLo = Math.Floor(lo / step + 1e-9) * step;
        double niceHi = Math.Ceiling(hi / step - 1e-9) * step;
        if (niceHi - niceLo < step)
            niceHi = niceLo + step;
        int decimals = Decimals(step);
        string unit = string.IsNullOrEmpty(Unit) ? "" : " " + Unit;
        var ticks = new List<double>();
        for (double t = niceLo; t <= niceHi + step * 1e-6; t += step)
            ticks.Add(Math.Round(t, 6));

        // ---- Область графика ----
        double left = 0, top = 2, right = width, bottom = height - 2;
        var tickLabels = ticks.Select(t => Text(FormatNumber(t, decimals) + unit, labelBrush)).ToList();
        if (axes)
        {
            left = tickLabels.Max(l => l.Width) + 10;
            top = fontSize / 2 + 2;
            right = width - 12;
            bottom = height - fontSize - 10;
        }

        var plot = new Rect(new Point(left, top), new Point(Math.Max(left + 1, right), Math.Max(top + 1, bottom)));
        double Y(double value) => plot.Bottom - (value - niceLo) / (niceHi - niceLo) * plot.Height;

        var gridPen = new Pen(gridBrush, 1);
        gridPen.Freeze();
        var faintGridPen = new Pen(gridBrush, 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
        faintGridPen.Freeze();

        // Горизонтальные линии и подписи значений.
        for (int i = 0; i < ticks.Count; i++)
        {
            double y = Snap(Y(ticks[i]));
            dc.DrawLine(i == 0 ? gridPen : faintGridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            if (count == 0)
                continue; // без данных шкала значений ничего не значит

            if (axes)
            {
                FormattedText label = tickLabels[i];
                dc.DrawText(label, new Point(plot.Left - 8 - label.Width, y - label.Height / 2));
            }
            else if (i == 0 || i == ticks.Count - 1)
            {
                // В компактном виде — только верхняя и нижняя границы, над линией у правого края.
                FormattedText label = tickLabels[i];
                double ly = i == 0 ? y - label.Height : y + 1;
                dc.DrawText(label, new Point(plot.Right - label.Width - 2, Math.Clamp(ly, 0, height - label.Height)));
            }
        }

        // ---- Время ----
        bool rolling = Capacity > 0;
        double sample = Math.Max(SampleSeconds, 0.001);
        int slots = Math.Max(2, rolling ? Math.Max(count, Capacity) : count);
        double span = (slots - 1) * sample; // секунд по ширине
        if (!rolling && count < 2)
            span = 60;

        double X(int index) => rolling
            ? plot.Right - (count - 1 - index) / (double)(slots - 1) * plot.Width
            : plot.Left + index * sample / span * plot.Width;

        if (axes)
        {
            int maxLabels = Math.Max(2, (int)(plot.Width / 80));
            int timeStep = TimeSteps.FirstOrDefault(s => span / s <= maxLabels, TimeSteps[^1]);
            for (int s = 0; s <= span + 1e-6; s += timeStep)
            {
                double x = Snap(rolling ? plot.Right - s / span * plot.Width : plot.Left + s / span * plot.Width);
                if (s > 0 && x > plot.Left + 1 && x < plot.Right - 1)
                    dc.DrawLine(faintGridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

                FormattedText label = Text(rolling ? AgoLabel(s) : ElapsedLabel(s), labelBrush);
                double lx = Math.Clamp(x - label.Width / 2, 0, width - label.Width);
                dc.DrawText(label, new Point(lx, plot.Bottom + 5));
            }
        }

        // ---- Данные ----
        if (count == 0)
        {
            if (!string.IsNullOrEmpty(EmptyText))
            {
                FormattedText empty = Text(EmptyText, labelBrush, 12);
                dc.DrawText(empty, new Point(plot.Left + (plot.Width - empty.Width) / 2, plot.Top + (plot.Height - empty.Height) / 2));
            }

            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left - 4, plot.Top - 4, plot.Width + 8, plot.Height + 8)));
        Point At(int i) => new(X(i), Y(Math.Clamp(values[i], niceLo, niceHi)));

        if (count >= 2)
        {
            Brush area = Fill ?? new LinearGradientBrush(
                Color.FromArgb(0x50, strokeColor.R, strokeColor.G, strokeColor.B),
                Color.FromArgb(0x00, strokeColor.R, strokeColor.G, strokeColor.B),
                new Point(0, plot.Top), new Point(0, plot.Bottom)) { MappingMode = BrushMappingMode.Absolute };

            var areaGeometry = new StreamGeometry();
            using (StreamGeometryContext ctx = areaGeometry.Open())
            {
                ctx.BeginFigure(new Point(X(0), plot.Bottom), isFilled: true, isClosed: true);
                for (int i = 0; i < count; i++)
                    ctx.LineTo(At(i), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(X(count - 1), plot.Bottom), isStroked: false, isSmoothJoin: false);
            }

            areaGeometry.Freeze();
            dc.DrawGeometry(area, null, areaGeometry);

            var line = new StreamGeometry();
            using (StreamGeometryContext ctx = line.Open())
            {
                ctx.BeginFigure(At(0), isFilled: false, isClosed: false);
                for (int i = 1; i < count; i++)
                    ctx.LineTo(At(i), isStroked: true, isSmoothJoin: true);
            }

            line.Freeze();
            var pen = new Pen(stroke, StrokeThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);
        }

        // Последнее значение — точкой.
        dc.DrawEllipse(stroke, null, At(count - 1), axes ? 3.5 : 2.5, axes ? 3.5 : 2.5);
        dc.Pop();

        // ---- Подсказка при наведении ----
        if (_hover is not { } hover || hover.X < plot.Left - 4 || hover.X > plot.Right + 4)
            return;

        int index = rolling
            ? count - 1 - (int)Math.Round((plot.Right - hover.X) / plot.Width * (slots - 1))
            : (int)Math.Round((hover.X - plot.Left) / plot.Width * span / sample);
        if (index < 0 || index >= count)
            return;

        Point point = At(index);
        var hoverPen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, strokeColor.R, strokeColor.G, strokeColor.B)), 1);
        dc.DrawLine(hoverPen, new Point(Snap(point.X), plot.Top), new Point(Snap(point.X), plot.Bottom));
        dc.DrawEllipse(Resource("CardBrush", Color.FromRgb(0x18, 0x1C, 0x24)), new Pen(stroke, 2), point, 4, 4);

        int valueDecimals = Math.Max(decimals, Math.Abs(values[index]) < 100 ? 1 : 0);
        double seconds = rolling ? (count - 1 - index) * sample : index * sample;
        string time = rolling ? AgoLabel((int)Math.Round(seconds)) : ElapsedLabel((int)Math.Round(seconds));
        FormattedText tip = Text($"{FormatNumber(values[index], valueDecimals)}{unit}  ·  {time}", Resource("TextBrush", Colors.White), 12);
        double boxWidth = tip.Width + 16;
        double boxHeight = tip.Height + 8;
        double bx = point.X + 10 + boxWidth <= width ? point.X + 10 : point.X - 10 - boxWidth;
        double by = Math.Clamp(point.Y - boxHeight - 8, 0, Math.Max(0, height - boxHeight));
        var box = new Rect(Math.Max(0, bx), by, boxWidth, boxHeight);
        dc.DrawRoundedRectangle(Resource("CardHoverBrush", Color.FromRgb(0x20, 0x26, 0x31)), new Pen(gridBrush, 1), box, 6, 6);
        dc.DrawText(tip, new Point(box.Left + 8, box.Top + 4));
    }

    private (double Lo, double Hi) ValueRange(IReadOnlyList<double> values)
    {
        if (Unit == "%")
            return (0, 100);

        double lo = double.MaxValue, hi = double.MinValue;
        foreach (double v in values)
        {
            if (!double.IsFinite(v))
                continue;
            lo = Math.Min(lo, v);
            hi = Math.Max(hi, v);
        }

        if (lo > hi)
            return (0, Math.Max(1, MinimumRange));

        double minimumRange = Math.Max(MinimumRange, 1e-6);
        if (hi - lo < minimumRange)
        {
            double middle = (hi + lo) / 2;
            lo = middle - minimumRange / 2;
            hi = middle + minimumRange / 2;
            // Температуры, частоты и ватты не бывают отрицательными: не уводим шкалу ниже нуля без нужды.
            if (lo < 0 && values.All(v => v >= 0))
            {
                hi -= lo;
                lo = 0;
            }
        }

        return (lo, hi);
    }

    /// <summary>«Красивый» шаг сетки: 1, 2, 2.5, 5 × 10ⁿ.</summary>
    private static double NiceStep(double raw)
    {
        if (!(raw > 0) || !double.IsFinite(raw))
            return 1;
        double exponent = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double fraction = raw / exponent;
        double nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10;
        return nice * exponent;
    }

    private static int Decimals(double step)
    {
        int decimals = 0;
        while (decimals < 3 && Math.Abs(step * Math.Pow(10, decimals) - Math.Round(step * Math.Pow(10, decimals))) > 1e-6)
            decimals++;
        return decimals;
    }

    private static string FormatNumber(double value, int decimals) =>
        value.ToString("N" + decimals, Russian);

    private static string AgoLabel(int seconds) => seconds switch
    {
        0 => "сейчас",
        < 60 => $"−{seconds} с",
        _ when seconds % 60 == 0 => $"−{seconds / 60} мин",
        _ => $"−{seconds / 60}:{seconds % 60:00}",
    };

    private static string ElapsedLabel(int seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
        : $"{seconds / 60}:{seconds % 60:00}";

    private static double Snap(double value) => Math.Round(value) + 0.5;

    private Brush Resource(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(Sparkline),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}
