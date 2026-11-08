using PcToolkit.Core.Diagnostics;

namespace PcToolkit.Core.Tests;

public sealed class ThermalHistoryTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 8);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pct-thermal-" + Guid.NewGuid().ToString("N"));

    public ThermalHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ThermalDay Day(DateOnly date, double cpuHigh, int samples = 400) => new()
    {
        Date = date,
        CpuHigh = new ThermalBand { Sum = cpuHigh * samples, Count = samples },
    };

    [Fact]
    public void WarmerUnderSameLoadGivesTip()
    {
        var days = new List<ThermalDay>
        {
            Day(Today.AddDays(-60), 68), Day(Today.AddDays(-55), 70),
            Day(Today.AddDays(-3), 79), Day(Today.AddDays(-1), 78),
        };

        Finding? tip = ThermalHistory.InMemory(days).CheckTrend(Today);

        Assert.NotNull(tip);
        Assert.True(tip.IsTip);
        Assert.Equal("При той же нагрузке процессор стал горячее на 10 °C", tip.Title);
        Assert.Contains("было в среднем 69 °C, сейчас — 79 °C", tip.Detail);
    }

    [Fact]
    public void SmallRiseOrTooLittleDataIsQuiet()
    {
        Assert.Null(ThermalHistory.InMemory(new[] { Day(Today.AddDays(-60), 70, 800), Day(Today.AddDays(-2), 75, 800) }).CheckTrend(Today));
        Assert.Null(ThermalHistory.InMemory(new[] { Day(Today.AddDays(-60), 60, 100), Day(Today.AddDays(-2), 80, 800) }).CheckTrend(Today));
        // Данные есть только за последние две недели — сравнивать не с чем.
        Assert.Null(ThermalHistory.InMemory(new[] { Day(Today.AddDays(-12), 60, 800), Day(Today.AddDays(-2), 80, 800) }).CheckTrend(Today));
    }

    [Fact]
    public void SamplesAreBandedByLoadAndSurviveRestart()
    {
        string path = Path.Combine(_dir, "thermal.json");
        ThermalHistory history = ThermalHistory.Load(path);
        DateTime morning = new(2026, 9, 1, 10, 0, 0);
        for (int i = 0; i < 700; i++)
            history.Add(morning.AddSeconds(i), cpuLoad: 95, cpuTemperature: 70, gpuLoad: 10, gpuTemperature: 40);
        history.Add(morning, cpuLoad: 20, cpuTemperature: 45, gpuLoad: null, gpuTemperature: null);
        history.Save();

        history = ThermalHistory.Load(path);
        DateTime evening = new(2026, 11, 7, 21, 0, 0);
        for (int i = 0; i < 700; i++)
            history.Add(evening.AddSeconds(i), cpuLoad: 90, cpuTemperature: 81, gpuLoad: 0, gpuTemperature: 35);

        Finding? tip = history.CheckTrend(Today);
        Assert.NotNull(tip);
        Assert.StartsWith("При той же нагрузке процессор стал горячее на 11 °C", tip.Title);
    }
}
