using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Network;

/// <summary>Итог серии пингов до одного адреса.</summary>
public sealed record PingStats(string Name, string Address, int Sent, int Received, double? AvgMs, double? MinMs, double? MaxMs, double? JitterMs)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;

    /// <summary>Считает среднее, разброс и джиттер (средняя разница между соседними ответами) по временам ответа; null — нет ответа.</summary>
    public static PingStats Compute(string name, string address, IReadOnlyList<double?> times)
    {
        List<double> ok = times.Where(t => t is not null).Select(t => t!.Value).ToList();
        double? jitter = null;
        if (ok.Count >= 2)
        {
            double sum = 0;
            for (int i = 1; i < ok.Count; i++)
                sum += Math.Abs(ok[i] - ok[i - 1]);
            jitter = sum / (ok.Count - 1);
        }

        return new PingStats(name, address, times.Count, ok.Count,
            ok.Count > 0 ? ok.Average() : null, ok.Count > 0 ? ok.Min() : null, ok.Count > 0 ? ok.Max() : null, jitter);
    }
}

/// <summary>Активное подключение: кабель или Wi-Fi, скорость, сигнал, шлюз и DNS.</summary>
public sealed record ConnectionInfo
{
    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public bool IsWireless { get; init; }

    /// <summary>Скорость подключения, бит/с (у Wi-Fi — текущая скорость канала).</summary>
    public long SpeedBitsPerSecond { get; init; }

    public IPAddress? Gateway { get; init; }

    public IReadOnlyList<IPAddress> DnsServers { get; init; } = Array.Empty<IPAddress>();

    public WifiInfo? Wifi { get; init; }
}

/// <summary>Сведения о Wi-Fi: сеть, сигнал, стандарт, диапазон.</summary>
public sealed record WifiInfo(string Ssid, int SignalPercent, string Standard, int Channel, int RxMbps, int TxMbps)
{
    public string Band => Channel switch
    {
        <= 0 => "",
        <= 14 => "2,4 ГГц",
        _ => "5 ГГц",
    };
}

/// <summary>Строка проверки сети с объяснением.</summary>
public sealed record NetworkItem(string Title, string Value, HealthLevel Level, string What, string? Advice = null);

/// <summary>Сравнение DNS-серверов: медиана времени ответа.</summary>
public sealed record DnsResult(string Name, string Address, double? MedianMs, int Answered, int Asked, bool IsCurrent);

/// <summary>
/// Проверка сети для игр: пинг до роутера и до интернета (среднее, джиттер, потери), Wi-Fi и скорость ответа DNS.
/// Только измеряет — ничего не меняет.
/// </summary>
public static class NetworkCheck
{
    public static readonly (string Name, IPAddress Address)[] InternetTargets =
    {
        ("Cloudflare", IPAddress.Parse("1.1.1.1")),
        ("Google", IPAddress.Parse("8.8.8.8")),
    };

    public static readonly (string Name, IPAddress Address)[] PublicDns =
    {
        ("Cloudflare", IPAddress.Parse("1.1.1.1")),
        ("Google", IPAddress.Parse("8.8.8.8")),
        ("Quad9", IPAddress.Parse("9.9.9.9")),
        ("Яндекс", IPAddress.Parse("77.88.8.8")),
    };

    private static readonly string[] DnsTestNames =
    {
        "steampowered.com", "steamcommunity.com", "epicgames.com", "riotgames.com", "discord.com",
        "twitch.tv", "youtube.com", "google.com", "yandex.ru", "vk.com", "wikipedia.org", "github.com",
    };

    /// <summary>
    /// Подключение, через которое идёт интернет. Берётся то, через которое Windows отправила бы пакет в интернет
    /// (адаптеры VPN и виртуальных машин тоже бывают «включены» и со шлюзом); если так не вышло — первое работающее со шлюзом.
    /// </summary>
    public static ConnectionInfo? ReadConnection()
    {
        try
        {
            List<NetworkInterface> candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                          && !g.Address.Equals(IPAddress.Any)))
                .ToList();
            IPAddress? local = LocalAddressFor(InternetTargets[0].Address);
            NetworkInterface? active = candidates.FirstOrDefault(n => local is not null
                                                                      && n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(local)))
                                       ?? candidates.OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet).FirstOrDefault();
            if (active is null)
                return null;

            IPInterfaceProperties ip = active.GetIPProperties();
            bool wireless = active.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            return new ConnectionInfo
            {
                Name = active.Name,
                Description = active.Description,
                IsWireless = wireless,
                SpeedBitsPerSecond = active.Speed,
                Gateway = ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork),
                DnsServers = ip.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList(),
                Wifi = wireless && OperatingSystem.IsWindows() ? WifiReader.Read() : null,
            };
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
        {
            AppLog.Warn("Не удалось прочитать сетевые подключения.", ex);
            return null;
        }
    }

    /// <summary>Локальный адрес, с которого Windows отправила бы пакет на <paramref name="remote"/> (сам пакет не отправляется).</summary>
    private static IPAddress? LocalAddressFor(IPAddress remote)
    {
        try
        {
            using var socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(remote, 53);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>Сколько первых запросов подряд без ответа считать «адрес не отвечает» и не ждать остальные.</summary>
    public const int SilentLimit = 8;

    /// <summary>
    /// Серия пингов: <paramref name="count"/> штук через <paramref name="interval"/>. <paramref name="progress"/> получает число
    /// сделанных запросов; если первые <see cref="SilentLimit"/> остались без ответа, серия заканчивается раньше
    /// (некоторые роутеры не отвечают на пинг — ждать их минуту незачем), а прогресс сразу получает <paramref name="count"/>.
    /// </summary>
    public static async Task<PingStats> PingAsync(string name, IPAddress address, int count, TimeSpan interval, CancellationToken cancel,
                                                  Action<int>? progress = null)
    {
        var times = new List<double?>(count);
        using var ping = new Ping();
        byte[] payload = new byte[32];
        for (int i = 0; i < count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var started = Stopwatch.StartNew();
            try
            {
                // Время ответа — как у команды ping (целые миллисекунды): своё измерение добавило бы задержки потоков.
                PingReply reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(1), payload, null, cancel);
                times.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
            }
            catch (PingException)
            {
                times.Add(null);
            }

            if (i + 1 == SilentLimit && times.All(t => t is null))
            {
                progress?.Invoke(count);
                break;
            }

            progress?.Invoke(i + 1);
            TimeSpan rest = interval - started.Elapsed;
            if (rest > TimeSpan.Zero)
                await Task.Delay(rest, cancel);
        }

        return PingStats.Compute(name, address.ToString(), times);
    }

    /// <summary>
    /// Оценка серии пингов простыми словами. <paramref name="local"/> — пинг до роутера: у него свои нормы (1–2 мс по кабелю).
    /// </summary>
    public static NetworkItem Explain(PingStats stats, bool local, bool wireless)
    {
        string where = local ? $"До роутера ({stats.Address})" : $"До интернета: {stats.Name} ({stats.Address})";
        if (stats.Received == 0)
        {
            return local
                ? new NetworkItem(where, "не отвечает на пинг", HealthLevel.Notice,
                    "Роутер не ответил ни на один запрос. Многие роутеры просто не отвечают на пинг — если интернет ниже в порядке, это не проблема.")
                : new NetworkItem(where, "нет ответа", HealthLevel.Bad,
                    "Адрес в интернете не ответил. Если сайты при этом открываются, пинг может блокировать провайдер или антивирус.",
                    "Проверьте, открываются ли сайты; если нет — перезагрузите роутер.");
        }

        double avg = stats.AvgMs!.Value;
        double jitter = stats.JitterMs ?? 0;
        double loss = stats.LossPercent;
        string value = $"{Ms(avg)}, джиттер {jitter:0.#} мс, потери {loss:0.#} %";

        HealthLevel level;
        string what;
        string? advice = null;
        if (local)
        {
            double good = wireless ? 5 : 2;
            double bad = wireless ? 20 : 10;
            level = loss >= 1 || avg >= bad || jitter >= 10 ? HealthLevel.Warning
                : avg >= good || jitter >= 3 || loss > 0 ? HealthLevel.Notice
                : HealthLevel.Good;
            what = wireless
                ? "Связь компьютера с роутером по Wi-Fi. Хорошо — до 5 мс без потерь; больше — помехи, слабый сигнал или занятый канал."
                : "Связь компьютера с роутером по кабелю. Хорошо — 1–2 мс без потерь.";
            if (level >= HealthLevel.Notice)
            {
                advice = wireless
                    ? "Для игр лучше подключиться кабелем. Если нельзя — переключитесь на сеть 5 ГГц, поставьте компьютер ближе к роутеру, " +
                      "уберите преграды между ними."
                    : "Проверьте кабель и разъёмы, попробуйте другой порт роутера.";
            }
        }
        else
        {
            level = loss >= 2 || jitter >= 20 || avg >= 120 ? HealthLevel.Warning
                : loss > 0 || jitter >= 8 || avg >= 60 ? HealthLevel.Notice
                : HealthLevel.Good;
            what = "Задержка до крупного сервера в интернете. Для шутеров хорошо до 30–40 мс, терпимо до 60–80 мс. " +
                   "Джиттер — насколько скачет задержка: больше 10 мс заметно как рывки. Потери вызывают «телепорты» и рассинхронизацию.";
            if (level >= HealthLevel.Notice)
            {
                advice = loss > 0
                    ? "Потери пакетов: проверьте, не качается ли что-то в фоне (торренты, обновления), перезагрузите роутер. Если не поможет — обратитесь к провайдеру."
                    : jitter >= 8
                        ? "Задержка скачет: закройте загрузки и стримы в фоне; по Wi-Fi джиттер обычно больше, чем по кабелю."
                        : "Задержка высокая: это расстояние до сервера и качество канала провайдера. В играх выбирайте ближайший регион серверов.";
            }
        }

        return new NetworkItem(where, value, level, what, advice);
    }

    /// <summary>Задержка для показа: время ответа Windows меряет целыми миллисекундами, 0 — это «меньше 1 мс».</summary>
    public static string Ms(double ms) => ms < 1 ? "<1 мс" : $"{ms:0} мс";

    /// <summary>Общий итог по результатам пингов.</summary>
    public static (HealthLevel Level, string Summary) Summarize(PingStats? router, IReadOnlyList<PingStats> internet, bool wireless)
    {
        bool routerSilent = router is { Received: 0 };
        if (internet.Count > 0 && internet.All(s => s.Received == 0))
        {
            return (HealthLevel.Bad, routerSilent
                ? "Нет связи ни с роутером, ни с интернетом. Проверьте кабель или Wi-Fi и перезагрузите роутер."
                : "Роутер отвечает, а интернет — нет. Если сайты тоже не открываются, перезагрузите роутер; если не поможет — обратитесь к провайдеру. " +
                  "Если сайты открываются, пинг может блокировать антивирус или провайдер.");
        }

        // Роутер, который молчит на пинг, при работающем интернете — обычное дело, а не проблема.
        bool routerBad = router is { Received: > 0 } && (router.LossPercent >= 1 || router.AvgMs >= (wireless ? 20 : 10) || router.JitterMs >= 10);
        bool internetBad = internet.Any(s => s.Received == 0 || s.LossPercent >= 2 || s.JitterMs >= 20 || s.AvgMs >= 120);
        bool internetMeh = internet.Any(s => s.LossPercent > 0 || s.JitterMs >= 8 || s.AvgMs >= 60);
        if (routerBad)
        {
            return (HealthLevel.Warning, wireless
                ? "Проблема в домашней сети: связь с роутером по Wi-Fi нестабильна. Для игр лучше кабель или сеть 5 ГГц."
                : "Проблема в домашней сети: связь с роутером по кабелю нестабильна. Проверьте кабель и порт роутера.");
        }

        if (internetBad)
            return (HealthLevel.Warning, "Домашняя сеть в порядке, но связь с интернетом нестабильна — похоже, дело в провайдере или в загрузках в фоне.");
        if (internetMeh)
            return (HealthLevel.Notice, "Сеть работает, но есть небольшие задержки или скачки — в онлайн-играх это может быть заметно.");
        return (HealthLevel.Good, "Сеть подходит для онлайн-игр: задержка низкая, без скачков и потерь.");
    }

    /// <summary>
    /// Сравнивает скорость ответа DNS: текущих серверов и публичных. Каждому серверу задаются одни и те же имена;
    /// результат — медиана времени ответа.
    /// </summary>
    public static async Task<List<DnsResult>> CompareDnsAsync(IReadOnlyList<IPAddress> current, CancellationToken cancel, Action<int, int>? progress = null)
    {
        var servers = new List<(string Name, IPAddress Address, bool Current)>();
        foreach (IPAddress address in current.Take(2))
            servers.Add((IsPrivate(address) ? "Ваш роутер" : "Ваш DNS", address, true));
        foreach ((string name, IPAddress address) in PublicDns)
        {
            if (!servers.Any(s => s.Address.Equals(address)))
                servers.Add((name, address, false));
        }

        var times = servers.ToDictionary(s => s.Address, _ => new List<double>());
        int total = DnsTestNames.Length * servers.Count;
        int done = 0;
        ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        // По очереди: имя — всем серверам, потом следующее имя. Так ни один сервер не получает преимущества от времени.
        foreach (string name in DnsTestNames)
        {
            foreach ((string _, IPAddress address, bool _) in servers)
            {
                cancel.ThrowIfCancellationRequested();
                double? ms = await QueryAsync(address, name, id++, TimeSpan.FromSeconds(2), cancel);
                if (ms is double value)
                    times[address].Add(value);
                progress?.Invoke(++done, total);
            }
        }

        return servers.Select(s => new DnsResult(s.Name, s.Address.ToString(), Median(times[s.Address]), times[s.Address].Count,
                                                 DnsTestNames.Length, s.Current))
                      .OrderBy(r => r.MedianMs ?? double.MaxValue)
                      .ToList();
    }

    /// <summary>Совет по итогам сравнения DNS.</summary>
    public static string DnsAdvice(IReadOnlyList<DnsResult> results)
    {
        DnsResult? best = results.FirstOrDefault(r => r.MedianMs is not null);
        DnsResult? current = results.Where(r => r.IsCurrent && r.MedianMs is not null).OrderBy(r => r.MedianMs).FirstOrDefault();
        if (best is null)
            return "Ни один DNS-сервер не ответил: проверьте подключение к интернету.";
        if (current is null)
            return $"Ваш DNS не ответил на прямые запросы (так бывает у некоторых роутеров). Быстрее всех ответил {best.Name} — {best.MedianMs:0} мс.";
        if (best.IsCurrent || current.MedianMs - best.MedianMs < 10)
            return $"Ваш DNS отвечает быстро ({current.MedianMs:0} мс) — менять ничего не нужно.";
        return $"{best.Name} отвечает быстрее вашего DNS: {best.MedianMs:0} мс против {current.MedianMs:0} мс. Это ускорит открытие сайтов и подключение " +
               "к серверам, но не пинг в самой игре. Сменить DNS можно в свойствах подключения Windows (IPv4 → «Использовать следующие адреса DNS-серверов»).";
    }

    /// <summary>Один запрос A-записи по UDP: время ответа в мс или null.</summary>
    public static async Task<double?> QueryAsync(IPAddress server, string name, ushort id, TimeSpan timeout, CancellationToken cancel)
    {
        byte[] query = BuildQuery(id, name);
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            udp.Connect(server, 53);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(timeout);
            var watch = Stopwatch.StartNew();
            await udp.SendAsync(query, limit.Token);
            while (true)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(limit.Token);
                if (IsAnswerTo(result.Buffer, id))
                    return watch.Elapsed.TotalMilliseconds;
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    public static byte[] BuildQuery(ushort id, string name)
    {
        var bytes = new List<byte>(32 + name.Length);
        byte[] header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 0x0100); // рекурсия желательна
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 1);      // один вопрос
        bytes.AddRange(header);
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] ascii = System.Text.Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)ascii.Length);
            bytes.AddRange(ascii);
        }

        bytes.Add(0);
        bytes.AddRange(new byte[] { 0, 1, 0, 1 }); // тип A, класс IN
        return bytes.ToArray();
    }

    public static bool IsAnswerTo(byte[] response, ushort id) =>
        response.Length >= 12
        && BinaryPrimitives.ReadUInt16BigEndian(response) == id
        && (response[2] & 0x80) != 0;

    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return null;

        double[] sorted = values.Order().ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public static bool IsPrivate(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
               && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
    }
}
