using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Diagnostics;

/// <summary>Событие из журнала Windows в простом виде: его можно собрать и в тестах.</summary>
public sealed record RawEvent
{
    public string Provider { get; init; } = "";

    public int Id { get; init; }

    /// <summary>Местное время.</summary>
    public DateTime Time { get; init; }

    /// <summary>1 — критическое, 2 — ошибка, 3 — предупреждение, 4 — сведения.</summary>
    public int Level { get; init; } = 4;

    /// <summary>Значения &lt;Data&gt; по порядку.</summary>
    public IReadOnlyList<string> Data { get; init; } = Array.Empty<string>();

    /// <summary>Значения &lt;Data Name="…"&gt;.</summary>
    public IReadOnlyDictionary<string, string> Named { get; init; } = new Dictionary<string, string>();

    /// <summary>Текст события, как его показывает «Просмотр событий» (есть не у всех).</summary>
    public string? Message { get; init; }

    public string DataAt(int index) => index < Data.Count ? Data[index] : "";

    public string NamedOr(string name, int index) =>
        Named.TryGetValue(name, out string? value) && value.Length > 0 ? value : DataAt(index);
}

/// <summary>
/// Читает из журналов «Система» и «Приложение» только события о сбоях: синие экраны, внезапные выключения,
/// аппаратные ошибки, сбросы драйвера видеокарты, ошибки дисков и вылеты программ. Права администратора не нужны.
/// </summary>
public static class CrashEventReader
{
    private const int MaxPerQuery = 400;

    /// <summary>(журнал, условие XPath, нужен ли текст события)</summary>
    private static readonly (string Log, string Filter, bool Message)[] Queries =
    {
        ("System", "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001", false),
        ("System", "Provider[@Name='BugCheck'] and EventID=1001", false),
        ("System", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41", false),
        ("System", "Provider[@Name='EventLog'] and EventID=6008", false),
        ("System", "Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2 or Level=3)", true),
        ("System", "Provider[@Name='Display'] and EventID=4101", false),
        ("System", "Provider[@Name='nvlddmkm'] and EventID=153", false),
        ("System", "Provider[@Name='disk'] and (EventID=7 or EventID=11 or EventID=51 or EventID=153)", false),
        ("System", "(Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs']) and (EventID=55 or EventID=98) and (Level=1 or Level=2)", true),
        ("System", "EventID=129 and Level=3", false),
        ("System", "Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and (EventID=1101 or EventID=1102)", false),
        ("Application", "Provider[@Name='Application Error'] and EventID=1000", false),
        ("Application", "Provider[@Name='Application Hang'] and EventID=1002", false),
    };

    /// <summary>События не старше <paramref name="since"/>. Ошибки чтения отдельных журналов — в <paramref name="error"/>.</summary>
    public static List<RawEvent> Read(DateTime since, out string? error)
    {
        var events = new List<RawEvent>();
        error = null;
        if (!OperatingSystem.IsWindows())
            return events;

        long ms = Math.Max(1, (long)(DateTime.Now - since).TotalMilliseconds);
        var errors = new List<string>();
        foreach ((string log, string filter, bool message) in Queries)
        {
            string xpath = $"*[System[({filter}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            try
            {
                var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                for (int i = 0; i < MaxPerQuery; i++)
                {
                    using EventRecord? record = reader.ReadEvent();
                    if (record is null)
                        break;

                    events.Add(Convert(record, message));
                }
            }
            catch (EventLogNotFoundException)
            {
                // Журнала нет (урезанная сборка Windows) — пропускаем.
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogException or InvalidOperationException)
            {
                AppLog.Warn($"Не удалось прочитать журнал {log} ({filter}).", ex);
                string text = ex is UnauthorizedAccessException ? $"нет доступа к журналу «{log}»" : ex.Message;
                if (!errors.Contains(text))
                    errors.Add(text);
            }
        }

        if (errors.Count > 0)
            error = "Часть журнала Windows не прочитана: " + string.Join("; ", errors) + ".";
        return events;
    }

    private static RawEvent Convert(EventRecord record, bool withMessage)
    {
        var data = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            XElement root = XElement.Parse(record.ToXml());
            XElement? eventData = root.Elements().FirstOrDefault(e => e.Name.LocalName is "EventData" or "UserData");
            IEnumerable<XElement> items = eventData?.Name.LocalName == "UserData"
                ? eventData.Elements().FirstOrDefault()?.Elements() ?? Enumerable.Empty<XElement>()
                : eventData?.Elements().Where(e => e.Name.LocalName == "Data") ?? Enumerable.Empty<XElement>();
            foreach (XElement item in items)
            {
                data.Add(item.Value);
                string? name = item.Attribute("Name")?.Value ?? (eventData?.Name.LocalName == "UserData" ? item.Name.LocalName : null);
                if (!string.IsNullOrEmpty(name))
                    named[name] = item.Value;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось разобрать событие {record.ProviderName} {record.Id}.", ex);
        }

        string? message = null;
        if (withMessage)
        {
            try
            {
                message = record.FormatDescription();
            }
            catch (EventLogException)
            {
                // Нет файла с текстами сообщений — обойдёмся данными события.
            }
        }

        return new RawEvent
        {
            Provider = record.ProviderName ?? "",
            Id = record.Id,
            Time = record.TimeCreated ?? DateTime.Now,
            Level = record.Level ?? 4,
            Data = data,
            Named = named,
            Message = message,
        };
    }
}
