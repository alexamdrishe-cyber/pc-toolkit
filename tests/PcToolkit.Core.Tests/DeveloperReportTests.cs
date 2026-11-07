using System.IO.Compression;
using PcToolkit.Core.Diagnostics;

namespace PcToolkit.Core.Tests;

public sealed class DeveloperReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PcToolkit.Tests", "report-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ArchiveHasLogsDataAndSummary()
    {
        string data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        string logs = Directory.CreateDirectory(Path.Combine(_root, "logs")).FullName;
        string output = Path.Combine(_root, "out");
        File.WriteAllText(Path.Combine(data, "settings.json"), "{ \"SensorIntervalMs\": 250 }");
        File.WriteAllText(Path.Combine(data, "secret.txt"), "не должен попасть в архив");
        File.WriteAllText(Path.Combine(logs, "app.log"), "INFO старт");

        // Журнал открыт на запись, как у работающего приложения: архив всё равно собирается.
        using var open = new FileStream(Path.Combine(logs, "app.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        string path = DeveloperReport.Create(output, "PC Toolkit 0.2.0", "Процессор: тест", data, logs, Path.Combine(_root, "нет такой папки"));

        using ZipArchive zip = ZipFile.OpenRead(path);
        string[] names = zip.Entries.Select(e => e.FullName).Order().ToArray();
        Assert.Equal(new[] { "about.txt", "data/settings.json", "logs/app.log", "system.txt" }, names);
        using var reader = new StreamReader(zip.GetEntry("about.txt")!.Open());
        Assert.Equal("PC Toolkit 0.2.0", reader.ReadToEnd());
        Assert.False(File.Exists(path + ".tmp"));
    }
}
