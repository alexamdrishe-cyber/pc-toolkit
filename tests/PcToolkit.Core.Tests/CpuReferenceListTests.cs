using PcToolkit.Core.Benchmarks;

namespace PcToolkit.Core.Tests;

public sealed class CpuReferenceListTests
{
    [Fact]
    public void ReferenceCpuEstimatesToOwnScale()
    {
        CpuReference reference = CpuReferenceList.All.Single(r => r.Name == "AMD Ryzen 5 5600");
        Assert.Equal(new CpuScoreResult(1000, 5142), reference.Estimate);
    }

    [Theory]
    [InlineData("AMD Ryzen 5 5600 6-Core Processor", "AMD Ryzen 5 5600")]
    [InlineData("AMD Ryzen 5 5600X 6-Core Processor", "AMD Ryzen 5 5600X")]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", "AMD Ryzen 7 7800X3D")]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12400F", "Intel Core i5-12400F")]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", "Intel Core i7-13700K")]
    [InlineData("Intel(R) Core(TM) Ultra 9 285K", "Intel Core Ultra 9 285K")]
    public void FindsModelBySystemName(string systemName, string expected)
    {
        Assert.Equal(expected, CpuReferenceList.Find(systemName)?.Name);
    }

    [Theory]
    [InlineData("AMD Ryzen 5 5500")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownModelIsNotGuessed(string? systemName)
    {
        Assert.Null(CpuReferenceList.Find(systemName));
    }

    [Fact]
    public void ListHasNoDuplicatesAndSaneNumbers()
    {
        Assert.Equal(CpuReferenceList.All.Count, CpuReferenceList.All.Select(r => r.Name).Distinct().Count());
        Assert.All(CpuReferenceList.All, r =>
        {
            Assert.InRange(r.CinebenchSingle, 1000, 3000);
            Assert.InRange(r.CinebenchMulti, 3000, 60000);
            Assert.True(r.Threads > 0);
        });
    }
}
