using PcToolkit.Core.Changes;
using PcToolkit.Core.Overclock;

namespace PcToolkit.Core.Tests;

public sealed class OverclockTests
{
    [Theory]
    [InlineData(2603, 2600)]
    [InlineData(2608, 2610)]
    [InlineData(100, 500)]
    [InlineData(9999, 3000)]
    public void RangeSnapsToStep(int value, int expected)
    {
        var range = new TuningRange(500, 3000, 10);
        Assert.Equal(expected, range.Snap(value));
        Assert.True(range.Contains(expected));
    }

    [Fact]
    public void FanCurveRoundTrips()
    {
        var points = new List<FanPoint> { new(35, 20), new(50, 30), new(65, 45), new(80, 65), new(90, 85) };
        string text = AdlxGpuTuning.FormatCurve(points);
        Assert.Equal("35:20;50:30;65:45;80:65;90:85", text);
        Assert.Equal(points, AdlxGpuTuning.ParseCurve(text));
    }

    [Theory]
    [InlineData(AdlxGpuTuning.GfxMaxFrequency, "2650", "2650 МГц")]
    [InlineData(AdlxGpuTuning.GfxVoltage, "1100", "1100 мВ")]
    [InlineData(AdlxGpuTuning.PowerLimit, "15", "+15 %")]
    [InlineData(AdlxGpuTuning.PowerLimit, "-6", "-6 %")]
    [InlineData(AdlxGpuTuning.FanZeroRpm, "1", "включена")]
    [InlineData(AdlxGpuTuning.Preset, "Rage", "Rage")]
    [InlineData(AdlxGpuTuning.FanCurve, "40:25;80:70", "40° → 25 %, 80° → 70 %")]
    public void DescribesValues(string parameter, string value, string expected)
    {
        Assert.Equal(expected, GpuParameters.Describe(parameter, value));
    }

    [Fact]
    public void FactoryResolvesGpuParameters()
    {
        ResolvedChange change = new SystemTargetFactory().Resolve(GpuTuningTarget.Definition(AdlxGpuTuning.GfxVoltage, "1150"));
        Assert.Equal("gpu:" + AdlxGpuTuning.GfxVoltage, change.Target.Key);
        Assert.False(change.Target.RequiresAdmin);
        Assert.Equal("1150", change.Desired.Data);
    }

    [Fact]
    public void ReadsAmdGpuWhenPresent()
    {
        // Только чтение: на компьютере с видеокартой AMD проверяем, что таблицы функций ADLX совпадают с драйвером.
        AdlxGpuTuning? tuning = AdlxGpuTuning.Shared(out string? error);
        if (tuning is null)
        {
            Assert.False(string.IsNullOrEmpty(error));
            return;
        }

        GpuTuningInfo info = tuning.Read();
        Assert.False(string.IsNullOrWhiteSpace(info.GpuName));
        Assert.True(info.VramMb > 0);
        if (info.MaxFrequencyRange is { } range)
        {
            Assert.True(range.Max > range.Min);
            Assert.InRange(info.MaxFrequency!.Value, range.Min, range.Max);
            Assert.Equal(info.MaxFrequency.Value.ToString(), tuning.Get(AdlxGpuTuning.GfxMaxFrequency));
        }

        if (info.PowerLimitRange is { } power)
            Assert.InRange(info.PowerLimit!.Value, power.Min, power.Max);
        if (info.FanCurve.Count > 0)
            Assert.Equal(AdlxGpuTuning.FormatCurve(info.FanCurve), tuning.Get(AdlxGpuTuning.FanCurve));
    }
}
