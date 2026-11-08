namespace PcToolkit.Core.Games;

/// <summary>Вывод по одному показателю: стало лучше, хуже, не изменилось или по этим прогонам сказать нельзя.</summary>
public enum ChangeVerdict
{
    Same,
    Better,
    Worse,
    Unclear,
}

/// <summary>
/// Сравнение одного показателя (средний FPS, 1% low…) между замерами «до» и «после».
/// <see cref="LowPercent"/>–<see cref="HighPercent"/> — где с вероятностью 95 % лежит настоящая разница с учётом
/// разброса между прогонами; есть, только когда с каждой стороны хотя бы два прогона.
/// <see cref="SpreadA"/> и <see cref="SpreadB"/> — разброс между прогонами (коэффициент вариации, %).
/// </summary>
public sealed record MetricChange(
    int RunsA,
    int RunsB,
    double MeanA,
    double MeanB,
    double ChangePercent,
    double? LowPercent,
    double? HighPercent,
    double? SpreadA,
    double? SpreadB,
    ChangeVerdict Verdict)
{
    public bool HasInterval => LowPercent is not null && HighPercent is not null;
}

/// <summary>
/// Выходит ли разница между замерами за погрешность. Один прогон с каждой стороны — только правило «меньше 3 % —
/// не разница»: сцены в игре никогда не повторяются точно. Несколько прогонов — t-критерий Уэлча: разброс между
/// прогонами даёт погрешность, и разница считается настоящей, если 95-процентный интервал не включает ноль.
/// </summary>
public static class FpsComparison
{
    /// <summary>Разница меньше этого, %, на глаз не заметна и обычно в пределах погрешности.</summary>
    public const double SameThreshold = 3;

    /// <summary>Сколько прогонов с каждой стороны советовать, чтобы посчитать погрешность.</summary>
    public const int RecommendedRuns = 3;

    // Квантиль 0,975 распределения Стьюдента для 1–30 степеней свободы (двусторонние 95 %).
    private static readonly double[] TTable =
    {
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
    };

    public static MetricChange Compare(IReadOnlyList<double> a, IReadOnlyList<double> b, bool higherIsBetter = true)
    {
        if (a.Count == 0 || b.Count == 0)
            throw new ArgumentException("С каждой стороны нужен хотя бы один замер.");

        double meanA = a.Average();
        double meanB = b.Average();
        double change = meanA > 0 ? (meanB - meanA) / meanA * 100 : 0;
        double? spreadA = Spread(a);
        double? spreadB = Spread(b);
        if (a.Count < 2 || b.Count < 2 || meanA <= 0)
        {
            ChangeVerdict single = Math.Abs(change) < SameThreshold ? ChangeVerdict.Same : Direction(change, higherIsBetter);
            return new MetricChange(a.Count, b.Count, meanA, meanB, change, null, null, spreadA, spreadB, single);
        }

        double va = Variance(a) / a.Count;
        double vb = Variance(b) / b.Count;
        double se = Math.Sqrt(va + vb);
        // Степени свободы по Уэлчу–Саттертуэйту: группы могут быть разного размера и с разным разбросом.
        double df = se > 0 ? (va + vb) * (va + vb) / (va * va / (a.Count - 1) + vb * vb / (b.Count - 1)) : double.PositiveInfinity;
        double margin = TCritical95(df) * se;
        double low = (meanB - meanA - margin) / meanA * 100;
        double high = (meanB - meanA + margin) / meanA * 100;

        ChangeVerdict verdict = Math.Max(Math.Abs(low), Math.Abs(high)) < SameThreshold
            ? ChangeVerdict.Same // даже с погрешностью разница меньше заметной
            : low > 0 || high < 0
                ? Direction(change, higherIsBetter)
                : ChangeVerdict.Unclear;
        return new MetricChange(a.Count, b.Count, meanA, meanB, change, low, high, spreadA, spreadB, verdict);
    }

    /// <summary>Квантиль 0,975 распределения Стьюдента; дробные степени свободы округляются вниз (осторожнее).</summary>
    public static double TCritical95(double df)
    {
        if (double.IsNaN(df) || df < 1)
            return TTable[0];
        if (df <= 30)
            return TTable[(int)Math.Floor(df) - 1];
        if (df < 40)
            return 2.021 + (2.042 - 2.021) * (40 - df) / 10;
        if (df < 60)
            return 2.000 + (2.021 - 2.000) * (60 - df) / 20;
        if (df < 120)
            return 1.980 + (2.000 - 1.980) * (120 - df) / 60;
        return 1.960;
    }

    /// <summary>Разброс между прогонами: стандартное отклонение в процентах от среднего; null — прогон один.</summary>
    public static double? Spread(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
            return null;

        double mean = values.Average();
        return mean > 0 ? Math.Sqrt(Variance(values)) / mean * 100 : null;
    }

    private static double Variance(IReadOnlyList<double> values)
    {
        double mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
    }

    private static ChangeVerdict Direction(double change, bool higherIsBetter) =>
        (change > 0) == higherIsBetter ? ChangeVerdict.Better : ChangeVerdict.Worse;
}
