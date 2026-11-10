using PcToolkit.Core.Startup;

namespace PcToolkit.Core.Tests;

public sealed class StartupTests
{
    [Fact]
    public void ApprovedDataMatchesTaskManagerFormat()
    {
        var now = new DateTime(2026, 11, 10, 20, 0, 0, DateTimeKind.Utc);
        byte[] off = StartupApproved.Data(false, now);
        byte[] on = StartupApproved.Data(true, now);

        Assert.Equal(12, off.Length);
        Assert.Equal(3, off[0]);
        Assert.Equal(now.ToFileTimeUtc(), BitConverter.ToInt64(off, 4));
        Assert.Equal(2, on[0]);
        Assert.All(on.Skip(1), b => Assert.Equal(0, b));

        Assert.False(StartupApproved.IsEnabled(off));
        Assert.True(StartupApproved.IsEnabled(on));
        Assert.True(StartupApproved.IsEnabled(null));
        Assert.False(StartupApproved.IsEnabled(new byte[] { 7, 0, 0, 0 }));
        Assert.True(StartupApproved.IsEnabled(new byte[] { 6, 0, 0, 0 }));
    }

    [Fact]
    public void CatalogKnowsLaunchersMessengersAndSystemParts()
    {
        Assert.Equal(StartupAdvice.CanDisable, StartupCatalog.Match("Steam", @"C:\Program Files (x86)\Steam\steam.exe", "", "Valve").Advice);
        Assert.Equal(StartupImpact.High, StartupCatalog.Match("Steam", @"C:\Program Files (x86)\Steam\steam.exe", "", "Valve").Impact);

        // Discord запускается через Update.exe --processStart Discord.exe
        StartupRule discord = StartupCatalog.Match("Discord", @"C:\Users\x\AppData\Local\Discord\Update.exe",
            @"C:\Users\x\AppData\Local\Discord\Update.exe --processStart Discord.exe", "GitHub");
        Assert.Equal(StartupImpact.High, discord.Impact);

        Assert.Equal(StartupAdvice.Keep, StartupCatalog.Match("SecurityHealth", @"C:\Windows\system32\SecurityHealthSystray.exe", "", "Microsoft").Advice);
        Assert.Equal(StartupImpact.Unknown, StartupCatalog.Match("Something", @"C:\Tools\thing.exe", "", "Acme").Impact);
    }

    [Fact]
    public void ShortcutNamesShowWithoutExtension()
    {
        var entry = new StartupEntry("Telegram.lnk", "", "", StartupLocation.UserFolder, true, "", "", true,
                                     StartupImpact.Medium, StartupAdvice.Optional, "");
        Assert.Equal("Telegram", entry.DisplayName);
        Assert.False(entry.NeedsAdmin);
        Assert.True(entry with { Location = StartupLocation.MachineRun32 } is { NeedsAdmin: true });
    }
}

public sealed class WindowsRepairTests
{
    [Theory]
    [InlineData("Windows Resource Protection did not find any integrity violations.", PcToolkit.Core.Repair.SfcVerdict.Clean)]
    [InlineData("Защита ресурсов Windows не обнаружила нарушений целостности.", PcToolkit.Core.Repair.SfcVerdict.Clean)]
    [InlineData("Windows Resource Protection found corrupt files and successfully repaired them.", PcToolkit.Core.Repair.SfcVerdict.Repaired)]
    [InlineData("Программа защиты ресурсов Windows обнаружила поврежденные файлы и успешно их восстановила.", PcToolkit.Core.Repair.SfcVerdict.Repaired)]
    [InlineData("Windows Resource Protection found corrupt files but was unable to fix some of them.", PcToolkit.Core.Repair.SfcVerdict.Partial)]
    [InlineData("Защита ресурсов Windows не может выполнить запрошенную операцию.", PcToolkit.Core.Repair.SfcVerdict.Failed)]
    [InlineData("", PcToolkit.Core.Repair.SfcVerdict.Unknown)]
    public void SfcVerdictFromItsLastMessage(string output, PcToolkit.Core.Repair.SfcVerdict expected) =>
        Assert.Equal(expected, PcToolkit.Core.Repair.WindowsRepair.ParseSfc(output));

    [Theory]
    [InlineData("[==========                 18.6%                          ]", 18.6)]
    [InlineData("Verification 45% complete.", 45)]
    [InlineData("Проверка 7% завершена.", 7)]
    public void ProgressPercent(string line, double expected) =>
        Assert.Equal(expected, PcToolkit.Core.Repair.WindowsRepair.Percent(line));

    [Fact]
    public void NoPercentInPlainLines() => Assert.Null(PcToolkit.Core.Repair.WindowsRepair.Percent("Deployment Image Servicing and Management tool"));
}

public sealed class FpsLimitTests
{
    [Fact]
    public void LimitIsChillWithEqualBoundsWithinDriverRange()
    {
        var state = new PcToolkit.Core.Games.RadeonState(new PcToolkit.Core.Games.RadeonValues(), true, true, false,
            new PcToolkit.Core.Overclock.TuningRange(30, 300, 1), null);
        var profile = new PcToolkit.Core.Games.GameProfile { RadeonMode = PcToolkit.Core.Games.RadeonModes.Limit, LimitFps = 141 };

        PcToolkit.Core.Games.RadeonValues wanted = PcToolkit.Core.Games.RadeonPlan.Wanted(profile, state);

        Assert.True(wanted.Chill);
        Assert.Equal(141, wanted.ChillMinFps);
        Assert.Equal(141, wanted.ChillMaxFps);
        Assert.False(wanted.AntiLag);

        profile.LimitFps = 500;
        Assert.Equal(300, PcToolkit.Core.Games.RadeonPlan.Wanted(profile, state).ChillMaxFps);
    }
}

public sealed class UpdateManifestTests
{
    [Fact]
    public void ManifestNeedsVersionAndHttpsUrl()
    {
        var info = PcToolkit.Core.Updates.UpdateChecker.FromManifest(
            """{"version":"0.5.0","url":"https://example.com/PcToolkit-0.5.0-Setup.exe","sha256":"ab","notes":"Новое"}""");
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 5, 0), info!.Version);
        Assert.Equal("Новое", info.Notes);

        Assert.Null(PcToolkit.Core.Updates.UpdateChecker.FromManifest("""{"version":"0.5.0","url":"http://example.com/x.exe"}"""));
        Assert.Null(PcToolkit.Core.Updates.UpdateChecker.FromManifest("""{"version":"бета","url":"https://example.com/x.exe"}"""));
    }
}

public sealed class StartupNameTests
{
    [Fact]
    public void CodedRunNamesShowTheProgramDescription()
    {
        var edge = new PcToolkit.Core.Startup.StartupEntry("MicrosoftEdgeAutoLaunch_7A0F6FD0DBBB9EB7B8CD8C27457ED04E", "", "",
            PcToolkit.Core.Startup.StartupLocation.UserRun, true, "Microsoft Corporation", "Microsoft Edge", true,
            PcToolkit.Core.Startup.StartupImpact.Medium, PcToolkit.Core.Startup.StartupAdvice.CanDisable, "");
        Assert.Equal("Microsoft Edge", edge.DisplayName);
        Assert.Equal("Steam", (edge with { Name = "Steam", Description = "Steam Client" }).DisplayName);
    }
}

public sealed class TracerouteVerdictTests
{
    private static PcToolkit.Core.Network.TraceHop Hop(int n, string ip, params double?[] times)
    {
        var hop = new PcToolkit.Core.Network.TraceHop(n) { Address = System.Net.IPAddress.Parse(ip) };
        foreach (double? t in times)
            typeof(PcToolkit.Core.Network.TraceHop).GetMethod("Record", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(hop, new object?[] { t });
        return hop;
    }

    [Fact]
    public void OneHopToPublicServerMeansVpn()
    {
        var verdict = PcToolkit.Core.Network.Traceroute.Judge(new[] { Hop(1, "8.8.8.8", 0.4, 0.3) }, "outline-tap0");
        Assert.Equal("Маршрут скрыт VPN", verdict.Title);
        Assert.Contains("outline-tap0", verdict.Details);
    }

    [Fact]
    public void LossesThatReachTheTargetAreLocated()
    {
        var hops = new[]
        {
            Hop(1, "192.168.1.1", 1, 1, 1, 1),
            Hop(2, "10.0.0.1", 5, null, 5, null),
            Hop(3, "8.8.8.8", 20, null, 20, null),
        };
        var verdict = PcToolkit.Core.Network.Traceroute.Judge(hops);
        Assert.Equal(PcToolkit.Core.Disks.HealthLevel.Bad, verdict.Level);
        Assert.Contains("узле 2", verdict.Title);
    }
}
