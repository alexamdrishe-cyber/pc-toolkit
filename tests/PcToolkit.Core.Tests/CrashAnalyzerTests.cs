using PcToolkit.Core.Diagnostics;
using PcToolkit.Core.Disks;

namespace PcToolkit.Core.Tests;

public sealed class CrashAnalyzerTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);
    private static readonly DateTime From = Now.AddDays(-30);

    private static RawEvent Event(string provider, int id, DateTime time, int level = 2, string[]? data = null,
                                  Dictionary<string, string>? named = null, string? message = null) => new()
    {
        Provider = provider,
        Id = id,
        Time = time,
        Level = level,
        Data = data ?? Array.Empty<string>(),
        Named = named ?? new Dictionary<string, string>(),
        Message = message,
    };

    private static RawEvent BlueScreen(DateTime time, string code) =>
        Event("Microsoft-Windows-WER-SystemErrorReporting", 1001, time,
              data: new[] { $"{code} (0x0000000000000000, 0xfffff80123456789, 0x0000000000000000, 0x0000000000000000)", @"C:\WINDOWS\Minidump\110726-1.dmp", "id" });

    private static RawEvent KernelPower(DateTime time, long bugcheck = 0, long powerButton = 0) =>
        Event("Microsoft-Windows-Kernel-Power", 41, time, level: 1, named: new Dictionary<string, string>
        {
            ["BugcheckCode"] = bugcheck.ToString(),
            ["SleepInProgress"] = "0",
            ["PowerButtonTimestamp"] = powerButton.ToString(),
        });

    [Fact]
    public void NoEventsMeansGood()
    {
        CrashReport report = CrashAnalyzer.Analyze(Array.Empty<RawEvent>(), From, Now);

        Assert.Equal(HealthLevel.Good, report.Level);
        Assert.Empty(report.Groups);
        Assert.StartsWith("За 30 дней сбоев не найдено", report.Summary);
    }

    [Fact]
    public void BlueScreensAreGroupedByCodeAndExplained()
    {
        RawEvent[] events =
        {
            BlueScreen(Now.AddDays(-1), "0x00000133"),
            KernelPower(Now.AddDays(-1).AddMinutes(-1), bugcheck: 0x133),
            BlueScreen(Now.AddDays(-5), "0x00000133"),
            BlueScreen(Now.AddDays(-9), "0x1000007e"),
        };

        CrashReport report = CrashAnalyzer.Analyze(events, From, Now);

        Assert.Equal(3, report.BlueScreens);
        Assert.Equal(0, report.SuddenShutdowns);
        CrashGroup dpc = report.Groups.Single(g => g.Key == "bsod:133");
        Assert.Equal(2, dpc.Count);
        Assert.Equal("Синий экран: DPC_WATCHDOG_VIOLATION — 2 раза", dpc.Title);
        Assert.Equal(HealthLevel.Warning, dpc.Level);
        Assert.Contains("прошивку SSD", dpc.Advice);
        Assert.Contains("110726-1.dmp", dpc.Occurrences[0].Text);
        // 0x1000007E — тот же SYSTEM_THREAD_EXCEPTION_NOT_HANDLED, что и 0x7E.
        Assert.Contains(report.Groups, g => g.Key == "bsod:7E" && g.Title.Contains("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED"));
        Assert.StartsWith("За 30 дней: 3 синих экрана", report.Summary);
    }

    [Fact]
    public void ThreeBlueScreensWithOneCodeAreBad()
    {
        RawEvent[] events = Enumerable.Range(1, 3).Select(d => BlueScreen(Now.AddDays(-d), "0x0000001a")).ToArray();

        CrashReport report = CrashAnalyzer.Analyze(events, From, Now);

        Assert.Equal(HealthLevel.Bad, report.Level);
        Assert.Contains(report.Hints, h => h.Contains("оперативной памятью"));
    }

    [Fact]
    public void KernelPowerWithoutWerReportStillCountsAsBlueScreen()
    {
        CrashReport report = CrashAnalyzer.Analyze(new[] { KernelPower(Now.AddHours(-3), bugcheck: 0x124) }, From, Now);

        Assert.Equal(1, report.BlueScreens);
        Assert.Contains(report.Groups, g => g.Key == "bsod:124");
        Assert.Contains(report.Hints, h => h.Contains("процессора"));
    }

    [Fact]
    public void SuddenShutdownsWithoutBlueScreenPointToPowerOrHeat()
    {
        RawEvent[] events =
        {
            KernelPower(Now.AddDays(-2)),
            Event("EventLog", 6008, Now.AddDays(-2).AddMinutes(1)),
            KernelPower(Now.AddDays(-4), powerButton: 132456789),
            Event("EventLog", 6008, Now.AddDays(-20)),
        };

        CrashReport report = CrashAnalyzer.Analyze(events, From, Now);

        CrashGroup sudden = Assert.Single(report.Groups);
        Assert.Equal(CrashKind.SuddenShutdown, sudden.Kind);
        // 6008 рядом с Kernel-Power 41 — тот же случай; отдельное 6008 через 20 дней — новый.
        Assert.Equal(3, sudden.Count);
        Assert.Contains(sudden.Occurrences, o => o.Text.Contains("кнопки питания"));
        Assert.Contains(report.Hints, h => h.Contains("блоком питания"));
    }

    [Fact]
    public void WheaAndGpuResetsAreRecognized()
    {
        RawEvent[] events =
        {
            Event("Microsoft-Windows-WHEA-Logger", 19, Now.AddDays(-1), level: 3, message: "Произошла исправленная аппаратная ошибка.\r\nКомпонент: ядро процессора"),
            Event("Microsoft-Windows-WHEA-Logger", 17, Now.AddDays(-2), level: 3),
            Event("Display", 4101, Now.AddDays(-3), level: 3, data: new[] { "nvlddmkm", "" }),
            Event("nvlddmkm", 153, Now.AddDays(-3).AddSeconds(2), level: 2),
        };

        CrashReport report = CrashAnalyzer.Analyze(events, From, Now);

        Assert.Equal(2, report.HardwareErrors);
        Assert.Equal(1, report.GpuResets);
        CrashGroup cpu = report.Groups.Single(g => g.Key == "whea-cpu");
        Assert.Equal("Произошла исправленная аппаратная ошибка.", cpu.Occurrences[0].Text);
        Assert.Equal(HealthLevel.Notice, report.Groups.Single(g => g.Key == "whea-pcie").Level);
        Assert.Equal("Драйвер NVIDIA (nvlddmkm)", report.Groups.Single(g => g.Key == "gpu").Occurrences[0].Text);
        Assert.Contains(report.Hints, h => h.Contains("видеокарты"));
    }

    [Fact]
    public void DiskErrorsNameTheDisk()
    {
        RawEvent[] events =
        {
            Event("disk", 7, Now.AddDays(-1), data: new[] { @"\Device\Harddisk1\DR1" }),
            Event("stornvme", 129, Now.AddDays(-2), level: 3, data: new[] { @"\Device\RaidPort0" }),
            Event("Microsoft-Windows-Ntfs", 98, Now.AddDays(-3), level: 4, message: "Том C: исправен."),
            Event("SomethingElse", 129, Now.AddDays(-3), level: 3),
        };

        CrashReport report = CrashAnalyzer.Analyze(events, From, Now);

        Assert.Equal(2, report.DiskErrors);
        Assert.Equal("Повреждённый блок (диск 1)", report.Groups.Single(g => g.Key == "disk-bad").Occurrences[0].Text);
        Assert.Contains(report.Hints, h => h.Contains("«Диски»"));
    }

    [Fact]
    public void AppCrashesAreGroupedByProgramWithModuleExplained()
    {
        var events = new List<RawEvent>();
        for (int i = 0; i < 5; i++)
        {
            events.Add(Event("Application Error", 1000, Now.AddDays(-i - 1),
                data: new[] { "Cyberpunk2077.exe", "3.0.0.0", "0", "nvwgf2umx.dll", "32.0.15.6094", "0", "c0000005", "0x1234" }));
        }

        events.Add(Event("Application Hang", 1002, Now.AddDays(-2), data: new[] { "Discord.exe", "1.0" }));
        events.Add(Event("Application Error", 1000, Now.AddDays(-3), named: new Dictionary<string, string>
        {
            ["AppName"] = "Discord.exe",
            ["ModuleName"] = "ntdll.dll",
            ["ExceptionCode"] = "0xc0000374",
        }));

        CrashReport report = CrashAnalyzer.Analyze(events, From, Now);

        Assert.Equal(7, report.AppCrashes);
        CrashGroup game = report.Groups.Single(g => g.Key == "app:Cyberpunk2077.exe");
        Assert.Equal("Cyberpunk2077: вылеты — 5 раз", game.Title);
        Assert.Equal(HealthLevel.Warning, game.Level);
        Assert.Contains("драйвере видеокарты", game.What);
        Assert.Equal("Вылет в драйвере видеокарты (nvwgf2umx.dll), код 0xc0000005 (обращение к недопустимой памяти)", game.Occurrences[0].Text);

        CrashGroup discord = report.Groups.Single(g => g.Key == "app:Discord.exe");
        Assert.Equal("Discord: вылеты и зависания — 2 раза", discord.Title);
        Assert.Equal(HealthLevel.Notice, discord.Level);
        Assert.Contains("системной библиотеке", discord.What);
        Assert.Contains("7 вылетов программ (2 программы)", report.Summary);
    }

    [Fact]
    public void OldEventsAreIgnoredAndOrderIsBySeverity()
    {
        RawEvent[] events =
        {
            Event("Application Error", 1000, Now.AddDays(-1), data: new[] { "game.exe", "", "", "game.exe", "", "", "c0000409" }),
            Event("Microsoft-Windows-MemoryDiagnostics-Results", 1102, Now.AddDays(-10)),
            BlueScreen(Now.AddDays(-40), "0x0000000a"),
        };

        CrashReport report = CrashAnalyzer.Analyze(events, From, Now);

        Assert.Equal(0, report.BlueScreens);
        Assert.Equal("memtest-bad", report.Groups[0].Key);
        Assert.Equal(HealthLevel.Bad, report.Level);
        Assert.Contains("коде самой программы", report.Groups[1].Occurrences[0].Text);
    }
    [Fact]
    public void BurstsOfDiskErrorsCountAsOneCase()
    {
        var events = new List<RawEvent>();
        for (int i = 0; i < 398; i++)
            events.Add(Event("disk", 51, Now.AddDays(-10).AddSeconds(-i / 4.0), level: 3, data: new[] { @"\Device\Harddisk1\DR1" }));
        events.Add(Event("disk", 51, Now.AddDays(-2), level: 3, data: new[] { @"\Device\Harddisk1\DR1" }));

        CrashReport report = CrashAnalyzer.Analyze(events, From, Now, diskNames: new Dictionary<int, string> { [1] = "MSI M480 PRO 1TB" });

        CrashGroup paging = Assert.Single(report.Groups);
        Assert.Equal(2, paging.Count);
        Assert.Equal(2, report.DiskErrors);
        Assert.EndsWith("— 2 раза (399 записей в журнале)", paging.Title);
        Assert.Equal("Ошибка при страничном обмене (диск 1 · MSI M480 PRO 1TB)", paging.Occurrences[0].Text);
        Assert.Equal("Ошибка при страничном обмене (диск 1 · MSI M480 PRO 1TB) — 398 записей за 2 мин", paging.Occurrences[1].Text);
        Assert.Contains("2 ошибки дисков", report.Summary);
    }

    [Fact]
    public void UnknownModuleAndGameEngineAreWordedPlainly()
    {
        RawEvent[] events =
        {
            Event("Application Error", 1000, Now.AddDays(-1), data: new[] { "PcToolkit.exe", "", "", "unknown", "", "", "c0000005" }),
            Event("Application Error", 1000, Now.AddDays(-2), data: new[] { "KSP_x64.exe", "", "", "UnityPlayer.dll", "", "", "c0000005" }),
        };

        CrashReport report = CrashAnalyzer.Analyze(events, From, Now);

        CrashGroup toolkit = report.Groups.Single(g => g.Key == "app:PcToolkit.exe");
        Assert.Equal("Программа аварийно закрывалась.", toolkit.What);
        Assert.Equal("Вылет, код 0xc0000005 (обращение к недопустимой памяти)", toolkit.Occurrences[0].Text);

        CrashGroup game = report.Groups.Single(g => g.Key == "app:KSP_x64.exe");
        Assert.Contains("в своём движке (UnityPlayer.dll)", game.What);
        Assert.StartsWith("Вылет в движке игры (UnityPlayer.dll)", game.Occurrences[0].Text);
    }
}
