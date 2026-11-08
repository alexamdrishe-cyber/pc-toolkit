using PcToolkit.Core.Diagnostics;
using PcToolkit.Core.Disks;

namespace PcToolkit.Core.Tests;

public sealed class DriverCheckTests
{
    private static readonly DateTime Now = new(2026, 11, 8);

    private static SignedDriverRaw Gpu(string version, DateTime date, string provider = "NVIDIA") =>
        new("NVIDIA GeForce RTX 3070", "DISPLAY", @"PCI\VEN_10DE&DEV_2484&SUBSYS_146B10DE&REV_A1\4&1", provider, "NVIDIA", version, date);

    [Theory]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.3623", "536.23")]
    [InlineData("27.21.14.5671", "456.71")]
    [InlineData("10.0.1", null)]
    public void ConvertsNvidiaVersion(string windows, string? expected) =>
        Assert.Equal(expected, DriverAnalyzer.NvidiaVersion(windows));

    [Fact]
    public void HealthyComputerIsGood()
    {
        DriverReport report = DriverAnalyzer.Analyze(Array.Empty<PnpDeviceRaw>(), Array.Empty<PnpDeviceRaw>(),
            new[] { Gpu("32.0.15.6094", Now.AddDays(-40)) }, Now);

        Assert.Equal(HealthLevel.Good, report.Level);
        Assert.Empty(report.Problems);
        DriverItem gpu = Assert.Single(report.Drivers);
        Assert.Equal("Видеокарта", gpu.Category);
        Assert.StartsWith("Драйвер 560.94 (32.0.15.6094) от ", gpu.Details);
        Assert.Contains("(1 месяц назад)", gpu.Details);
        Assert.Equal(DriverAnalyzer.NvidiaUrl, gpu.LinkUrl);
        Assert.StartsWith("Все устройства работают", report.Summary);
    }

    [Fact]
    public void OldGpuDriverIsNoticed()
    {
        DriverReport report = DriverAnalyzer.Analyze(Array.Empty<PnpDeviceRaw>(), Array.Empty<PnpDeviceRaw>(),
            new[] { Gpu("31.0.15.3623", Now.AddDays(-500)) }, Now);

        Assert.Equal(HealthLevel.Notice, report.Drivers[0].Level);
        Assert.Contains("1 год назад", report.Drivers[0].Details);
        Assert.Contains("давно не обновлялся", report.Summary);
    }

    [Fact]
    public void BasicDisplayAdapterMeansNoGpuDriver()
    {
        var display = new PnpDeviceRaw("Базовый видеоадаптер (Майкрософт)", "Display", 0, @"PCI\VEN_1002&DEV_73BF&SUBSYS_0E3A1002\6&1", "(Стандартные видеоадаптеры)", "BasicDisplay");

        DriverReport report = DriverAnalyzer.Analyze(Array.Empty<PnpDeviceRaw>(), new[] { display }, Array.Empty<SignedDriverRaw>(), Now);

        Assert.Equal(HealthLevel.Bad, report.Level);
        DriverItem problem = Assert.Single(report.Problems);
        Assert.Equal("Драйвер видеокарты не установлен", problem.Title);
        Assert.Equal(DriverAnalyzer.AmdUrl, problem.LinkUrl);
    }

    [Fact]
    public void ProblemDevicesAreExplained()
    {
        PnpDeviceRaw[] devices =
        {
            new("Сетевой контроллер", "", 28, @"PCI\VEN_10EC&DEV_8168&SUBSYS_86771043&REV_15\01000000684CE00000", "", ""),
            new("Bluetooth Adapter", "Bluetooth", 22, @"USB\VID_8087&PID_0029\5&1", "Intel", "BTHUSB"),
            new("USB Flash", "USB", 45, @"USB\VID_0781&PID_5567\123", "", ""),
        };

        DriverReport report = DriverAnalyzer.Analyze(devices, Array.Empty<PnpDeviceRaw>(), Array.Empty<SignedDriverRaw>(), Now);

        Assert.Equal(2, report.Problems.Count);
        DriverItem missing = report.Problems[0];
        Assert.Equal("Сетевой контроллер: драйвер не установлен", missing.Title);
        Assert.Equal(HealthLevel.Warning, missing.Level);
        Assert.Contains(@"PCI\VEN_10EC&DEV_8168", missing.Details);
        Assert.Equal(HealthLevel.Notice, report.Problems[1].Level);
        Assert.Equal("1 устройство без драйвера или с ошибкой.", report.Summary);
    }

    [Fact]
    public void AmdChipsetWithoutAmdDriversIsNoticed()
    {
        SignedDriverRaw[] drivers =
        {
            new("Контроллер шины SMBus", "SYSTEM", @"PCI\VEN_1022&DEV_790B&SUBSYS_88771043&REV_61\3&1", "Microsoft", "Advanced Micro Devices, Inc.", "10.0.19041.1", Now.AddYears(-5)),
        };

        DriverReport report = DriverAnalyzer.Analyze(Array.Empty<PnpDeviceRaw>(), Array.Empty<PnpDeviceRaw>(), drivers, Now);

        DriverItem chipset = Assert.Single(report.Drivers);
        Assert.Equal("Драйверы чипсета AMD не установлены", chipset.Title);
        Assert.Equal(HealthLevel.Notice, chipset.Level);

        DriverReport installed = DriverAnalyzer.Analyze(Array.Empty<PnpDeviceRaw>(), Array.Empty<PnpDeviceRaw>(), new[]
        {
            drivers[0] with { Provider = "Advanced Micro Devices, Inc.", Version = "5.12.0.38", Date = Now.AddDays(-100) },
        }, Now);
        Assert.Equal("Драйверы чипсета AMD", installed.Drivers[0].Title);
        Assert.Equal(HealthLevel.Good, installed.Drivers[0].Level);
    }

    [Fact]
    public void VirtualAdaptersAreHidden()
    {
        SignedDriverRaw[] drivers =
        {
            new("Realtek PCIe GbE Family Controller", "NET", @"PCI\VEN_10EC&DEV_8168\1", "Realtek", "Realtek", "10.68.815.2023", Now.AddDays(-300)),
            new("WAN Miniport (IP)", "NET", @"SWD\MSRRAS\MS_NDISWANIP", "Microsoft", "Microsoft", "10.0.1", Now.AddYears(-6)),
            new("Hyper-V Virtual Ethernet Adapter", "NET", @"ROOT\VMS_MP\0000", "Microsoft", "Microsoft", "10.0.1", Now.AddYears(-6)),
            new("NVIDIA High Definition Audio", "MEDIA", @"HDAUDIO\FUNC_01&VEN_10DE&DEV_0094\5&1", "NVIDIA", "NVIDIA", "1.4.0.1", Now.AddYears(-1)),
            new("Realtek High Definition Audio", "MEDIA", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897\4&1", "Realtek", "Realtek", "6.0.9549.1", Now.AddYears(-1)),
        };

        DriverReport report = DriverAnalyzer.Analyze(Array.Empty<PnpDeviceRaw>(), Array.Empty<PnpDeviceRaw>(), drivers, Now);

        Assert.Equal(new[] { "Realtek PCIe GbE Family Controller", "Realtek High Definition Audio" }, report.Drivers.Select(d => d.Title));
        Assert.All(report.Drivers, d => Assert.Equal(HealthLevel.Fact, d.Level));
    }
    [Fact]
    public void BrokenPawnIoGetsItsOwnExplanationAndAction()
    {
        PnpDeviceRaw[] devices = { new("PawnIO", "SoftwareDevice", 19, @"ROOT\PAWNIO\0000", "", "PawnIO") };

        DriverReport report = DriverAnalyzer.Analyze(devices, Array.Empty<PnpDeviceRaw>(), Array.Empty<SignedDriverRaw>(), Now);

        DriverItem pawnIo = Assert.Single(report.Problems);
        Assert.Equal("Драйвер датчиков PawnIO не работает", pawnIo.Title);
        Assert.Equal(DriverActions.ReinstallPawnIo, pawnIo.Action);
        Assert.Contains("код 19", pawnIo.Details);
        Assert.DoesNotContain("Удалите", pawnIo.Advice);
        Assert.StartsWith("Драйвер датчиков PawnIO не работает", report.Summary);
        Assert.EndsWith("Остальные устройства работают с драйверами.", report.Summary);

        Finding finding = Assert.Single(HealthFindings.FromDrivers(report));
        Assert.Equal("Драйвер датчиков PawnIO не работает", finding.Title);
        Assert.Equal(FindingAreas.Drivers, finding.Area);
    }

    [Fact]
    public void InboxDriverDateIsNotShownAsAge()
    {
        SignedDriverRaw[] drivers =
        {
            new("Standard NVM Express Controller", "SCSIADAPTER", @"PCI\VEN_1987&DEV_5012\4&1", "Microsoft", "Standard NVM Express Controller", "10.0.19041.4355", new DateTime(2006, 6, 21)),
        };

        DriverReport report = DriverAnalyzer.Analyze(Array.Empty<PnpDeviceRaw>(), Array.Empty<PnpDeviceRaw>(), drivers, Now);

        Assert.Equal("Драйвер 10.0.19041.4355 · стандартный драйвер Windows · Microsoft", report.Drivers[0].Details);
    }
}
