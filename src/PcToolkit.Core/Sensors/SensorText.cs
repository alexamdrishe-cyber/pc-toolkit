using System.Globalization;
using System.Text.RegularExpressions;

namespace PcToolkit.Core.Sensors;

/// <summary>Как показать значение: обычные единицы датчика или более удобные.</summary>
public enum ValueStyle
{
    Default,
    /// <summary>Частота в МГц показывается в ГГц: «4,03 ГГц».</summary>
    Gigahertz,
    /// <summary>Объём в МБ показывается в ГБ: «2,3 ГБ».</summary>
    GigabytesFromMegabytes,
    /// <summary>Скорость в Б/с показывается в Мбит/с — привычно для сети.</summary>
    Megabits,
    /// <summary>Множитель частоты: «×37,2».</summary>
    Multiplier,
}

/// <summary>
/// Русские названия датчиков LibreHardwareMonitor и аккуратное форматирование значений для экранов мониторинга.
/// Неизвестные названия показываются как есть.
/// </summary>
public static partial class SensorText
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly Dictionary<string, string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        // Процессор
        ["CPU Total"] = "Общая",
        ["CPU Core Max"] = "Макс. по ядрам",
        ["Core Max"] = "Макс. по ядрам",
        ["Core Average"] = "Средняя по ядрам",
        ["Cores (Average)"] = "Средняя",
        ["Cores (Average Effective)"] = "Эффективная",
        ["Bus Speed"] = "Шина",
        ["Package"] = "Пакет",
        ["CPU Package"] = "Пакет",
        ["CPU Cores"] = "Ядра",
        ["CPU Memory"] = "Память",
        ["Core (Tctl/Tdie)"] = "Tctl/Tdie",
        ["Core (Tctl)"] = "Tctl",
        ["Core (Tdie)"] = "Tdie",
        ["CCDs Max (Tdie)"] = "CCD, макс.",
        ["CCDs Average (Tdie)"] = "CCD, среднее",
        ["Core (SVI2 TFN)"] = "Ядро (SVI2 TFN)",
        ["SoC (SVI2 TFN)"] = "SoC (SVI2 TFN)",
        ["Core (SVI3 TFN)"] = "Ядро (SVI3 TFN)",
        ["SoC (SVI3 TFN)"] = "SoC (SVI3 TFN)",
        ["CPU Core"] = "Ядро",
        // Видеокарта
        ["GPU Hot Spot"] = "Горячая точка",
        ["GPU Package"] = "Пакет",
        ["GPU Power"] = "Мощность",
        ["GPU PPT"] = "PPT",
        ["GPU SoC"] = "SoC",
        ["GPU Fan"] = "Вентилятор",
        ["GPU Memory Used"] = "Видеопамять, занято",
        ["GPU Memory Free"] = "Видеопамять, свободно",
        ["GPU Memory Total"] = "Видеопамять, всего",
        ["GPU Memory Controller"] = "Контроллер памяти",
        ["GPU Video Engine"] = "Видеодвижок",
        ["GPU Bus"] = "Шина",
        ["GPU Board Power"] = "Плата",
        ["Fullscreen FPS"] = "FPS игры на весь экран",
        // Память
        ["Memory Used"] = "Занято",
        ["Memory Available"] = "Свободно",
        ["Virtual Memory Used"] = "Виртуальная, занято",
        ["Virtual Memory Available"] = "Виртуальная, свободно",
        ["Virtual Memory"] = "Виртуальная",
        ["Capacity"] = "Объём",
        // Накопители
        ["Composite Temperature"] = "Температура",
        ["Temperature"] = "Температура",
        ["Warning Temperature"] = "Порог предупреждения",
        ["Critical Temperature"] = "Критический порог",
        ["Life"] = "Ресурс",
        ["Data Read"] = "Прочитано всего",
        ["Data Written"] = "Записано всего",
        ["Power On Count"] = "Включений",
        ["Power On Hours"] = "Часов работы",
        ["Used Space"] = "Занято места",
        ["Free Space"] = "Свободно",
        ["Total Space"] = "Объём",
        ["Read Activity"] = "Активность чтения",
        ["Write Activity"] = "Активность записи",
        ["Total Activity"] = "Активность",
        ["Read Rate"] = "Чтение",
        ["Write Rate"] = "Запись",
        ["Available Spare"] = "Резерв",
        ["Available Spare Threshold"] = "Порог резерва",
        ["Percentage Used"] = "Износ",
        // Сеть
        ["Data Uploaded"] = "Отправлено",
        ["Data Downloaded"] = "Получено",
        ["Upload Speed"] = "Отдача ↑",
        ["Download Speed"] = "Загрузка ↓",
        ["Network Utilization"] = "Загрузка канала",
        // Материнская плата
        ["Vcore"] = "Ядро процессора",
        ["CMOS Battery"] = "Батарейка CMOS",
        ["System"] = "Система",
        ["Chipset"] = "Чипсет",
        ["VRM MOS"] = "VRM",
        ["PCH"] = "Чипсет",
        ["Auxiliary"] = "Дополнительный",
        ["CPU Socket"] = "Сокет процессора",
    };

    /// <summary>Название устройства для таблицы: у памяти LibreHardwareMonitor отдаёт английские служебные имена.</summary>
    public static string DeviceName(DeviceKind kind, string name)
    {
        string trimmed = name.Trim();
        if (kind != DeviceKind.Memory)
            return trimmed;
        return trimmed switch
        {
            "Total Memory" or "Generic Memory" => "Оперативная память",
            "Virtual Memory" => "Виртуальная память",
            _ => trimmed,
        };
    }

    /// <summary>Название датчика по-русски (насколько получится).</summary>
    public static string Name(DeviceKind device, SensorKind kind, string name)
    {
        string trimmed = name.Trim();

        // У видеокарт «GPU Core» и «GPU Memory» значат разное в зависимости от типа датчика.
        if (device is DeviceKind.GpuAmd or DeviceKind.GpuNvidia or DeviceKind.GpuIntel)
        {
            if (trimmed.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                return "Ядро";
            if (trimmed.Equals("GPU Memory", StringComparison.OrdinalIgnoreCase))
                return kind == SensorKind.Load ? "Контроллер памяти" : "Память";
        }

        if (device == DeviceKind.Memory && kind == SensorKind.Load && trimmed.Equals("Memory", StringComparison.OrdinalIgnoreCase))
            return "Загрузка";

        if (Common.TryGetValue(trimmed, out string? known))
            return known;

        Match match = NumberedName().Match(trimmed);
        if (match.Success)
        {
            string prefix = match.Groups["prefix"].Value.Trim();
            string number = match.Groups["n"].Value;
            string suffix = match.Groups["suffix"].Value.Trim();
            string? baseName = prefix switch
            {
                "CPU Core" when kind == SensorKind.Load => "Поток",
                "Core" or "CPU Core" or "P-Core" => "Ядро",
                "E-Core" => "E-ядро",
                "Temperature" => "Датчик",
                "Fan" => "Вентилятор",
                "Voltage" => "Напряжение",
                "CCD" => "CCD",
                _ => null,
            };

            if (baseName is not null)
            {
                string extra = suffix switch
                {
                    "" => "",
                    "(Effective)" => ", эффективная",
                    "(SMU)" => "",
                    "VID" => ", VID",
                    "Distance to TjMax" => ", запас до TjMax",
                    _ => " " + suffix,
                };
                if (kind == SensorKind.Factor && prefix is "Core" or "CPU Core")
                    extra = ", множитель";
                return $"{baseName} {number}{extra}";
            }
        }

        if (trimmed.StartsWith("CCD", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith("(Tdie)", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^"(Tdie)".Length].Trim();

        return trimmed;
    }

    /// <summary>Порядковый номер в названии «Core #3», «CPU Core #3», «Core #3 (SMU)». Null, если номера нет.</summary>
    public static int? IndexOf(string name)
    {
        Match match = NumberedName().Match(name.Trim());
        return match.Success && int.TryParse(match.Groups["n"].Value, out int n) ? n : null;
    }

    /// <summary>Значение с единицей: «66,1 °C», «24 %», «4,03 ГГц», «2,3 ГБ».</summary>
    public static string Format(SensorKind kind, double? value, ValueStyle style = ValueStyle.Default)
    {
        if (value is not double v || !double.IsFinite(v))
            return "—";

        return style switch
        {
            ValueStyle.Gigahertz => $"{Number(v / 1000, 2)} ГГц",
            ValueStyle.GigabytesFromMegabytes => v >= 1024 ? $"{Number(v / 1024, 1)} ГБ" : $"{Number(v, 0)} МБ",
            ValueStyle.Megabits => FormatBits(v * 8),
            ValueStyle.Multiplier => $"×{Number(v, 1)}",
            _ => kind switch
            {
                SensorKind.Temperature => $"{Smart(v, 1)} °C",
                SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => $"{Percent(v)} %",
                SensorKind.Clock => v >= 10 ? $"{Number(v, 0)} МГц" : $"{Number(v, 2)} МГц",
                SensorKind.Power => $"{Number(v, Math.Abs(v) >= 100 ? 0 : 1)} Вт",
                SensorKind.Voltage => $"{Number(v, 3)} В",
                SensorKind.Current => $"{Number(v, 2)} А",
                SensorKind.Fan => $"{Number(v, 0)} об/мин",
                SensorKind.Data => v >= 1000 ? $"{Number(v / 1024, 1)} ТБ" : $"{Number(v, 1)} ГБ",
                SensorKind.SmallData => v >= 1024 ? $"{Number(v / 1024, 1)} ГБ" : $"{Number(v, 0)} МБ",
                SensorKind.Throughput => FormatBytes(v),
                SensorKind.Frequency => $"{Number(v, 0)} Гц",
                SensorKind.Timing => $"{Smart(v, 2)} нс",
                SensorKind.Energy => $"{Number(v, 0)} мВт·ч",
                SensorKind.Noise => $"{Number(v, 0)} дБА",
                SensorKind.Flow => $"{Number(v, 0)} л/ч",
                SensorKind.TimeSpan => $"{Number(v / 3600, 1)} ч",
                _ => Smart(v, 2),
            },
        };
    }

    /// <summary>Число без единицы — для столбцов «мин», «среднее», «макс» (единица уже есть в «сейчас»).</summary>
    public static string Bare(SensorKind kind, double? value, ValueStyle style = ValueStyle.Default)
    {
        string text = Format(kind, value, style);
        int space = text.LastIndexOf(' ');
        return space > 0 && !text.StartsWith('×') ? text[..space] : text;
    }

    /// <summary>Единица для подписи шкалы графика.</summary>
    public static string Unit(SensorKind kind, ValueStyle style = ValueStyle.Default) => style switch
    {
        ValueStyle.Gigahertz => "ГГц",
        ValueStyle.GigabytesFromMegabytes => "ГБ",
        ValueStyle.Megabits => "Мбит/с",
        ValueStyle.Multiplier => "×",
        _ => kind == SensorKind.Throughput ? "МБ/с" : SensorFormatter.Unit(kind),
    };

    /// <summary>Значение в единицах шкалы графика: ГГц, ГБ, Мбит/с, МБ/с.</summary>
    public static double Scale(SensorKind kind, double value, ValueStyle style = ValueStyle.Default) => style switch
    {
        ValueStyle.Gigahertz => value / 1000,
        ValueStyle.GigabytesFromMegabytes => value / 1024,
        ValueStyle.Megabits => value * 8 / 1_000_000,
        _ => kind == SensorKind.Throughput ? value / (1024 * 1024) : value,
    };

    /// <summary>Число с запятой и без разделителя тысяч: «2352», «4,03».</summary>
    public static string Number(double value, int decimals) => value.ToString("F" + decimals, Russian);

    private static string Smart(double value, int decimals) =>
        Math.Abs(value - Math.Round(value)) < 1e-6 ? Number(value, 0) : Number(value, decimals);

    private static string Percent(double value) => Math.Abs(value) >= 10 || value == 0 ? Number(value, 0) : Number(value, 1);

    private static string FormatBytes(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{Number(bytesPerSecond / (1024 * 1024), 1)} МБ/с",
        >= 1024 => $"{Number(bytesPerSecond / 1024, 0)} КБ/с",
        _ => $"{Number(bytesPerSecond, 0)} Б/с",
    };

    private static string FormatBits(double bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000 => $"{Number(bitsPerSecond / 1_000_000, 1)} Мбит/с",
        >= 1_000 => $"{Number(bitsPerSecond / 1_000, 0)} Кбит/с",
        _ => $"{Number(bitsPerSecond, 0)} бит/с",
    };

    [GeneratedRegex(@"^(?<prefix>.*?)\s*#(?<n>\d+)(?<suffix>.*)$")]
    private static partial Regex NumberedName();
}
