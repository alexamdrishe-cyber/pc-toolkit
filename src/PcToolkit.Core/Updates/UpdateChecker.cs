using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Updates;

/// <summary>Новая версия: номер, откуда скачать установщик, его SHA-256 (если известен) и что нового.</summary>
public sealed record UpdateInfo(Version Version, string Url, string? Sha256, string Notes);

/// <summary>
/// Автообновление. Источник — файл описания (update.json: version, url, sha256, notes) по адресу из настроек
/// или выпуски GitHub (api.github.com/repos/…/releases/latest: тег, установщик *-Setup.exe и рядом *.sha256).
/// Установщик скачивается во временную папку, сверяется по SHA-256 и запускается тихо с /relaunch=1:
/// Windows спросит разрешение, установщик закроет программу, обновит и запустит её снова.
/// </summary>
public static class UpdateChecker
{
    private static readonly HttpClient Http = CreateClient();

    public static Version Current => typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>Есть ли версия новее текущей. null — нет (или адрес не задан, или сеть недоступна).</summary>
    public static async Task<UpdateInfo?> CheckAsync(string url, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        string json = await Http.GetStringAsync(uri, cancel).ConfigureAwait(false);
        UpdateInfo? info = uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            ? await FromGitHubAsync(json, cancel).ConfigureAwait(false)
            : FromManifest(json);
        if (info is null)
            return null;
        AppLog.Info($"Обновления: на сервере {info.Version}, установлена {Current}.");
        return info.Version > Current ? info : null;
    }

    /// <summary>update.json: {"version":"0.4.1","url":"https://…/PcToolkit-0.4.1-Setup.exe","sha256":"…","notes":"…"}</summary>
    public static UpdateInfo? FromManifest(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!Version.TryParse(Text(root, "version"), out Version? version) || Text(root, "url") is not { Length: > 0 } url ||
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        return new UpdateInfo(version, url, Text(root, "sha256"), Text(root, "notes") ?? "");
    }

    /// <summary>Скачать установщик (с проверкой SHA-256, если он известен). Возвращает путь к файлу.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken cancel)
    {
        string folder = Path.Combine(Path.GetTempPath(), "PcToolkitUpdate");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"PcToolkit-{info.Version}-Setup.exe");

        using HttpResponseMessage response = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using (Stream source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
        await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                done += read;
                if (total > 0)
                    progress?.Report((double)done / total.Value);
            }
        }

        if (info.Sha256 is { Length: 64 } expected)
        {
            string actual = Convert.ToHexString(await File.ReadAllBytesAsync(path, cancel).ConfigureAwait(false) is { } bytes ? SHA256.HashData(bytes) : Array.Empty<byte>());
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException("Скачанный установщик не совпал с контрольной суммой — файл повреждён или подменён. Попробуйте позже.");
            }
        }

        AppLog.Info($"Обновления: скачан установщик {info.Version}.");
        return path;
    }

    /// <summary>Запустить установщик тихо; после установки он сам запустит программу снова.</summary>
    public static void Install(string setup)
    {
        Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1") { UseShellExecute = true });
        AppLog.Info("Обновления: запущен установщик.");
    }

    private static async Task<UpdateInfo?> FromGitHubAsync(string json, CancellationToken cancel)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string tag = (Text(root, "tag_name") ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(tag, out Version? version) || !root.TryGetProperty("assets", out JsonElement assets))
            return null;

        string? setup = null, sha = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name = Text(asset, "name") ?? "";
            string link = Text(asset, "browser_download_url") ?? "";
            if (name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                setup = link;
            else if (name.EndsWith("-Setup.exe.sha256", StringComparison.OrdinalIgnoreCase))
                sha = link;
        }

        if (setup is null)
            return null;
        string? hash = null;
        if (sha is not null)
        {
            string text = await Http.GetStringAsync(sha, cancel).ConfigureAwait(false);
            hash = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }

        return new UpdateInfo(version, setup, hash, Text(root, "body") ?? "");
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PcToolkit-Updater/" + Current);
        return client;
    }
}
