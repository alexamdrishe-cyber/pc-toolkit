namespace PcToolkit.Core.Sensors;

/// <summary>
/// Кусок истории датчика для графика: значения от старых к новым и шаг между ними в секундах.
/// <paramref name="FirstIndex"/> — сквозной номер первого значения на этой сетке шагов: у одного и того же
/// значения он не меняется от вызова к вызову. По нему график группирует точки так, чтобы границы групп
/// не сдвигались с каждым новым снимком.
/// </summary>
public readonly record struct HistoryWindow(double[] Values, double SampleSeconds, long FirstIndex = 0)
{
    public static HistoryWindow Empty { get; } = new(Array.Empty<double>(), 1);

    public int Count => Values.Length;
}

/// <summary>
/// История значений всех датчиков для графиков.
/// Вместе со значениями хранится время каждого снимка, поэтому графики верны при любой частоте опроса
/// (1–4 раза в секунду, служба старой версии — раз в секунду) и при пропущенных снимках.
/// Датчик, которого не оказалось в очередном снимке, ненадолго повторяет прошлое значение.
/// Не потокобезопасен: используется только из потока интерфейса.
/// </summary>
public sealed class SensorHistory
{
    /// <summary>Сколько снимков подряд датчика может не быть, прежде чем его история обнулится.</summary>
    private const int MaxMissed = 20;

    private readonly long[] _times;
    private readonly Dictionary<string, Series> _series = new();
    private int _start;
    private int _count;
    private long _added;
    private int _generation;

    /// <param name="capacity">Сколько снимков помнить: 7300 — это 30 минут при опросе 4 раза в секунду.</param>
    public SensorHistory(int capacity = 300)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _times = new long[capacity];
    }

    public int Capacity => _times.Length;

    /// <summary>Сколько снимков сейчас в истории.</summary>
    public int Count => _count;

    /// <summary>Средний шаг между последними снимками, в секундах (1, если снимков ещё мало).</summary>
    public double RecentSampleSeconds
    {
        get
        {
            if (_count < 2)
                return 1;

            int span = Math.Min(8, _count - 1);
            return (TimeAt(_count - 1) - TimeAt(_count - 1 - span)) / (double)span / TimeSpan.TicksPerSecond;
        }
    }

    public void Add(HardwareSnapshot snapshot)
    {
        long ticks = snapshot.Timestamp.Ticks;
        // Повтор того же снимка (служба отдаёт последний, а спрашиваем чаще) — пропускаем.
        if (_count > 0 && ticks <= TimeAt(_count - 1))
            return;

        int previous = _count > 0 ? Physical(_count - 1) : -1;
        int slot;
        if (_count < Capacity)
        {
            slot = Physical(_count);
            _count++;
        }
        else
        {
            slot = _start;
            _start = (_start + 1) % Capacity;
        }

        _times[slot] = ticks;
        _added++;
        _generation++;

        foreach (DeviceSnapshot device in snapshot.Devices)
        {
            foreach (SensorReading sensor in device.Sensors)
            {
                if (sensor.Value is not float value)
                    continue;

                if (!_series.TryGetValue(sensor.Id, out Series? series))
                {
                    series = new Series(Capacity);
                    _series[sensor.Id] = series;
                }

                series.Values[slot] = value;
                series.Valid = Math.Min(series.Valid + 1, Capacity);
                series.Missed = 0;
                series.Generation = _generation;
            }
        }

        foreach (Series series in _series.Values)
        {
            if (series.Generation == _generation || series.Valid == 0)
                continue;

            if (++series.Missed > MaxMissed || previous < 0)
            {
                series.Valid = 0;
                continue;
            }

            series.Values[slot] = series.Values[previous];
            series.Valid = Math.Min(series.Valid + 1, Capacity);
        }
    }

    /// <summary>
    /// Значения за последние <paramref name="seconds"/> секунд. Если снимков заметно больше, чем
    /// <paramref name="maxPoints"/>, они усредняются по отрезкам времени длиной seconds / maxPoints:
    /// график в 300 точек шириной не рисует 7200 значений.
    /// Отрезки привязаны к часам, а не к последнему снимку: новый снимок меняет только самую свежую точку,
    /// остальные остаются прежними и просто сдвигаются. Иначе при каждом обновлении соседние значения
    /// разбивались бы на пары по-новому, и шумная линия (загрузка процессора) прыгала бы целиком.
    /// </summary>
    public HistoryWindow GetWindow(string? sensorId, double seconds, int maxPoints = int.MaxValue)
    {
        if (sensorId is null || _count == 0 || !_series.TryGetValue(sensorId, out Series? series) || series.Valid == 0)
            return HistoryWindow.Empty;

        int available = Math.Min(series.Valid, _count);
        long last = TimeAt(_count - 1);
        long span = (long)(seconds * TimeSpan.TicksPerSecond);
        long bin = maxPoints >= 2 && maxPoints < int.MaxValue ? span / maxPoints : 0;
        // Усреднять имеет смысл, только если в отрезок попадает хотя бы полтора снимка в среднем.
        // Порог далёк от обычных сочетаний (снимок 0,25/0,5/1 с, отрезок 0,5/1/2/3 с), поэтому
        // дрожание времени снимков не переключает график туда-сюда между «как есть» и «усреднённо».
        bool binned = bin > 0 && bin >= RecentSampleSeconds * TimeSpan.TicksPerSecond * 1.5;

        long from = last - span;
        if (binned)
            from = from / bin * bin;   // окно начинается с границы отрезка — крайняя левая точка полная

        int first = FirstAtOrAfter(from, _count - available);
        int n = _count - first;
        if (n == 0)
            return HistoryWindow.Empty;

        if (binned)
        {
            long firstBin = TimeAt(first) / bin;
            int bins = (int)(last / bin - firstBin + 1);
            var sums = new double[bins];
            var counts = new int[bins];
            for (int i = first; i < _count; i++)
            {
                int b = (int)(TimeAt(i) / bin - firstBin);
                sums[b] += series.Values[Physical(i)];
                counts[b]++;
            }

            var averaged = new double[bins];
            for (int b = 0; b < bins; b++)
                // Отрезок без снимков (служба задержалась) — повтор предыдущего значения.
                averaged[b] = counts[b] > 0 ? sums[b] / counts[b] : averaged[Math.Max(0, b - 1)];

            return new HistoryWindow(averaged, bin / (double)TimeSpan.TicksPerSecond, firstBin);
        }

        double sample = n >= 2
            ? (last - TimeAt(first)) / (double)(n - 1) / TimeSpan.TicksPerSecond
            : RecentSampleSeconds;
        var values = new double[n];
        for (int i = 0; i < n; i++)
            values[i] = series.Values[Physical(first + i)];
        return new HistoryWindow(values, sample, _added - _count + first);
    }

    /// <summary>Первый снимок не раньше <paramref name="ticks"/> (время только растёт — двоичный поиск).</summary>
    private int FirstAtOrAfter(long ticks, int lowest)
    {
        int lo = lowest, hi = _count - 1, first = _count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (TimeAt(mid) >= ticks)
            {
                first = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return first;
    }

    /// <summary>Все сохранённые значения от старых к новым. Пустой массив, если истории нет.</summary>
    public double[] Get(string? sensorId) => GetLast(sensorId, int.MaxValue);

    /// <summary>Последние <paramref name="count"/> значений (или меньше, если столько ещё не накопилось).</summary>
    public double[] GetLast(string? sensorId, int count)
    {
        if (sensorId is null || !_series.TryGetValue(sensorId, out Series? series))
            return Array.Empty<double>();

        int take = Math.Clamp(count, 0, Math.Min(series.Valid, _count));
        var result = new double[take];
        for (int i = 0; i < take; i++)
            result[i] = series.Values[Physical(_count - take + i)];
        return result;
    }

    public void Clear()
    {
        _series.Clear();
        _start = 0;
        _count = 0;
    }

    private int Physical(int logical) => (_start + logical) % Capacity;

    private long TimeAt(int logical) => _times[Physical(logical)];

    private sealed class Series
    {
        public Series(int capacity) => Values = new float[capacity];

        public float[] Values { get; }

        /// <summary>Сколько последних снимков у датчика есть значение.</summary>
        public int Valid { get; set; }

        public int Missed { get; set; }

        public int Generation { get; set; }
    }
}
