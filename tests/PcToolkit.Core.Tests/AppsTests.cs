using Microsoft.Win32;
using PcToolkit.Core.Apps;

namespace PcToolkit.Core.Tests;

public sealed class AppsTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "C:\\Program Files\\App\\unins000.exe", "/SILENT")]
    [InlineData("\"C:\\App\\uninstall.exe\"", "C:\\App\\uninstall.exe", "")]
    [InlineData("C:\\NoSuchFolder\\uninst.exe /S", "C:\\NoSuchFolder\\uninst.exe", "/S")]
    public void SplitsUninstallString(string command, string file, string arguments)
    {
        UninstallCommand parsed = Uninstaller.Split(command);
        Assert.Equal(file, parsed.FileName);
        Assert.Equal(arguments, parsed.Arguments);
    }

    [Fact]
    public void MsiInstallSwitchBecomesUninstall()
    {
        var app = new InstalledApp
        {
            Id = "x", Kind = AppKind.Desktop, Name = "Test",
            UninstallString = "MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}", IsMsi = true,
        };
        UninstallCommand? command = Uninstaller.Parse(app);
        Assert.NotNull(command);
        Assert.Equal("msiexec.exe", command!.FileName);
        Assert.Equal("/X{12345678-1234-1234-1234-123456789ABC}", command.Arguments);
    }

    [Theory]
    [InlineData("Steam (x64) 2.10.91.91", "steam")]
    [InlineData("Discord®", "discord")]
    [InlineData("7-Zip 23.01 (x64)", "7 zip")]
    [InlineData("Mozilla Firefox (x64 ru)", "mozilla firefox")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "microsoft visual c redistributable")]
    public void NormalizesNames(string name, string expected)
    {
        Assert.Equal(expected, LeftoverScanner.Normalize(name));
    }

    [Fact]
    public void GenericNamesAreNotUsedForSearch()
    {
        var app = new InstalledApp { Id = "x", Kind = AppKind.Desktop, Name = "Update", InstallLocation = @"C:\Program Files\App" };
        Assert.Empty(LeftoverScanner.NameKeys(app));
    }

    [Theory]
    [InlineData(@"HKCU\Software\Microsoft\Windows")]
    [InlineData(@"HKCU\Software\Classes\.txt")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Test")]
    [InlineData(@"HKCU\Software")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall")]
    public void ProtectedRegistryKeysAreRefused(string path)
    {
        Assert.NotNull(LeftoverRemover.Validate(new Leftover(LeftoverKind.RegistryKey, path, RegistryView.Registry64, 0, false, "")));
    }

    [Fact]
    public void ProtectedFoldersAreRefused()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.False(LeftoverRemover.IsAllowedLocation(Path.Combine(windows, "SomeApp"), shortcut: false));
        Assert.False(LeftoverRemover.IsAllowedLocation(Path.Combine(documents, "SomeApp"), shortcut: false));
        Assert.False(LeftoverRemover.IsAllowedLocation(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), shortcut: false));
        Assert.False(LeftoverRemover.IsAllowedLocation(Path.Combine(documents, "game.lnk"), shortcut: true));
        Assert.True(LeftoverRemover.IsAllowedLocation(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SomeApp"), shortcut: false));
        Assert.True(LeftoverRemover.IsAllowedLocation(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SomeApp"), shortcut: false));
    }

    [Fact]
    public void FindsAndRemovesTestLeftovers()
    {
        string name = "PcToolkitTestApp" + Guid.NewGuid().ToString("N")[..8];
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);
        string backup = Path.Combine(Path.GetTempPath(), "pct-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.ini"), "x=1");
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\{name}"))
            key.SetValue("Test", 1);

        try
        {
            var app = new InstalledApp { Id = "test", Kind = AppKind.Desktop, Name = name, Publisher = "Nobody" };
            IReadOnlyList<Leftover> found = LeftoverScanner.Find(app, Array.Empty<InstalledApp>(), includeUninstallEntry: false, elevated: false);

            Leftover folderItem = Assert.Single(found, f => f.Kind == LeftoverKind.Folder);
            Assert.Equal(folder, folderItem.Path, ignoreCase: true);
            Assert.False(folderItem.NeedsAdmin);
            Leftover keyItem = Assert.Single(found, f => f.Kind == LeftoverKind.RegistryKey);
            Assert.Equal($@"HKCU\Software\{name}", keyItem.Path);

            // Другая программа с тем же именем — ничего не трогаем.
            var twin = new InstalledApp { Id = "twin", Kind = AppKind.Desktop, Name = name };
            Assert.Empty(LeftoverScanner.Find(app, new[] { twin }, includeUninstallEntry: false, elevated: false));

            // Ключ реестра удаляется с резервной копией.
            IReadOnlyList<LeftoverResult> results = LeftoverRemover.Remove(new[] { keyItem }, backup);
            Assert.True(results[0].Removed, results[0].Error);
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\{name}"));
            Assert.True(File.Exists(Path.Combine(backup, "01.reg")));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{name}", throwOnMissingSubKey: false);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);
        }
    }

    [Fact]
    public void ReadsInstalledPrograms()
    {
        IReadOnlyList<InstalledApp> apps = InstalledAppsProvider.ListDesktop();
        Assert.NotEmpty(apps);
        Assert.All(apps, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.NotNull(a.Key);
        });
    }

    [Theory]
    [InlineData("Microsoft.BingWeather", AdviceLevel.Remove)]
    [InlineData("king.com.CandyCrushSaga", AdviceLevel.Remove)]
    [InlineData("Microsoft.WindowsStore", AdviceLevel.Blocked)]
    [InlineData("Microsoft.XboxGamingOverlay", AdviceLevel.Keep)]
    [InlineData("Some.UserApp", AdviceLevel.None)]
    public void StoreAdvice(string package, AdviceLevel level)
    {
        Assert.Equal(level, StoreAppAdvice.For(package).Level);
    }

    [Fact]
    public void PrettyStoreNames()
    {
        Assert.Equal("Bing Weather", StoreAppsProvider.PrettyName("Microsoft.BingWeather"));
        Assert.Equal(new DateTime(2024, 10, 15), InstalledAppsProvider.ParseDate("20241015"));
        Assert.Null(InstalledAppsProvider.ParseDate("15.10.2024"));
    }
}
