using System.Text.Json;
using System.Text.Json.Serialization;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Disks;

/// <summary>Что PC Toolkit помнит о диске между проверками.</summary>
public sealed class DiskHealthRecord
{
    public string Model { get; set; } = "";

    /// <summary>Первая проверка: с этого момента считается «выросло с …».</summary>
    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset CheckedAt { get; set; }

    public DiskVerdict Verdict { get; set; } = DiskVerdict.Unknown;

    /// <summary>Счётчики ошибок при первой проверке (новые счётчики добавляются, когда появляются).</summary>
    public Dictionary<string, long> FirstCounters { get; set; } = new();

    /// <summary>Счётчики при последней проверке: по ним видно, что изменилось с прошлого раза.</summary>
    public Dictionary<string, long> Counters { get; set; } = new();
}

/// <summary>Что ухудшилось с прошлой проверки — для уведомления.</summary>
public sealed record DiskHealthNews(string Disk, DiskVerdict Verdict, string Text);

/// <summary>
/// История проверок дисков в %LocalAppData%\PcToolkit\disk-health.json: по ней видно, растут ли счётчики ошибок,
/// и приходят уведомления, когда диску стало хуже.
/// </summary>
public sealed class DiskHealthHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Dictionary<string, DiskHealthRecord> _records;
    private readonly object _lock = new();

    private DiskHealthHistory(string path, Dictionary<string, DiskHealthRecord> records)
    {
        _path = path;
        _records = records;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "disk-health.json");

    public static DiskHealthHistory Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                Dictionary<string, DiskHealthRecord>? records =
                    JsonSerializer.Deserialize<Dictionary<string, DiskHealthRecord>>(File.ReadAllText(path), JsonOptions);
                return new DiskHealthHistory(path, records ?? new Dictionary<string, DiskHealthRecord>());
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать историю проверок дисков, начинаю новую.", ex);
        }

        return new DiskHealthHistory(path, new Dictionary<string, DiskHealthRecord>());
    }

    public DiskHealthRecord? Get(string key)
    {
        lock (_lock)
            return _records.TryGetValue(key, out DiskHealthRecord? record) ? record : null;
    }

    /// <summary>Оценивает диски с учётом истории.</summary>
    public List<DiskHealthReport> Evaluate(IEnumerable<DiskHealthData> disks) =>
        disks.Select(d => DiskHealthEvaluator.Evaluate(d, Get(d.HistoryKey))).ToList();

    /// <summary>
    /// Запоминает результаты проверки и возвращает, что ухудшилось с прошлого раза:
    /// оценка стала хуже или вырос счётчик ошибок. О проблемном диске, который проверяется впервые, тоже сообщает.
    /// </summary>
    public IReadOnlyList<DiskHealthNews> Record(IEnumerable<DiskHealthReport> reports, DateTimeOffset now)
    {
        var news = new List<DiskHealthNews>();
        lock (_lock)
        {
            foreach (DiskHealthReport report in reports)
            {
                if (report.Verdict == DiskVerdict.Unknown)
                    continue;

                string key = report.Disk.HistoryKey;
                if (!_records.TryGetValue(key, out DiskHealthRecord? record))
                {
                    record = new DiskHealthRecord { FirstSeen = now, FirstCounters = new Dictionary<string, long>(report.Counters) };
                    _records[key] = record;
                    if (report.Verdict is DiskVerdict.Bad or DiskVerdict.Warning)
                        news.Add(new DiskHealthNews(report.Title, report.Verdict, report.Summary));
                }
                else if (report.Verdict is DiskVerdict.Bad or DiskVerdict.Warning && Worse(report.Verdict, record.Verdict))
                {
                    news.Add(new DiskHealthNews(report.Title, report.Verdict, report.Summary));
                }
                else
                {
                    var grown = new List<string>();
                    foreach ((string counter, string name) in DiskHealthEvaluator.WatchedCounters)
                    {
                        if (report.Counters.TryGetValue(counter, out long value) && record.Counters.TryGetValue(counter, out long old) && value > old)
                            grown.Add($"{name}: было {Formatting.Number(old)}, стало {Formatting.Number(value)}");
                    }

                    if (grown.Count > 0)
                    {
                        news.Add(new DiskHealthNews(report.Title, report.Verdict,
                            "Счётчики ошибок выросли с прошлой проверки (" + string.Join("; ", grown) + "). Скопируйте важные файлы."));
                    }
                }

                record.Model = report.Title;
                record.CheckedAt = now;
                record.Verdict = report.Verdict;
                record.Counters = new Dictionary<string, long>(report.Counters);
                foreach ((string counter, long value) in report.Counters)
                    record.FirstCounters.TryAdd(counter, value);
            }

            Save();
        }

        return news;
    }

    private static bool Worse(DiskVerdict now, DiskVerdict before) => Rank(now) > Rank(before);

    private static int Rank(DiskVerdict verdict) => verdict switch
    {
        DiskVerdict.Good => 1,
        DiskVerdict.Warning => 2,
        DiskVerdict.Bad => 3,
        _ => 0,
    };

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_records, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить историю проверок дисков.", ex);
        }
    }
}
