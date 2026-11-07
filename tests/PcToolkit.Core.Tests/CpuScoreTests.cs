using PcToolkit.Core.Benchmarks;

namespace PcToolkit.Core.Tests;

public sealed class CpuScoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pct-score-" + Guid.NewGuid().ToString("N"));

    public CpuScoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static List<CpuBenchmarkScore> ReferenceScores(double singleScale = 1, double multiScale = 6) => new()
    {
        new(CpuWorkload.Integer, 11.24 * singleScale, 11.24 * multiScale, "млрд оп/с"),
        new(CpuWorkload.Float, 72.9 * singleScale, 72.9 * multiScale, "GFLOPS"),
        new(CpuWorkload.Cache, 680 * singleScale, 680 * multiScale, "млн обращ./с"),
        new(CpuWorkload.Memory, 13 * singleScale, 13 * multiScale, "ГБ/с"),
    };

    [Fact]
    public void ReferenceCpuScoresThousandPerThread()
    {
        CpuScoreResult? score = CpuScore.Compute(ReferenceScores());

        Assert.NotNull(score);
        Assert.Equal(1000, score.SingleThread);
        Assert.Equal(6000, score.MultiThread);
    }

    [Fact]
    public void TwiceFasterCpuScoresTwice()
    {
        Assert.Equal(2000, CpuScore.Compute(ReferenceScores(2, 2))!.SingleThread);
    }

    [Fact]
    public void NoScoreWithoutComputeWorkloads()
    {
        List<CpuBenchmarkScore> scores = ReferenceScores();
        scores.RemoveAll(s => s.Workload == CpuWorkload.Cache);

        Assert.Null(CpuScore.Compute(scores));
    }

    [Fact]
    public void MemorySpeedDoesNotChangeScore()
    {
        // Во всех потоках скорость памяти упирается в саму память, а не в процессор: в баллы она не входит.
        List<CpuBenchmarkScore> slowMemory = ReferenceScores();
        slowMemory[3] = slowMemory[3] with { SingleThread = 5, MultiThread = 9 };
        List<CpuBenchmarkScore> noMemory = ReferenceScores();
        noMemory.RemoveAt(3);

        Assert.Equal(CpuScore.Compute(ReferenceScores()), CpuScore.Compute(slowMemory));
        Assert.Equal(CpuScore.Compute(ReferenceScores()), CpuScore.Compute(noMemory));
    }

    [Fact]
    public void ExportedResultImportsBack()
    {
        var record = new CpuBenchmarkRecord
        {
            At = DateTimeOffset.Now, Cpu = "Test CPU", Threads = 12, Scores = ReferenceScores(1.1, 7),
        };
        string path = Path.Combine(_dir, BenchmarkExchange.SuggestedFileName(record));

        BenchmarkExchange.ExportCpu(record, path);
        CpuBenchmarkRecord imported = BenchmarkExchange.ImportCpu(path);

        Assert.Equal(record.Id, imported.Id);
        Assert.Equal("Test CPU", imported.Cpu);
        Assert.Equal(12, imported.Threads);
        Assert.Equal(record.Score, imported.Score);
        Assert.Equal(Path.GetFileName(path), imported.Source);
    }

    [Fact]
    public void ImportRejectsForeignAndBrokenFiles()
    {
        string junk = Path.Combine(_dir, "junk.pctbench");
        File.WriteAllText(junk, "{ \"hello\": 1 }");
        string broken = Path.Combine(_dir, "broken.pctbench");
        File.WriteAllText(broken, "not json");

        Assert.Throws<BenchmarkImportException>(() => BenchmarkExchange.ImportCpu(junk));
        Assert.Throws<BenchmarkImportException>(() => BenchmarkExchange.ImportCpu(broken));
    }

    [Fact]
    public void ImportRejectsOtherTestVersionAndBadNumbers()
    {
        var record = new CpuBenchmarkRecord { At = DateTimeOffset.Now, Cpu = "X", Scores = ReferenceScores() };
        string path = Path.Combine(_dir, "a.pctbench");
        BenchmarkExchange.ExportCpu(record, path);

        File.WriteAllText(path, File.ReadAllText(path).Replace($"\"ScoreVersion\": {CpuScore.Version}", "\"ScoreVersion\": 99"));
        Assert.Throws<BenchmarkImportException>(() => BenchmarkExchange.ImportCpu(path));

        BenchmarkExchange.ExportCpu(record, path);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"SingleThread\": 11.24", "\"SingleThread\": -5"));
        Assert.Throws<BenchmarkImportException>(() => BenchmarkExchange.ImportCpu(path));
    }

    [Fact]
    public void HistoryKeepsImportedSeparatelyAndSkipsDuplicates()
    {
        string file = Path.Combine(_dir, "benchmarks.json");
        BenchmarkHistory history = BenchmarkHistory.Load(file);
        var own = new CpuBenchmarkRecord { At = DateTimeOffset.Now, Cpu = "Mine", Scores = ReferenceScores() };
        history.AddCpu(own);
        var other = new CpuBenchmarkRecord { At = DateTimeOffset.Now, Cpu = "Other", Scores = ReferenceScores(2, 10) };

        Assert.True(history.AddImportedCpu(other));
        Assert.False(history.AddImportedCpu(other));
        Assert.False(history.AddImportedCpu(own));

        BenchmarkHistory reloaded = BenchmarkHistory.Load(file);
        Assert.Single(reloaded.Cpu);
        Assert.Single(reloaded.ImportedCpu);
        Assert.Equal(own.Id, reloaded.Cpu[0].Id);

        reloaded.RemoveImportedCpu(other.Id);
        Assert.Empty(BenchmarkHistory.Load(file).ImportedCpu);
    }
}
