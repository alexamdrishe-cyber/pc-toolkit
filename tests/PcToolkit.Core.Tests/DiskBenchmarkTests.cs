using PcToolkit.Core.Benchmarks.Disk;

namespace PcToolkit.Core.Tests;

public sealed class DiskBenchmarkTests
{
    [Fact]
    public async Task QuickRunMeasuresAllTestsAndRemovesFile()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string folder = DiskBenchmark.TestFolder(root);
        int filesBefore = Directory.Exists(folder) ? Directory.GetFiles(folder).Length : 0;

        IReadOnlyList<DiskTestResult> results = await DiskBenchmark.RunAsync(root, 16L << 20, 1, TimeSpan.FromMilliseconds(200));

        Assert.Equal(DiskBenchmark.Specs.Count, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.ReadMBps > 0, $"{r.Name}: чтение 0");
            Assert.True(r.WriteMBps > 0, $"{r.Name}: запись 0");
        });
        Assert.True(results[2].ReadIops > 0);
        int filesAfter = Directory.Exists(folder) ? Directory.GetFiles(folder).Length : 0;
        Assert.Equal(filesBefore, filesAfter);
    }

    [Theory]
    [InlineData(3400, "NVMe PCIe 3.0")]
    [InlineData(6900, "NVMe PCIe 4.0")]
    [InlineData(540, "SSD SATA")]
    [InlineData(190, "Жёсткий диск (HDD)")]
    public void ClosestClass(double sequentialRead, string expected)
    {
        Assert.Equal(expected, DiskClasses.Closest(sequentialRead).Name);
    }
}
