namespace PcToolkit.Core.Benchmarks;

/// <summary>Популярный процессор с публичным результатом Cinebench R23.</summary>
public sealed record CpuReference(string Name, int Threads, int CinebenchSingle, int CinebenchMulti)
{
    /// <summary>Примерные баллы PC Toolkit, пересчитанные из Cinebench R23 относительно эталона.</summary>
    public CpuScoreResult Estimate => CpuReferenceList.Estimate(CinebenchSingle, CinebenchMulti);
}

/// <summary>
/// Рейтинг популярных настольных процессоров, чтобы было с чем сравнить свой результат без второго ПК.
/// Это не прогоны нашего теста, а оценка: результат Cinebench R23 пересчитан в баллы PC Toolkit
/// через эталон — Ryzen 5 5600, для которого известны и Cinebench, и наш замер.
/// Источник: обзоры HWCooling.net (одна лаборатория, одинаковые условия), 2023–2024;
/// i5-12600K и i7-12700K — CGDirector, приведены к шкале HWCooling по их же результату Ryzen 5 5600.
/// Оценка приблизительная: Cinebench — это рендеринг, а наш тест — целые числа, AVX2 и кэш.
/// </summary>
public static class CpuReferenceList
{
    public const string SourceText = "оценка по Cinebench R23 (HWCooling.net)";

    // Эталон: Ryzen 5 5600 в Cinebench R23 у HWCooling и в нашем замере (баллы версии 2, bench-cpu.cmd).
    private const double ReferenceCinebenchSingle = 1462;
    private const double ReferenceCinebenchMulti = 11052;
    private const double ReferenceScoreSingle = 1000;
    private const double ReferenceScoreMulti = 5142;

    public static IReadOnlyList<CpuReference> All { get; } = new CpuReference[]
    {
        new("Intel Core Ultra 9 285K", 24, 2430, 42400),
        new("AMD Ryzen 9 9950X", 32, 2277, 41667),
        new("Intel Core i9-14900K", 32, 2359, 38853),
        new("Intel Core i9-13900K", 32, 2282, 38052),
        new("AMD Ryzen 9 7950X", 32, 2024, 36827),
        new("Intel Core Ultra 7 265K", 20, 2344, 35917),
        new("Intel Core i7-14700K", 28, 2204, 33982),
        new("AMD Ryzen 9 9900X", 24, 2233, 32787),
        new("Intel Core i7-13700K", 24, 2125, 30651),
        new("AMD Ryzen 9 7900X", 24, 2034, 28805),
        new("Intel Core i9-12900K", 24, 2014, 26725),
        new("AMD Ryzen 9 5950X", 32, 1627, 25479),
        new("Intel Core Ultra 5 245K", 14, 2201, 25008),
        new("Intel Core i5-14600K", 20, 2085, 24371),
        new("Intel Core i5-13600K", 20, 2005, 24232),
        new("AMD Ryzen 7 9800X3D", 16, 2099, 23269),
        new("Intel Core i7-12700K", 20, 1926, 22713),
        new("AMD Ryzen 9 5900X", 24, 1626, 21255),
        new("AMD Ryzen 7 9700X", 16, 2201, 20686),
        new("AMD Ryzen 7 7700X", 16, 1974, 19731),
        new("AMD Ryzen 7 7800X3D", 16, 1806, 17492),
        new("Intel Core i5-12600K", 16, 1905, 17077),
        new("AMD Ryzen 5 9600X", 12, 2171, 16922),
        new("Intel Core i9-10900K", 20, 1346, 16454),
        new("Intel Core i5-13400F", 16, 1772, 16038),
        new("Intel Core i5-14400F", 16, 1794, 15858),
        new("AMD Ryzen 7 5800X", 16, 1584, 15427),
        new("Intel Core i9-11900K", 16, 1627, 15160),
        new("AMD Ryzen 5 7600X", 12, 1959, 15105),
        new("Intel Core i7-11700K", 16, 1553, 14795),
        new("AMD Ryzen 5 7600", 12, 1833, 14369),
        new("AMD Ryzen 7 5800X3D", 16, 1472, 14259),
        new("AMD Ryzen 7 5700X", 16, 1524, 13791),
        new("AMD Ryzen 7 3700X", 16, 1291, 12458),
        new("Intel Core i5-12400F", 12, 1676, 12374),
        new("AMD Ryzen 5 5600X", 12, 1517, 11056),
        new("AMD Ryzen 5 5600", 12, 1462, 11052),
        new("Intel Core i5-11400F", 12, 1317, 9566),
        new("AMD Ryzen 5 3600", 12, 1237, 9453),
        new("Intel Core i3-14100F", 8, 1773, 8946),
        new("Intel Core i5-10400F", 12, 1114, 8219),
        new("Intel Core i3-12100F", 8, 1636, 8196),
        new("Intel Core i3-13100F", 8, 1653, 8090),
        new("Intel Core i3-10105F", 8, 1146, 5615),
        new("Intel Pentium Gold G7400", 4, 1423, 3773),
    };

    public static CpuScoreResult Estimate(int cinebenchSingle, int cinebenchMulti) => new(
        (int)Math.Round(ReferenceScoreSingle * cinebenchSingle / ReferenceCinebenchSingle),
        (int)Math.Round(ReferenceScoreMulti * cinebenchMulti / ReferenceCinebenchMulti));

    /// <summary>
    /// Ищет в списке модель процессора по имени из системы
    /// («AMD Ryzen 5 5600 6-Core Processor», «12th Gen Intel(R) Core(TM) i5-12400F»). null — не нашли.
    /// </summary>
    public static CpuReference? Find(string? systemName)
    {
        if (string.IsNullOrWhiteSpace(systemName))
            return null;

        string name = Normalize(systemName);
        CpuReference? best = null;
        foreach (CpuReference reference in All)
        {
            string model = ModelOf(reference.Name);
            if (ContainsToken(name, model) && (best is null || model.Length > ModelOf(best.Name).Length))
                best = reference;
        }

        return best;
    }

    /// <summary>Модель без производителя и серии: «5600X», «i5-12400F», «ultra 9 285k».</summary>
    private static string ModelOf(string name)
    {
        string n = Normalize(name).Replace("intel ", "").Replace("amd ", "").Replace("core ", "")
            .Replace("pentium gold ", "").Replace("ryzen 9 ", "").Replace("ryzen 7 ", "").Replace("ryzen 5 ", "")
            .Replace("ryzen 3 ", "");
        return n.Trim();
    }

    private static string Normalize(string name) =>
        name.ToLowerInvariant().Replace("(r)", "").Replace("(tm)", "").Replace("  ", " ").Trim();

    /// <summary>Модель встречается отдельным словом: «5600» не должно найтись в «5600x».</summary>
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
