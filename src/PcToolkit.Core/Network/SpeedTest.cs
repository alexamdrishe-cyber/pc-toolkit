using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Network;

/// <summary>Сервер проверки скорости (открытые серверы LibreSpeed: скачивание, отдача, пинг).</summary>
public sealed record SpeedServer(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("dlURL")] string DownloadPath,
    [property: JsonPropertyName("ulURL")] string UploadPath,
    [property: JsonPropertyName("pingURL")] string PingPath)
{
    public Uri Url(string path) => new(Server.TrimEnd('/') + "/" + path.TrimStart('/'));
}

/// <summary>Итог проверки скорости.</summary>
public sealed record SpeedResult(SpeedServer Server, double PingMs, double JitterMs, double DownloadMbps, double UploadMbps);

/// <summary>Ход проверки: этап и текущее значение.</summary>
public readonly record struct SpeedProgress(string Stage, double Value, double Fraction);

/// <summary>
/// Проверка скорости интернета по открытым серверам LibreSpeed (их список публикует проект librespeed.org;
/// серверы предназначены для таких проверок). «Рядом» — сервер с самым низким пингом, «далеко» — выбранный
/// человеком: так видно и скорость до ближайшего узла, и скорость до другой части света.
/// Скачивание — 6 потоков, отдача — 3 потока, по 10 секунд; первые полторы секунды (разгон TCP) не считаются.
/// </summary>
public sealed class SpeedTest : IDisposable
{
    private const string ServersUrl = "https://librespeed.org/backend-servers/servers.php";
    private static readonly TimeSpan Phase = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(1.5);

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        MaxConnectionsPerServer = 8,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public void Dispose() => _http.Dispose();

    /// <summary>Список серверов: свежий с librespeed.org, при ошибке — встроенный.</summary>
    public async Task<IReadOnlyList<SpeedServer>> ServersAsync(CancellationToken cancel)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            string json = await _http.GetStringAsync(ServersUrl, timeout.Token).ConfigureAwait(false);
            List<SpeedServer>? servers = JsonSerializer.Deserialize<List<SpeedServer>>(json);
            if (servers is { Count: > 0 })
                return servers.Where(s => Uri.TryCreate(s.Server, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.Warn("Список серверов скорости не загрузился — беру встроенный.", ex);
        }

        return Fallback;
    }

    /// <summary>Пинг до сервера по HTTP (лучший из нескольких), мс; null — не отвечает.</summary>
    public async Task<double?> PingAsync(SpeedServer server, int attempts, CancellationToken cancel)
    {
        double? best = null;
        for (int i = 0; i < attempts; i++)
        {
            double? ms = await OnePingAsync(server, cancel).ConfigureAwait(false);
            if (ms is double value && (best is null || value < best))
                best = value;
        }

        return best;
    }

    /// <summary>Ближайший сервер: все пингуются по разу, лучшие пять — ещё по три раза.</summary>
    public async Task<(SpeedServer Server, double PingMs)?> NearestAsync(IReadOnlyList<SpeedServer> servers, CancellationToken cancel)
    {
        var first = await Task.WhenAll(servers.Select(async s => (Server: s, Ms: await PingAsync(s, 1, cancel).ConfigureAwait(false))))
            .ConfigureAwait(false);
        var top = first.Where(x => x.Ms is not null).OrderBy(x => x.Ms).Take(5).Select(x => x.Server).ToList();
        if (top.Count == 0)
            return null;
        var second = await Task.WhenAll(top.Select(async s => (Server: s, Ms: await PingAsync(s, 3, cancel).ConfigureAwait(false))))
            .ConfigureAwait(false);
        var best = second.Where(x => x.Ms is not null).OrderBy(x => x.Ms).FirstOrDefault();
        return best.Ms is double ms ? (best.Server, ms) : null;
    }

    public async Task<SpeedResult> RunAsync(SpeedServer server, IProgress<SpeedProgress>? progress, CancellationToken cancel)
    {
        // Пинг и джиттер: 10 запросов.
        var pings = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            if (await OnePingAsync(server, cancel).ConfigureAwait(false) is double ms)
                pings.Add(ms);
            progress?.Report(new SpeedProgress("ping", pings.Count > 0 ? pings.Min() : 0, (i + 1) / 10.0));
        }

        if (pings.Count == 0)
            throw new HttpRequestException("Сервер не отвечает: " + server.Name);
        double ping = pings.Min();
        double jitter = pings.Count > 1 ? pings.Zip(pings.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;

        double download = await MeasureAsync(server, upload: false, streams: 6, progress, cancel).ConfigureAwait(false);
        double upload = await MeasureAsync(server, upload: true, streams: 3, progress, cancel).ConfigureAwait(false);
        AppLog.Info($"Скорость интернета ({server.Name}): пинг {ping:0} мс, джиттер {jitter:0.0} мс, скачивание {download:0.0} Мбит/с, отдача {upload:0.0} Мбит/с.");
        return new SpeedResult(server, ping, jitter, download, upload);
    }

    private async Task<double?> OnePingAsync(SpeedServer server, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var watch = Stopwatch.StartNew();
            using HttpResponseMessage response = await _http.GetAsync(Bust(server.Url(server.PingPath)), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            watch.Stop();
            return response.IsSuccessStatusCode ? watch.Elapsed.TotalMilliseconds : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            cancel.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>Скорость в Мбит/с: несколько потоков 10 секунд, без первых полутора.</summary>
    private async Task<double> MeasureAsync(SpeedServer server, bool upload, int streams, IProgress<SpeedProgress>? progress, CancellationToken cancel)
    {
        long total = 0;
        long counted = 0;
        var watch = Stopwatch.StartNew();
        bool graceOver = false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stop.CancelAfter(Phase);

        void Add(long bytes)
        {
            Interlocked.Add(ref total, bytes);
            if (graceOver)
                Interlocked.Add(ref counted, bytes);
        }

        var workers = Enumerable.Range(0, streams).Select(_ => upload ? UploadLoopAsync(server, Add, stop.Token) : DownloadLoopAsync(server, Add, stop.Token)).ToList();
        double graceSeconds = 0;
        string stage = upload ? "upload" : "download";
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, stop.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            if (!graceOver && watch.Elapsed >= Grace)
            {
                graceOver = true;
                graceSeconds = watch.Elapsed.TotalSeconds;
            }

            double seconds = graceOver ? watch.Elapsed.TotalSeconds - graceSeconds : watch.Elapsed.TotalSeconds;
            long bytes = graceOver ? Interlocked.Read(ref counted) : Interlocked.Read(ref total);
            progress?.Report(new SpeedProgress(stage, seconds > 0.2 ? bytes * 8 / seconds / 1e6 : 0, watch.Elapsed / Phase));
        }

        await Task.WhenAll(workers.Select(w => w.ContinueWith(_ => { }, TaskScheduler.Default))).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        double measured = Math.Max(0.5, watch.Elapsed.TotalSeconds - graceSeconds);
        return graceOver ? Interlocked.Read(ref counted) * 8 / measured / 1e6 : 0;
    }

    private async Task DownloadLoopAsync(SpeedServer server, Action<long> add, CancellationToken cancel)
    {
        var buffer = new byte[64 * 1024];
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                // garbage.php?ckSize=100 — 100 МБ случайных данных
                using HttpResponseMessage response = await _http.GetAsync(Bust(server.Url(server.DownloadPath + "?ckSize=100")),
                    HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                int read;
                while ((read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                    add(read);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                if (cancel.IsCancellationRequested)
                    return;
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task UploadLoopAsync(SpeedServer server, Action<long> add, CancellationToken cancel)
    {
        var payload = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var content = new CountingContent(payload, add);
                using HttpResponseMessage response = await _http.PostAsync(Bust(server.Url(server.UploadPath)), content, cancel).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                if (cancel.IsCancellationRequested)
                    return;
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static Uri Bust(Uri uri) => new(uri + (uri.Query.Length > 0 ? "&" : "?") + "r=" + Random.Shared.NextInt64());

    /// <summary>Тело запроса, которое считает отправленные байты по мере записи в сеть.</summary>
    private sealed class CountingContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly Action<long> _add;

        public CountingContent(byte[] data, Action<long> add)
        {
            _data = data;
            _add = add;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            const int chunk = 64 * 1024;
            for (int offset = 0; offset < _data.Length; offset += chunk)
            {
                int size = Math.Min(chunk, _data.Length - offset);
                await stream.WriteAsync(_data.AsMemory(offset, size), cancellationToken).ConfigureAwait(false);
                _add(size);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;
            return true;
        }
    }

    /// <summary>Встроенный список (если librespeed.org недоступен).</summary>
    public static IReadOnlyList<SpeedServer> Fallback { get; } = new[]
    {
        new SpeedServer(101, "Helsinki, Finland (Hetzner)", "https://www.librespeed.fi/", "backend/garbage.php", "backend/empty.php", "backend/empty.php"),
        new SpeedServer(74, "Poznan, Poland (INEA)", "https://speedtest.kamilszczepanski.com", "garbage.php", "empty.php", "empty.php"),
        new SpeedServer(79, "Prague, Czech Republic (CESNET)", "https://speedtest.cesnet.cz", "backend/garbage.php", "backend/empty.php", "backend/empty.php"),
        new SpeedServer(50, "Frankfurt, Germany (Clouvider)", "https://fra.speedtest.clouvider.net/backend", "garbage.php", "empty.php", "empty.php"),
        new SpeedServer(51, "Amsterdam, Netherlands (Clouvider)", "https://ams.speedtest.clouvider.net/backend", "garbage.php", "empty.php", "empty.php"),
        new SpeedServer(49, "London, England (Clouvider)", "https://lon.speedtest.clouvider.net/backend", "garbage.php", "empty.php", "empty.php"),
        new SpeedServer(52, "New York, United States (Clouvider)", "https://nyc.speedtest.clouvider.net/backend", "garbage.php", "empty.php", "empty.php"),
        new SpeedServer(54, "Los Angeles, United States (Clouvider)", "https://la.speedtest.clouvider.net/backend", "garbage.php", "empty.php", "empty.php"),
        new SpeedServer(82, "Tokyo, Japan (A573)", "https://librespeed.a573.net/", "backend/garbage.php", "backend/empty.php", "backend/empty.php"),
    };
}
