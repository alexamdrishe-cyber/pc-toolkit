using PcToolkit.Core.Benchmarks.Memory;
using PcToolkit.Core.Inventory;

namespace PcToolkit.Core.Tests;

public sealed class MemoryTestTests
{
    [Fact]
    public void SmallRunCompletesCycleWithoutErrors()
    {
        using var test = new MemoryTest(64L << 20, threads: 2);
        test.Start();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (test.CompletedCycles < 1 && DateTime.UtcNow < deadline && test.Failure is null)
            Thread.Sleep(50);
        test.Stop("тест");

        Assert.Null(test.Failure);
        Assert.True(test.CompletedCycles >= 1, "Цикл не завершился за минуту.");
        Assert.Equal(0, test.ErrorCount);
        Assert.Equal(MemoryTestPhase.Finished, test.Phase);
        Assert.True(test.ProcessedBytes > 64L << 20);
    }

    [Fact]
    public void TheoreticalBandwidthIsSpeedTimesEightTimesChannels()
    {
        var config = new MemoryConfig(32UL << 30, 2, 3200, "DDR4", 2);
        Assert.Equal(51.2, config.TheoreticalBandwidth, 3);
        Assert.Equal("DDR4-3200, 2 модуля, два канала", config.Description);
    }

    [Fact]
    public void FreeMemoryIsReported()
    {
        Assert.True(MemoryTest.AvailablePhysicalMemory() > 0);
        Assert.True(MemoryTest.TotalPhysicalMemory() >= MemoryTest.AvailablePhysicalMemory());
    }
}
