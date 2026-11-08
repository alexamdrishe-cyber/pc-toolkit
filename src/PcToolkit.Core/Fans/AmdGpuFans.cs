using System.Text.Json;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Overclock;

namespace PcToolkit.Core.Fans;

/// <summary>
/// Вентиляторы видеокарты AMD — через её драйвер (ADLX): в драйвер записывается кривая из пяти точек, и дальше он
/// сам крутит вентиляторы по ней, плавно и с собственными защитами. То, что было до программы, сначала сохраняется
/// в файл: при выключении управления и при выходе из программы возвращается, а если программа упала — при
/// следующем запуске.
/// </summary>
public sealed class AmdGpuFans
{
    private readonly string _statePath;
    private readonly object _lock = new();

    public AmdGpuFans(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "fan-gpu.json");
    }

    /// <summary>Что было в драйвере до того, как программа поменяла кривую.</summary>
    public sealed class SavedState
    {
        public bool WasAtFactory { get; set; }

        public string Curve { get; set; } = "";

        public bool? ZeroRpm { get; set; }

        public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    }

    /// <summary>Кривая сейчас изменена программой (её нужно будет вернуть).</summary>
    public bool HasSavedState => File.Exists(_statePath);

    /// <summary>Видеокарта AMD с настраиваемой кривой вентиляторов: сведения о ней или null с причиной.</summary>
    public static GpuTuningInfo? Probe(out string? reason)
    {
        AdlxGpuTuning? adlx = AdlxGpuTuning.Shared(out reason);
        if (adlx is null)
            return null;

        try
        {
            GpuTuningInfo info = adlx.Read();
            if (info.SupportsFan && info.FanCurve.Count >= 2 && info.FanSpeedRange is not null && info.FanTemperatureRange is not null)
                return info;
            reason = "драйвер видеокарты не даёт настроить кривую вентиляторов";
        }
        catch (AdlxException ex)
        {
            reason = ex.Message;
        }

        return null;
    }

    /// <summary>Записывает кривую и Zero RPM в драйвер (если они отличаются от текущих). Возвращает кривую так, как её принял драйвер.</summary>
    public IReadOnlyList<FanPoint> Apply(FanCurve curve, bool zeroRpm)
    {
        lock (_lock)
        {
            AdlxGpuTuning adlx = AdlxGpuTuning.Shared(out string? error) ?? throw new AdlxException(error ?? "библиотека AMD недоступна", -1);
            GpuTuningInfo info = adlx.Read();
            if (info.FanSpeedRange is not { } speed || info.FanTemperatureRange is not { } temperature || info.FanCurve.Count < 2)
                throw new AdlxException("драйвер видеокарты не даёт настроить кривую вентиляторов", -1);

            if (!HasSavedState)
            {
                Save(new SavedState
                {
                    WasAtFactory = info.IsAtFactory,
                    Curve = AdlxGpuTuning.FormatCurve(info.FanCurve),
                    ZeroRpm = info.ZeroRpm,
                });
            }

            List<FanPoint> points = ToDriver(curve, info.FanCurve.Count, temperature, speed);
            string text = AdlxGpuTuning.FormatCurve(points);
            bool changed = false;
            if (text != AdlxGpuTuning.FormatCurve(info.FanCurve))
            {
                adlx.Set(AdlxGpuTuning.FanCurve, text);
                changed = true;
            }

            if (info.ZeroRpm is bool current && current != zeroRpm)
            {
                adlx.Set(AdlxGpuTuning.FanZeroRpm, zeroRpm ? "1" : "0");
                changed = true;
            }

            if (changed)
                AppLog.Info($"Вентиляторы видеокарты: кривая {text}, остановка в простое {(zeroRpm ? "включена" : "выключена")}.");
            return points;
        }
    }

    /// <summary>Возвращает драйверу то, что было до программы. Если библиотека AMD недоступна — файл остаётся до следующего раза.</summary>
    public void Restore()
    {
        lock (_lock)
        {
            SavedState? state = Load();
            if (state is null)
                return;

            AdlxGpuTuning? adlx = AdlxGpuTuning.Shared(out string? error);
            if (adlx is null)
            {
                AppLog.Warn("Не удалось вернуть вентиляторы видеокарты драйверу: " + error);
                return;
            }

            try
            {
                if (state.WasAtFactory)
                {
                    adlx.ResetToFactory();
                }
                else
                {
                    if (state.Curve.Length > 0)
                        adlx.Set(AdlxGpuTuning.FanCurve, state.Curve);
                    if (state.ZeroRpm is bool zero)
                        adlx.Set(AdlxGpuTuning.FanZeroRpm, zero ? "1" : "0");
                }

                File.Delete(_statePath);
                AppLog.Info(state.WasAtFactory
                    ? "Вентиляторы видеокарты возвращены драйверу: заводские настройки."
                    : $"Вентиляторы видеокарты возвращены драйверу: кривая {state.Curve}.");
            }
            catch (Exception ex) when (ex is AdlxException or IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Не удалось вернуть вентиляторы видеокарты драйверу.", ex);
            }
        }
    }

    /// <summary>
    /// Кривая в точках драйвера: столько точек, сколько у видеокарты (обычно пять), температуры строго по возрастанию
    /// и в его диапазоне, скорости в его диапазоне и не убывают.
    /// </summary>
    public static List<FanPoint> ToDriver(FanCurve curve, int count, TuningRange temperature, TuningRange speed)
    {
        var source = curve.Points.Count == count
            ? curve.Points.ToList()
            : Enumerable.Range(0, count)
                .Select(i =>
                {
                    double t = curve.Points[0].Temperature + (curve.Points[^1].Temperature - curve.Points[0].Temperature) * i / (count - 1);
                    return new CurvePoint(t, curve.DutyAt(t));
                })
                .ToList();

        var result = new List<FanPoint>(count);
        int step = Math.Max(1, temperature.Step);
        for (int i = 0; i < count; i++)
        {
            int t = temperature.Snap((int)Math.Round(source[i].Temperature));
            if (result.Count > 0 && t <= result[^1].Temperature)
                t = Math.Min(temperature.Max, result[^1].Temperature + step);
            int s = speed.Snap((int)Math.Round(source[i].Duty));
            if (result.Count > 0 && s < result[^1].Speed)
                s = result[^1].Speed;
            result.Add(new FanPoint(t, s));
        }

        return result;
    }

    private SavedState? Load()
    {
        try
        {
            return File.Exists(_statePath) ? JsonSerializer.Deserialize<SavedState>(File.ReadAllText(_statePath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn("Не удалось прочитать, какими были вентиляторы видеокарты.", ex);
            return null;
        }
    }

    private void Save(SavedState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
