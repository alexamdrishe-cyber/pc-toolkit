using PcToolkit.Core.Diagnostics;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Inventory;

namespace PcToolkit.Core.Tests;

public sealed class HealthFindingsTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);

    private static CrashReport Week(params RawEvent[] events) => CrashAnalyzer.Analyze(events, Now.AddDays(-7), Now);

    private static RawEvent BlueScreen(DateTime time, string code) => new()
    {
        Provider = "Microsoft-Windows-WER-SystemErrorReporting",
        Id = 1001,
        Time = time,
        Data = new[] { code + " (0x0)", "", "" },
    };

    [Fact]
    public void DiskVerdictsBecomeFindings()
    {
        var bad = new DiskHealthReport { Disk = new DiskHealthData { Model = "WDC WD10EZEX" }, Verdict = DiskVerdict.Bad, Summary = "Диск может скоро отказать." };
        var good = new DiskHealthReport { Disk = new DiskHealthData { Model = "Samsung 980" }, Verdict = DiskVerdict.Good };

        Finding finding = Assert.Single(HealthFindings.FromDisks(new[] { bad, good }));

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Equal("Диск WDC WD10EZEX может скоро отказать", finding.Title);
        Assert.Equal(FindingAreas.Disks, finding.Area);
    }

    [Fact]
    public void WeeklyCrashesBecomeFindings()
    {
        CrashReport report = Week(
            BlueScreen(Now.AddDays(-1), "0x00000133"),
            BlueScreen(Now.AddDays(-2), "0x0000001a"),
            new RawEvent { Provider = "Display", Id = 4101, Time = Now.AddDays(-3), Data = new[] { "nvlddmkm" } });

        List<Finding> findings = HealthFindings.FromCrashes(report).ToList();

        Finding blue = findings[0];
        Assert.Equal("2 синих экрана за неделю", blue.Title);
        Assert.StartsWith("DPC_WATCHDOG_VIOLATION, MEMORY_MANAGEMENT.", blue.Detail);
        Assert.Contains(findings, f => f.Title == "Драйвер видеокарты перезапускался 1 раз за неделю");
        Assert.All(findings, f => Assert.Equal(FindingAreas.Crashes, f.Area));
    }

    [Fact]
    public void QuietWeekHasNoFindings() => Assert.Empty(HealthFindings.FromCrashes(Week()));

    [Fact]
    public void CrashNotificationOnlyForNewEvents()
    {
        CrashReport report = Week(
            BlueScreen(Now.AddHours(-1), "0x00000116"),
            new RawEvent { Provider = "Display", Id = 4101, Time = Now.AddDays(-3), Data = new[] { "nvlddmkm" } });

        HealthNotification? fresh = HealthFindings.CrashNotification(report, Now.AddHours(-2));
        Assert.NotNull(fresh);
        Assert.Equal("Синий экран VIDEO_TDR_FAILURE. Подробности и возможные причины — раздел «Сбои».", fresh.Text);

        Assert.Null(HealthFindings.CrashNotification(report, Now.AddMinutes(-30)));
    }

    [Fact]
    public void DriverProblemsBecomeFindings()
    {
        var report = new DriverReport
        {
            Problems = new[]
            {
                new DriverItem("Видеокарта", "Драйвер видеокарты не установлен", "…", HealthLevel.Bad),
                new DriverItem("Сеть", "Сетевой контроллер: драйвер не установлен", "…", HealthLevel.Warning),
                new DriverItem("Bluetooth", "Bluetooth Adapter: отключено", "…", HealthLevel.Notice),
            },
            Drivers = new[] { new DriverItem("Видеокарта", "RTX 3070", "…", HealthLevel.Notice, "Обновите драйвер.") },
        };

        List<Finding> findings = HealthFindings.FromDrivers(report).ToList();

        Assert.Equal(3, findings.Count);
        Assert.Equal(FindingSeverity.Critical, findings[0].Severity);
        Assert.Equal("1 устройство без драйвера или с ошибкой", findings[1].Title);
        Assert.Equal("Сетевой контроллер.", findings[1].Detail);
        Assert.True(findings[2].IsTip);
        Assert.Equal("Драйвер видеокарты давно не обновлялся", findings[2].Title);
    }

    [Fact]
    public void MemoryTipsForBaseSpeedAndSingleModule()
    {
        List<Finding> tips = HealthFindings.FromMemory(new MemoryConfig(16UL << 30, 1, 2400, "DDR4", 1)).ToList();

        Assert.Equal(2, tips.Count);
        Assert.All(tips, t => Assert.True(t.IsTip));
        Assert.Contains("XMP/EXPO", tips[0].Detail);

        Assert.Empty(HealthFindings.FromMemory(new MemoryConfig(32UL << 30, 2, 3600, "DDR4", 2)));
        Assert.Empty(HealthFindings.FromMemory(null));
    }
}
