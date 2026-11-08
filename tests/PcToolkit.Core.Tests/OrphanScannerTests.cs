using Microsoft.Win32;
using PcToolkit.Core.Apps;

namespace PcToolkit.Core.Tests;

public sealed class OrphanScannerTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\Foo\foo.exe"" --minimized", @"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\Program Files\Foo\foo.exe /background", @"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\Tools\agent.exe", @"C:\Tools\agent.exe")]
    [InlineData(@"rundll32.exe ""C:\Program Files\Foo\helper.dll"",Start", @"C:\Program Files\Foo\helper.dll")]
    [InlineData(@"C:\Windows\System32\rundll32.exe C:\Foo\helper.dll,Start", @"C:\Foo\helper.dll")]
    [InlineData(@"C:\Program Files\Foo\run.bat now", "")] // путь с пробелами без кавычек и без .exe — не гадаем
    [InlineData(@"C:\Tools\run.cmd", @"C:\Tools\run.cmd")]
    [InlineData(@"""C:\broken", "")]
    [InlineData("", "")]
    public void CommandTargetFindsTheLaunchedFile(string command, string expected)
    {
        Assert.Equal(expected, OrphanScanner.CommandTarget(command));
    }

    [Fact]
    public void AlternatePathsCoverProgramFilesAndSysWow()
    {
        Assert.Equal(new[] { @"C:\Program Files (x86)\Foo\foo.exe" }, OrphanScanner.AlternatePaths(@"C:\Program Files\Foo\foo.exe"));
        Assert.Equal(new[] { @"D:\Program Files\Foo\foo.exe" }, OrphanScanner.AlternatePaths(@"D:\Program Files (x86)\Foo\foo.exe"));
        Assert.Equal(new[] { @"C:\Windows\SysWOW64\foo.dll" }, OrphanScanner.AlternatePaths(@"C:\Windows\System32\foo.dll"));
        Assert.Empty(OrphanScanner.AlternatePaths(@"C:\Games\Program Files\foo.exe"));
    }

    [Fact]
    public void IconFileStripsIndexAndQuotes()
    {
        Assert.Equal(@"C:\App\app.exe", OrphanScanner.IconFile(@"""C:\App\app.exe"",0"));
        Assert.Equal(@"C:\App\app.ico", OrphanScanner.IconFile(@"C:\App\app.ico"));
        Assert.Equal(@"C:\App\app,v2.exe", OrphanScanner.IconFile(@"C:\App\app,v2.exe"));
    }

    [Fact]
    public void TaskPathSplitsIntoFolderAndName()
    {
        Assert.Equal((@"\", "GoogleUpdate"), OrphanScanner.SplitTaskPath(@"\GoogleUpdate"));
        Assert.Equal((@"\Vendor\Sub", "Updater"), OrphanScanner.SplitTaskPath(@"\Vendor\Sub\Updater"));
    }

    [Fact]
    public void RegFileRestoresOneValue()
    {
        string text = LeftoverRemover.RegFile(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "Foo \"Agent\"",
            @"""C:\Program Files\Foo\foo.exe"" -min", RegistryValueKind.String);
        Assert.Equal("Windows Registry Editor Version 5.00\r\n\r\n" +
                     "[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n" +
                     "\"Foo \\\"Agent\\\"\"=\"\\\"C:\\\\Program Files\\\\Foo\\\\foo.exe\\\" -min\"\r\n", text);

        string expand = LeftoverRemover.RegFile("HKEY_CURRENT_USER\\X", "A", "%A%", RegistryValueKind.ExpandString);
        Assert.EndsWith("\"A\"=hex(2):25,00,41,00,25,00,00,00\r\n", expand);

        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            LeftoverRemover.RegKeyName(RegistryHive.LocalMachine, RegistryView.Registry32, OrphanScanner.RunKeyPath));
        Assert.Equal(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Run",
            LeftoverRemover.RegKeyName(RegistryHive.LocalMachine, RegistryView.Registry64, OrphanScanner.RunKeyPath));
    }

    [Fact]
    public void EmptyTreeMeansNoFilesAtAnyDepth()
    {
        string root = Path.Combine(Path.GetTempPath(), "pct-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a", "b", "c"));
            Directory.CreateDirectory(Path.Combine(root, "d"));
            Assert.True(OrphanScanner.IsEmptyTree(root));

            File.WriteAllText(Path.Combine(root, "a", "b", "note.txt"), "x");
            Assert.False(OrphanScanner.IsEmptyTree(root));
            Assert.True(OrphanScanner.IsEmptyTree(Path.Combine(root, "d")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LeftoverWithValueNameSurvivesJson()
    {
        var item = new Leftover(LeftoverKind.RegistryValue, @"HKCU\" + OrphanScanner.RunKeyPath, RegistryView.Registry64, 0, false, "автозапуск", "Foo");
        string json = System.Text.Json.JsonSerializer.Serialize(new LeftoverRemover.Request { Items = { item } });
        Leftover back = System.Text.Json.JsonSerializer.Deserialize<LeftoverRemover.Request>(json)!.Items[0];
        Assert.Equal(item, back);
    }
}
