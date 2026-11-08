using System.Net;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Network;

namespace PcToolkit.Core.Tests;

public sealed class NetworkCheckTests
{
    private static PingStats Stats(string name, params double?[] times) => PingStats.Compute(name, "1.1.1.1", times);

    private static PingStats Steady(double ms, int count = 60) => Stats("Cloudflare", Enumerable.Repeat<double?>(ms, count).ToArray());

    [Fact]
    public void ComputeCountsAverageJitterAndLoss()
    {
        PingStats s = Stats("x", 10, 14, null, 12, 10);

        Assert.Equal(5, s.Sent);
        Assert.Equal(4, s.Received);
        Assert.Equal(20, s.LossPercent, 3);
        Assert.Equal(11.5, s.AvgMs!.Value, 3);
        Assert.Equal(10, s.MinMs);
        Assert.Equal(14, s.MaxMs);
        // Разницы между соседними ответами (потерянный пропускается): 4, 2, 2 → 2,67.
        Assert.Equal(8.0 / 3, s.JitterMs!.Value, 3);
    }

    [Fact]
    public void ComputeWithoutRepliesHasNoNumbers()
    {
        PingStats s = Stats("x", null, null, null);

        Assert.Equal(0, s.Received);
        Assert.Equal(100, s.LossPercent);
        Assert.Null(s.AvgMs);
        Assert.Null(s.JitterMs);
    }

    [Fact]
    public void GoodInternetPingIsGood()
    {
        NetworkItem item = NetworkCheck.Explain(Steady(18), local: false, wireless: false);

        Assert.Equal(HealthLevel.Good, item.Level);
        Assert.Equal("18 мс, джиттер 0 мс, потери 0 %", item.Value);
        Assert.Null(item.Advice);
        Assert.StartsWith("До интернета: Cloudflare", item.Title);
    }

    [Fact]
    public void LossAndJitterAreExplained()
    {
        double?[] lossy = Enumerable.Repeat<double?>(20, 50).Concat(Enumerable.Repeat<double?>(null, 2)).ToArray();
        NetworkItem loss = NetworkCheck.Explain(Stats("Google", lossy), local: false, wireless: false);
        Assert.Equal(HealthLevel.Warning, loss.Level);
        Assert.Contains("Потери пакетов", loss.Advice);

        double?[] jumpy = Enumerable.Range(0, 60).Select(i => (double?)(i % 2 == 0 ? 15 : 27)).ToArray();
        NetworkItem jitter = NetworkCheck.Explain(Stats("Google", jumpy), local: false, wireless: false);
        Assert.Equal(HealthLevel.Notice, jitter.Level);
        Assert.Contains("скачет", jitter.Advice);
    }

    [Fact]
    public void RouterNormsDependOnCableOrWifi()
    {
        PingStats fourMs = Steady(4);

        Assert.Equal(HealthLevel.Notice, NetworkCheck.Explain(fourMs, local: true, wireless: false).Level);
        Assert.Equal(HealthLevel.Good, NetworkCheck.Explain(fourMs, local: true, wireless: true).Level);
        Assert.Equal("<1 мс, джиттер 0 мс, потери 0 %", NetworkCheck.Explain(Steady(0), local: true, wireless: false).Value);
    }

    [Fact]
    public void SilentRouterIsNotAnAlarm()
    {
        PingStats router = Stats("Роутер", null, null, null, null, null, null, null, null);

        NetworkItem item = NetworkCheck.Explain(router, local: true, wireless: false);
        Assert.Equal(HealthLevel.Notice, item.Level);
        Assert.Equal("не отвечает на пинг", item.Value);

        (HealthLevel level, string summary) = NetworkCheck.Summarize(router, new[] { Steady(20), Steady(25) }, wireless: false);
        Assert.Equal(HealthLevel.Good, level);
        Assert.StartsWith("Сеть подходит для онлайн-игр", summary);
    }

    [Fact]
    public void SummaryPointsAtHomeNetworkOrProvider()
    {
        double?[] shakyRouter = Enumerable.Range(0, 60).Select(i => (double?)(i % 2 == 0 ? 2 : 30)).ToArray();
        (HealthLevel home, string homeText) = NetworkCheck.Summarize(Stats("Роутер", shakyRouter), new[] { Steady(20) }, wireless: true);
        Assert.Equal(HealthLevel.Warning, home);
        Assert.Contains("домашней сети", homeText);

        double?[] lossy = Enumerable.Repeat<double?>(20, 55).Concat(Enumerable.Repeat<double?>(null, 5)).ToArray();
        (HealthLevel provider, string providerText) = NetworkCheck.Summarize(Steady(1), new[] { Stats("Google", lossy) }, wireless: false);
        Assert.Equal(HealthLevel.Warning, provider);
        Assert.Contains("провайдере", providerText);

        PingStats down = Stats("Google", null, null, null, null, null, null, null, null);
        (HealthLevel offline, string offlineText) = NetworkCheck.Summarize(Steady(1), new[] { down, down }, wireless: false);
        Assert.Equal(HealthLevel.Bad, offline);
        Assert.StartsWith("Роутер отвечает, а интернет — нет", offlineText);
    }

    [Fact]
    public void DnsQueryIsWellFormed()
    {
        byte[] query = NetworkCheck.BuildQuery(0x1234, "vk.com");

        Assert.Equal(new byte[] { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }, query[..12]);
        Assert.Equal(new byte[] { 2, (byte)'v', (byte)'k', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1 }, query[12..]);

        byte[] answer = (byte[])query.Clone();
        answer[2] |= 0x80;
        Assert.True(NetworkCheck.IsAnswerTo(answer, 0x1234));
        Assert.False(NetworkCheck.IsAnswerTo(query, 0x1234));  // это сам вопрос, а не ответ
        Assert.False(NetworkCheck.IsAnswerTo(answer, 0x1235)); // чужой ответ
        Assert.False(NetworkCheck.IsAnswerTo(new byte[5], 0));
    }

    [Fact]
    public void MedianAndPrivateAddresses()
    {
        Assert.Null(NetworkCheck.Median(Array.Empty<double>()));
        Assert.Equal(5, NetworkCheck.Median(new double[] { 9, 1, 5 }));
        Assert.Equal(4, NetworkCheck.Median(new double[] { 9, 1, 3, 5 }));

        Assert.True(NetworkCheck.IsPrivate(IPAddress.Parse("192.168.1.1")));
        Assert.True(NetworkCheck.IsPrivate(IPAddress.Parse("10.0.0.1")));
        Assert.True(NetworkCheck.IsPrivate(IPAddress.Parse("172.20.0.1")));
        Assert.False(NetworkCheck.IsPrivate(IPAddress.Parse("172.32.0.1")));
        Assert.False(NetworkCheck.IsPrivate(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void DnsAdviceComparesWithCurrent()
    {
        var slowRouter = new List<DnsResult>
        {
            new("Cloudflare", "1.1.1.1", 9, 12, 12, false),
            new("Ваш роутер", "192.168.1.1", 45, 12, 12, true),
        };
        Assert.StartsWith("Cloudflare отвечает быстрее вашего DNS: 9 мс против 45 мс", NetworkCheck.DnsAdvice(slowRouter));

        var fine = new List<DnsResult>
        {
            new("Cloudflare", "1.1.1.1", 9, 12, 12, false),
            new("Ваш роутер", "192.168.1.1", 14, 12, 12, true),
        };
        Assert.StartsWith("Ваш DNS отвечает быстро (14 мс)", NetworkCheck.DnsAdvice(fine));

        var silent = new List<DnsResult>
        {
            new("Google", "8.8.8.8", 20, 12, 12, false),
            new("Ваш роутер", "192.168.1.1", null, 0, 12, true),
        };
        Assert.StartsWith("Ваш DNS не ответил на прямые запросы", NetworkCheck.DnsAdvice(silent));
        Assert.StartsWith("Ни один DNS-сервер не ответил", NetworkCheck.DnsAdvice(new[] { new DnsResult("Google", "8.8.8.8", null, 0, 12, false) }));
    }

    [Fact]
    public void WifiBandFromChannel()
    {
        Assert.Equal("2,4 ГГц", new WifiInfo("home", 80, "", 6, 144, 144).Band);
        Assert.Equal("5 ГГц", new WifiInfo("home", 80, "", 36, 866, 866).Band);
        Assert.Equal("", new WifiInfo("home", 80, "", 0, 0, 0).Band);
    }
}
