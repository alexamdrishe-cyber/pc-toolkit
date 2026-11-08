using PcToolkit.Core.Games;

namespace PcToolkit.Core.Tests;

public sealed class FpsComparisonTests
{
    [Fact]
    public void ConsistentRunsShowRealGain()
    {
        MetricChange c = FpsComparison.Compare(new double[] { 100, 101, 99 }, new double[] { 110, 111, 109 });

        Assert.Equal(ChangeVerdict.Better, c.Verdict);
        Assert.Equal(10, c.ChangePercent, 3);
        Assert.True(c.HasInterval);
        Assert.True(c.LowPercent > 0 && c.HighPercent > c.LowPercent);
        Assert.Equal(1, c.SpreadA!.Value, 3); // стандартное отклонение 1 при среднем 100
    }

    [Fact]
    public void ScatteredRunsAreUnclear()
    {
        MetricChange c = FpsComparison.Compare(new double[] { 100, 110, 90 }, new double[] { 103, 93, 113 });

        Assert.Equal(ChangeVerdict.Unclear, c.Verdict);
        Assert.True(c.LowPercent < 0 && c.HighPercent > 0);
    }

    [Fact]
    public void TinyDifferenceWithTightRunsIsSame()
    {
        MetricChange c = FpsComparison.Compare(new double[] { 100, 100.2, 99.8 }, new double[] { 100.5, 100.7, 100.3 });

        Assert.Equal(ChangeVerdict.Same, c.Verdict);
        Assert.InRange(c.ChangePercent, 0.49, 0.51);
    }

    [Fact]
    public void SingleRunsUseThreePercentRule()
    {
        Assert.Equal(ChangeVerdict.Better, FpsComparison.Compare(new double[] { 100 }, new double[] { 105 }).Verdict);
        Assert.Equal(ChangeVerdict.Worse, FpsComparison.Compare(new double[] { 100 }, new double[] { 90 }).Verdict);
        MetricChange same = FpsComparison.Compare(new double[] { 100 }, new double[] { 102 });
        Assert.Equal(ChangeVerdict.Same, same.Verdict);
        Assert.False(same.HasInterval);
        Assert.Null(same.SpreadA);
    }

    [Fact]
    public void LowerIsBetterForFrameTime()
    {
        MetricChange c = FpsComparison.Compare(new double[] { 10, 10.1, 9.9 }, new double[] { 9, 9.1, 8.9 }, higherIsBetter: false);

        Assert.Equal(ChangeVerdict.Better, c.Verdict);
        Assert.True(c.ChangePercent < 0);
    }

    [Fact]
    public void IdenticalRunsHaveZeroWidthInterval()
    {
        MetricChange same = FpsComparison.Compare(new double[] { 100, 100 }, new double[] { 100, 100 });
        Assert.Equal(ChangeVerdict.Same, same.Verdict);
        Assert.Equal(0, same.LowPercent);

        MetricChange gain = FpsComparison.Compare(new double[] { 100, 100 }, new double[] { 110, 110 });
        Assert.Equal(ChangeVerdict.Better, gain.Verdict);
        Assert.Equal(10, gain.LowPercent!.Value, 3);
    }

    [Fact]
    public void StudentQuantiles()
    {
        Assert.Equal(12.706, FpsComparison.TCritical95(1));
        Assert.Equal(4.303, FpsComparison.TCritical95(2.9)); // дробные степени свободы — вниз, осторожнее
        Assert.Equal(2.042, FpsComparison.TCritical95(30));
        Assert.Equal(1.96, FpsComparison.TCritical95(1000));
        Assert.Equal(12.706, FpsComparison.TCritical95(double.NaN));
    }
}
