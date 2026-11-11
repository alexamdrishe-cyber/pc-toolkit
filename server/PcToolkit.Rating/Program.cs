using System.Collections.Concurrent;
using PcToolkit.Core.Rating;
using PcToolkit.Rating;

// Сервер общего рейтинга PC Toolkit: принимает результаты тестов (анонимно, с согласия) и отдаёт сводные таблицы.
// Наружу его выводит туннель (tuna) — сам он слушает только внутреннюю сеть Docker, HTTPS делает туннель.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// В контейнере — том /data (RATING_DATA задан в образе), при запуске у себя — папка data рядом с сервером.
string data = Environment.GetEnvironmentVariable("RATING_DATA") is { Length: > 0 } folder ? folder : Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(data);
var store = new RatingStore(Path.Combine(data, "rating.db"));

// --demo (только для проверки у себя, run-local.cmd): пустая база заполняется выдуманными результатами популярных устройств.
if (args.Contains("--demo") && store.IsEmpty)
{
    var demo = System.Text.Json.JsonSerializer.Deserialize<List<RatingSubmission>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "demo.json")),
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new();
    foreach (RatingSubmission item in demo)
        store.Add(item);
    Console.WriteLine($"Демо: добавлено {demo.Count} выдуманных результатов.");
}
var limits = new ConcurrentDictionary<string, (DateOnly Day, int Count)>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapPost("/api/v1/results", (RatingSubmission submission, HttpContext http) =>
{
    if (submission.Validate() is { } error)
        return Results.BadRequest(new { ok = false, error });

    // Не больше 40 результатов в день с одного компьютера и 200 — с одного адреса (IP нигде не сохраняется).
    string ip = http.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                ?? http.Connection.RemoteIpAddress?.ToString() ?? "?";
    if (!Allow(limits, "m:" + submission.Machine, 40) || !Allow(limits, "ip:" + ip, 200))
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);

    bool added = store.Add(submission);
    return Results.Ok(new { ok = true, added });
});

app.MapGet("/api/v1/top/{kind}", (string kind, int? version) =>
    RatingKinds.IsKnown(kind) ? Results.Ok(store.Table(kind, version)) : Results.NotFound());

app.Run();

static bool Allow(ConcurrentDictionary<string, (DateOnly Day, int Count)> limits, string key, int perDay)
{
    DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
    var entry = limits.AddOrUpdate(key, _ => (today, 1), (_, old) => old.Day == today ? (today, old.Count + 1) : (today, 1));
    if (limits.Count > 100_000)
        limits.Clear();
    return entry.Count <= perDay;
}
