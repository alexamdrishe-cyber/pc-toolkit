using System.Diagnostics;
using PcToolkit.Core.Benchmarks.Gpu;
using PcToolkit.Core.Benchmarks.Disk;
using PcToolkit.Core.Benchmarks.Memory;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Benchmarks;

/// <summary>Результат замера одного вида нагрузки.</summary>
public sealed record CpuBenchmarkScore(CpuWorkload Workload, double SingleThread, double MultiThread, string Unit);

public sealed record CpuBenchmarkProgress(CpuWorkload Workload, bool MultiThread, int Step, int Steps);

/// <summary>Замер производительности: каждая нагрузка сначала в одном потоке, затем во всех.</summary>
public static class CpuBenchmark
{
    public static readonly CpuWorkload[] AllWorkloads =
        { CpuWorkload.Integer, CpuWorkload.Float, CpuWorkload.Cache, CpuWorkload.Memory };

    public static IReadOnlyList<CpuBenchmarkScore> Run(
        IReadOnlyList<CpuWorkload> workloads,
        TimeSpan durationPerStep,
        IProgress<CpuBenchmarkProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var scores = new List<CpuBenchmarkScore>();
        int steps = workloads.Count * 2;
        int step = 0;
        foreach (CpuWorkload workload in workloads)
        {
            progress?.Report(new CpuBenchmarkProgress(workload, false, ++step, steps));
            double single = Measure(workload, 1, durationPerStep, cancellation);
            progress?.Report(new CpuBenchmarkProgress(workload, true, ++step, steps));
            double multi = Measure(workload, Environment.ProcessorCount, durationPerStep, cancellation);
            scores.Add(new CpuBenchmarkScore(workload, single, multi, UnitOf(workload)));
        }

        AppLog.Info("Замер процессора: " + string.Join("; ", scores.Select(s =>
            $"{s.Workload} {s.SingleThread:0.##}/{s.MultiThread:0.##} {s.Unit}")));
        return scores;
    }

    public static string UnitOf(CpuWorkload workload) => workload switch
    {
        CpuWorkload.Integer => "млрд оп/с",
        CpuWorkload.Float => "GFLOPS",
        CpuWorkload.Cache => "млн обращ./с",
        CpuWorkload.Memory => "ГБ/с",
        _ => "",
    };

    /// <summary>Производительность нагрузки в указанном числе потоков.</summary>
    private static double Measure(CpuWorkload workload, int threads, TimeSpan duration, CancellationToken cancellation)
    {
        CalibratedWorkload? calibrated = workload == CpuWorkload.Memory ? null : CpuStressTest.Calibrate(workload);
        long memoryWords = workload == CpuWorkload.Memory
            ? Math.Max(1024, Math.Min(CpuStressTest.DefaultMemoryBytes(), 2L * 1024 * 1024 * 1024) / threads / sizeof(ulong))
            : 0;

        long totalUnits = 0;
        var start = new ManualResetEventSlim(false);
        var workers = new Thread[threads];
        var watch = new Stopwatch();
        for (int t = 0; t < threads; t++)
        {
            int index = t;
            workers[t] = new Thread(() =>
            {
                ulong[]? cache = workload == CpuWorkload.Cache ? new ulong[CpuKernels.CacheWords] : null;
                ulong[]? memory = memoryWords > 0 ? GC.AllocateUninitializedArray<ulong>((int)memoryWords) : null;
                ulong pass = (ulong)index;
                start.Wait();
                long units = 0;
                while (watch.Elapsed < duration && !cancellation.IsCancellationRequested)
                {
                    if (workload == CpuWorkload.Memory)
                    {
                        CpuKernels.Memory(memory!, pass++);
                        units += memory!.LongLength * sizeof(ulong) * 2; // запись + чтение
                    }
                    else
                    {
                        CpuStressTest.Run(workload, calibrated!.Rounds, cache);
                        units += calibrated.Rounds;
                    }
                }

                Interlocked.Add(ref totalUnits, units);
            })
            { IsBackground = true, Priority = ThreadPriority.Normal, Name = $"PcToolkit.CpuBench.{t}" };
            workers[t].Start();
        }

        watch.Start();
        start.Set();
        foreach (Thread worker in workers)
            worker.Join();
        watch.Stop();
        cancellation.ThrowIfCancellationRequested();

        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.001);
        return workload switch
        {
            CpuWorkload.Integer => totalUnits * (double)CpuKernels.IntegerOpsPerRound / seconds / 1e9,
            CpuWorkload.Float => totalUnits * (double)CpuKernels.FloatOpsPerRound / seconds / 1e9,
            CpuWorkload.Cache => totalUnits / seconds / 1e6,
            CpuWorkload.Memory => totalUnits / seconds / (1024.0 * 1024 * 1024),
            _ => 0,
        };
    }
}

public sealed class CpuBenchmarkRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset At { get; set; }

    public string Cpu { get; set; } = "";

    /// <summary>Логических процессоров (потоков). 0 — неизвестно (старые записи).</summary>
    public int Threads { get; set; }

    /// <summary>Версия теста, которой сделан замер (<see cref="CpuScore.Version"/>).</summary>
    public int ScoreVersion { get; set; } = CpuScore.Version;

    /// <summary>Пусто — замер на этом ПК; иначе имя файла, из которого результат добавлен.</summary>
    public string Source { get; set; } = "";

    public List<CpuBenchmarkScore> Scores { get; set; } = new();

    [JsonIgnore]
    public CpuScoreResult? Score => ScoreVersion == CpuScore.Version ? CpuScore.Compute(Scores) : null;
}

/// <summary>
/// История замеров в %LocalAppData%\PcToolkit\benchmarks.json: свои замеры (с чем сравнивать после разгона или чистки)
/// и результаты с других ПК, добавленные из файлов.
/// </summary>
public sealed class BenchmarkHistory
{
    private const int MaxRecords = 50;
    private const int MaxImported = 200;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly List<CpuBenchmarkRecord> _cpu;
    private readonly List<CpuBenchmarkRecord> _imported;
    private readonly List<GpuBenchmarkRecord> _gpu;
    private readonly List<MemoryBenchmarkRecord> _memory;
    private readonly List<DiskBenchmarkRecord> _disk;

    private BenchmarkHistory(string path, List<CpuBenchmarkRecord> cpu, List<CpuBenchmarkRecord> imported,
        List<GpuBenchmarkRecord> gpu, List<MemoryBenchmarkRecord> memory, List<DiskBenchmarkRecord> disk)
    {
        _disk = disk;
        _path = path;
        _cpu = cpu;
        _imported = imported;
        _gpu = gpu;
        _memory = memory;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "benchmarks.json");

    /// <summary>Замеры процессора на этом ПК от новых к старым.</summary>
    public IReadOnlyList<CpuBenchmarkRecord> Cpu => _cpu;

    /// <summary>Результаты процессоров с других ПК, добавленные из файлов.</summary>
    public IReadOnlyList<CpuBenchmarkRecord> ImportedCpu => _imported;

    /// <summary>Замеры видеокарты на этом ПК от новых к старым.</summary>
    public IReadOnlyList<GpuBenchmarkRecord> Gpu => _gpu;

    /// <summary>Замеры скорости памяти от новых к старым.</summary>
    public IReadOnlyList<MemoryBenchmarkRecord> Memory => _memory;

    /// <summary>Замеры дисков от новых к старым.</summary>
    public IReadOnlyList<DiskBenchmarkRecord> Disk => _disk;

    public static BenchmarkHistory Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(path), JsonOptions);
                return new BenchmarkHistory(path, file?.Cpu ?? new(), file?.ImportedCpu ?? new(), file?.Gpu ?? new(), file?.Memory ?? new(), file?.Disk ?? new());
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать историю замеров, начинаю новую.", ex);
        }

        return new BenchmarkHistory(path, new(), new(), new(), new(), new());
    }

    public void AddCpu(CpuBenchmarkRecord record)
    {
        _cpu.Insert(0, record);
        if (_cpu.Count > MaxRecords)
            _cpu.RemoveRange(MaxRecords, _cpu.Count - MaxRecords);
        Save();
    }

    public void AddDisk(DiskBenchmarkRecord record)
    {
        _disk.Insert(0, record);
        if (_disk.Count > MaxRecords)
            _disk.RemoveRange(MaxRecords, _disk.Count - MaxRecords);
        Save();
    }

    public void AddMemory(MemoryBenchmarkRecord record)
    {
        _memory.Insert(0, record);
        if (_memory.Count > MaxRecords)
            _memory.RemoveRange(MaxRecords, _memory.Count - MaxRecords);
        Save();
    }

    public void AddGpu(GpuBenchmarkRecord record)
    {
        _gpu.Insert(0, record);
        if (_gpu.Count > MaxRecords)
            _gpu.RemoveRange(MaxRecords, _gpu.Count - MaxRecords);
        Save();
    }

    /// <summary>Добавляет результат из файла. false — этот результат уже есть.</summary>
    public bool AddImportedCpu(CpuBenchmarkRecord record)
    {
        if (_imported.Any(r => r.Id == record.Id) || _cpu.Any(r => r.Id == record.Id))
            return false;

        _imported.Add(record);
        if (_imported.Count > MaxImported)
            _imported.RemoveRange(0, _imported.Count - MaxImported);
        Save();
        return true;
    }

    public void RemoveImportedCpu(Guid id)
    {
        if (_imported.RemoveAll(r => r.Id == id) > 0)
            Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new HistoryFile { Cpu = _cpu, ImportedCpu = _imported, Gpu = _gpu, Memory = _memory, Disk = _disk }, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить историю замеров.", ex);
        }
    }

    private sealed class HistoryFile
    {
        public List<CpuBenchmarkRecord> Cpu { get; set; } = new();

        public List<CpuBenchmarkRecord> ImportedCpu { get; set; } = new();

        public List<GpuBenchmarkRecord> Gpu { get; set; } = new();

        public List<MemoryBenchmarkRecord> Memory { get; set; } = new();

        public List<DiskBenchmarkRecord> Disk { get; set; } = new();
    }
}
