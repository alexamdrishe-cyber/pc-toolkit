namespace PcToolkit.Core;

/// <summary>Форматирование чисел для интерфейса на русском языке.</summary>
public static class Formatting
{
    private static readonly string[] ByteUnits = ["Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ"];

    /// <summary>Размер в двоичных единицах, как в Проводнике Windows: 1 ГБ = 1024 МБ.</summary>
    public static string Bytes(ulong bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {ByteUnits[unit]}" : $"{value:0.#} {ByteUnits[unit]}";
    }

    public static string Uptime(TimeSpan time)
    {
        if (time.TotalDays >= 1)
            return $"{(int)time.TotalDays} д {time.Hours} ч {time.Minutes} мин";

        if (time.TotalHours >= 1)
            return $"{time.Hours} ч {time.Minutes} мин";

        return $"{Math.Max(time.Minutes, 0)} мин";
    }

    /// <summary>Склонение: Plural(5, "датчик", "датчика", "датчиков") → «датчиков».</summary>
    public static string Plural(long count, string one, string few, string many)
    {
        long n = Math.Abs(count) % 100;
        if (n is >= 11 and <= 14)
            return many;

        return (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }
}
