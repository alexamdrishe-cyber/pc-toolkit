using PcToolkit.Core.Cleanup;

namespace PcToolkit.Core.Tests;

/// <summary>
/// Тесты очистки работают только в собственной временной папке %TEMP%\PcToolkit.Tests\…,
/// которую создают и удаляют сами. Настоящие файлы пользователя не трогаются.
/// </summary>
public sealed class CleanupTests : IDisposable
{
    private readonly string _root;
    private readonly string _template;

    public CleanupTests()
    {
        string name = "PcToolkit.Tests." + Guid.NewGuid().ToString("N");
        _root = Path.Combine(Path.GetTempPath(), name);
        _template = @"%TEMP%\" + name;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string MakeFile(string relative, int size = 100, double ageHours = 48)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours));
        return path;
    }

    private CleanupRule Rule(double minAge = 24, string[]? patterns = null) => new()
    {
        Id = "test",
        Title = "Тест",
        Category = "system",
        Paths = new List<string> { _template },
        Patterns = (patterns ?? new[] { "*" }).ToList(),
        MinAgeHours = minAge,
    };

    [Fact]
    public void ScanFindsOnlyOldMatchingFiles()
    {
        MakeFile("old.tmp", 100);
        MakeFile(@"sub\old.log", 200);
        MakeFile("fresh.tmp", 300, ageHours: 1);

        CleanupScanResult scan = CleanupEngine.Scan(Rule(), isElevated: false);

        Assert.Equal(CleanupScanState.Ready, scan.State);
        Assert.Equal(2, scan.ItemCount);
        Assert.Equal(300, scan.TotalBytes);
    }

    [Fact]
    public void PatternsLimitFiles()
    {
        MakeFile("a.dmp", 10);
        MakeFile("b.txt", 10);

        CleanupScanResult scan = CleanupEngine.Scan(Rule(patterns: new[] { "*.dmp" }), isElevated: false);

        Assert.Single(scan.Files);
        Assert.EndsWith("a.dmp", scan.Files[0].Path);
    }

    [Fact]
    public void CleanDeletesFilesAndEmptyFoldersButKeepsRoot()
    {
        MakeFile(@"sub\deep\old.tmp", 100);
        MakeFile("fresh.tmp", 100, ageHours: 1);
        string deep = Path.Combine(_root, "sub", "deep");
        Directory.SetCreationTimeUtc(deep, DateTime.UtcNow.AddDays(-3));
        Directory.SetCreationTimeUtc(Path.Combine(_root, "sub"), DateTime.UtcNow.AddDays(-3));

        CleanupScanResult scan = CleanupEngine.Scan(Rule(), isElevated: false);
        CleanupRunResult result = CleanupEngine.Clean(new[] { scan });

        Assert.Equal(1, result.Deleted);
        Assert.Equal(100, result.FreedBytes);
        Assert.True(Directory.Exists(_root));
        Assert.False(Directory.Exists(Path.Combine(_root, "sub")));
        Assert.True(File.Exists(Path.Combine(_root, "fresh.tmp")));
    }

    [Fact]
    public void LockedFileIsSkipped()
    {
        string path = MakeFile("locked.tmp", 100);
        CleanupScanResult scan = CleanupEngine.Scan(Rule(), isElevated: false);

        CleanupRunResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            result = CleanupEngine.Clean(new[] { scan });

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void FileRefreshedAfterScanIsKept()
    {
        string path = MakeFile("old.tmp", 100);
        CleanupScanResult scan = CleanupEngine.Scan(Rule(), isElevated: false);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        CleanupRunResult result = CleanupEngine.Clean(new[] { scan });

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AdminRuleWithoutRightsIsNotScanned()
    {
        MakeFile("old.tmp");
        CleanupRule rule = Rule();
        rule.RequiresAdmin = true;

        CleanupScanResult scan = CleanupEngine.Scan(rule, isElevated: false);

        Assert.Equal(CleanupScanState.NeedsAdmin, scan.State);
        Assert.False(scan.CanClean);
    }

    [Fact]
    public void WildcardSegmentExpandsToEveryProfile()
    {
        MakeFile(@"Default\Cache\a.bin");
        MakeFile(@"Profile 1\Cache\b.bin");
        MakeFile(@"Profile 2\Other\c.bin");

        IReadOnlyList<string> roots = CleanupPaths.ExpandRoots(_template + @"\*\Cache");

        Assert.Equal(2, roots.Count);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Users")]
    [InlineData(@"%USERPROFILE%")]
    [InlineData(@"%USERPROFILE%\Documents")]
    [InlineData(@"%LOCALAPPDATA%")]
    [InlineData(@"%PROGRAMFILES%")]
    public void DangerousRootsAreRejected(string path)
    {
        Assert.False(CleanupPaths.IsSafeRoot(Environment.ExpandEnvironmentVariables(path), out _));
    }

    [Fact]
    public void TempFolderIsAllowed()
    {
        Assert.True(CleanupPaths.IsSafeRoot(Path.GetTempPath(), out string? reason), reason);
    }

    [Fact]
    public void EmbeddedCatalogHasNoProblems()
    {
        CleanupCatalog catalog = CleanupCatalog.LoadEmbedded();

        Assert.Empty(catalog.Problems);
        Assert.True(catalog.Rules.Count >= 10);
    }

    [Fact]
    public void CatalogRejectsPathOutsideEnvironmentVariables()
    {
        const string json = """
            {
              "categories": [ { "id": "system", "title": "Windows" } ],
              "rules": [
                { "id": "bad", "title": "Плохое", "category": "system", "paths": [ "C:\\Users" ] },
                { "id": "home", "title": "Профиль", "category": "system", "paths": [ "%USERPROFILE%" ] }
              ]
            }
            """;

        CleanupCatalog catalog = CleanupCatalog.Parse(json);

        Assert.Empty(catalog.Rules);
        Assert.Equal(2, catalog.Problems.Count);
    }

    [Fact]
    public void GateAllowsOneOperationAtATime()
    {
        var gate = new OperationGate();
        using IDisposable? first = gate.TryEnter("применение", out _);
        IDisposable? second = gate.TryEnter("очистка", out string? busy);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal("применение", busy);
    }
}
