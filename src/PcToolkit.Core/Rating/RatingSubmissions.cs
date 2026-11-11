using System.Globalization;
using PcToolkit.Core.Benchmarks;
using PcToolkit.Core.Benchmarks.Disk;
using PcToolkit.Core.Benchmarks.Gpu;
using PcToolkit.Core.Benchmarks.Memory;

namespace PcToolkit.Core.Rating;

/// <summary>
/// Что из замера уходит в рейтинг. Только модель устройства и баллы: ни имени компьютера, ни пользователя,
/// ни серийных номеров. Номер отправки — номер самого замера, поэтому повторная отправка не задвоится.
/// </summary>
public static class RatingSubmissions
{
    public const string SequentialTest = "SEQ1M Q8T1";
    public const string RandomTest = "RND4K Q1T1";

    /// <summary>Свежие замеры: старше этого в рейтинг не уходят и «своим устройством» не считаются.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(120);

    /// <summary>Версия теста для вида: таблица запрашивается по ней (результаты разных версий не сравниваются).</summary>
    public static int Version(string kind) => kind switch
    {
        RatingKinds.Cpu => CpuScore.Version,
        RatingKinds.Gpu => GpuScore.Version,
        RatingKinds.Memory => MemoryBenchmark.Version,
        RatingKinds.Disk => DiskBenchmark.Version,
        _ => 0,
    };

    public static RatingSubmission? From(object record) => record switch
    {
        CpuBenchmarkRecord cpu => FromCpu(cpu),
        GpuBenchmarkRecord gpu => FromGpu(gpu),
        MemoryBenchmarkRecord memory => FromMemory(memory),
        DiskBenchmarkRecord disk => FromDisk(disk),
        _ => null,
    };

    /// <summary>Процессор: баллы во всех потоках, второе число — в одном потоке. Только полный замер на этом ПК.</summary>
    public static RatingSubmission? FromCpu(CpuBenchmarkRecord record)
    {
        if (record.Source.Length > 0 || record.Cpu.Trim().Length == 0 || record.Score is not { MultiThread: > 0 } score)
            return null;
        return new RatingSubmission
        {
            ClientId = record.Id.ToString("N"),
            Kind = RatingKinds.Cpu,
            Model = record.Cpu.Trim(),
            Score = score.MultiThread,
            Score2 = score.SingleThread,
            TestVersion = record.ScoreVersion,
            Conditions = record.Threads > 0 ? new() { ["threads"] = record.Threads.ToString(CultureInfo.InvariantCulture) } : null,
        };
    }

    public static RatingSubmission? FromGpu(GpuBenchmarkRecord record)
    {
        if (record.Gpu.Trim().Length == 0 || record.Score is not { } score)
            return null;
        return new RatingSubmission
        {
            ClientId = record.Id.ToString("N"),
            Kind = RatingKinds.Gpu,
            Model = record.Gpu.Trim(),
            Score = score,
            TestVersion = record.ScoreVersion,
            Conditions = new()
            {
                ["fill"] = Round(record.Fill, 1),
                ["gflops"] = Round(record.Gflops, 0),
                ["bandwidth"] = Round(record.Bandwidth, 0),
            },
        };
    }

    /// <summary>Память: чтение ГБ/с, второе число — задержка нс. Группа — тип, частота и каналы («DDR4-3200, 2 модуля, два канала»).</summary>
    public static RatingSubmission? FromMemory(MemoryBenchmarkRecord record)
    {
        if (record.Memory.Trim().Length == 0 || !(record.Read > 0))
            return null;
        return new RatingSubmission
        {
            ClientId = record.Id.ToString("N"),
            Kind = RatingKinds.Memory,
            Model = record.Memory.Trim(),
            Score = Math.Round(record.Read, 2),
            Score2 = record.Latency > 0 ? Math.Round(record.Latency, 1) : null,
            TestVersion = MemoryBenchmark.Version,
            Conditions = record.Theoretical > 0 ? new() { ["theoretical"] = Round(record.Theoretical, 1) } : null,
        };
    }

    /// <summary>Диск: последовательное чтение SEQ1M Q8T1, второе число — чтение вразброс RND4K Q1T1 (отзывчивость).</summary>
    public static RatingSubmission? FromDisk(DiskBenchmarkRecord record)
    {
        if (record.Model.Trim().Length == 0 || record[SequentialTest] is not { ReadMBps: > 0 } sequential)
            return null;
        return new RatingSubmission
        {
            ClientId = record.Id.ToString("N"),
            Kind = RatingKinds.Disk,
            Model = record.Model.Trim(),
            Score = Math.Round(sequential.ReadMBps, 1),
            Score2 = record[RandomTest] is { ReadMBps: > 0 } random ? Math.Round(random.ReadMBps, 2) : null,
            TestVersion = DiskBenchmark.Version,
            Conditions = new() { ["fileMb"] = (record.FileSize >> 20).ToString(CultureInfo.InvariantCulture) },
        };
    }

    /// <summary>
    /// Свои устройства этого вида — последний свежий замер каждого. Процессор, видеокарта и память — одно устройство
    /// (последнее: после замены старое уже не своё), дисков может быть несколько.
    /// </summary>
    public static IReadOnlyList<RatingSubmission> Mine(BenchmarkHistory history, string kind, DateTimeOffset now)
    {
        IEnumerable<RatingSubmission> fresh = kind switch
        {
            RatingKinds.Cpu => history.Cpu.Where(r => now - r.At < MaxAge).Select(FromCpu).OfType<RatingSubmission>().Take(1),
            RatingKinds.Gpu => history.Gpu.Where(r => now - r.At < MaxAge).Select(FromGpu).OfType<RatingSubmission>().Take(1),
            RatingKinds.Memory => history.Memory.Where(r => now - r.At < MaxAge).Select(FromMemory).OfType<RatingSubmission>().Take(1),
            RatingKinds.Disk => history.Disk.Where(r => now - r.At < MaxAge).Select(FromDisk).OfType<RatingSubmission>()
                .GroupBy(s => RatingStats.ModelKey(s.Model), StringComparer.OrdinalIgnoreCase).Select(g => g.First()),
            _ => Enumerable.Empty<RatingSubmission>(),
        };
        return fresh.ToList();
    }

    private static string Round(double value, int digits) => Math.Round(value, digits).ToString(CultureInfo.InvariantCulture);
}
