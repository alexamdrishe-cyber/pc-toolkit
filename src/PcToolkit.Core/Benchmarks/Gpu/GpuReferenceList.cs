namespace PcToolkit.Core.Benchmarks.Gpu;

/// <summary>Популярная видеокарта с результатом 3DMark Time Spy (Graphics Score).</summary>
public sealed record GpuReference(string Name, int TimeSpyGraphics)
{
    /// <summary>Примерные баллы PC Toolkit, пересчитанные из Time Spy относительно эталона.</summary>
    public int Estimate => GpuReferenceList.Estimate(TimeSpyGraphics);
}

/// <summary>
/// Рейтинг популярных видеокарт для сравнения. Это оценка, а не прогоны нашего теста:
/// Graphics Score из 3DMark Time Spy пересчитан в баллы PC Toolkit через эталон — Radeon RX 6700 XT.
/// Источник: ComputerBase, «Über 110 Grafikkarten im 3DMark Time Spy getestet» (одна лаборатория, одинаковые условия).
/// Оценка приблизительная: Time Spy — игровая сцена, а наш тест — чистые вычисления и скорость памяти.
/// </summary>
public static class GpuReferenceList
{
    public const string SourceText = "оценка по 3DMark Time Spy (ComputerBase)";

    private const double ReferenceTimeSpy = 12739; // RX 6700 XT

    public static IReadOnlyList<GpuReference> All { get; } = new GpuReference[]
    {
        new("NVIDIA GeForce RTX 5090", 47760),
        new("NVIDIA GeForce RTX 4090", 36676),
        new("NVIDIA GeForce RTX 5080", 31987),
        new("AMD Radeon RX 9070 XT", 29464),
        new("AMD Radeon RX 7900 XTX", 28910),
        new("NVIDIA GeForce RTX 4080 Super", 27920),
        new("NVIDIA GeForce RTX 5070 Ti", 27727),
        new("NVIDIA GeForce RTX 4080", 27724),
        new("AMD Radeon RX 7900 XT", 26046),
        new("AMD Radeon RX 9070", 26039),
        new("NVIDIA GeForce RTX 4070 Ti Super", 24404),
        new("NVIDIA GeForce RTX 4070 Ti", 22608),
        new("NVIDIA GeForce RTX 5070", 22267),
        new("NVIDIA GeForce RTX 3090 Ti", 21748),
        new("AMD Radeon RX 7900 GRE", 21302),
        new("AMD Radeon RX 6950 XT", 20969),
        new("NVIDIA GeForce RTX 4070 Super", 20671),
        new("NVIDIA GeForce RTX 3090", 20041),
        new("AMD Radeon RX 6900 XT", 19969),
        new("NVIDIA GeForce RTX 3080 Ti", 19698),
        new("AMD Radeon RX 7800 XT", 19079),
        new("AMD Radeon RX 6800 XT", 18732),
        new("NVIDIA GeForce RTX 3080", 17967),
        new("NVIDIA GeForce RTX 4070", 17557),
        new("AMD Radeon RX 7700 XT", 16917),
        new("AMD Radeon RX 9060 XT", 16422),
        new("AMD Radeon RX 6800", 15868),
        new("NVIDIA GeForce RTX 5060 Ti", 15599),
        new("Intel Arc B580", 14819),
        new("NVIDIA GeForce RTX 3070", 13854),
        new("NVIDIA GeForce RTX 2080 Ti", 13832),
        new("NVIDIA GeForce RTX 5060", 13620),
        new("AMD Radeon RX 6750 XT", 13450),
        new("NVIDIA GeForce RTX 4060 Ti", 13292),
        new("Intel Arc A770", 13129),
        new("AMD Radeon RX 6700 XT", 12739),
        new("Intel Arc A750", 12407),
        new("NVIDIA GeForce RTX 3060 Ti", 11916),
        new("AMD Radeon RX 7600 XT", 11206),
        new("NVIDIA GeForce RTX 2080", 11061),
        new("AMD Radeon RX 7600", 11007),
        new("NVIDIA GeForce RTX 4060", 10596),
        new("NVIDIA GeForce RTX 2070 Super", 10265),
        new("NVIDIA GeForce RTX 5050", 10197),
        new("AMD Radeon RX 6650 XT", 9802),
        new("NVIDIA GeForce GTX 1080 Ti", 9525),
        new("AMD Radeon RX 6600 XT", 9463),
        new("AMD Radeon RX 5700 XT", 9270),
        new("NVIDIA GeForce RTX 2070", 8874),
        new("NVIDIA GeForce RTX 2060 Super", 8744),
        new("NVIDIA GeForce RTX 3060", 8637),
        new("AMD Radeon RX 6600", 8263),
        new("NVIDIA GeForce RTX 2060", 8096),
        new("NVIDIA GeForce GTX 1080", 7223),
        new("NVIDIA GeForce GTX 1660 Ti", 6262),
        new("NVIDIA GeForce RTX 3050", 6193),
        new("NVIDIA GeForce GTX 1660 Super", 6133),
        new("NVIDIA GeForce GTX 1070", 5814),
        new("NVIDIA GeForce GTX 1650 Super", 4621),
        new("AMD Radeon RX 580", 4416),
        new("NVIDIA GeForce GTX 1060", 4150),
        new("AMD Radeon RX 570", 3882),
        new("NVIDIA GeForce GTX 1650", 3306),
        new("NVIDIA GeForce GTX 1050", 1851),
    };

    public static int Estimate(int timeSpyGraphics) => (int)Math.Round(10000 * timeSpyGraphics / ReferenceTimeSpy);

    /// <summary>Ищет модель по имени из системы («NVIDIA GeForce RTX 4060 Ti», «AMD Radeon RX 6700 XT»). null — нет в списке.</summary>
    public static GpuReference? Find(string? systemName)
    {
        if (string.IsNullOrWhiteSpace(systemName))
            return null;

        string name = Normalize(systemName);
        GpuReference? best = null;
        foreach (GpuReference reference in All)
        {
            string model = ModelOf(reference.Name);
            if (ContainsToken(name, model) && (best is null || model.Length > ModelOf(best.Name).Length))
                best = reference;
        }

        return best;
    }

    /// <summary>«rtx 4060 ti», «rx 6700 xt», «arc b580».</summary>
    private static string ModelOf(string name)
    {
        string n = Normalize(name);
        foreach (string vendor in new[] { "nvidia ", "amd ", "intel ", "geforce ", "radeon " })
            n = n.Replace(vendor, "");
        return n.Trim();
    }

    private static string Normalize(string name) =>
        string.Join(' ', name.ToLowerInvariant().Replace("(r)", "").Replace("(tm)", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Модель встречается отдельными словами: «rtx 4060» не должно найтись в «rtx 40600».</summary>
    private static bool ContainsToken(string text, string token)
    {
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            int end = index + token.Length;
            bool endOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (startOk && endOk)
                return true;
            index = end;
        }

        return false;
    }
}
