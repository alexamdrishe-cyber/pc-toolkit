using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcToolkit.Core.Rating;

namespace PcToolkit.Rating;

/// <summary>Результаты в SQLite (один файл в томе с данными) и сводные таблицы с кэшем на минуту.</summary>
public sealed class RatingStore
{
    private readonly string _connection;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset At, RatingTable Table)> _cache = new();

    public RatingStore(string path)
    {
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using SqliteConnection db = Open();
        Execute(db, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS results(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL,
                test_version INTEGER NOT NULL,
                model TEXT NOT NULL,
                model_key TEXT NOT NULL,
                score REAL NOT NULL,
                score2 REAL,
                machine TEXT NOT NULL,
                app_version TEXT,
                conditions TEXT,
                created TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_results_kind ON results(kind, test_version, model_key);
            """);
    }

    /// <summary>В базе ещё нет ни одного результата.</summary>
    public bool IsEmpty
    {
        get
        {
            using SqliteConnection db = Open();
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM results)";
            return command.ExecuteScalar() is long exists && exists == 0;
        }
    }

    /// <summary>Записать результат. false — такой уже есть (повторная отправка).</summary>
    public bool Add(RatingSubmission submission)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO results(client_id, kind, test_version, model, model_key, score, score2, machine, app_version, conditions, created)
            VALUES ($client, $kind, $version, $model, $key, $score, $score2, $machine, $app, $conditions, $created)
            """;
        command.Parameters.AddWithValue("$client", submission.ClientId);
        command.Parameters.AddWithValue("$kind", submission.Kind);
        command.Parameters.AddWithValue("$version", submission.TestVersion);
        command.Parameters.AddWithValue("$model", submission.Model.Trim());
        command.Parameters.AddWithValue("$key", RatingStats.ModelKey(submission.Model));
        command.Parameters.AddWithValue("$score", submission.Score);
        command.Parameters.AddWithValue("$score2", (object?)submission.Score2 ?? DBNull.Value);
        command.Parameters.AddWithValue("$machine", submission.Machine.ToLowerInvariant());
        command.Parameters.AddWithValue("$app", submission.AppVersion);
        command.Parameters.AddWithValue("$conditions", submission.Conditions is null ? DBNull.Value : JsonSerializer.Serialize(submission.Conditions));
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        bool added = command.ExecuteNonQuery() > 0;
        if (added)
        {
            lock (_cacheLock)
                _cache.Clear();
        }

        return added;
    }

    /// <summary>Сводная таблица вида: по моделям, новые версии теста; кэш — минута.</summary>
    public RatingTable Table(string kind, int? version)
    {
        string cacheKey = $"{kind}:{version}";
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(1))
                return cached.Table;
        }

        using SqliteConnection db = Open();
        int testVersion = version ?? LatestVersion(db, kind);
        var rows = new List<(string Key, double Score, double? Score2, string Machine)>();
        using (SqliteCommand command = db.CreateCommand())
        {
            command.CommandText = "SELECT model_key, score, score2, machine FROM results WHERE kind = $kind AND test_version = $version";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$version", testVersion);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetDouble(1), reader.IsDBNull(2) ? null : reader.GetDouble(2), reader.GetString(3)));
        }

        var table = new RatingTable
        {
            Kind = kind,
            TestVersion = testVersion,
            Updated = DateTimeOffset.UtcNow,
            Models = rows
                .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => RatingStats.Summarize(g.Key, g.Select(r => (r.Score, r.Score2, r.Machine))))
                .OfType<RatingModel>()
                .OrderByDescending(m => m.Median)
                .ToList(),
        };
        lock (_cacheLock)
            _cache[cacheKey] = (DateTimeOffset.UtcNow, table);
        return table;
    }

    private static int LatestVersion(SqliteConnection db, string kind)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT MAX(test_version) FROM results WHERE kind = $kind";
        command.Parameters.AddWithValue("$kind", kind);
        return command.ExecuteScalar() is long value ? (int)value : 0;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
