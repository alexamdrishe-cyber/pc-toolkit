using System.IO.Compression;
using System.Text;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Diagnostics;

/// <summary>
/// Архив для разработчика: журналы приложения и службы, сведения о железе и Windows,
/// настройки и результаты замеров. Нужен, чтобы разбирать ошибки на чужих компьютерах.
/// Документов, паролей и личных файлов в нём нет; в журналах встречаются пути к папкам пользователя.
/// </summary>
public static class DeveloperReport
{
    /// <summary>Что попадает в архив — для подписи у кнопки.</summary>
    public const string ContentsText =
        "журналы программы и службы, сведения о железе и Windows, настройки, журнал изменений и результаты замеров";

    // Файлы данных из %LocalAppData%\PcToolkit. Резервные копии реестра (backups) не берём.
    private static readonly string[] DataFiles =
    {
        "settings.json", "journal.json", "benchmarks.json", "games.json", "game-session.json",
        "fps-sessions.json", "cleanup-history.json", "removed-store-apps.json", "sensors.txt", "gpu-tuning-check.json",
    };

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit");

    public static string ServiceLogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcToolkit", "logs");

    /// <summary>
    /// Собирает архив в <paramref name="folder"/> и возвращает путь к нему.
    /// <paramref name="about"/> — короткая сводка (версия, Windows, права, служба), <paramref name="systemReport"/> — отчёт «Система».
    /// </summary>
    public static string Create(string folder, string about, string? systemReport, string? dataDirectory = null,
                                string? appLogDirectory = null, string? serviceLogDirectory = null)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"PcToolkit-report-{DateTime.Now:yyyy-MM-dd-HHmm}.zip");
        string temp = path + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            AddText(zip, "about.txt", about);
            if (!string.IsNullOrWhiteSpace(systemReport))
                AddText(zip, "system.txt", systemReport);

            AddFolder(zip, appLogDirectory ?? AppLog.LogDirectory, "logs/");
            AddFolder(zip, serviceLogDirectory ?? ServiceLogDirectory, "service-logs/");
            string data = dataDirectory ?? DataDirectory;
            foreach (string name in DataFiles)
                AddFile(zip, Path.Combine(data, name), "data/" + name);
        }

        File.Move(temp, path, overwrite: true);
        AppLog.Info($"Отчёт для разработчика сохранён: {path}.");
        return path;
    }

    private static void AddFolder(ZipArchive zip, string folder, string prefix)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;

            foreach (string file in Directory.EnumerateFiles(folder, "*.log").Order(StringComparer.OrdinalIgnoreCase))
                AddFile(zip, file, prefix + Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            AddText(zip, prefix + "error.txt", $"Не удалось прочитать папку {folder}: {ex.Message}");
        }
    }

    private static void AddFile(ZipArchive zip, string source, string entryName)
    {
        try
        {
            if (!File.Exists(source))
                return;

            // Журнал может быть открыт на запись прямо сейчас: читаем с общим доступом.
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(source);
            using Stream output = entry.Open();
            input.CopyTo(output);
        }
        catch (Exception ex)
        {
            AddText(zip, entryName + ".error.txt", $"Не удалось прочитать {source}: {ex.Message}");
        }
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.Write(text);
    }
}
