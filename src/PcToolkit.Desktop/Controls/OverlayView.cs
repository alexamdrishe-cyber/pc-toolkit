using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PcToolkit.Core.Overlay;
using PcToolkit.Desktop.ViewModels;

namespace PcToolkit.Desktop.Controls;

/// <summary>
/// Оверлей: подписи, значения и графики рисуются напрямую, без шаблонов. Один и тот же элемент показывает
/// оверлей поверх игры и предпросмотр в настройках — столбиком или в одну строку, с обводкой букв и без.
/// </summary>
public sealed class OverlayView : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(OverlayContent), typeof(OverlayView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnSourceChanged));

    /// <summary>Пунктирная рамка и полупрозрачный фон — пока оверлей двигают мышью (за прозрачный фон не ухватиться).</summary>
    public static readonly DependencyProperty ShowFrameProperty = DependencyProperty.Register(
        nameof(ShowFrame), typeof(bool), typeof(OverlayView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double BigScale = 2.3;
    private const double MutedScale = 0.8;
    private const double UnitScale = 0.85;

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Brush TrackBrush = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    private static readonly Brush BadgeBrush = Frozen(Color.FromRgb(0xF2, 0x64, 0x5F));
    private static readonly Pen GuidePen = FrozenPen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1, DashStyles.Dash);
    private static readonly Pen FramePen = FrozenPen(Color.FromRgb(0x5B, 0x9C, 0xFF), 1.5, DashStyles.Dash);
    private static readonly Pen BorderPen = FrozenPen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 1, null);

    // Ширина значения каждой ячейки только растёт (до смены настроек): «99» → «100» не дёргает оверлей у правого края.
    private readonly Dictionary<int, double> _valueWidths = new();
    private OverlayContent? _subscribed;
    private int _layoutVersion = -1;
    private Scene? _scene;

    public OverlayView()
    {
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    public OverlayContent? Source
    {
        get => (OverlayContent?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool ShowFrame
    {
        get => (bool)GetValue(ShowFrameProperty);
        set => SetValue(ShowFrameProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (OverlayView)d;
        view.Unsubscribe();
        if (view.IsLoaded)
            view.Subscribe();
    }

    private void Subscribe()
    {
        if (_subscribed is not null || Source is not { } source)
            return;
        source.Changed += OnContentChanged;
        _subscribed = source;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Unsubscribe()
    {
        if (_subscribed is null)
            return;
        _subscribed.Changed -= OnContentChanged;
        _subscribed = null;
    }

    private void OnContentChanged(object? sender, EventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _scene = Build();
        return _scene?.Size ?? new Size(0, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_scene is not { } scene)
            return;

        var bounds = new Rect(scene.Size);
        Brush? background = scene.Background;
        if (ShowFrame && (background is null || scene.BackgroundAlpha < 0x40))
            background = Frozen(Color.FromArgb(0x55, 0x10, 0x14, 0x1C));
        if (background is not null)
            dc.DrawRoundedRectangle(background, scene.Border, Inset(bounds, 0.5), scene.Radius, scene.Radius);

        foreach ((Rect rect, OverlayCell cell) in scene.Graphs)
            DrawGraph(dc, rect, cell, scene.Em);

        if (scene.Badge is { } badge)
        {
            dc.DrawRoundedRectangle(BadgeBrush, null, badge.Rect, 4, 4);
            dc.DrawText(badge.Text, badge.Origin);
        }

        foreach ((FormattedText text, Point origin) in scene.Texts)
        {
            if (scene.Outline is not null)
                dc.DrawGeometry(null, scene.Outline, text.BuildGeometry(origin));
            dc.DrawText(text, origin);
        }

        if (ShowFrame)
            dc.DrawRoundedRectangle(null, FramePen, Inset(bounds, 0.75), scene.Radius, scene.Radius);
    }

    // ----- Раскладка -----

    private Scene? Build()
    {
        if (Source is not { } source)
            return null;

        if (source.LayoutVersion != _layoutVersion)
        {
            _valueWidths.Clear();
            _layoutVersion = source.LayoutVersion;
        }

        IReadOnlyList<OverlayCell> cells = source.Cells();
        if (cells.Count == 0 && !source.IsRecording)
            return null;

        OverlayLayout layout = source.Layout;
        OverlayStyle style = layout.Style;
        double em = style.FontSize;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(SafeFont(style.FontFamily), FontStyles.Normal, Weight(style.FontWeight), FontStretches.Normal);
        Brush labelBrush = Frozen(OverlayContent.ToColor(style.LabelColor));
        bool horizontal = layout.IsHorizontal;

        FormattedText Text(string text, double size, Brush brush, Typeface? face = null) =>
            new(text, Culture, FlowDirection.LeftToRight, face ?? typeface, size, brush, null,
                size < 20 ? TextFormattingMode.Display : TextFormattingMode.Ideal, pixelsPerDip);

        double labelGap = em * 0.3;
        double unitGap = em * 0.2;
        double inlineGraphWidth = Math.Round(em * 4.5);

        // 1. Каждая ячейка: подпись, значение, единица.
        var measured = new List<Measured>(cells.Count);
        for (int i = 0; i < cells.Count; i++)
        {
            OverlayCell cell = cells[i];
            double valueSize = cell.Big ? em * BigScale : cell.Muted ? em * MutedScale : em;
            var m = new Measured(cell)
            {
                Label = cell.Label.Length > 0 ? Text(cell.Label, cell.Muted ? em * MutedScale : em, labelBrush) : null,
                Value = Text(cell.Value, valueSize, Frozen(cell.ValueColor)),
                Unit = cell.Unit.Length > 0 ? Text(cell.Unit, cell.Big ? em : em * UnitScale, labelBrush) : null,
            };

            double width = m.Value.WidthIncludingTrailingWhitespace;
            m.ValueWidth = _valueWidths[i] = Math.Max(width, _valueWidths.GetValueOrDefault(i));
            m.Width = (m.Label is null ? 0 : m.Label.WidthIncludingTrailingWhitespace + labelGap)
                      + m.ValueWidth
                      + (m.Unit is null ? 0 : unitGap + m.Unit.WidthIncludingTrailingWhitespace)
                      + (horizontal && cell.Graph != OverlayGraphKind.None ? unitGap * 2 + inlineGraphWidth : 0);
            foreach (FormattedText part in m.Parts)
            {
                m.Ascent = Math.Max(m.Ascent, part.Baseline);
                m.Descent = Math.Max(m.Descent, part.Height - part.Baseline);
            }

            measured.Add(m);
        }

        // 2. Строки: столбиком — «в одной строке с предыдущим» продолжает строку; в строку — всё в одной.
        var rows = new List<List<Measured>>();
        foreach (Measured m in measured)
        {
            if (rows.Count == 0 || (!horizontal && !m.Cell.SameLine))
                rows.Add(new List<Measured>());
            rows[^1].Add(m);
        }

        if (rows.Count == 0)
            rows.Add(new List<Measured>());

        FormattedText? badgeText = source.IsRecording
            ? Text(source.RecordingText, em * 0.78, Brushes.White, new Typeface(SafeFont(style.FontFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal))
            : null;
        Size badgeSize = badgeText is null ? default
            : new Size(badgeText.WidthIncludingTrailingWhitespace + em * 0.9, badgeText.Height + em * 0.24);

        double Gap(Measured m) => horizontal ? (m.Cell.SameLine ? em * 0.5 : em * 1.1) : em * 0.55;

        double RowWidth(List<Measured> row, int index)
        {
            double width = 0;
            for (int i = 0; i < row.Count; i++)
                width += (i > 0 ? Gap(row[i]) : 0) + row[i].Width;
            if (index == 0 && badgeText is not null)
                width += (row.Count > 0 ? em * 0.8 : 0) + badgeSize.Width;
            return width;
        }

        double padX = Math.Round(em * 0.7);
        double padY = Math.Round(em * 0.45);
        double graphHeight = Math.Round(em * 2.1);
        bool anyGraph = !horizontal && measured.Any(m => m.Cell.Graph != OverlayGraphKind.None);
        double contentWidth = rows.Select(RowWidth).DefaultIfEmpty(0).Max();
        if (anyGraph)
            contentWidth = Math.Max(contentWidth, Math.Round(em * 9));

        // 3. Расстановка.
        var scene = new Scene { Em = em };
        double y = padY;
        for (int r = 0; r < rows.Count; r++)
        {
            List<Measured> row = rows[r];
            double ascent = row.Select(m => m.Ascent).DefaultIfEmpty(0).Max();
            double descent = row.Select(m => m.Descent).DefaultIfEmpty(0).Max();
            if (r == 0 && badgeText is not null && row.Count == 0)
            {
                ascent = badgeSize.Height;
                descent = 0;
            }

            double baseline = y + ascent;
            double x = padX;
            for (int i = 0; i < row.Count; i++)
            {
                Measured m = row[i];
                if (i > 0)
                    x += Gap(m);
                double start = x;
                if (m.Label is not null)
                {
                    scene.Texts.Add((m.Label, new Point(x, baseline - m.Label.Baseline)));
                    x += m.Label.WidthIncludingTrailingWhitespace + labelGap;
                }

                scene.Texts.Add((m.Value!, new Point(x, baseline - m.Value!.Baseline)));
                x += m.ValueWidth;
                if (m.Unit is not null)
                {
                    x += unitGap;
                    scene.Texts.Add((m.Unit, new Point(x, baseline - m.Unit.Baseline)));
                    x += m.Unit.WidthIncludingTrailingWhitespace;
                }

                if (horizontal && m.Cell.Graph != OverlayGraphKind.None)
                {
                    x += unitGap * 2;
                    double height = Math.Round(em * 1.05);
                    scene.Graphs.Add((new Rect(x, baseline - em * 0.85, inlineGraphWidth, height), m.Cell));
                    x += inlineGraphWidth;
                }

                x = start + m.Width;
            }

            if (r == 0 && badgeText is not null)
            {
                if (row.Count > 0)
                    x += em * 0.8;
                double middle = row.Count > 0 ? baseline - ascent / 2 + descent / 2 : y + badgeSize.Height / 2;
                var rect = new Rect(x, Math.Round(middle - badgeSize.Height / 2), badgeSize.Width, badgeSize.Height);
                scene.Badge = new Badge(rect, badgeText, new Point(rect.X + em * 0.45, rect.Y + em * 0.12));
            }

            y = baseline + descent;
            if (!horizontal)
            {
                foreach (Measured m in row.Where(m => m.Cell.Graph != OverlayGraphKind.None))
                {
                    y += Math.Round(em * 0.3);
                    scene.Graphs.Add((new Rect(padX, y, contentWidth, graphHeight), m.Cell));
                    y += graphHeight;
                }

                if (r < rows.Count - 1)
                    y += Math.Round(em * 0.12);
            }
        }

        scene.Size = new Size(Math.Ceiling(contentWidth + padX * 2), Math.Ceiling(y + padY));
        scene.Radius = Math.Min(style.CornerRadius, Math.Min(scene.Size.Width, scene.Size.Height) / 2);
        scene.BackgroundAlpha = (byte)Math.Round(style.BackgroundOpacity * 255);
        if (scene.BackgroundAlpha > 0)
        {
            Color background = OverlayContent.ToColor(style.BackgroundColor);
            scene.Background = Frozen(Color.FromArgb(scene.BackgroundAlpha, background.R, background.G, background.B));
            scene.Border = style.BackgroundOpacity >= 0.15 ? BorderPen : null;
        }

        if (style.Outline > 0)
        {
            Color outline = OverlayContent.ToColor(style.OutlineColor);
            var pen = new Pen(Frozen(outline), style.Outline * 2) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            scene.Outline = pen;
        }

        return scene;
    }

    // ----- Графики -----

    private static void DrawGraph(DrawingContext dc, Rect rect, OverlayCell cell, double em)
    {
        dc.DrawRoundedRectangle(TrackBrush, null, rect, 3, 3);
        double[] values = cell.GraphValues;
        if (values.Length < 2 || rect.Width < 8 || rect.Height < 6)
            return;

        Rect area = Inset(rect, 2);
        double min;
        double max;
        if (cell.Graph == OverlayGraphKind.Frames)
        {
            double top = 0;
            foreach (double v in values)
            {
                if (double.IsFinite(v))
                    top = Math.Max(top, v);
            }

            min = 0;
            max = Math.Max(1000.0 / 30, top * 1.1);
            double guide = area.Bottom - 1000.0 / 60 / max * area.Height;
            dc.DrawLine(GuidePen, new Point(area.Left, guide), new Point(area.Right, guide));
        }
        else
        {
            double lo = double.MaxValue;
            double hi = double.MinValue;
            foreach (double v in values)
            {
                if (!double.IsFinite(v))
                    continue;
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }

            if (lo > hi)
                return;

            double pad = Math.Max(hi - lo, Math.Max(Math.Abs(hi) * 0.05, 1e-6)) * 0.2;
            min = cell.GraphMin ?? lo - pad;
            max = cell.GraphMax ?? hi + pad;
            if (cell.GraphMin is double fixedMin && cell.GraphMax is null)
                max = Math.Max(hi + pad, fixedMin + 1e-6);
            if (max - min < 1e-9)
                max = min + 1;
        }

        List<Point?> points = Points(values, cell, area, min, max);
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (StreamGeometryContext stroke = line.Open())
        using (StreamGeometryContext area2 = fill.Open())
        {
            var segment = new List<Point>();
            foreach (Point? point in points.Append(null))
            {
                if (point is Point p)
                {
                    segment.Add(p);
                    continue;
                }

                if (segment.Count > 1)
                {
                    stroke.BeginFigure(segment[0], isFilled: false, isClosed: false);
                    stroke.PolyLineTo(segment.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
                    if (cell.Graph == OverlayGraphKind.Series)
                    {
                        area2.BeginFigure(new Point(segment[0].X, area.Bottom), isFilled: true, isClosed: true);
                        area2.PolyLineTo(segment.Append(new Point(segment[^1].X, area.Bottom)).ToList(), isStroked: false, isSmoothJoin: false);
                    }
                }

                segment.Clear();
            }
        }

        line.Freeze();
        fill.Freeze();
        Color color = cell.GraphColor;
        if (cell.Graph == OverlayGraphKind.Series)
            dc.DrawGeometry(Frozen(Color.FromArgb(0x38, color.R, color.G, color.B)), null, fill);
        var pen = new Pen(Frozen(color), Math.Max(1.2, em * 0.1)) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    /// <summary>Точки линии; null — разрыв (значения не было). Кадров больше, чем пикселей, — берётся худший кадр столбца.</summary>
    private static List<Point?> Points(double[] values, OverlayCell cell, Rect area, double min, double max)
    {
        double Y(double v) => area.Bottom - (Math.Clamp(v, min, max) - min) / (max - min) * area.Height;
        var points = new List<Point?>();
        int columns = Math.Max(2, (int)area.Width);

        if (cell.Graph == OverlayGraphKind.Frames && values.Length > columns)
        {
            for (int c = 0; c < columns; c++)
            {
                int from = c * values.Length / columns;
                int to = Math.Max(from + 1, (c + 1) * values.Length / columns);
                double worst = double.NaN;
                for (int i = from; i < to && i < values.Length; i++)
                {
                    if (double.IsFinite(values[i]) && !(worst >= values[i]))
                        worst = values[i];
                }

                points.Add(double.IsFinite(worst) ? new Point(area.Left + c * area.Width / (columns - 1), Y(worst)) : null);
            }

            return points;
        }

        int slots = Math.Max(Math.Max(cell.GraphSlots, values.Length), 2);
        int offset = slots - values.Length; // новые значения — у правого края
        double step = area.Width / (slots - 1);
        for (int i = 0; i < values.Length; i++)
            points.Add(double.IsFinite(values[i]) ? new Point(area.Left + (offset + i) * step, Y(values[i])) : null);
        return points;
    }

    // ----- Помощники -----

    private static FontFamily SafeFont(string name)
    {
        try
        {
            return new FontFamily(name);
        }
        catch (ArgumentException)
        {
            return new FontFamily("Segoe UI");
        }
    }

    private static FontWeight Weight(string weight) => weight switch
    {
        OverlayStyle.Normal => FontWeights.Normal,
        OverlayStyle.Bold => FontWeights.Bold,
        _ => FontWeights.SemiBold,
    };

    private static Rect Inset(Rect rect, double by) =>
        rect.Width > by * 2 && rect.Height > by * 2 ? new Rect(rect.X + by, rect.Y + by, rect.Width - by * 2, rect.Height - by * 2) : rect;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness, DashStyle? dash)
    {
        var pen = new Pen(Frozen(color), thickness);
        if (dash is not null)
            pen.DashStyle = dash;
        pen.Freeze();
        return pen;
    }

    private sealed class Measured(OverlayCell cell)
    {
        public OverlayCell Cell { get; } = cell;
        public FormattedText? Label { get; init; }
        public FormattedText? Value { get; init; }
        public FormattedText? Unit { get; init; }
        public double ValueWidth { get; set; }
        public double Width { get; set; }
        public double Ascent { get; set; }
        public double Descent { get; set; }

        public IEnumerable<FormattedText> Parts
        {
            get
            {
                if (Label is not null)
                    yield return Label;
                if (Value is not null)
                    yield return Value;
                if (Unit is not null)
                    yield return Unit;
            }
        }
    }

    private sealed record Badge(Rect Rect, FormattedText Text, Point Origin);

    private sealed class Scene
    {
        public Size Size { get; set; }
        public double Em { get; init; }
        public double Radius { get; set; }
        public byte BackgroundAlpha { get; set; }
        public Brush? Background { get; set; }
        public Pen? Border { get; set; }
        public Pen? Outline { get; set; }
        public Badge? Badge { get; set; }
        public List<(FormattedText Text, Point Origin)> Texts { get; } = new();
        public List<(Rect Rect, OverlayCell Cell)> Graphs { get; } = new();
    }
}
