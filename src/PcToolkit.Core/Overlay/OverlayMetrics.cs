using System.Globalization;
using PcToolkit.Core.Games;
using PcToolkit.Core.Sensors;

namespace PcToolkit.Core.Overlay;

/// <summary>Раздел списка показателей в настройках оверлея.</summary>
public enum OverlayGroup
{
    Frames,
    Cpu,
    Gpu,
    Memory,
    Other,
    Sensors,
}

/// <summary>Как показать число.</summary>
public enum OverlayFormat
{
    /// <summary>Заданное число знаков и постоянная единица.</summary>
    Number,

    /// <summary>МГц показываются в ГГц: «4,51 ГГц».</summary>
    Gigahertz,

    /// <summary>МБ, а от 1024 МБ — ГБ: «5,2 ГБ».</summary>
    Megabytes,

    /// <summary>Единица и точность — по типу датчика.</summary>
    Sensor,

    /// <summary>Текст, а не число: время, имя игры, название устройства.</summary>
    Text,
}

/// <summary>Значение в норме, за порогом «внимание» или «опасно».</summary>
public enum OverlayLevel
{
    Normal,
    Warning,
    Critical,
}

/// <summary>Что можно показать в оверлее.</summary>
/// <param name="Key">Ключ для overlay.json.</param>
/// <param name="Title">Название в списке показателей.</param>
/// <param name="Label">Подпись в оверлее по умолчанию.</param>
/// <param name="Unit">Единица после значения (для <see cref="OverlayFormat.Number"/>).</param>
public sealed record OverlayMetric(string Key, OverlayGroup Group, string Title, string Label, string Unit)
{
    public int Decimals { get; init; }

    /// <summary>Плохо, когда значение падает (FPS), а не растёт (температура).</summary>
    public bool LowerIsWorse { get; init; }

    /// <summary>Порог «внимание» для нового показателя.</summary>
    public double? Warn { get; init; }

    /// <summary>Порог «опасно» для нового показателя.</summary>
    public double? Critical { get; init; }

    /// <summary>Нижний край шкалы графика; null — по значениям.</summary>
    public double? GraphMin { get; init; }

    /// <summary>Верхний край шкалы графика; null — по значениям.</summary>
    public double? GraphMax { get; init; }

    public OverlayFormat Format { get; init; } = OverlayFormat.Number;

    /// <summary>Тип датчика (для <see cref="OverlayFormat.Sensor"/>).</summary>
    public SensorKind Kind { get; init; } = SensorKind.Other;

    /// <summary>Мелкий приглушённый текст: имя игры, название устройства.</summary>
    public bool Muted { get; init; }

    /// <summary>Значение от счётчика кадров: пока игры нет, предпросмотр показывает пример.</summary>
    public bool FromFrames => Group == OverlayGroup.Frames;

    public bool IsText => Format == OverlayFormat.Text;

    public bool CanGraph => !IsText;

    /// <summary>График из отдельных кадров за последние секунды: ровная линия — плавно, пики — рывки.</summary>
    public bool FrameGraph => Key == "frametime";

    public bool IsSensor => Key.StartsWith(OverlayMetrics.SensorPrefix, StringComparison.Ordinal);
}

/// <summary>Значение показателя: число (для порогов и графика), текст и единица.</summary>
public readonly record struct OverlayReading(double? Value, string Text, string Unit)
{
    public static OverlayReading Missing { get; } = new(null, "—", "");
}

/// <summary>Откуда оверлей берёт значения.</summary>
public sealed class OverlaySources
{
    public FrameMonitorState? Frames { get; set; }

    public HardwareSnapshot? Snapshot { get; set; }

    public KeyMetrics? Metrics { get; set; }

    /// <summary>Пока игра не выводит кадры, показывать пример FPS (предпросмотр в настройках).</summary>
    public bool Sample { get; set; }

    public DateTime Now { get; set; } = DateTime.Now;

    public bool HasFrames => Frames is { HasTarget: true, Fps: > 0 };

    public bool UseSample => Sample && !HasFrames;
}

/// <summary>Показатели оверлея: какие есть, как их прочитать и показать.</summary>
public static class OverlayMetrics
{
    /// <summary>Ключ любого датчика: «sensor:» и его Id.</summary>
    public const string SensorPrefix = "sensor:";

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly OverlayMetric[] Catalog =
    {
        new("fps", OverlayGroup.Frames, "Кадры в секунду (FPS)", "", "FPS")
            { LowerIsWorse = true, Warn = 60, Critical = 30, GraphMin = 0 },
        new("fps.low1", OverlayGroup.Frames, "1% low — FPS самых медленных кадров", "1% low", "")
            { LowerIsWorse = true, GraphMin = 0 },
        new("frametime", OverlayGroup.Frames, "Время кадра (график — рывки)", "", "мс") { Decimals = 1, GraphMin = 0 },
        new("process", OverlayGroup.Frames, "Игра и графический API", "", "") { Format = OverlayFormat.Text, Muted = true },

        new("cpu.load", OverlayGroup.Cpu, "Загрузка процессора", "CPU", "%") { GraphMin = 0, GraphMax = 100 },
        new("cpu.temp", OverlayGroup.Cpu, "Температура процессора", "CPU", "°C") { Warn = 85, Critical = 95 },
        new("cpu.clock", OverlayGroup.Cpu, "Частота процессора", "CPU", "ГГц") { Format = OverlayFormat.Gigahertz, Decimals = 2 },
        new("cpu.power", OverlayGroup.Cpu, "Мощность процессора", "CPU", "Вт") { GraphMin = 0 },

        new("gpu.load", OverlayGroup.Gpu, "Загрузка видеокарты", "GPU", "%") { GraphMin = 0, GraphMax = 100 },
        new("gpu.temp", OverlayGroup.Gpu, "Температура видеокарты", "GPU", "°C") { Warn = 85, Critical = 95 },
        new("gpu.hotspot", OverlayGroup.Gpu, "Горячая точка видеокарты", "Hot spot", "°C") { Warn = 100, Critical = 110 },
        new("gpu.clock", OverlayGroup.Gpu, "Частота видеокарты", "GPU", "МГц"),
        new("gpu.memclock", OverlayGroup.Gpu, "Частота видеопамяти", "VRAM", "МГц"),
        new("gpu.vram", OverlayGroup.Gpu, "Занятая видеопамять", "VRAM", "") { Format = OverlayFormat.Megabytes, GraphMin = 0 },
        new("gpu.power", OverlayGroup.Gpu, "Мощность видеокарты", "GPU", "Вт") { GraphMin = 0 },
        new("gpu.fan", OverlayGroup.Gpu, "Вентилятор видеокарты", "Fan", "об/мин") { GraphMin = 0 },

        new("ram.used", OverlayGroup.Memory, "Занятая оперативная память", "RAM", "ГБ") { Decimals = 1, GraphMin = 0 },
        new("ram.load", OverlayGroup.Memory, "Загрузка оперативной памяти", "RAM", "%") { GraphMin = 0, GraphMax = 100 },

        new("time", OverlayGroup.Other, "Время", "", "") { Format = OverlayFormat.Text },
        new("cpu.name", OverlayGroup.Other, "Название процессора", "", "") { Format = OverlayFormat.Text, Muted = true },
        new("gpu.name", OverlayGroup.Other, "Название видеокарты", "", "") { Format = OverlayFormat.Text, Muted = true },
    };

    /// <summary>Показатели, которые есть всегда (кроме отдельных датчиков).</summary>
    public static IReadOnlyList<OverlayMetric> BuiltIn => Catalog;

    public static string GroupTitle(OverlayGroup group) => group switch
    {
        OverlayGroup.Frames => "Кадры",
        OverlayGroup.Cpu => "Процессор",
        OverlayGroup.Gpu => "Видеокарта",
        OverlayGroup.Memory => "Память",
        OverlayGroup.Other => "Прочее",
        _ => "Все датчики",
    };

    public static string SensorKey(string sensorId) => SensorPrefix + sensorId;

    /// <summary>Описание показателя. Отдельный датчик ищется в снимке; если его там нет — null.</summary>
    public static OverlayMetric? Find(string key, HardwareSnapshot? snapshot = null)
    {
        foreach (OverlayMetric metric in Catalog)
        {
            if (metric.Key == key)
                return metric;
        }

        if (snapshot is null || !key.StartsWith(SensorPrefix, StringComparison.Ordinal))
            return null;

        string id = key[SensorPrefix.Length..];
        foreach (DeviceSnapshot device in snapshot.Devices)
        {
            foreach (SensorReading sensor in device.Sensors)
            {
                if (sensor.Id == id)
                    return ForSensor(device, sensor);
            }
        }

        return null;
    }

    /// <summary>Описание для показателя, которого сейчас нет в данных (датчик пропал, устройство отключено).</summary>
    public static OverlayMetric Missing(OverlayItem item) =>
        new(item.Metric, OverlayGroup.Sensors, item.Title ?? item.Metric, item.Title ?? "", "") { Format = OverlayFormat.Sensor };

    /// <summary>Все датчики, у которых сейчас есть значение, — для списка «Добавить показатель».</summary>
    public static IEnumerable<OverlayMetric> Sensors(HardwareSnapshot snapshot)
    {
        foreach (DeviceSnapshot device in snapshot.Devices.OrderBy(d => SensorFormatter.DeviceOrder(d.Kind)))
        {
            foreach (SensorReading sensor in device.Sensors.OrderBy(s => SensorFormatter.GroupOrder(s.Kind)))
            {
                if (sensor.Value is not float value || !float.IsFinite(value) || !KeyMetrics.IsPlausible(sensor.Kind, value))
                    continue;
                if (sensor.Kind == SensorKind.Temperature && KeyMetrics.IsThreshold(sensor.Name))
                    continue;
                yield return ForSensor(device, sensor);
            }
        }
    }

    /// <summary>Отдельный датчик как показатель оверлея.</summary>
    public static OverlayMetric ForSensor(DeviceSnapshot device, SensorReading sensor)
    {
        string name = SensorText.Name(device.Kind, sensor.Kind, sensor.Name);
        string deviceName = SensorText.DeviceName(device.Kind, device.Name);
        bool percent = sensor.Kind is SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity;
        return new OverlayMetric(SensorKey(sensor.Id), OverlayGroup.Sensors,
            $"{deviceName} · {SensorFormatter.GroupTitle(sensor.Kind)}: {name}",
            $"{SensorFormatter.DeviceTag(device.Kind)} {name}", SensorFormatter.Unit(sensor.Kind))
        {
            Format = OverlayFormat.Sensor,
            Kind = sensor.Kind,
            Warn = sensor.Kind == SensorKind.Temperature ? 85 : null,
            Critical = sensor.Kind == SensorKind.Temperature ? 95 : null,
            GraphMin = percent ? 0 : null,
            GraphMax = percent ? 100 : null,
        };
    }

    /// <summary>Значение показателя сейчас.</summary>
    public static OverlayReading Read(OverlayMetric metric, OverlaySources sources)
    {
        if (metric.IsText)
            return Text(metric, sources) is { Length: > 0 } text ? new OverlayReading(null, text, "") : OverlayReading.Missing;

        if (Value(metric, sources) is not double value || !double.IsFinite(value))
            return OverlayReading.Missing;

        (string number, string unit) = Format(metric, value);
        return new OverlayReading(value, number, unit);
    }

    /// <summary>Число для порогов и графика; null — значения нет.</summary>
    public static double? Value(OverlayMetric metric, OverlaySources sources)
    {
        if (metric.FromFrames)
        {
            if (sources.UseSample)
                return SampleValue(metric.Key, sources.Now);

            FrameMonitorState? frames = sources.Frames;
            if (frames is not { HasTarget: true } || frames.Fps <= 0)
                return null;
            return metric.Key switch
            {
                "fps" => frames.Fps,
                "fps.low1" => frames.Low1Fps > 0 ? frames.Low1Fps : null,
                "frametime" => frames.FrameMs > 0 ? frames.FrameMs : null,
                _ => null,
            };
        }

        KeyMetrics? m = sources.Metrics;
        switch (metric.Key)
        {
            case "cpu.load": return m?.CpuLoad?.Value;
            case "cpu.temp": return m?.CpuTemperature?.Value;
            case "cpu.clock": return m?.CpuClock;
            case "cpu.power": return m?.CpuPower?.Value;
            case "gpu.load": return m?.GpuLoad?.Value;
            case "gpu.temp": return m?.GpuTemperature?.Value;
            case "gpu.hotspot": return m?.GpuHotSpot?.Value;
            case "gpu.clock": return m?.GpuClock?.Value;
            case "gpu.power": return m?.GpuPower?.Value;
            case "gpu.vram": return m?.GpuMemoryUsed?.Value;
            case "gpu.memclock": return GpuSensor(sources, SensorKind.Clock, s => s.Name.Equals("GPU Memory", StringComparison.OrdinalIgnoreCase));
            case "gpu.fan": return GpuSensor(sources, SensorKind.Fan, _ => true);
            case "ram.used": return m?.MemoryUsed?.Value;
            case "ram.load": return m?.MemoryLoad?.Value;
        }

        if (!metric.IsSensor || sources.Snapshot is not { } snapshot)
            return null;

        string id = metric.Key[SensorPrefix.Length..];
        foreach (DeviceSnapshot device in snapshot.Devices)
        {
            foreach (SensorReading sensor in device.Sensors)
            {
                if (sensor.Id == id)
                    return sensor.Value is float value && KeyMetrics.IsPlausible(sensor.Kind, value) ? value : null;
            }
        }

        return null;
    }

    /// <summary>Шкала графика: у видеопамяти и оперативной памяти верх — весь объём.</summary>
    public static (double? Min, double? Max) GraphRange(OverlayMetric metric, OverlaySources sources)
    {
        KeyMetrics? m = sources.Metrics;
        return metric.Key switch
        {
            "gpu.vram" when m?.GpuMemoryTotal is { Value: > 0 } total => (0, total.Value),
            "ram.used" when m is { MemoryUsed: { } used, MemoryAvailable: { } free } => (0, used.Value + free.Value),
            _ => (metric.GraphMin, metric.GraphMax),
        };
    }

    /// <summary>Число и единица: «4,51» и «ГГц», «62» и «°C».</summary>
    public static (string Text, string Unit) Format(OverlayMetric metric, double value) => metric.Format switch
    {
        OverlayFormat.Gigahertz => (Number(value / 1000, 2), "ГГц"),
        OverlayFormat.Megabytes => value >= 1024 ? (Number(value / 1024, 1), "ГБ") : (Number(value, 0), "МБ"),
        OverlayFormat.Sensor => SensorValue(metric.Kind, value),
        _ => (Number(value, metric.Decimals), metric.Unit),
    };

    /// <summary>
    /// Где значение относительно порогов. Если «опасно» меньше «внимания», плохо, когда значение падает
    /// (FPS ниже 60 — внимание, ниже 30 — опасно), иначе — когда растёт (температура от 85 и от 95).
    /// Если задан только один порог, направление берётся из показателя.
    /// </summary>
    public static OverlayLevel Level(double value, double? warn, double? critical, bool lowerIsWorse)
    {
        lowerIsWorse = IsLowerWorse(warn, critical, lowerIsWorse);
        if (critical is double c && Beyond(c))
            return OverlayLevel.Critical;
        if (warn is double w && Beyond(w))
            return OverlayLevel.Warning;
        return OverlayLevel.Normal;

        bool Beyond(double threshold) => lowerIsWorse ? value < threshold : value >= threshold;
    }

    /// <summary>Плохо ли падение значения при таких порогах.</summary>
    public static bool IsLowerWorse(double? warn, double? critical, bool metricDefault) =>
        warn is double w && critical is double c && w != c ? c < w : metricDefault;

    /// <summary>Число по-русски, без разделителя тысяч: «4,51», «2352».</summary>
    public static string Number(double value, int decimals)
    {
        string text = Math.Round(value, decimals).ToString("F" + decimals, Russian);
        return text.StartsWith('-') && text.Trim('-', '0', ',') == "" ? text[1..] : text;
    }

    /// <summary>Пример времени кадров для предпросмотра: около 7 мс, изредка рывки. Сдвигается со временем.</summary>
    public static double[] SampleFrameTimes(DateTime now, int count = 300)
    {
        var values = new double[count];
        long tick = (long)(now.TimeOfDay.TotalSeconds * 60);
        for (int i = 0; i < count; i++)
        {
            long n = tick - (count - 1 - i);
            double value = 7.0 + (Noise(n) - 0.5) * 1.2 + 0.6 * Math.Sin(n / 25.0);
            if (Noise(n * 7919 + 13) > 0.985)
                value += 6 + Noise(n * 31 + 7) * 10;
            values[i] = value;
        }

        return values;
    }

    private static double SampleValue(string key, DateTime now)
    {
        double t = now.TimeOfDay.TotalSeconds;
        double fps = 141 + 5 * Math.Sin(t * 0.9) + 2 * Math.Sin(t * 2.3);
        return key switch
        {
            "fps" => fps,
            "fps.low1" => 112 + 3 * Math.Sin(t * 0.4),
            "frametime" => 1000 / fps,
            _ => 0,
        };
    }

    private static string? Text(OverlayMetric metric, OverlaySources sources)
    {
        switch (metric.Key)
        {
            case "time":
                return sources.Now.ToString("HH:mm", Russian);
            case "cpu.name":
                return sources.Metrics?.CpuName?.Trim();
            case "gpu.name":
                return sources.Metrics?.GpuName?.Trim();
            case "process":
                if (sources.UseSample)
                    return "game · DX12";
                if (sources.Frames is not { HasTarget: true, ProcessName: { Length: > 0 } name } frames)
                    return null;
                return frames.Api is { Length: > 0 } api ? $"{name} · {api}" : name;
            default:
                return null;
        }
    }

    private static double? GpuSensor(OverlaySources sources, SensorKind kind, Func<SensorReading, bool> filter)
    {
        if (sources.Snapshot is not { } snapshot || sources.Metrics?.GpuName is not { } gpuName)
            return null;

        DeviceSnapshot? gpu = snapshot.Devices.FirstOrDefault(d => d.IsGpu && d.Name == gpuName);
        if (gpu is null)
            return null;

        double? best = null;
        foreach (SensorReading sensor in gpu.Sensors)
        {
            if (sensor.Kind == kind && sensor.Value is float value && KeyMetrics.IsPlausible(kind, value) && filter(sensor))
                best = Math.Max(best ?? double.MinValue, value);
        }

        return best;
    }

    private static (string, string) SensorValue(SensorKind kind, double v) => kind switch
    {
        SensorKind.Temperature => (Number(v, 0), "°C"),
        SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => (Number(v, 0), "%"),
        SensorKind.Clock => (Number(v, 0), "МГц"),
        SensorKind.Power => (Number(v, Math.Abs(v) < 10 ? 1 : 0), "Вт"),
        SensorKind.Voltage => (Number(v, 3), "В"),
        SensorKind.Current => (Number(v, 2), "А"),
        SensorKind.Fan => (Number(v, 0), "об/мин"),
        SensorKind.Data => v >= 1000 ? (Number(v / 1024, 1), "ТБ") : (Number(v, 1), "ГБ"),
        SensorKind.SmallData => v >= 1024 ? (Number(v / 1024, 1), "ГБ") : (Number(v, 0), "МБ"),
        SensorKind.Throughput => v >= 1024 * 1024 ? (Number(v / (1024 * 1024), 1), "МБ/с")
            : v >= 1024 ? (Number(v / 1024, 0), "КБ/с") : (Number(v, 0), "Б/с"),
        SensorKind.Frequency => (Number(v, 0), "Гц"),
        SensorKind.Factor => (Number(v, 2), ""),
        SensorKind.TimeSpan => (Number(v / 3600, 1), "ч"),
        _ => (Number(v, Math.Abs(v) < 10 ? 2 : 1), SensorFormatter.Unit(kind)),
    };

    private static double Noise(long n)
    {
        ulong x = unchecked((ulong)n * 0x9E3779B97F4A7C15UL);
        x ^= x >> 29;
        x = unchecked(x * 0xBF58476D1CE4E5B9UL);
        x ^= x >> 32;
        return (x & 0xFFFFFF) / (double)0x1000000;
    }
}

/// <summary>Последние значения показателей для графиков в оверлее.</summary>
public sealed class OverlayHistory
{
    private readonly Dictionary<string, Queue<double>> _series = new();

    public OverlayHistory(int capacity = 90)
    {
        Capacity = Math.Max(2, capacity);
    }

    public int Capacity { get; }

    /// <summary>Добавить значение; null (значения нет) — разрыв, на графике он не рисуется.</summary>
    public void Push(string key, double? value)
    {
        if (!_series.TryGetValue(key, out Queue<double>? queue))
            _series[key] = queue = new Queue<double>(Capacity);
        if (queue.Count == Capacity)
            queue.Dequeue();
        queue.Enqueue(value is double v && double.IsFinite(v) ? v : double.NaN);
    }

    public double[] Values(string key) =>
        _series.TryGetValue(key, out Queue<double>? queue) ? queue.ToArray() : Array.Empty<double>();

    /// <summary>Забыть показатели, которых больше нет в оверлее.</summary>
    public void Keep(IReadOnlySet<string> keys)
    {
        foreach (string key in _series.Keys.Where(k => !keys.Contains(k)).ToList())
            _series.Remove(key);
    }

    public void Clear() => _series.Clear();
}
