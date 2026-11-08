using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Fans;

/// <summary>Точка кривой: при такой температуре, °C, — такие обороты, %.</summary>
public readonly record struct CurvePoint(double Temperature, double Duty);

/// <summary>
/// Кривая «температура → обороты» из пяти точек по возрастанию температуры. Между точками — по прямой,
/// ниже первой — как у первой, выше последней — как у последней. Обороты по точкам не убывают:
/// горячее — значит не медленнее.
/// </summary>
public sealed class FanCurve
{
    public const int PointCount = 5;
    public const double MinTemperature = 20;
    public const double MaxTemperature = 100;

    /// <summary>Минимальный шаг температуры между соседними точками, °C.</summary>
    public const double MinGap = 2;

    [JsonConstructor]
    public FanCurve(IReadOnlyList<CurvePoint> points)
    {
        Points = Normalize(points);
    }

    public IReadOnlyList<CurvePoint> Points { get; }

    public static FanCurve Of(params (double Temperature, double Duty)[] points) =>
        new(points.Select(p => new CurvePoint(p.Temperature, p.Duty)).ToList());

    public double DutyAt(double temperature)
    {
        IReadOnlyList<CurvePoint> p = Points;
        if (temperature <= p[0].Temperature)
            return p[0].Duty;
        for (int i = 1; i < p.Count; i++)
        {
            if (temperature <= p[i].Temperature)
            {
                double t = (temperature - p[i - 1].Temperature) / (p[i].Temperature - p[i - 1].Temperature);
                return p[i - 1].Duty + t * (p[i].Duty - p[i - 1].Duty);
            }
        }

        return p[^1].Duty;
    }

    /// <summary>
    /// Кривая с передвинутой точкой: температура остаётся между соседями, обороты — не ниже левой
    /// и не выше правой точки (их можно двигать следом — точка «толкает» соседей по оборотам).
    /// </summary>
    public FanCurve WithPoint(int index, double temperature, double duty)
    {
        var points = Points.ToArray();
        double low = index == 0 ? MinTemperature : points[index - 1].Temperature + MinGap;
        double high = index == points.Length - 1 ? MaxTemperature : points[index + 1].Temperature - MinGap;
        points[index] = new CurvePoint(Math.Round(Math.Clamp(temperature, low, high)), Math.Round(Math.Clamp(duty, 0, 100)));
        for (int i = index + 1; i < points.Length; i++)
        {
            if (points[i].Duty < points[i - 1].Duty)
                points[i] = points[i] with { Duty = points[i - 1].Duty };
        }

        for (int i = index - 1; i >= 0; i--)
        {
            if (points[i].Duty > points[i + 1].Duty)
                points[i] = points[i] with { Duty = points[i + 1].Duty };
        }

        return new FanCurve(points);
    }

    public bool SameAs(FanCurve? other) =>
        other is not null && Points.Count == other.Points.Count && Points.Zip(other.Points).All(pair => pair.First == pair.Second);

    public override string ToString() =>
        string.Join(" ", Points.Select(p => $"{p.Temperature.ToString("0", CultureInfo.InvariantCulture)}°→{p.Duty.ToString("0", CultureInfo.InvariantCulture)}%"));

    /// <summary>Пять точек, температуры по возрастанию с шагом не меньше <see cref="MinGap"/>, обороты не убывают.</summary>
    private static IReadOnlyList<CurvePoint> Normalize(IReadOnlyList<CurvePoint>? input)
    {
        List<CurvePoint> points = (input ?? Array.Empty<CurvePoint>())
            .Where(p => !double.IsNaN(p.Temperature) && !double.IsNaN(p.Duty))
            .OrderBy(p => p.Temperature)
            .ToList();
        if (points.Count != PointCount)
            points = Resample(points);

        var result = new CurvePoint[PointCount];
        for (int i = 0; i < PointCount; i++)
        {
            double low = i == 0 ? MinTemperature : result[i - 1].Temperature + MinGap;
            double high = MaxTemperature - (PointCount - 1 - i) * MinGap;
            double temperature = Math.Round(Math.Clamp(points[i].Temperature, low, high));
            double duty = Math.Round(Math.Clamp(points[i].Duty, 0, 100));
            if (i > 0 && duty < result[i - 1].Duty)
                duty = result[i - 1].Duty;
            result[i] = new CurvePoint(temperature, duty);
        }

        return result;
    }

    /// <summary>Если точек не пять (старый или испорченный файл) — пять точек на той же кривой.</summary>
    private static List<CurvePoint> Resample(List<CurvePoint> points)
    {
        if (points.Count == 0)
            return FanPresets.SafeFallback.Points.ToList();

        var source = new FanCurve(points.Count >= PointCount ? points.Take(PointCount).ToList() : Pad(points));
        double from = points[0].Temperature;
        double to = Math.Max(points[^1].Temperature, from + (PointCount - 1) * MinGap);
        return Enumerable.Range(0, PointCount)
            .Select(i =>
            {
                double t = from + (to - from) * i / (PointCount - 1);
                return new CurvePoint(t, source.DutyAt(t));
            })
            .ToList();
    }

    private static List<CurvePoint> Pad(List<CurvePoint> points)
    {
        var padded = new List<CurvePoint>(points);
        while (padded.Count < PointCount)
        {
            CurvePoint last = padded[^1];
            padded.Add(new CurvePoint(Math.Min(MaxTemperature, last.Temperature + 10), last.Duty));
        }

        return padded;
    }
}

/// <summary>Что делает вентилятор материнской платы или контроллера.</summary>
public enum FanRole
{
    /// <summary>Кулер процессора — по температуре процессора.</summary>
    Cpu,

    /// <summary>Корпусной — по температуре видеокарты (она греется сильнее и чаще в играх).</summary>
    Case,

    /// <summary>Помпа водяного охлаждения: не трогаем, ей нужны постоянные обороты.</summary>
    Pump,

    /// <summary>Не управлять (разъём пустой или вентилятор лучше оставить BIOS).</summary>
    Ignore,

    /// <summary>Вентилятор видеокарты (NVIDIA через библиотеку датчиков; AMD — через драйвер, отдельно).</summary>
    Gpu,
}

/// <summary>Режим: два готовых и ручной с шаблонами.</summary>
public enum FanMode
{
    Quiet,
    Cool,
    Manual,
}

/// <summary>По какой температуре крутятся корпусные вентиляторы.</summary>
public enum CaseSource
{
    Gpu,
    Cpu,

    /// <summary>По большей из двух.</summary>
    Hottest,
}

/// <summary>Набор кривых: готовый режим или шаблон пользователя.</summary>
public sealed class FanTemplate
{
    public string Name { get; set; } = "";

    public FanCurve Gpu { get; set; } = FanPresets.QuietGpu;

    public FanCurve Cpu { get; set; } = FanPresets.QuietCpu;

    public FanCurve Case { get; set; } = FanPresets.QuietCase;

    /// <summary>Вентиляторы видеокарты AMD стоят, пока она холодная (Zero RPM).</summary>
    public bool GpuZeroRpm { get; set; } = true;

    public CaseSource CaseSource { get; set; } = CaseSource.Gpu;

    public FanTemplate Copy(string name) => new()
    {
        Name = name,
        Gpu = Gpu,
        Cpu = Cpu,
        Case = Case,
        GpuZeroRpm = GpuZeroRpm,
        CaseSource = CaseSource,
    };

    public FanCurve CurveFor(FanRole role) => role switch
    {
        FanRole.Cpu => Cpu,
        FanRole.Gpu => Gpu,
        _ => Case,
    };
}

/// <summary>
/// Готовые кривые. «Тихо» — в простое почти не слышно, в игре обороты растут плавно; «Холодно» — заметно
/// быстрее и раньше. Корпусные в обоих режимах идут по температуре видеокарты.
/// </summary>
public static class FanPresets
{
    public static FanCurve QuietGpu { get; } = FanCurve.Of((50, 20), (60, 28), (70, 38), (80, 55), (90, 80));

    public static FanCurve QuietCpu { get; } = FanCurve.Of((40, 25), (55, 32), (65, 42), (75, 60), (85, 100));

    public static FanCurve QuietCase { get; } = FanCurve.Of((40, 25), (55, 32), (65, 42), (75, 58), (85, 80));

    public static FanCurve CoolGpu { get; } = FanCurve.Of((40, 30), (55, 45), (65, 60), (75, 78), (85, 100));

    public static FanCurve CoolCpu { get; } = FanCurve.Of((35, 40), (50, 55), (60, 70), (70, 85), (80, 100));

    public static FanCurve CoolCase { get; } = FanCurve.Of((35, 40), (50, 55), (60, 70), (70, 85), (80, 100));

    /// <summary>На случай пустой или испорченной кривой: не тихо, но безопасно.</summary>
    public static FanCurve SafeFallback { get; } = new(new[]
    {
        new CurvePoint(30, 40), new CurvePoint(45, 50), new CurvePoint(60, 65), new CurvePoint(70, 80), new CurvePoint(80, 100),
    });

    public static FanTemplate Quiet => new()
    {
        Name = "Тихо",
        Gpu = QuietGpu,
        Cpu = QuietCpu,
        Case = QuietCase,
        GpuZeroRpm = true,
        CaseSource = CaseSource.Gpu,
    };

    public static FanTemplate Cool => new()
    {
        Name = "Холодно",
        Gpu = CoolGpu,
        Cpu = CoolCpu,
        Case = CoolCase,
        GpuZeroRpm = false,
        CaseSource = CaseSource.Gpu,
    };
}

/// <summary>Настройки одного вентилятора: что он делает и ниже каких оборотов его не опускать.</summary>
public sealed class FanChannelConfig
{
    /// <summary>Идентификатор управления в библиотеке датчиков («/lpc/it8655e/0/control/0»).</summary>
    public string Id { get; set; } = "";

    public FanRole Role { get; set; } = FanRole.Case;

    /// <summary>Ниже этого, %, вентилятор не опускается (калибровка: на чуть меньших он останавливался).</summary>
    public double MinDuty { get; set; } = FanSettings.DefaultMinDuty;

    /// <summary>Калибровка проводилась.</summary>
    public bool Calibrated { get; set; }

    /// <summary>Калибровка показала, что обороты не меняются: разъём, похоже, не управляется (вентилятор на 3 проводах или хаб).</summary>
    public bool Unresponsive { get; set; }
}

/// <summary>Настройки вентиляторов (fans.json). По умолчанию программа вентиляторами не управляет.</summary>
public sealed class FanSettings
{
    public const double DefaultMinDuty = 20;

    /// <summary>Нижний предел для любого вентилятора, %, — даже если калибровка нашла меньше.</summary>
    public const double AbsoluteMinDuty = 10;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public bool Enabled { get; set; }

    public FanMode Mode { get; set; } = FanMode.Quiet;

    /// <summary>Шаблоны ручного режима.</summary>
    public List<FanTemplate> Templates { get; set; } = new();

    /// <summary>Выбранный шаблон ручного режима (по имени).</summary>
    public string SelectedTemplate { get; set; } = "";

    public List<FanChannelConfig> Channels { get; set; } = new();

    [JsonIgnore]
    public string Path { get; private set; } = DefaultPath;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "fans.json");

    /// <summary>Набор кривых для текущего режима.</summary>
    public FanTemplate Active => Mode switch
    {
        FanMode.Cool => FanPresets.Cool,
        FanMode.Manual => Templates.FirstOrDefault(t => t.Name == SelectedTemplate) ?? Templates.FirstOrDefault() ?? FanPresets.Quiet,
        _ => FanPresets.Quiet,
    };

    public FanChannelConfig Channel(string id, Func<FanChannelConfig> create)
    {
        FanChannelConfig? config = Channels.FirstOrDefault(c => c.Id == id);
        if (config is null)
        {
            config = create();
            config.Id = id;
            Channels.Add(config);
        }

        return config;
    }

    /// <summary>Независимая копия (для потока управления, пока окно меняет настройки).</summary>
    public FanSettings Clone()
    {
        FanSettings copy = JsonSerializer.Deserialize<FanSettings>(JsonSerializer.Serialize(this, Json), Json) ?? new FanSettings();
        copy.Path = Path;
        return copy;
    }

    public static FanSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        FanSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<FanSettings>(File.ReadAllText(path), Json) ?? new FanSettings()
                : new FanSettings();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать настройки вентиляторов — начинаю с чистых.", ex);
            settings = new FanSettings();
        }

        settings.Path = path;
        if (settings.Templates.Count == 0)
            settings.Templates.Add(FanPresets.Quiet.Copy("Мой шаблон"));
        if (settings.Templates.All(t => t.Name != settings.SelectedTemplate))
            settings.SelectedTemplate = settings.Templates[0].Name;
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить настройки вентиляторов.", ex);
        }
    }
}
