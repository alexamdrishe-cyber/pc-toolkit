using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Settings;
using PcToolkit.Core.Updates;

namespace PcToolkit.Core.Rating;

/// <summary>Таблица рейтинга и откуда она: свежая с сервера или сохранённая (сервер недоступен).</summary>
public sealed record RatingTableResult(RatingTable Table, bool Fresh);

/// <summary>
/// Связь с сервером общего рейтинга. Адрес сервера берётся из pctoolkit.json в репозитории проекта на GitHub
/// (его можно поменять без выпуска новой версии) или свой из настроек. Отправка — только с согласия пользователя;
/// что не ушло (нет сети, сервер выключен), ждёт в очереди и уходит при следующей возможности.
/// Таблицы сохраняются на диске: без сети виден последний полученный список.
/// </summary>
public sealed class RatingClient
{
    private const int MaxQueue = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = CreateClient();
    private static readonly Lazy<string> Machine = new(ComputeMachineId);
    private readonly string _folder;
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly object _serverLock = new();
    private string? _server;
    private DateTimeOffset _serverCheckedAt;

    public RatingClient(string? folder = null) => _folder = folder ?? DefaultFolder;

    public static RatingClient Shared { get; } = new();

    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "rating");

    /// <summary>Анонимный номер компьютера: хеш номера Windows (MachineGuid) с солью; сам номер никуда не уходит.</summary>
    public static string MachineId => Machine.Value;

    /// <summary>Сколько результатов ждут отправки.</summary>
    public int QueuedCount => LoadQueue().Count;

    /// <summary>Рейтинг вообще можно подключить: задан репозиторий проекта или свой сервер.</summary>
    public static bool IsAvailable => ProjectLinks.ConfigUrls.Count > 0 || !string.IsNullOrWhiteSpace(AppSettings.Load().RatingUrl);

    /// <summary>
    /// Адрес сервера рейтинга или null, если он не задан. Сначала свой из настроек, потом pctoolkit.json
    /// (проверяется раз в 6 часов), а без сети — последний известный.
    /// </summary>
    public async Task<string?> ServerAsync(CancellationToken cancel)
    {
        string? own = AppSettings.Load().RatingUrl;
        if (!string.IsNullOrWhiteSpace(own))
            return NormalizeServer(own, allowHttp: true);

        lock (_serverLock)
        {
            if (_server is not null && DateTimeOffset.UtcNow - _serverCheckedAt < TimeSpan.FromHours(6))
                return _server;
        }

        foreach (string url in ProjectLinks.ConfigUrls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
                    request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                using HttpResponseMessage response = await Http.SendAsync(request, cancel).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;
                string? server = ParseConfig(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
                lock (_serverLock)
                {
                    _server = server;
                    _serverCheckedAt = DateTimeOffset.UtcNow;
                }

                WriteText("server.txt", server ?? "");
                return server;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancel.IsCancellationRequested))
            {
                AppLog.Warn($"Рейтинг: не удалось прочитать {url}.", ex);
            }
        }

        string cached = ReadText("server.txt");
        return cached.Length > 0 ? NormalizeServer(cached, allowHttp: false) : null;
    }

    /// <summary>pctoolkit.json: {"ratingUrl": "https://…"}. null — рейтинг выключен или адрес неправильный.</summary>
    public static string? ParseConfig(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.TryGetProperty("ratingUrl", out JsonElement url) && url.ValueKind == JsonValueKind.String
            ? NormalizeServer(url.GetString() ?? "", allowHttp: false)
            : null;
    }

    /// <summary>Адрес без «/» в конце; только https (http — только для своего сервера из настроек, например в домашней сети).</summary>
    public static string? NormalizeServer(string url, bool allowHttp)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.Scheme == Uri.UriSchemeHttp)) ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>
    /// Отправить результат. Он сначала ложится в очередь на диске, потом уходит вместе со всем, что ждало.
    /// true — сервер его принял (или уже имел).
    /// </summary>
    public async Task<bool> SubmitAsync(RatingSubmission submission, CancellationToken cancel)
    {
        submission.Machine = MachineId;
        submission.AppVersion = UpdateChecker.Current.ToString(3);
        if (submission.Validate() is { } error)
        {
            AppLog.Warn($"Рейтинг: результат «{submission.Model}» не отправлен — {error}.");
            return false;
        }

        await _queueLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            List<RatingSubmission> queue = LoadQueue();
            if (queue.All(q => q.ClientId != submission.ClientId))
                queue.Add(submission);
            SaveQueue(queue);
        }
        finally
        {
            _queueLock.Release();
        }

        await FlushAsync(cancel).ConfigureAwait(false);
        return LoadQueue().All(q => q.ClientId != submission.ClientId);
    }

    /// <summary>Отправить всё, что ждёт. Возвращает, сколько осталось в очереди.</summary>
    public async Task<int> FlushAsync(CancellationToken cancel)
    {
        await _queueLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            List<RatingSubmission> queue = LoadQueue();
            if (queue.Count == 0)
                return 0;
            string? server = await ServerAsync(cancel).ConfigureAwait(false);
            if (server is null)
                return queue.Count;

            foreach (RatingSubmission item in queue.ToList())
            {
                using HttpResponseMessage response = await Http.PostAsJsonAsync(server + "/api/v1/results", item, Json, cancel).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    queue.Remove(item);
                }
                else if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    // Сервер такой результат не примет никогда — не держим его в очереди.
                    AppLog.Warn($"Рейтинг: сервер отклонил результат «{item.Model}»: {await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false)}");
                    queue.Remove(item);
                }
                else
                {
                    // 429 (лимит на сегодня), 5xx, туннель выключен — попробуем позже.
                    AppLog.Info($"Рейтинг: сервер ответил {(int)response.StatusCode}, отправка отложена.");
                    break;
                }
            }

            SaveQueue(queue);
            if (queue.Count == 0)
                AppLog.Info("Рейтинг: результаты отправлены.");
            return queue.Count;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancel.IsCancellationRequested))
        {
            AppLog.Info("Рейтинг: сервер недоступен, отправка отложена. " + ex.Message);
            return LoadQueue().Count;
        }
        finally
        {
            _queueLock.Release();
        }
    }

    /// <summary>
    /// Таблица рейтинга для вида и версии теста: с сервера (если сохранённой больше 10 минут или <paramref name="refresh"/>),
    /// при недоступном сервере — сохранённая. null — таблицы нет ни там, ни там.
    /// </summary>
    public async Task<RatingTableResult?> TableAsync(string kind, int version, bool refresh, CancellationToken cancel)
    {
        string file = $"top-{kind}-v{version}.json";
        RatingTable? cached = ReadJson<RatingTable>(file);
        if (!refresh && cached is not null && DateTimeOffset.UtcNow - cached.Updated < TimeSpan.FromMinutes(10))
            return new RatingTableResult(cached, true);

        try
        {
            string? server = await ServerAsync(cancel).ConfigureAwait(false);
            if (server is not null)
            {
                RatingTable? table = await Http.GetFromJsonAsync<RatingTable>($"{server}/api/v1/top/{kind}?version={version}", Json, cancel).ConfigureAwait(false);
                if (table is not null)
                {
                    WriteText(file, JsonSerializer.Serialize(table, Json));
                    return new RatingTableResult(table, true);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancel.IsCancellationRequested))
        {
            AppLog.Info($"Рейтинг: таблица «{kind}» не загрузилась. {ex.Message}");
        }

        return cached is null ? null : new RatingTableResult(cached, false);
    }

    private static string ComputeMachineId()
    {
        string id = "";
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            id = (key?.GetValue("MachineGuid") as string ?? "").Trim().ToLowerInvariant();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            AppLog.Warn("Рейтинг: номер Windows не прочитан, будет случайный номер.", ex);
        }

        if (id.Length == 0)
        {
            id = AppSettings.Load().RatingMachineId ?? "";
            if (id.Length == 0)
            {
                id = Guid.NewGuid().ToString("N");
                string generated = id;
                AppSettings.Update(s => s.RatingMachineId = generated);
            }
        }

        return Hash(id);
    }

    /// <summary>SHA-256 с солью проекта: по хешу нельзя узнать номер Windows, а один ПК всегда даёт один и тот же хеш.</summary>
    public static string Hash(string machineGuid) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("PcToolkit.Rating.v1:" + machineGuid))).ToLowerInvariant();

    private List<RatingSubmission> LoadQueue() => ReadJson<List<RatingSubmission>>("queue.json") ?? new();

    private void SaveQueue(List<RatingSubmission> queue)
    {
        if (queue.Count > MaxQueue)
            queue.RemoveRange(0, queue.Count - MaxQueue);
        WriteText("queue.json", JsonSerializer.Serialize(queue, Json));
    }

    private T? ReadJson<T>(string name) where T : class
    {
        string text = ReadText(name);
        if (text.Length == 0)
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (JsonException ex)
        {
            AppLog.Warn($"Рейтинг: файл {name} повреждён, начинаю заново.", ex);
            return null;
        }
    }

    private string ReadText(string name)
    {
        try
        {
            string path = Path.Combine(_folder, name);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private void WriteText(string name, string text)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, name), temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Рейтинг: не удалось сохранить {name}.", ex);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PcToolkit/" + UpdateChecker.Current.ToString(3));
        // Бесплатный туннель tuna показывает браузерам страницу-предупреждение; программе она не нужна.
        client.DefaultRequestHeaders.Add("tuna-skip-browser-warning", "1");
        return client;
    }
}
