using Microsoft.Win32;
using PcToolkit.Core.Benchmarks;

// Калибровка: прогоняет замер процессора несколько раз и печатает медианы.
// На эталонном процессоре (Ryzen 5 5600) медианы одного потока — это CpuScore.Reference,
// а «многопоточный балл эталона» — CpuReferenceList.ReferenceScoreMulti.
Console.OutputEncoding = System.Text.Encoding.UTF8;
int runs = args.Length > 0 && int.TryParse(args[0], out int requested) ? Math.Clamp(requested, 1, 10) : 3;
Console.WriteLine($"Процессор: {CpuName()}, потоков: {Environment.ProcessorCount}, прогонов: {runs}");
Console.WriteLine($"Вещественная нагрузка: {CpuKernels.FloatInstructionSet}");

var all = new List<IReadOnlyList<CpuBenchmarkScore>>();
for (int i = 0; i < runs; i++)
{
    IReadOnlyList<CpuBenchmarkScore> scores = CpuBenchmark.Run(CpuBenchmark.AllWorkloads, TimeSpan.FromSeconds(3));
    all.Add(scores);
    Console.WriteLine($"Прогон {i + 1}: " + string.Join("; ", scores.Select(s => $"{s.Workload} {s.SingleThread:0.###} / {s.MultiThread:0.###}")) +
                      $"  →  баллы {CpuScore.Compute(scores.ToList())?.ToString() ?? "—"}");
}

Console.WriteLine();
Console.WriteLine("Медианы (один поток / все потоки, во сколько раз):");
var medians = new Dictionary<CpuWorkload, (double Single, double Multi)>();
foreach (CpuWorkload workload in CpuBenchmark.AllWorkloads)
{
    double single = Median(all.Select(s => s.First(x => x.Workload == workload).SingleThread));
    double multi = Median(all.Select(s => s.First(x => x.Workload == workload).MultiThread));
    medians[workload] = (single, multi);
    Console.WriteLine($"  {workload,-8} {single,10:0.###} {multi,10:0.###}   x{multi / single:0.00}  {CpuBenchmark.UnitOf(workload)}");
}

var median = CpuBenchmark.AllWorkloads.Select(w => new CpuBenchmarkScore(w, medians[w].Single, medians[w].Multi, CpuBenchmark.UnitOf(w))).ToList();
Console.WriteLine();
Console.WriteLine($"Баллы по медианам (текущая формула): {CpuScore.Compute(median)?.ToString() ?? "—"}");
Console.WriteLine("Если этот процессор — эталон (1000 баллов за один поток):");
foreach (CpuWorkload[] set in new[] { CpuScore.Workloads.ToArray(), CpuBenchmark.AllWorkloads })
{
    double log = set.Average(w => Math.Log(medians[w].Multi / medians[w].Single));
    Console.WriteLine($"  все потоки по {string.Join(", ", set)}: {1000 * Math.Exp(log):0}");
}

return 0;

static double Median(IEnumerable<double> values)
{
    double[] sorted = values.Order().ToArray();
    return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
}

static string CpuName()
{
    try
    {
        return Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string ?? "?";
    }
    catch
    {
        return "?";
    }
}
