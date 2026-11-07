using System.Text.Json;

namespace PcToolkit.Core.Benchmarks;

public sealed class BenchmarkImportException(string message) : Exception(message);

/// <summary>
/// Файл результата для обмена между ПК (*.pctbench, внутри JSON).
/// В файле только модель процессора, число потоков, дата и замеры — ни имени компьютера, ни пользователя.
/// Баллы при чтении пересчитываются из замеров, числам «баллов» в файле не доверяем.
/// </summary>
public static class BenchmarkExchange
{
    public const string Extension = ".pctbench";
    public const string FileFilter = "Результат PC Toolkit (*.pctbench)|*.pctbench";

    private const string Format = "PcToolkit.Benchmark";
    private const int FormatVersion = 1;
    private const long MaxFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SuggestedFileName(CpuBenchmarkRecord record)
    {
        string cpu = string.Concat((record.Cpu.Length > 0 ? record.Cpu : "CPU")
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return $"{cpu} {record.At.LocalDateTime:yyyy-MM-dd HH-mm}{Extension}";
    }

    public static void ExportCpu(CpuBenchmarkRecord record, string path)
    {
        if (record.Score is null)
            throw new InvalidOperationException("В этом замере нет итоговых баллов: нужны нагрузки «целые числа», «плавающая точка» и «кэш».");

        var file = new ExchangeFile
        {
            Format = Format,
            FormatVersion = FormatVersion,
            Kind = "cpu",
            Cpu = new CpuBenchmarkRecord
            {
                Id = record.Id,
                At = record.At,
                Cpu = record.Cpu,
                Threads = record.Threads,
                ScoreVersion = record.ScoreVersion,
                Scores = record.Scores,
            },
            Score = record.Score,
        };

        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Читает и проверяет файл. Ошибки — <see cref="BenchmarkImportException"/> с понятным текстом.</summary>
    public static CpuBenchmarkRecord ImportCpu(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new BenchmarkImportException("Файл не найден.");
        if (info.Length > MaxFileBytes)
            throw new BenchmarkImportException("Это не файл результата PC Toolkit: он слишком большой.");

        ExchangeFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ExchangeFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            throw new BenchmarkImportException("Это не файл результата PC Toolkit или он повреждён.");
        }

        if (file is null || file.Format != Format || file.Cpu is null)
            throw new BenchmarkImportException("Это не файл результата PC Toolkit.");
        if (file.FormatVersion > FormatVersion)
            throw new BenchmarkImportException("Файл сохранён более новой версией PC Toolkit. Обновите программу.");
        if (file.Kind != "cpu")
            throw new BenchmarkImportException("В файле результат не процессора.");

        CpuBenchmarkRecord record = file.Cpu;
        if (record.ScoreVersion != CpuScore.Version)
            throw new BenchmarkImportException(
                $"Результат получен другой версией теста (версия {record.ScoreVersion}, у вас {CpuScore.Version}): баллы несопоставимы.");

        foreach (CpuBenchmarkScore score in record.Scores)
        {
            if (!IsSane(score.SingleThread) || !IsSane(score.MultiThread))
                throw new BenchmarkImportException("В файле некорректные числа замера.");
        }

        if (CpuScore.Compute(record.Scores) is null)
            throw new BenchmarkImportException("В файле не хватает замеров: нужны нагрузки «целые числа», «плавающая точка» и «кэш».");

        string cpu = (record.Cpu ?? "").Trim();
        if (cpu.Length > 120)
            cpu = cpu[..120];

        return new CpuBenchmarkRecord
        {
            Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id,
            At = record.At,
            Cpu = cpu.Length > 0 ? cpu : "Неизвестный процессор",
            Threads = Math.Clamp(record.Threads, 0, 4096),
            ScoreVersion = record.ScoreVersion,
            Source = Path.GetFileName(path),
            Scores = record.Scores
                .Select(s => s with { Unit = CpuBenchmark.UnitOf(s.Workload) })
                .ToList(),
        };
    }

    private static bool IsSane(double value) => double.IsFinite(value) && value > 0 && value < 1e9;

    private sealed class ExchangeFile
    {
        public string Format { get; set; } = "";

        public int FormatVersion { get; set; }

        public string Kind { get; set; } = "";

        public CpuBenchmarkRecord? Cpu { get; set; }

        /// <summary>Для человека, открывшего файл в блокноте; при чтении не используется.</summary>
        public CpuScoreResult? Score { get; set; }
    }
}
