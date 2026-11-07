using System.Text.Json;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Cleanup;

public sealed class CleanupRunRecord
{
    public DateTimeOffset At { get; set; }

    public long FreedBytes { get; set; }

    public int Deleted { get; set; }

    public int Skipped { get; set; }

    public List<string> Rules { get; set; } = new();
}

/// <summary>
/// История очисток в %LocalAppData%\PcToolkit\cleanup-history.json.
/// Удаление файлов отменить нельзя, поэтому в журнал изменений (с откатом) очистка не пишется — только сюда.
/// </summary>
public sealed class CleanupHistory
{
    private const int MaxRecords = 30;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly List<CleanupRunRecord> _records;

    private CleanupHistory(string path, List<CleanupRunRecord> records)
    {
        _path = path;
        _records = records;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "cleanup-history.json");

    /// <summary>Записи от новых к старым.</summary>
    public IReadOnlyList<CleanupRunRecord> Records => _records;

    public static CleanupHistory Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                List<CleanupRunRecord>? records = JsonSerializer.Deserialize<List<CleanupRunRecord>>(File.ReadAllText(path), JsonOptions);
                return new CleanupHistory(path, records ?? new List<CleanupRunRecord>());
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать историю очистки, начинаю новую.", ex);
        }

        return new CleanupHistory(path, new List<CleanupRunRecord>());
    }

    public void Add(CleanupRunRecord record)
    {
        _records.Insert(0, record);
        if (_records.Count > MaxRecords)
            _records.RemoveRange(MaxRecords, _records.Count - MaxRecords);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_records, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить историю очистки.", ex);
        }
    }
}
