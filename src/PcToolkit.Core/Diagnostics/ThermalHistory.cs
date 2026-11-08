using System.Text.Json;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Diagnostics;

/// <summary>Температура под нагрузкой за один день: сумма и число замеров (раз в секунду) в каждой полосе загрузки.</summary>
public sealed class ThermalDay
{
    public DateOnly Date { get; set; }

    /// <summary>Процессор, загрузка 50–80 %.</summary>
    public ThermalBand CpuMid { get; set; } = new();

    /// <summary>Процессор, загрузка от 80 %.</summary>
    public ThermalBand CpuHigh { get; set; } = new();

    /// <summary>Видеокарта, загрузка 50–80 %.</summary>
    public ThermalBand GpuMid { get; set; } = new();

    /// <summary>Видеокарта, загрузка от 80 %.</summary>
    public ThermalBand GpuHigh { get; set; } = new();
}

public sealed class ThermalBand
{
    public double Sum { get; set; }

    public int Count { get; set; }

    public void Add(double value)
    {
        Sum += value;
        Count++;
    }
}

/// <summary>
/// История температур процессора и видеокарты под нагрузкой (%LocalAppData%\PcToolkit\thermal-history.json).
/// По ней видно, что при той же нагрузке компьютер стал заметно горячее, чем несколько недель назад:
/// так проявляются пыль в радиаторах, высохшая термопаста и уставшие вентиляторы.
/// </summary>
public sealed class ThermalHistory
{
    /// <summary>Сколько дней хранить.</summary>
    public const int KeepDays = 180;

    /// <summary>Сколько секунд под нагрузкой нужно в каждом из сравниваемых периодов.</summary>
    public const int MinSamples = 600;

    /// <summary>На сколько градусов должно стать горячее, чтобы подсказать.</summary>
    public const double MinRise = 8;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly List<ThermalDay> _days;
    private readonly object _lock = new();
    private bool _dirty;

    private ThermalHistory(string path, List<ThermalDay> days)
    {
        _path = path;
        _days = days;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "thermal-history.json");

    public static ThermalHistory Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return new ThermalHistory(path, JsonSerializer.Deserialize<List<ThermalDay>>(File.ReadAllText(path), JsonOptions) ?? new());
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать историю температур, начинаю новую.", ex);
        }

        return new ThermalHistory(path, new List<ThermalDay>());
    }

    public static ThermalHistory InMemory(IEnumerable<ThermalDay> days) => new("", days.ToList());

    /// <summary>Замер раз в секунду: учитывается, только если устройство под нагрузкой от 50 %.</summary>
    public void Add(DateTime time, float? cpuLoad, float? cpuTemperature, float? gpuLoad, float? gpuTemperature)
    {
        bool cpu = cpuLoad >= 50 && cpuTemperature is > 0 and < 120;
        bool gpu = gpuLoad >= 50 && gpuTemperature is > 0 and < 120;
        if (!cpu && !gpu)
            return;

        lock (_lock)
        {
            ThermalDay day = Today(DateOnly.FromDateTime(time));
            if (cpu)
                (cpuLoad >= 80 ? day.CpuHigh : day.CpuMid).Add(cpuTemperature!.Value);
            if (gpu)
                (gpuLoad >= 80 ? day.GpuHigh : day.GpuMid).Add(gpuTemperature!.Value);
            _dirty = true;
        }
    }

    public void Save()
    {
        if (_path.Length == 0)
            return;

        List<ThermalDay> copy;
        lock (_lock)
        {
            if (!_dirty)
                return;

            _dirty = false;
            copy = _days.ToList();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить историю температур.", ex);
        }
    }

    /// <summary>
    /// Подсказка, если при той же нагрузке стало горячее: самые ранние две недели с данными сравниваются
    /// с последней неделей, между ними не меньше трёх недель. null — сравнивать не с чем или всё в порядке.
    /// </summary>
    public Finding? CheckTrend(DateOnly today)
    {
        lock (_lock)
        {
            List<ThermalDay> recent = _days.Where(d => d.Date > today.AddDays(-7)).ToList();
            if (recent.Count == 0)
                return null;

            DateOnly first = _days.Min(d => d.Date);
            if (first > today.AddDays(-28))
                return null;

            List<ThermalDay> baseline = _days.Where(d => d.Date < first.AddDays(14) && d.Date <= today.AddDays(-21)).ToList();
            (double rise, double before, double now, string device)? worst = null;
            foreach ((string device, Func<ThermalDay, ThermalBand> band) in new (string, Func<ThermalDay, ThermalBand>)[]
                     {
                         ("процессор", d => d.CpuHigh), ("процессор", d => d.CpuMid),
                         ("видеокарта", d => d.GpuHigh), ("видеокарта", d => d.GpuMid),
                     })
            {
                (double sum, int count) old = Total(baseline, band);
                (double sum, int count) cur = Total(recent, band);
                if (old.count < MinSamples || cur.count < MinSamples)
                    continue;

                double before = old.sum / old.count;
                double now = cur.sum / cur.count;
                double rise = now - before;
                if (rise >= MinRise && (worst is null || rise > worst.Value.rise))
                    worst = (rise, before, now, device);
            }

            if (worst is not { } w)
                return null;

            string who = w.device == "процессор" ? "процессор стал" : "видеокарта стала";
            return new Finding(FindingSeverity.Info, $"При той же нагрузке {who} горячее на {w.rise:0} °C",
                $"Несколько недель назад под нагрузкой было в среднем {w.before:0} °C, сейчас — {w.now:0} °C. Частые причины: пыль " +
                "в радиаторе и вентиляторах, высохшая термопаста, уставший вентилятор; бывает и просто жара в комнате. " +
                "Почистите компьютер от пыли; если не поможет — замените термопасту.")
            {
                Area = "monitoring",
                IsTip = true,
            };
        }
    }

    private static (double Sum, int Count) Total(IEnumerable<ThermalDay> days, Func<ThermalDay, ThermalBand> band)
    {
        double sum = 0;
        int count = 0;
        foreach (ThermalDay day in days)
        {
            ThermalBand b = band(day);
            sum += b.Sum;
            count += b.Count;
        }

        return (sum, count);
    }

    private ThermalDay Today(DateOnly date)
    {
        ThermalDay? day = _days.Count > 0 && _days[^1].Date == date ? _days[^1] : _days.FirstOrDefault(d => d.Date == date);
        if (day is not null)
            return day;

        day = new ThermalDay { Date = date };
        _days.Add(day);
        _days.RemoveAll(d => d.Date < date.AddDays(-KeepDays));
        return day;
    }
}
