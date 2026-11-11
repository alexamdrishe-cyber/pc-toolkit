using System.Text.RegularExpressions;

// Этот файл общий для программы и сервера рейтинга (server/PcToolkit.Rating подключает его ссылкой):
// здесь нет ничего, что работает только в Windows.
namespace PcToolkit.Core.Rating;

/// <summary>Что сравнивается в рейтинге.</summary>
public static class RatingKinds
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Memory = "memory";
    public const string Disk = "disk";

    public static IReadOnlyList<string> All { get; } = new[] { Cpu, Gpu, Memory, Disk };

    public static bool IsKnown(string kind) => All.Contains(kind);

    /// <summary>Верхняя граница правдоподобного результата: всё выше — ошибка или подделка.</summary>
    public static double MaxScore(string kind) => kind switch
    {
        Cpu => 500_000,
        Gpu => 500_000,
        Memory => 2_000, // ГБ/с
        Disk => 50_000, // МБ/с
        _ => 0,
    };
}

/// <summary>
/// Результат теста, который программа отправляет в общий рейтинг — только с согласия и без личных данных.
/// Score — главный показатель (процессор: все потоки, видеокарта: баллы, память: чтение ГБ/с, диск: SEQ1M Q8T1 чтение МБ/с),
/// Score2 — второй (процессор: один поток, память: задержка нс, диск: RND4K Q1T1 чтение МБ/с).
/// </summary>
public sealed class RatingSubmission
{
    /// <summary>Случайный номер отправки: повторная отправка того же результата не задвоится.</summary>
    public string ClientId { get; set; } = "";

    public string Kind { get; set; } = "";

    public string Model { get; set; } = "";

    public double Score { get; set; }

    public double? Score2 { get; set; }

    /// <summary>Версия теста: баллы разных версий не сравниваются.</summary>
    public int TestVersion { get; set; }

    /// <summary>Анонимный номер компьютера (хеш), чтобы один ПК не перевешивал остальных.</summary>
    public string Machine { get; set; } = "";

    public string AppVersion { get; set; } = "";

    /// <summary>Условия замера простыми словами: память, схема питания, драйвер.</summary>
    public Dictionary<string, string>? Conditions { get; set; }

    /// <summary>Проверка до записи на сервере. null — всё правильно, иначе причина отказа.</summary>
    public string? Validate()
    {
        if (!RatingKinds.IsKnown(Kind))
            return "неизвестный вид теста";
        if (ClientId.Length is < 8 or > 64)
            return "нет номера отправки";
        if (Machine.Length is < 8 or > 64 || !Machine.All(Uri.IsHexDigit))
            return "нет номера компьютера";
        if (RatingStats.ModelKey(Model).Length is < 2 or > 120)
            return "нет модели устройства";
        if (!(Score > 0) || Score > RatingKinds.MaxScore(Kind) || (Score2 is double s2 && (!(s2 >= 0) || s2 > RatingKinds.MaxScore(Kind))))
            return "результат вне разумных пределов";
        if (TestVersion is < 1 or > 1000)
            return "неизвестная версия теста";
        if (Conditions is { Count: > 12 } || Conditions?.Any(c => c.Key.Length > 40 || c.Value.Length > 120) == true)
            return "слишком много сведений";
        return null;
    }
}

/// <summary>Сводка по одной модели: сколько замеров, медиана и типичный диапазон.</summary>
public sealed class RatingModel
{
    public string Model { get; set; } = "";

    /// <summary>Замеров всего (после отсева выбросов).</summary>
    public int Results { get; set; }

    /// <summary>Разных компьютеров.</summary>
    public int Machines { get; set; }

    public double Median { get; set; }

    /// <summary>Типичный диапазон: от 25-го до 75-го процентиля.</summary>
    public double Low { get; set; }

    public double High { get; set; }

    /// <summary>Что выжимают хорошо охлаждённые экземпляры: 90-й процентиль (рекорды-одиночки не в счёт).</summary>
    public double Top { get; set; }

    public double? Median2 { get; set; }

    /// <summary>Проверено: достаточно замеров с разных компьютеров.</summary>
    public bool Verified { get; set; }
}

/// <summary>Таблица рейтинга одного вида (её отдаёт сервер).</summary>
public sealed class RatingTable
{
    public string Kind { get; set; } = "";

    public int TestVersion { get; set; }

    public DateTimeOffset Updated { get; set; }

    public List<RatingModel> Models { get; set; } = new();
}

/// <summary>Сравнение своего результата с такими же устройствами.</summary>
public enum RatingVerdict
{
    /// <summary>Мало данных по этой модели.</summary>
    Unknown,
    Normal,
    Above,
    Below,
}

/// <summary>
/// Статистика рейтинга. Каждый компьютер сначала сводится к своей медиане (сто прогонов одного ПК весят как один),
/// потом по компьютерам — медиана, 25/75/90-й процентили. Выбросы (за полторы ширины «коробки» от квартилей)
/// отбрасываются, когда компьютеров хотя бы четыре: случайные и поддельные результаты на итог не влияют.
/// </summary>
public static partial class RatingStats
{
    public const int MinResults = 5;
    public const int MinMachines = 3;

    /// <summary>
    /// Имя модели для группировки: без «(R)», «(TM)», «CPU @ 3.50GHz», «6-Core Processor», лишних пробелов.
    /// «AMD Ryzen 5 5600 6-Core Processor» и «AMD Ryzen 5 5600» — одна модель.
    /// </summary>
    public static string ModelKey(string model)
    {
        string text = (model ?? "").Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("®", "").Replace("™", "");
        text = CpuSuffix().Replace(text, "");
        text = Spaces().Replace(text, " ").Trim();
        return text.Length > 120 ? text[..120] : text;
    }

    public static RatingModel? Summarize(string model, IEnumerable<(double Score, double? Score2, string Machine)> results)
    {
        var perMachine = results
            .Where(r => r.Score > 0)
            .GroupBy(r => r.Machine)
            .Select(g => (Score: Median(g.Select(r => r.Score).ToList()),
                          Score2: g.Any(r => r.Score2 is > 0) ? Median(g.Where(r => r.Score2 is > 0).Select(r => r.Score2!.Value).ToList()) : (double?)null,
                          Count: g.Count()))
            .ToList();
        if (perMachine.Count == 0)
            return null;

        if (perMachine.Count >= 4)
        {
            List<double> sorted = perMachine.Select(m => m.Score).OrderBy(s => s).ToList();
            double q1 = Percentile(sorted, 25), q3 = Percentile(sorted, 75), fence = (q3 - q1) * 1.5;
            perMachine = perMachine.Where(m => m.Score >= q1 - fence && m.Score <= q3 + fence).ToList();
        }

        List<double> scores = perMachine.Select(m => m.Score).OrderBy(s => s).ToList();
        List<double> second = perMachine.Where(m => m.Score2 is not null).Select(m => m.Score2!.Value).OrderBy(s => s).ToList();
        int total = perMachine.Sum(m => m.Count);
        return new RatingModel
        {
            Model = model,
            Results = total,
            Machines = perMachine.Count,
            Median = Percentile(scores, 50),
            Low = Percentile(scores, 25),
            High = Percentile(scores, 75),
            Top = Percentile(scores, 90),
            Median2 = second.Count > 0 ? Percentile(second, 50) : null,
            Verified = total >= MinResults && perMachine.Count >= MinMachines,
        };
    }

    /// <summary>Свой результат на фоне таких же устройств. Отклонение — в долях медианы (−0,15 — на 15 % ниже).</summary>
    public static (RatingVerdict Verdict, double Deviation) Compare(double mine, RatingModel? model)
    {
        if (model is null || !model.Verified || model.Median <= 0)
            return (RatingVerdict.Unknown, 0);
        double deviation = mine / model.Median - 1;
        // Типичный диапазон с запасом 3 % — обычный разброс от замера к замеру.
        if (mine < model.Low * 0.97)
            return (RatingVerdict.Below, deviation);
        if (mine > model.High * 1.03)
            return (RatingVerdict.Above, deviation);
        return (RatingVerdict.Normal, deviation);
    }

    /// <summary>Процентиль с линейной интерполяцией по отсортированному списку.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double percent)
    {
        if (sorted.Count == 0)
            return 0;
        double position = (sorted.Count - 1) * Math.Clamp(percent, 0, 100) / 100;
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Count - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return Percentile(values, 50);
    }

    [GeneratedRegex(@"\s+(\d+-Core\s+Processor|CPU\s*@\s*[\d.]+\s*GHz|with Radeon Graphics|Processor)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CpuSuffix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
