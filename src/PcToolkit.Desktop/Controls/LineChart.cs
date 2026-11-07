using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PcToolkit.Core.Sensors;

namespace PcToolkit.Desktop.Controls;

/// <summary>Линия графика: значения уже в единицах шкалы (°C, %, ГГц…), от старых к новым.</summary>
public sealed class ChartLine
{
    public IReadOnlyList<double> Values { get; init; } = Array.Empty<double>();

    public Color Color { get; init; } = Color.FromRgb(0x4C, 0x8D, 0xFF);

    /// <summary>Заливка под линией (обычно только у главной линии).</summary>
    public bool Fill { get; init; }

    /// <summary>Линия по правой шкале (другая единица: загрузка рядом с температурой).</summary>
    public bool RightAxis { get; init; }

    public double Thickness { get; init; } = 1.7;

    /// <summary>Подпись для подсказки при наведении.</summary>
    public string Label { get; init; } = "";

    /// <summary>Как показать значение в подсказке: «66,1 °C».</summary>
    public Func<double, string>? Format { get; init; }

    /// <summary>Шаг между значениями в секундах (из истории); 0 — взять общий шаг графика.</summary>
    public double SampleSeconds { get; init; }

    /// <summary>Сквозной номер первого значения (<see cref="HistoryWindow.FirstIndex"/>): к нему привязаны группы точек.</summary>
    public long FirstIndex { get; init; }
}

public enum ChartAxisMode
{
    /// <summary>Шкала значений слева, время снизу по каждой минуте.</summary>
    Full,
    /// <summary>Маленький график в плитке: значения справа, время только «−5 мин» и «сейчас».</summary>
    Compact,
}

/// <summary>
/// График нескольких датчиков за последние минуты: сетка, подписи шкал с единицами (слева и, если нужно, справа),
/// шкала времени, пунктир порога, заливка под главной линией и подсказка со всеми значениями при наведении.
/// Последнее значение всегда у правого края — «сейчас».
/// </summary>
public sealed class LineChart : FrameworkElement
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static readonly DependencyProperty LinesProperty = Register(nameof(Lines), typeof(IReadOnlyList<ChartLine>), null);
    public static readonly DependencyProperty LeftRangeProperty = Register(nameof(LeftRange), typeof(ChartRange), new ChartRange(0, 100));
    public static readonly DependencyProperty RightRangeProperty = Register(nameof(RightRange), typeof(ChartRange?), null);
    public static readonly DependencyProperty LeftUnitProperty = Register(nameof(LeftUnit), typeof(string), "");
    public static readonly DependencyProperty RightUnitProperty = Register(nameof(RightUnit), typeof(string), "");
    public static readonly DependencyProperty LeftLabelBrushProperty = Register(nameof(LeftLabelBrush), typeof(Brush), null);
    public static readonly DependencyProperty RightLabelBrushProperty = Register(nameof(RightLabelBrush), typeof(Brush), null);
    public static readonly DependencyProperty WindowSecondsProperty = Register(nameof(WindowSeconds), typeof(int), 300);
    public static readonly DependencyProperty SampleSecondsProperty = Register(nameof(SampleSeconds), typeof(double), 1.0);
    public static readonly DependencyProperty AxisModeProperty = Register(nameof(AxisMode), typeof(ChartAxisMode), ChartAxisMode.Full);
    public static readonly DependencyProperty LimitProperty = Register(nameof(Limit), typeof(double), double.NaN);
    public static readonly DependencyProperty EmptyTextProperty = Register(nameof(EmptyText), typeof(string), "Данные появятся через пару секунд");

    private Point? _hover;

    public LineChart()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public IReadOnlyList<ChartLine>? Lines { get => (IReadOnlyList<ChartLine>?)GetValue(LinesProperty); set => SetValue(LinesProperty, value); }

    public ChartRange LeftRange { get => (ChartRange)GetValue(LeftRangeProperty); set => SetValue(LeftRangeProperty, value); }

    /// <summary>Правая шкала; null — её нет.</summary>
    public ChartRange? RightRange { get => (ChartRange?)GetValue(RightRangeProperty); set => SetValue(RightRangeProperty, value); }

    public string LeftUnit { get => (string)GetValue(LeftUnitProperty); set => SetValue(LeftUnitProperty, value); }

    public string RightUnit { get => (string)GetValue(RightUnitProperty); set => SetValue(RightUnitProperty, value); }

    public Brush? LeftLabelBrush { get => (Brush?)GetValue(LeftLabelBrushProperty); set => SetValue(LeftLabelBrushProperty, value); }

    public Brush? RightLabelBrush { get => (Brush?)GetValue(RightLabelBrushProperty); set => SetValue(RightLabelBrushProperty, value); }

    /// <summary>Сколько секунд по ширине графика: 60, 300, 1800.</summary>
    public int WindowSeconds { get => (int)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }

    public double SampleSeconds { get => (double)GetValue(SampleSecondsProperty); set => SetValue(SampleSecondsProperty, value); }

    public ChartAxisMode AxisMode { get => (ChartAxisMode)GetValue(AxisModeProperty); set => SetValue(AxisModeProperty, value); }

    /// <summary>Пунктир порога по левой шкале; NaN — без него.</summary>
    public double Limit { get => (double)GetValue(LimitProperty); set => SetValue(LimitProperty, value); }

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
        if (width < 40 || height < 24)
            return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        bool compact = AxisMode == ChartAxisMode.Compact;
        double fontSize = 10.5;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Brush muted = Resource("TextMutedBrush", Color.FromRgb(0x8C, 0x95, 0xA8));
        Brush leftBrush = LeftLabelBrush ?? muted;
        Brush rightBrush = RightLabelBrush ?? muted;
        Brush gridBrush = Resource("ChartGridBrush", Color.FromRgb(0x26, 0x2D, 0x3A));
        Brush minorBrush = Resource("ChartMinorGridBrush", Color.FromRgb(0x1C, 0x22, 0x2C));

        FormattedText Text(string text, Brush brush) =>
            new(text, Russian, FlowDirection.LeftToRight, typeface, fontSize, brush, pixelsPerDip);

        ChartRange left = LeftRange;
        ChartRange? right = RightRange;
        string leftUnit = string.IsNullOrEmpty(LeftUnit) ? "" : " " + LeftUnit;
        string rightUnit = string.IsNullOrEmpty(RightUnit) ? "" : " " + RightUnit;

        var leftLabels = new[]
        {
            Text(left.Label(left.Max) + leftUnit, leftBrush),
            Text(left.Label(left.Mid), leftBrush),
            Text(left.Label(left.Min), leftBrush),
        };
        FormattedText[]? rightLabels = right is { } r
            ? new[] { Text(r.Label(r.Max) + rightUnit, rightBrush), Text(r.Label(r.Mid), rightBrush), Text(r.Label(r.Min), rightBrush) }
            : null;

        double timeHeight = Text("0", muted).Height + 3;
        double plotLeft = compact ? 0 : leftLabels.Max(l => l.Width) + 8;
        double plotRight = width - (compact ? leftLabels.Max(l => l.Width) + 8 : rightLabels is null ? 4 : rightLabels.Max(l => l.Width) + 8);
        double plotTop = fontSize / 2 + 1;
        double plotBottom = height - timeHeight - 2;
        if (plotRight - plotLeft < 20 || plotBottom - plotTop < 10)
            return;

        var plot = new Rect(new Point(plotLeft, plotTop), new Point(plotRight, plotBottom));
        double Y(double value, ChartRange range) =>
            plot.Bottom - (Math.Clamp(value, range.Min, range.Max) - range.Min) / Math.Max(1e-9, range.Max - range.Min) * plot.Height;

        var gridPen = new Pen(gridBrush, 1);
        gridPen.Freeze();
        var minorPen = new Pen(minorBrush, 1);
        minorPen.Freeze();

        // Время: вертикальные линии по минутам (или по 15 с / 5 мин) и подписи.
        int window = Math.Max(10, WindowSeconds);
        int step = window <= 90 ? 15 : window <= 600 ? 60 : 300;
        for (int s = step; s < window; s += step)
        {
            double x = Snap(plot.Right - s / (double)window * plot.Width);
            dc.DrawLine(minorPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        if (compact)
        {
            DrawTime(dc, Text(Ago(window, window), muted), plot.Left, plot.Bottom, alignLeft: true);
            DrawTime(dc, Text("сейчас", muted), plot.Right, plot.Bottom, alignRight: true);
        }
        else
        {
            // Крайние подписи («сейчас» и «−5 мин») — всегда; промежуточные — только если не налезают
            // на соседние (в узкой карточке остаются, например, «−10» и «−20»).
            FormattedText first = Text(Ago(window, window), muted);
            double firstEnd = plot.Left + first.Width;
            DrawTime(dc, first, plot.Left, plot.Bottom, alignLeft: true);
            double lastStart = double.MaxValue;
            for (int s = 0; s < window; s += step)
            {
                double x = plot.Right - s / (double)window * plot.Width;
                FormattedText label = Text(Ago(s, window), muted);
                double labelStart = s == 0 ? x - label.Width : x - label.Width / 2;
                double labelEnd = labelStart + label.Width;
                if (s > 0 && (labelEnd + 8 > lastStart || labelStart - 8 < firstEnd))
                    continue;
                DrawTime(dc, label, x, plot.Bottom, alignRight: s == 0);
                lastStart = labelStart;
            }
        }

        // Значения: три линии сетки и подписи.
        double[] fractions = { 0, 0.5, 1 };
        for (int i = 0; i < 3; i++)
        {
            double y = Snap(plot.Top + fractions[i] * plot.Height);
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            FormattedText label = leftLabels[i];
            double ly = Math.Clamp(y - label.Height / 2, 0, height - label.Height);
            if (compact)
                dc.DrawText(label, new Point(plot.Right + 6, ly));
            else
                dc.DrawText(label, new Point(plot.Left - 6 - label.Width, ly));

            if (!compact && rightLabels is not null)
                dc.DrawText(rightLabels[i], new Point(plot.Right + 6, Math.Clamp(y - rightLabels[i].Height / 2, 0, height - rightLabels[i].Height)));
        }

        // Порог.
        if (double.IsFinite(Limit) && Limit > left.Min && Limit < left.Max)
        {
            var limitPen = new Pen(Resource("CriticalBrush", Color.FromRgb(0xF2, 0x64, 0x5F)), 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
            limitPen.Freeze();
            double y = Snap(Y(Limit, left));
            dc.DrawLine(limitPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        IReadOnlyList<ChartLine> lines = Lines ?? Array.Empty<ChartLine>();
        if (lines.All(l => l.Values.Count == 0))
        {
            if (!string.IsNullOrEmpty(EmptyText) && !compact)
            {
                FormattedText empty = Text(EmptyText, muted);
                dc.DrawText(empty, new Point(plot.Left + (plot.Width - empty.Width) / 2, plot.Top + (plot.Height - empty.Height) / 2));
            }

            return;
        }

        // Время по горизонтали: «сейчас» у правого края, точка на (сколько секунд назад) × (пикселей в секунду).
        double pxPerSecond = plot.Width / window;
        double SampleOf(ChartLine line) => line.SampleSeconds > 0 ? line.SampleSeconds : Math.Max(0.01, SampleSeconds);

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left - 2, plot.Top - 3, plot.Width + 4, plot.Height + 6)));
        foreach (ChartLine line in lines.Reverse())
        {
            int count = line.Values.Count;
            if (count == 0)
                continue;

            ChartRange range = line.RightAxis && right is { } rr ? rr : left;
            List<Point> points = Thin(line, SampleOf(line), pxPerSecond, plot.Right, window, v => Y(v, range));

            if (points.Count >= 2)
            {
                if (line.Fill)
                {
                    var area = new StreamGeometry();
                    using (StreamGeometryContext ctx = area.Open())
                    {
                        ctx.BeginFigure(new Point(points[0].X, plot.Bottom), isFilled: true, isClosed: true);
                        foreach (Point point in points)
                            ctx.LineTo(point, isStroked: false, isSmoothJoin: false);
                        ctx.LineTo(new Point(points[^1].X, plot.Bottom), isStroked: false, isSmoothJoin: false);
                    }

                    area.Freeze();
                    var fill = new LinearGradientBrush(
                        Color.FromArgb(0x48, line.Color.R, line.Color.G, line.Color.B),
                        Color.FromArgb(0x00, line.Color.R, line.Color.G, line.Color.B),
                        new Point(0, plot.Top), new Point(0, plot.Bottom)) { MappingMode = BrushMappingMode.Absolute };
                    fill.Freeze();
                    dc.DrawGeometry(fill, null, area);
                }

                var geometry = new StreamGeometry();
                using (StreamGeometryContext ctx = geometry.Open())
                {
                    ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
                    for (int i = 1; i < points.Count; i++)
                        ctx.LineTo(points[i], isStroked: true, isSmoothJoin: true);
                }

                geometry.Freeze();
                var pen = new Pen(new SolidColorBrush(line.Color), line.Thickness)
                {
                    LineJoin = PenLineJoin.Round,
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                pen.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }

            // Точка «сейчас» — самое свежее значение, без усреднения.
            dc.DrawEllipse(new SolidColorBrush(line.Color), null, new Point(plot.Right, Y(line.Values[count - 1], range)),
                compact ? 2.2 : 2.8, compact ? 2.2 : 2.8);
        }

        dc.Pop();

        // Подсказка: все линии в точке под курсором.
        if (_hover is not { } hover || hover.X < plot.Left || hover.X > plot.Right || compact)
            return;

        ChartLine? reference = lines.FirstOrDefault(l => l.Values.Count > 0);
        if (reference is null)
            return;

        double referenceSample = SampleOf(reference);
        double secondsAgo = (plot.Right - hover.X) / pxPerSecond;
        int referenceFromEnd = (int)Math.Round(secondsAgo / referenceSample);
        double snappedSeconds = referenceFromEnd * referenceSample;
        double hx = Snap(plot.Right - snappedSeconds * pxPerSecond);
        var hoverPen = new Pen(Resource("TextMutedBrush", Color.FromRgb(0x8C, 0x95, 0xA8)), 1) { DashStyle = DashStyles.Dot };
        hoverPen.Freeze();
        dc.DrawLine(hoverPen, new Point(hx, plot.Top), new Point(hx, plot.Bottom));

        var rows = new List<(FormattedText Text, Color Color)>();
        rows.Add((Text(Ago((int)Math.Round(snappedSeconds), window, exact: true), muted), Colors.Transparent));
        foreach (ChartLine line in lines)
        {
            int index = line.Values.Count - 1 - (int)Math.Round(snappedSeconds / SampleOf(line));
            if (index < 0 || index >= line.Values.Count)
                continue;
            double value = line.Values[index];
            string text = (line.Label.Length > 0 ? line.Label + ": " : "") + (line.Format?.Invoke(value) ?? value.ToString("0.#", Russian));
            rows.Add((Text(text, Resource("TextBrush", Colors.White)), line.Color));
            ChartRange range = line.RightAxis && right is { } rr2 ? rr2 : left;
            dc.DrawEllipse(Resource("CardBrush", Color.FromRgb(0x15, 0x19, 0x22)), new Pen(new SolidColorBrush(line.Color), 2),
                new Point(hx, Y(value, range)), 3.5, 3.5);
        }

        if (rows.Count <= 1)
            return;

        double boxWidth = rows.Max(r => r.Text.Width) + 30;
        double rowHeight = rows[0].Text.Height + 2;
        double boxHeight = rows.Count * rowHeight + 10;
        double bx = hx + 12 + boxWidth <= width ? hx + 12 : hx - 12 - boxWidth;
        var box = new Rect(Math.Max(0, bx), Math.Max(0, plot.Top), boxWidth, boxHeight);
        dc.DrawRoundedRectangle(Resource("CardHoverBrush", Color.FromRgb(0x1C, 0x22, 0x2C)), new Pen(gridBrush, 1), box, 6, 6);
        for (int i = 0; i < rows.Count; i++)
        {
            double ty = box.Top + 5 + i * rowHeight;
            if (rows[i].Color != Colors.Transparent)
                dc.DrawRectangle(new SolidColorBrush(rows[i].Color), null, new Rect(box.Left + 9, ty + rowHeight / 2 - 2, 10, 3));
            dc.DrawText(rows[i].Text, new Point(box.Left + (rows[i].Color == Colors.Transparent ? 9 : 24), ty));
        }
    }

    /// <summary>
    /// Точки линии в пределах окна. Если значений больше, чем пикселей, соседние усредняются примерно
    /// до одной точки на пиксель: линия получается ровной, без «щётки» из 7200 отрезков.
    /// Группы привязаны к сквозному номеру значения (<see cref="ChartGroups"/>), поэтому линия не дёргается.
    /// </summary>
    private static List<Point> Thin(ChartLine line, double sample, double pxPerSecond, double right, int window, Func<double, double> y)
    {
        int count = line.Values.Count;
        // На одно значение больше, чем помещается: линия доходит до левого края, лишнее обрежется.
        int take = Math.Min(count, (int)Math.Floor(window / sample) + 2);
        double stepPx = sample * pxPerSecond;
        int group = ChartGroups.Size(sample, pxPerSecond);
        return ChartGroups.Average(line.Values, count - take, line.FirstIndex, group)
            .Select(p => new Point(right - p.StepsFromEnd * stepPx, y(p.Value)))
            .ToList();
    }

    private static void DrawTime(DrawingContext dc, FormattedText label, double x, double plotBottom, bool alignLeft = false, bool alignRight = false)
    {
        double lx = alignLeft ? x : alignRight ? x - label.Width : x - label.Width / 2;
        dc.DrawText(label, new Point(lx, plotBottom + 3));
    }

    /// <summary>«сейчас», «−1», «−5 мин», «−30 с».</summary>
    private static string Ago(int seconds, int window, bool exact = false)
    {
        if (seconds <= 0)
            return "сейчас";
        if (exact)
            return seconds < 60 ? $"{seconds} с назад" : $"{seconds / 60}:{seconds % 60:00} назад";
        bool minutes = window > 90;
        string number = minutes ? (seconds / 60).ToString(Russian) : seconds.ToString(Russian);
        return seconds == window ? $"−{number} {(minutes ? "мин" : "с")}" : "−" + number;
    }

    private static double Snap(double value) => Math.Round(value) + 0.5;

    private Brush Resource(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(LineChart),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}
