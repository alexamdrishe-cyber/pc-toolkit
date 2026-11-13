using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PcToolkit.Core.Overlay;

/// <summary>
/// Как устроен оверлей: какие показатели и в каком порядке, как он выглядит и где стоит на экране.
/// Хранится в overlay.json — общий для всех игр и свой для отдельных игр (<see cref="OverlaySettings"/>).
/// </summary>
public sealed class OverlayLayout
{
    public const string Vertical = "Vertical";
    public const string Horizontal = "Horizontal";

    /// <summary>Vertical — показатели столбиком, Horizontal — в одну строку.</summary>
    public string Orientation { get; set; } = Vertical;

    /// <summary>Показатели по порядку.</summary>
    public List<OverlayItem> Items { get; set; } = new();

    public OverlayStyle Style { get; set; } = new();

    public OverlayPosition Position { get; set; } = new();

    [JsonIgnore]
    public bool IsHorizontal => Orientation == Horizontal;

    public OverlayLayout Clone() => new()
    {
        Orientation = Orientation,
        Items = Items.Select(item => item.Clone()).ToList(),
        Style = Style.Clone(),
        Position = Position.Clone(),
    };

    /// <summary>Привести к допустимым значениям: файл мог быть исправлен вручную или записан старой версией.</summary>
    public OverlayLayout Normalize()
    {
        Orientation = Orientation == Horizontal ? Horizontal : Vertical;
        Items = (Items ?? new List<OverlayItem>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Metric))
            .ToList();
        foreach (OverlayItem item in Items)
            item.Normalize();
        Style = (Style ?? new OverlayStyle()).Normalize();
        Position = (Position ?? new OverlayPosition()).Normalize();
        return this;
    }

    /// <summary>Оверлей по умолчанию: тот же, что был до настраиваемого оверлея.</summary>
    public static OverlayLayout CreateDefault() => FromLegacy("TopLeft", showDetails: true, showGraph: true, showSensors: true,
        showProcess: true, scale: 1, opacity: 0.78);

    /// <summary>Оверлей из прежних настроек (угол, галочки «что показывать», размер, плотность фона).</summary>
    public static OverlayLayout FromLegacy(string? corner, bool showDetails, bool showGraph, bool showSensors, bool showProcess,
        double scale, double opacity)
    {
        var items = new List<OverlayItem>
        {
            OverlayItem.For("fps", item =>
            {
                item.Big = true;
                item.Color = "#34D399";
            }),
        };

        if (showDetails)
        {
            items.Add(OverlayItem.For("fps.low1"));
            items.Add(OverlayItem.For("frametime", item =>
            {
                item.SameLine = true;
                item.Graph = showGraph;
            }));
        }
        else if (showGraph)
        {
            items.Add(OverlayItem.For("frametime", item => item.Graph = true));
        }

        if (showSensors)
        {
            items.Add(OverlayItem.For("cpu.load"));
            items.Add(OverlayItem.For("cpu.temp", item => item.SameLine = true));
            items.Add(OverlayItem.For("gpu.load"));
            items.Add(OverlayItem.For("gpu.temp", item => item.SameLine = true));
            items.Add(OverlayItem.For("gpu.power", item => item.SameLine = true));
        }

        if (showProcess)
            items.Add(OverlayItem.For("process"));

        return new OverlayLayout
        {
            Items = items,
            Style = new OverlayStyle
            {
                FontSize = Math.Round(OverlayStyle.DefaultFontSize * Math.Clamp(scale, 0.6, 1.6) * 2, MidpointRounding.AwayFromZero) / 2,
                BackgroundOpacity = Math.Clamp(opacity, 0, 1),
            },
            Position = OverlayPosition.Corner(corner ?? "TopLeft"),
        }.Normalize();
    }
}

/// <summary>Один показатель оверлея.</summary>
public sealed class OverlayItem
{
    /// <summary>Что показывать: ключ из <see cref="OverlayMetrics"/> («fps», «cpu.temp») или «sensor:…» — любой датчик.</summary>
    public string Metric { get; set; } = "";

    /// <summary>Подпись перед значением; null — подпись по умолчанию, пустая строка — без подписи.</summary>
    public string? Label { get; set; }

    public bool Visible { get; set; } = true;

    /// <summary>
    /// Столбиком — в одной строке с предыдущим показателем («CPU 45 % 62 °C»); в строку — вплотную к предыдущему.
    /// </summary>
    public bool SameLine { get; set; }

    /// <summary>График значения за последние секунды.</summary>
    public bool Graph { get; set; }

    /// <summary>Крупное значение (как FPS по умолчанию).</summary>
    public bool Big { get; set; }

    /// <summary>Цвет значения «#RRGGBB», пока оно в норме; null — общий цвет значений.</summary>
    public string? Color { get; set; }

    /// <summary>Порог «внимание» (жёлтый). Если «опасно» меньше «внимания», порогом считается падение (FPS), иначе — рост.</summary>
    public double? Warn { get; set; }

    /// <summary>Порог «опасно» (красный).</summary>
    public double? Critical { get; set; }

    /// <summary>Название датчика для списка в настройках: датчика может не оказаться в текущих данных.</summary>
    public string? Title { get; set; }

    public OverlayItem Clone() => (OverlayItem)MemberwiseClone();

    /// <summary>Новый показатель с порогами по умолчанию.</summary>
    public static OverlayItem For(string metric, Action<OverlayItem>? setup = null)
    {
        OverlayItem item = OverlayMetrics.Find(metric) is { } definition ? For(definition) : new OverlayItem { Metric = metric };
        setup?.Invoke(item);
        return item;
    }

    /// <summary>Новый показатель с порогами по умолчанию; у отдельного датчика запоминается его название.</summary>
    public static OverlayItem For(OverlayMetric metric) => new()
    {
        Metric = metric.Key,
        Warn = metric.Warn,
        Critical = metric.Critical,
        Title = metric.IsSensor ? metric.Title : null,
        Label = metric.IsSensor ? metric.Label : null,
    };

    internal void Normalize()
    {
        Metric = Metric.Trim();
        if (Label is { Length: > 40 })
            Label = Label[..40];
        Color = OverlayStyle.ValidColor(Color);
        if (Warn is double warn && !double.IsFinite(warn))
            Warn = null;
        if (Critical is double critical && !double.IsFinite(critical))
            Critical = null;
    }
}

/// <summary>Оформление оверлея.</summary>
public sealed partial class OverlayStyle
{
    public const double DefaultFontSize = 13;
    public const string Normal = "Normal";
    public const string SemiBold = "SemiBold";
    public const string Bold = "Bold";

    public const string DefaultValueColor = "#E8EBF1";
    public const string DefaultLabelColor = "#9AA3B5";
    public const string DefaultWarnColor = "#F5B544";
    public const string DefaultCriticalColor = "#F2645F";
    public const string DefaultBackgroundColor = "#0E1116";
    public const string DefaultGraphColor = "#34D399";

    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Размер обычного текста в пикселях при масштабе экрана 100 %; крупные значения — в 2,3 раза больше.</summary>
    public double FontSize { get; set; } = DefaultFontSize;

    /// <summary>Normal, SemiBold или Bold.</summary>
    public string FontWeight { get; set; } = SemiBold;

    /// <summary>Цвет значений, у которых нет своего.</summary>
    public string ValueColor { get; set; } = DefaultValueColor;

    /// <summary>Цвет подписей и единиц измерения.</summary>
    public string LabelColor { get; set; } = DefaultLabelColor;

    /// <summary>Цвет значения за порогом «внимание».</summary>
    public string WarnColor { get; set; } = DefaultWarnColor;

    /// <summary>Цвет значения за порогом «опасно».</summary>
    public string CriticalColor { get; set; } = DefaultCriticalColor;

    /// <summary>Цвет графиков у показателей без своего цвета.</summary>
    public string GraphColor { get; set; } = DefaultGraphColor;

    /// <summary>Обводка букв, пиксели: 0 — нет. Помогает читать текст без фона на светлой картинке.</summary>
    public double Outline { get; set; }

    public string OutlineColor { get; set; } = "#000000";

    public string BackgroundColor { get; set; } = DefaultBackgroundColor;

    /// <summary>Плотность фона: 0 — фона нет, 1 — непрозрачный.</summary>
    public double BackgroundOpacity { get; set; } = 0.78;

    /// <summary>Скругление углов фона, пиксели.</summary>
    public double CornerRadius { get; set; } = 8;

    public OverlayStyle Clone() => (OverlayStyle)MemberwiseClone();

    internal OverlayStyle Normalize()
    {
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily.Trim();
        FontSize = Finite(FontSize, DefaultFontSize, 8, 40);
        FontWeight = FontWeight is Normal or Bold ? FontWeight : SemiBold;
        ValueColor = ValidColor(ValueColor) ?? DefaultValueColor;
        LabelColor = ValidColor(LabelColor) ?? DefaultLabelColor;
        WarnColor = ValidColor(WarnColor) ?? DefaultWarnColor;
        CriticalColor = ValidColor(CriticalColor) ?? DefaultCriticalColor;
        GraphColor = ValidColor(GraphColor) ?? DefaultGraphColor;
        Outline = Finite(Outline, 0, 0, 4);
        OutlineColor = ValidColor(OutlineColor) ?? "#000000";
        BackgroundColor = ValidColor(BackgroundColor) ?? DefaultBackgroundColor;
        BackgroundOpacity = Finite(BackgroundOpacity, 0.78, 0, 1);
        CornerRadius = Finite(CornerRadius, 8, 0, 24);
        return this;
    }

    /// <summary>Цвет «#RRGGBB» в верхнем регистре или null, если строка — не цвет. «#RGB» и «RRGGBB» тоже принимаются.</summary>
    public static string? ValidColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string value = text.Trim().TrimStart('#');
        if (value.Length == 3 && HexDigits().IsMatch(value))
            value = string.Concat(value.Select(c => new string(c, 2)));
        if (value.Length == 8 && HexDigits().IsMatch(value))
            value = value[2..]; // #AARRGGBB: прозрачность задаётся отдельно
        return value.Length == 6 && HexDigits().IsMatch(value) ? "#" + value.ToUpperInvariant() : null;
    }

    /// <summary>Компоненты цвета «#RRGGBB».</summary>
    public static (byte R, byte G, byte B) Rgb(string color)
    {
        string value = (ValidColor(color) ?? DefaultValueColor)[1..];
        return (Parse(value[..2]), Parse(value[2..4]), Parse(value[4..]));

        static byte Parse(string hex) => byte.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static double Finite(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    [GeneratedRegex("^[0-9A-Fa-f]+$")]
    private static partial Regex HexDigits();
}

/// <summary>
/// Где стоит оверлей: от какого угла (или середины края) экрана и на каком расстоянии от него.
/// Расстояния — доли размера экрана, поэтому место сохраняется на мониторах с любым разрешением.
/// </summary>
public sealed class OverlayPosition
{
    /// <summary>Отступ от края по умолчанию: около 14 пикселей на экране 1920×1080.</summary>
    public const double DefaultMarginX = 0.0075;

    public const double DefaultMarginY = 0.013;

    public static IReadOnlyList<string> Anchors { get; } =
        new[] { "TopLeft", "TopCenter", "TopRight", "BottomLeft", "BottomCenter", "BottomRight" };

    /// <summary>TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter или BottomRight.</summary>
    public string Anchor { get; set; } = "TopLeft";

    /// <summary>Отступ по горизонтали — доля ширины экрана: от левого или правого края, а у «по центру» — сдвиг от середины.</summary>
    public double X { get; set; } = DefaultMarginX;

    /// <summary>Отступ от верхнего или нижнего края — доля высоты экрана.</summary>
    public double Y { get; set; } = DefaultMarginY;

    [JsonIgnore]
    public bool IsRight => Anchor.EndsWith("Right", StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsCenter => Anchor.EndsWith("Center", StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsBottom => Anchor.StartsWith("Bottom", StringComparison.Ordinal);

    public OverlayPosition Clone() => (OverlayPosition)MemberwiseClone();

    /// <summary>Угол (или середина края) с обычным отступом.</summary>
    public static OverlayPosition Corner(string anchor)
    {
        var position = new OverlayPosition { Anchor = anchor };
        position.Normalize();
        position.X = position.IsCenter ? 0 : DefaultMarginX;
        position.Y = DefaultMarginY;
        return position;
    }

    internal OverlayPosition Normalize()
    {
        Anchor = Anchors.FirstOrDefault(a => string.Equals(a, Anchor, StringComparison.OrdinalIgnoreCase)) ?? "TopLeft";
        X = double.IsFinite(X) ? Math.Clamp(X, IsCenter ? -0.5 : 0, IsCenter ? 0.5 : 1) : (IsCenter ? 0 : DefaultMarginX);
        Y = double.IsFinite(Y) ? Math.Clamp(Y, 0, 1) : DefaultMarginY;
        return this;
    }
}
