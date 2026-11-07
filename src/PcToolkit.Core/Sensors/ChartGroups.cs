namespace PcToolkit.Core.Sensors;

/// <summary>
/// Как сливать соседние значения линии в точки, когда значений больше, чем пикселей.
/// Границы групп привязаны к сквозному номеру значения (<see cref="HistoryWindow.FirstIndex"/>), а не
/// к последнему значению: новое значение меняет только самую правую точку, остальные точки те же и просто
/// сдвигаются влево. Если считать группы от последнего значения, каждое обновление разбивает значения
/// на пары по-новому, и шумная линия (загрузка процессора) прыгает целиком.
/// </summary>
public static class ChartGroups
{
    /// <summary>
    /// Сколько соседних значений сливать в одну точку, чтобы точек было не больше, чем пикселей.
    /// Шаг округляется до 10 мс: дрожание времени снимков (0,2498 вместо 0,25 с) не должно менять размер группы.
    /// </summary>
    public static int Size(double sampleSeconds, double pixelsPerSecond)
    {
        double stepPx = Math.Max(0.01, Math.Round(sampleSeconds, 2)) * pixelsPerSecond;
        if (!double.IsFinite(stepPx) || stepPx <= 0)
            return 1;
        return stepPx >= 1 ? 1 : (int)Math.Min(int.MaxValue, Math.Ceiling(1 / stepPx - 1e-9));
    }

    /// <summary>
    /// Начало первой полной группы не раньше <paramref name="start"/>. Неполная группа у левого края
    /// менялась бы с каждым снимком — её пропускаем (это меньше пикселя).
    /// </summary>
    public static int FirstFull(int start, long firstIndex, int group, int count)
    {
        start = Math.Max(0, start);
        if (group <= 1 || Offset(firstIndex + start, group) == 0 || count - start <= group)
            return start;
        return Next(start, firstIndex, group, count);
    }

    /// <summary>Индекс, с которого начинается следующая группа после той, в которую попал <paramref name="start"/>.</summary>
    public static int Next(int start, long firstIndex, int group, int count) =>
        group <= 1 ? start + 1 : (int)Math.Min(count, start + group - Offset(firstIndex + start, group));

    /// <summary>
    /// Точки линии: середина группы (сколько шагов от последнего значения) и среднее значение группы.
    /// Только значения начиная с <paramref name="start"/>.
    /// </summary>
    public static List<(double StepsFromEnd, double Value)> Average(IReadOnlyList<double> values, int start, long firstIndex, int group)
    {
        int count = values.Count;
        int first = FirstFull(start, firstIndex, group, count);
        var points = new List<(double, double)>(Math.Max(1, (count - first) / Math.Max(1, group) + 1));
        for (int from = first; from < count;)
        {
            int to = Next(from, firstIndex, group, count);
            double sum = 0;
            for (int i = from; i < to; i++)
                sum += values[i];
            points.Add(((count - 1) - (from + to - 1) / 2.0, sum / (to - from)));
            from = to;
        }

        return points;
    }

    private static long Offset(long value, int group) => ((value % group) + group) % group;
}
