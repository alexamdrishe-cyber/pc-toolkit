namespace PcToolkit.Core.Benchmarks;

/// <summary>Итоговые баллы процессора: за один поток и за все потоки.</summary>
public sealed record CpuScoreResult(int SingleThread, int MultiThread);

/// <summary>
/// Итоговый балл как в Cinebench: одно число вместо нескольких.
/// Каждая нагрузка делится на эталон, берётся среднее геометрическое (ни одна нагрузка не перевешивает)
/// и умножается на 1000. Эталон — Ryzen 5 5600 в одном потоке, то есть у него 1000 баллов за 1 поток.
/// В баллы входят только вычисления (целые числа, плавающая точка, кэш). Скорость оперативной памяти
/// замеряется и показывается, но в баллы не входит: во всех потоках она упирается в саму память,
/// а не в процессор, и у 6- и 16-ядерного процессора на одинаковой памяти почти одна и та же.
/// С ней многопоточные баллы сжимались, и мощные процессоры отставали от своей оценки по Cinebench.
/// </summary>
public static class CpuScore
{
    /// <summary>
    /// Версия формулы и нагрузок. Меняется, если меняются ядра теста или формула: результаты разных версий не сравниваются.
    /// 2 — ядра теста всегда оптимизированы (версия 1 в отладочной сборке шла в 3–5 раз медленнее), память не входит в баллы.
    /// </summary>
    public const int Version = 2;

    public const string ReferenceName = "AMD Ryzen 5 5600, 1 поток";

    /// <summary>Нагрузки, из которых складываются баллы.</summary>
    public static IReadOnlyList<CpuWorkload> Workloads { get; } = new[] { CpuWorkload.Integer, CpuWorkload.Float, CpuWorkload.Cache };

    // Ryzen 5 5600 в одном потоке, без разгона: медианы трёх прогонов bench-cpu.cmd (25 октября 2026).
    private static readonly IReadOnlyDictionary<CpuWorkload, double> Reference = new Dictionary<CpuWorkload, double>
    {
        [CpuWorkload.Integer] = 11.24, // млрд оп/с
        [CpuWorkload.Float] = 72.9,    // GFLOPS (AVX2 + FMA)
        [CpuWorkload.Cache] = 680,     // млн обращений/с
    };

    public static bool IsComplete(IEnumerable<CpuBenchmarkScore> scores) =>
        Workloads.All(w => scores.Any(s => s.Workload == w));

    /// <summary>Баллы по результатам замера или null, если замерены не все нужные нагрузки.</summary>
    public static CpuScoreResult? Compute(IReadOnlyCollection<CpuBenchmarkScore> scores)
    {
        if (!IsComplete(scores))
            return null;

        double single = 0, multi = 0;
        foreach (CpuWorkload workload in Workloads)
        {
            CpuBenchmarkScore score = scores.First(s => s.Workload == workload);
            if (!(score.SingleThread > 0) || !(score.MultiThread > 0) ||
                double.IsInfinity(score.SingleThread) || double.IsInfinity(score.MultiThread))
                return null;

            single += Math.Log(score.SingleThread / Reference[workload]);
            multi += Math.Log(score.MultiThread / Reference[workload]);
        }

        return new CpuScoreResult(
            (int)Math.Round(1000 * Math.Exp(single / Workloads.Count)),
            (int)Math.Round(1000 * Math.Exp(multi / Workloads.Count)));
    }
}
