namespace PcToolkit.Core.Sensors;

/// <summary>Шкала графика: границы и три подписи (низ, середина, верх).</summary>
public readonly record struct ChartRange(double Min, double Max)
{
    public double Mid => (Min + Max) / 2;

    public string Label(double value) => Math.Abs(value) >= 10 || Math.Abs(value - Math.Round(value)) < 1e-9
        ? SensorText.Number(value, 0)
        : SensorText.Number(value, Math.Abs(value) >= 1 ? 1 : 2);
}

/// <summary>
/// Выбор шкалы для графиков. Проценты всегда 0–100, температуры от 20 °C с запасом до порога,
/// остальное — от нуля до «круглого» числа чуть выше максимума. Так графики разных датчиков
/// не скачут при каждом обновлении и их можно сравнивать на глаз.
/// </summary>
public static class ChartScale
{
    public static ChartRange For(SensorKind kind, IEnumerable<double> values, double? limit = null, double? fixedMax = null)
    {
        if (kind is SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity)
            return new ChartRange(0, 100);

        if (fixedMax is > 0)
            return new ChartRange(0, fixedMax.Value);

        double max = double.MinValue;
        double min = double.MaxValue;
        foreach (double v in values)
        {
            if (!double.IsFinite(v))
                continue;
            max = Math.Max(max, v);
            min = Math.Min(min, v);
        }

        if (kind == SensorKind.Temperature)
        {
            double lo = min < 20 && min != double.MaxValue ? Math.Floor(min / 10) * 10 : 20;
            double hi = 100;
            if (max != double.MinValue)
                hi = Math.Max(hi, Math.Ceiling((max + 5) / 10) * 10);
            if (limit is double l)
                hi = Math.Max(hi, Math.Ceiling((l + 5) / 10) * 10);
            return new ChartRange(Math.Max(0, lo), hi);
        }

        double top = max == double.MinValue ? 0 : max * 1.15;
        if (limit is double lim)
            top = Math.Max(top, lim * 1.1);
        if (top <= 0)
            top = 1;
        return new ChartRange(0, NiceCeiling(top));
    }

    /// <summary>Ближайшее сверху «круглое» число: 1, 2, 2.5, 3, 4, 5, 6, 8 × 10ⁿ (частота 4,65 ГГц → шкала до 6, а не до 10).</summary>
    private static readonly double[] Steps = { 1, 2, 2.5, 3, 4, 5, 6, 8, 10 };

    public static double NiceCeiling(double value)
    {
        if (!(value > 0) || !double.IsFinite(value))
            return 1;
        double exponent = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double fraction = value / exponent;
        double nice = Steps.First(step => fraction <= step + 1e-9);
        return nice * exponent;
    }
}
