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
