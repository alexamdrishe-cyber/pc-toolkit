using System.Text.Json;
using System.Text.Json.Serialization;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Changes;

public enum ChangeSetKind
{
    Apply,
    Revert,
}

public enum ChangeSetStatus
{
    /// <summary>Применение началось и ещё не закончилось. После сбоя такой набор откатывается при запуске.</summary>
    Pending,
    Applied,
    RolledBack,
}

/// <summary>Изменение одного параметра: что было, что стало, какая настройка его сделала.</summary>
public sealed class ChangeEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TweakId { get; set; } = "";
    public string TweakTitle { get; set; } = "";
    public string TargetKey { get; set; } = "";
    public string TargetDisplay { get; set; } = "";
    public ChangeDefinition Target { get; set; } = new();
    public ValueSnapshot Before { get; set; } = ValueSnapshot.Missing("");
    public ValueSnapshot After { get; set; } = ValueSnapshot.Missing("");
    public string BeforeText { get; set; } = "";
    public string AfterText { get; set; } = "";

    /// <summary>Значение уже записано в систему.</summary>
    public bool Done { get; set; }

    /// <summary>Для наборов отмены: какую запись отменяет эта.</summary>
    public Guid? RevertsEntryId { get; set; }

    /// <summary>Запись отменена (или освобождена) этим набором; после этого настройка больше не владеет параметром.</summary>
    public Guid? RevertedBySetId { get; set; }

    public DateTime? RevertedAt { get; set; }
}

/// <summary>Набор изменений, применённых за одно действие пользователя.</summary>
public sealed class ChangeSet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ChangeSetKind Kind { get; set; }
    public string Title { get; set; } = "";
    public ChangeSetStatus Status { get; set; }
    public string? Error { get; set; }
    public RestartRequirement Restart { get; set; }
    public List<ChangeEntry> Entries { get; set; } = new();
}

/// <summary>
/// Журнал всех изменений системы в JSON-файле. Запись атомарная: сначала во временный файл, потом замена.
/// </summary>
public sealed class ChangeJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly List<ChangeSet> _sets;

    private ChangeJournal(string path, List<ChangeSet> sets)
    {
        _path = path;
        _sets = sets;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "journal.json");

    public string FilePath => _path;

    public IReadOnlyList<ChangeSet> Sets => _sets;

    public static ChangeJournal Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (File.Exists(file))
            {
                List<ChangeSet>? sets = JsonSerializer.Deserialize<List<ChangeSet>>(File.ReadAllText(file), JsonOptions);
                return new ChangeJournal(file, sets ?? new List<ChangeSet>());
            }
        }
        catch (Exception ex)
        {
            // Повреждённый журнал не удаляем: откладываем в сторону, чтобы его можно было разобрать вручную.
            string broken = Path.ChangeExtension(file, $".broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            AppLog.Error($"Журнал изменений повреждён, сохранён как {broken}. Начинаю новый журнал.", ex);
            try
            {
                File.Move(file, broken);
            }
            catch (Exception moveError)
            {
                AppLog.Warn("Не удалось отложить повреждённый журнал.", moveError);
            }
        }

        return new ChangeJournal(file, new List<ChangeSet>());
    }

    public void Add(ChangeSet set)
    {
        _sets.Add(set);
        Save();
    }

    public ChangeEntry? FindEntry(Guid entryId) =>
        _sets.SelectMany(set => set.Entries).FirstOrDefault(entry => entry.Id == entryId);

    public void Save()
    {
        string? folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_sets, JsonOptions));
        File.Move(temporary, _path, overwrite: true);
    }
}
