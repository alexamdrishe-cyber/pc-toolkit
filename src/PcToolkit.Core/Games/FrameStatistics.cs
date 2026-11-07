namespace PcToolkit.Core.Games;

/// <summary>
/// Итоги по кадрам за отрезок времени.
/// «1% low» и «0.1% low» — частота кадров, хуже которой были 1% и 0,1% кадров
/// (1000 / 99-й и 99,9-й процентиль времени кадра), как в PresentMon и MSI Afterburner.
/// Именно эти числа показывают фризы и рывки, которых не видно в среднем FPS.
/// </summary>
public sealed record FrameStatistics(
    int Frames,
    double Seconds,
    double AverageFps,
    double AverageFrameMs,
    double Low1Fps,
    double Low01Fps,
    double P99FrameMs,
    double MaxFrameMs)
{
    /// <summary>Считает итоги по длительностям кадров в миллисекундах. null — кадров слишком мало.</summary>
    public static FrameStatistics? FromFrameTimes(IReadOnlyList<double> frameTimesMs)
    {
        if (frameTimesMs.Count < 2)
            return null;

        double[] sorted = frameTimesMs.Where(t => t > 0 && double.IsFinite(t)).ToArray();
        if (sorted.Length < 2)
            return null;

        Array.Sort(sorted);
        double totalMs = sorted.Sum();
        double p99 = FrameMath.Percentile(sorted, 99);
        double p999 = FrameMath.Percentile(sorted, 99.9);
        return new FrameStatistics(
            sorted.Length,
            totalMs / 1000,
            sorted.Length * 1000 / totalMs,
            totalMs / sorted.Length,
            1000 / p99,
            1000 / p999,
            p99,
            sorted[^1]);
    }
}

public static class FrameMath
{
    /// <summary>Процентиль по возрастающему массиву (метод ближайшего ранга).</summary>
    public static double Percentile(double[] sorted, double percent)
    {
        if (sorted.Length == 0)
            return 0;

        int rank = (int)Math.Ceiling(percent / 100 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <summary>Длительности кадров в миллисекундах по отметкам времени QPC.</summary>
    public static double[] ToFrameTimes(IReadOnlyList<long> timestamps, long frequency)
    {
        if (timestamps.Count < 2)
            return Array.Empty<double>();

        var result = new double[timestamps.Count - 1];
        for (int i = 1; i < timestamps.Count; i++)
            result[i - 1] = (timestamps[i] - timestamps[i - 1]) * 1000.0 / frequency;
        return result;
    }
}
