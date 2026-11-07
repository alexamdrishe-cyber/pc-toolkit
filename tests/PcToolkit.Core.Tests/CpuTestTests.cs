using PcToolkit.Core.Benchmarks;

namespace PcToolkit.Core.Tests;

public sealed class CpuTestTests
{
    [Fact]
    public void KernelsAreDeterministic()
    {
        var cache = new ulong[CpuKernels.CacheWords];

        Assert.Equal(CpuKernels.Integer(100_000), CpuKernels.Integer(100_000));
        Assert.Equal(CpuKernels.Float(100_000), CpuKernels.Float(100_000));
        Assert.Equal(CpuKernels.Cache(cache, 100_000), CpuKernels.Cache(cache, 100_000));
        Assert.NotEqual(CpuKernels.Integer(100_000), CpuKernels.Integer(100_001));
    }

    [Fact]
    public void FloatKernelStaysFinite()
    {
        double value = CpuKernels.Float(5_000_000);

        Assert.True(double.IsFinite(value));
        Assert.InRange(value, 1, 1000);
    }

    [Fact]
    public void MemoryKernelFindsNoErrorsOnHealthyMemory()
    {
        var buffer = new ulong[1 << 16];

        Assert.Equal(0, CpuKernels.Memory(buffer, 1));
        Assert.Equal(0, CpuKernels.Memory(buffer, 2));
    }

    [Fact]
    public void ShortStressRunHasNoErrors()
    {
        using var test = new CpuStressTest(
            new[] { CpuWorkload.Integer, CpuWorkload.Float, CpuWorkload.Cache, CpuWorkload.Memory },
            threads: 2,
            memoryBytes: 16 * 1024 * 1024);
        test.Prepare();
        test.Start();
        Thread.Sleep(600);
        test.Stop();

        Assert.True(test.Slices > 0);
        Assert.Equal(0, test.ErrorCount);
        Assert.False(test.IsRunning);
    }

    [Fact]
    public void BenchmarkProducesPositiveScores()
    {
        IReadOnlyList<CpuBenchmarkScore> scores = CpuBenchmark.Run(
            new[] { CpuWorkload.Integer, CpuWorkload.Float }, TimeSpan.FromMilliseconds(200));

        Assert.Equal(2, scores.Count);
        Assert.All(scores, s => Assert.True(s.SingleThread > 0 && s.MultiThread > 0));
    }

    [Fact]
    public void HistoryKeepsNewestFirst()
    {
        string path = Path.Combine(Path.GetTempPath(), "PcToolkit.Tests." + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            BenchmarkHistory history = BenchmarkHistory.Load(path);
            history.AddCpu(new CpuBenchmarkRecord { At = DateTimeOffset.Now.AddDays(-1), Cpu = "old" });
            history.AddCpu(new CpuBenchmarkRecord { At = DateTimeOffset.Now, Cpu = "new" });

            BenchmarkHistory reloaded = BenchmarkHistory.Load(path);

            Assert.Equal("new", reloaded.Cpu[0].Cpu);
            Assert.Equal(2, reloaded.Cpu.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
