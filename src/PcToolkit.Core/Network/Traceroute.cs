using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Network;

/// <summary>Узел маршрута: адрес, имя, сколько проб, сколько потеряно, задержки.</summary>
public sealed class TraceHop
{
    private readonly List<double> _times = new();

    public TraceHop(int number) => Number = number;

    public int Number { get; }

    public IPAddress? Address { get; set; }

    public string HostName { get; set; } = "";

    public int Sent { get; private set; }

    public int Lost { get; private set; }

    public double? Last { get; private set; }

    public double LossPercent => Sent == 0 ? 0 : Lost * 100.0 / Sent;

    public double? Average => _times.Count == 0 ? null : _times.Average();

    public double? Best => _times.Count == 0 ? null : _times.Min();

    public double? Worst => _times.Count == 0 ? null : _times.Max();

    internal void Record(double? ms)
    {
        Sent++;
        Last = ms;
        if (ms is double value)
            _times.Add(value);
        else
            Lost++;
    }
}

/// <summary>Итог трассировки: где начинаются потери или скачки задержки.</summary>
public sealed record TraceVerdict(HealthLevel Level, string Title, string Details);

/// <summary>
/// Трассировка как WinMTR: сначала находим узлы маршрута (ICMP с растущим TTL), потом несколько кругов проб
/// до каждого узла — потери и задержка по каждому. Промежуточные роутеры часто экономят на ответах, поэтому
/// потери на одном узле при нуле дальше — норма; важны потери, которые тянутся до конца маршрута.
/// </summary>
public static class Traceroute
{
    public const int MaxHops = 30;
    private static readonly byte[] Payload = new byte[32];

    /// <summary>Найти узлы маршрута до цели. <paramref name="onHop"/> — по мере нахождения.</summary>
    public static async Task<List<TraceHop>> DiscoverAsync(IPAddress target, Action<TraceHop>? onHop, CancellationToken cancel)
    {
        var hops = new List<TraceHop>();
        int silentInRow = 0;
        using var ping = new Ping();
        for (int ttl = 1; ttl <= MaxHops; ttl++)
        {
            cancel.ThrowIfCancellationRequested();
            var hop = new TraceHop(ttl);
            for (int attempt = 0; attempt < 2 && hop.Address is null; attempt++)
            {
                (IPStatus status, IPAddress? from, double? _) = await ProbeAsync(ping, target, ttl).ConfigureAwait(false);
                if (status is IPStatus.TtlExpired or IPStatus.Success)
                    hop.Address = from;
                if (status == IPStatus.Success)
                    break;
            }

            hops.Add(hop);
            onHop?.Invoke(hop);
            if (hop.Address is not null && hop.Address.Equals(target))
                break;
            silentInRow = hop.Address is null ? silentInRow + 1 : 0;
            if (silentInRow >= 5)
                break;
        }

        // Хвост из молчащих узлов (после последнего ответившего) не нужен.
        while (hops.Count > 1 && hops[^1].Address is null)
            hops.RemoveAt(hops.Count - 1);
        return hops;
    }

    /// <summary>Один круг проб: каждому узлу — проба с его TTL (параллельно).</summary>
    public static async Task RoundAsync(IPAddress target, IReadOnlyList<TraceHop> hops, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        await Task.WhenAll(hops.Select(async hop =>
        {
            using var ping = new Ping();
            (IPStatus status, IPAddress? from, double? ms) = await ProbeAsync(ping, target, hop.Number).ConfigureAwait(false);
            bool answered = status is IPStatus.TtlExpired or IPStatus.Success;
            if (answered && hop.Address is null)
                hop.Address = from;
            hop.Record(answered ? ms : null);
        })).ConfigureAwait(false);
    }

    /// <summary>Имена узлов (обратный DNS) — по возможности, без ожидания дольше пары секунд.</summary>
    public static async Task ResolveNamesAsync(IEnumerable<TraceHop> hops)
    {
        await Task.WhenAll(hops.Where(h => h.Address is not null).Select(async hop =>
        {
            try
            {
                Task<IPHostEntry> lookup = Dns.GetHostEntryAsync(hop.Address!);
                if (await Task.WhenAny(lookup, Task.Delay(2000)).ConfigureAwait(false) == lookup && lookup.Result.HostName != hop.Address!.ToString())
                    hop.HostName = lookup.Result.HostName;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
            {
                // имени нет — покажем адрес
            }
        })).ConfigureAwait(false);
    }

    /// <summary>Где начинаются потери, которые доходят до конца маршрута, или резкий скачок задержки.</summary>
    public static TraceVerdict Judge(IReadOnlyList<TraceHop> hops)
    {
        List<TraceHop> answered = hops.Where(h => h.Address is not null && h.Sent > 0).ToList();
        if (answered.Count == 0)
            return new TraceVerdict(HealthLevel.Bad, "Маршрут не построился", "Ни один узел не ответил: нет интернета или ICMP закрыт брандмауэром.");

        TraceHop last = answered[^1];
        if (last.LossPercent < 2)
        {
            TraceHop? jump = null;
            for (int i = 1; i < answered.Count; i++)
            {
                if (answered[i].Best is double now && answered[i - 1].Best is double before && now - before > 60 && answered.Skip(i).All(h => h.Best is null || h.Best > before + 50))
                {
                    jump = answered[i];
                    break;
                }
            }

            return jump is null
                ? new TraceVerdict(HealthLevel.Good, "Маршрут чистый", $"До цели {last.Number} узлов, потерь нет, лучшая задержка {last.Best:0} мс.")
                : new TraceVerdict(HealthLevel.Notice, $"Задержка вырастает на узле {jump.Number}",
                    $"С узла {jump.Number} ({Describe(jump)}) задержка выше на {jump.Best - answered[answered.IndexOf(jump) - 1].Best:0} мс — " +
                    "обычно это межгород или переход в другую страну, а не поломка.");
        }

        // Первый узел, с которого потери держатся до конца
        TraceHop start = last;
        for (int i = answered.Count - 1; i >= 0; i--)
        {
            if (answered[i].LossPercent >= Math.Max(2, last.LossPercent / 2))
                start = answered[i];
            else
                break;
        }

        string where = start.Number == 1
            ? "в домашней сети: на роутере или Wi-Fi"
            : start.Number <= 3 ? "у провайдера (первые узлы после роутера)" : "дальше по пути — у магистральных операторов или у сервера";
        return new TraceVerdict(HealthLevel.Bad, $"Потери {last.LossPercent:0} % — начинаются на узле {start.Number}",
            $"Потери начинаются {where} и тянутся до цели. Узел {start.Number}: {Describe(start)}.");
    }

    private static string Describe(TraceHop hop) => hop.HostName.Length > 0 ? $"{hop.HostName} ({hop.Address})" : hop.Address?.ToString() ?? "*";

    private static async Task<(IPStatus Status, IPAddress? From, double? Ms)> ProbeAsync(Ping ping, IPAddress target, int ttl)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            PingReply reply = await ping.SendPingAsync(target, 1000, Payload, new PingOptions(ttl, dontFragment: true)).ConfigureAwait(false);
            watch.Stop();
            // У ответов «TTL истёк» .NET не заполняет время — меряем сами.
            double ms = reply.Status == IPStatus.Success && reply.RoundtripTime > 0 ? reply.RoundtripTime : watch.Elapsed.TotalMilliseconds;
            return (reply.Status, reply.Address, ms);
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException)
        {
            AppLog.Warn("Трассировка: проба не отправилась.", ex);
            return (IPStatus.Unknown, null, null);
        }
    }
}
