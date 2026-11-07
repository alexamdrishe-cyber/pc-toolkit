namespace PcToolkit.Core.Disks;

/// <summary>Общая оценка диска. Порядок важен: по нему диски сортируются, проблемные сверху.</summary>
public enum DiskVerdict
{
    Bad,
    Warning,
    Good,
    Unknown,
}

/// <summary>Оценка отдельного показателя.</summary>
public enum HealthLevel
{
    /// <summary>Просто сведения: часы работы, объём записи.</summary>
    Fact,
    Good,

    /// <summary>Стоит знать, но это не неисправность.</summary>
    Notice,
    Warning,
    Bad,
}

/// <summary>Показатель здоровья диска, объяснённый простыми словами.</summary>
/// <param name="What">Что это за показатель.</param>
/// <param name="Risk">Чем грозит — для тревожных значений.</param>
/// <param name="Advice">Что делать — для тревожных значений.</param>
public sealed record HealthItem(string Title, string Value, HealthLevel Level, string What, string? Risk = null, string? Advice = null)
{
    /// <summary>Короткая фраза для общей сводки («ресурс почти исчерпан»); по умолчанию — название и значение.</summary>
    public string? Problem { get; init; }

    public string ProblemText => Problem ?? $"{char.ToLowerInvariant(Title[0])}{Title[1..]}: {Value}";
}

/// <summary>Строка подробной таблицы SMART (для тех, кто привык к CrystalDiskInfo).</summary>
public sealed record SmartRow(string Id, string Name, string Current, string Worst, string Threshold, string Raw, HealthLevel Level);

/// <summary>Итог проверки диска: оценка, объяснения и подробная таблица.</summary>
public sealed record DiskHealthReport
{
    public required DiskHealthData Disk { get; init; }

    public DiskVerdict Verdict { get; init; } = DiskVerdict.Unknown;

    /// <summary>Одна-две фразы: что с диском и что делать.</summary>
    public string Summary { get; init; } = "";

    /// <summary>«SSD NVMe», «Жёсткий диск SATA»…</summary>
    public string Kind { get; init; } = "";

    /// <summary>Оставшийся ресурс SSD, %.</summary>
    public int? LifePercent { get; init; }

    public int? TemperatureC { get; init; }

    public long? PowerOnHours { get; init; }

    public long? WrittenBytes { get; init; }

    /// <summary>Главные показатели с объяснениями — от самых тревожных.</summary>
    public IReadOnlyList<HealthItem> Items { get; init; } = Array.Empty<HealthItem>();

    /// <summary>Все атрибуты SMART как есть.</summary>
    public IReadOnlyList<SmartRow> Rows { get; init; } = Array.Empty<SmartRow>();

    /// <summary>В таблице есть нормированные значения и пороги (SATA); у NVMe — только значения.</summary>
    public bool RowsHaveThresholds { get; init; }

    /// <summary>Счётчики ошибок для истории: по ним видно, растут ли они.</summary>
    public IReadOnlyDictionary<string, long> Counters { get; init; } = new Dictionary<string, long>();

    public string Title => Disk.Model.Length > 0 ? Disk.Model : $"Диск {Disk.Number}";

    public string VerdictText => Verdict switch
    {
        DiskVerdict.Good => "Хорошо",
        DiskVerdict.Warning => "Есть тревожные признаки",
        DiskVerdict.Bad => "Плохо",
        _ => "Нет данных",
    };
}

/// <summary>
/// Оценивает SMART диска и объясняет каждый важный показатель: что это, чем грозит и что делать.
/// Правила близки к CrystalDiskInfo: «плохо» — только когда диск сам сообщает о неисправности,
/// «тревожно» — при повреждённых участках, ошибках памяти и почти исчерпанном ресурсе.
/// </summary>
public static class DiskHealthEvaluator
{
    /// <summary>Счётчики, рост которых означает, что диск продолжает портиться.</summary>
    public static readonly IReadOnlyDictionary<string, string> WatchedCounters = new Dictionary<string, string>
    {
        ["05"] = "переназначенные секторы",
        ["C5"] = "нестабильные секторы",
        ["C6"] = "неисправимые секторы",
        ["BB"] = "неисправленные ошибки",
        ["B8"] = "ошибки внутри диска",
        ["0A"] = "повторные раскрутки",
        ["AB"] = "ошибки записи в память",
        ["AC"] = "ошибки стирания памяти",
        ["B5"] = "ошибки записи в память",
        ["B6"] = "ошибки стирания памяти",
        ["C7"] = "ошибки передачи по кабелю",
        ["media"] = "неисправимые ошибки данных",
    };

    private const string NoRepeatAdvice = "PC Toolkit запомнит значение и предупредит, если оно вырастет.";

    public static DiskHealthReport Evaluate(DiskHealthData disk, DiskHealthRecord? history = null)
    {
        var context = new Context(disk, history);
        if (disk.Nvme is { } nvme)
            EvaluateNvme(context, nvme);
        else if (disk.Attributes is { Count: > 0 } attributes)
            EvaluateAta(context, attributes);

        return context.Build();
    }

    // ---------------------------------------------------------------- NVMe

    private static void EvaluateNvme(Context c, NvmeHealth n)
    {
        c.Ssd = true;
        c.Counters["warn"] = n.CriticalWarning;
        c.Counters["media"] = n.MediaErrors;
        c.Counters["spare"] = n.AvailableSpare;
        c.Counters["used"] = n.PercentageUsed;

        // Бит 1 — только температура: о ней скажет показатель температуры.
        int serious = n.CriticalWarning & ~0x02;
        if (serious != 0)
        {
            var reasons = new List<string>();
            if ((serious & 0x01) != 0)
                reasons.Add("запас резервной памяти ниже допустимого");
            if ((serious & 0x04) != 0)
                reasons.Add("надёжность снижена из-за ошибок памяти");
            if ((serious & 0x08) != 0)
                reasons.Add("диск перешёл в режим «только чтение»");
            if ((serious & 0x10) != 0)
                reasons.Add("отказала резервная память или конденсаторы");
            if (reasons.Count == 0)
                reasons.Add($"код 0x{serious:X2}");

            c.Add(new HealthItem("Диск сообщает о серьёзной неисправности", string.Join("; ", reasons), HealthLevel.Bad,
                "NVMe-диск сам выставляет флаги критических предупреждений, когда с ним что-то серьёзно не так.",
                (serious & 0x08) != 0
                    ? "Записывать на диск больше нельзя; данные пока можно прочитать, но диск может отказать полностью."
                    : "Диск может отказать в любой момент вместе со всеми файлами на нём.",
                "Сразу скопируйте важные данные на другой диск или в облако и замените этот диск.")
            {
                Problem = string.Join(", ", reasons),
            });
        }

        AddLife(c, Math.Max(0, 100 - n.PercentageUsed), n.PercentageUsed);

        // Дешёвые диски иногда не заполняют запас: 0 при пороге 0 — «не сообщает».
        if (n.AvailableSpare > 0 || n.AvailableSpareThreshold > 0)
        {
            const string what = "Резервные ячейки памяти, которыми диск заменяет испорченные. У исправного диска — 100 %, " +
                                "уменьшается, когда память начинает портиться.";
            string value = $"{n.AvailableSpare} % (порог {n.AvailableSpareThreshold} %)";
            if (n.AvailableSpare < n.AvailableSpareThreshold)
            {
                c.Add(new HealthItem("Запас резервной памяти", value, HealthLevel.Bad, what,
                    "Испорченных ячеек больше, чем диск может заменить: дальше данные начнут теряться.",
                    "Сразу скопируйте важные данные и замените диск.")
                {
                    Problem = "закончился запас резервной памяти",
                });
            }
            else if (n.AvailableSpare < 100 && c.Grew("spare", n.AvailableSpare, decreasing: true) is { } drop)
            {
                c.Add(new HealthItem("Запас резервной памяти", $"{value}, {drop}", HealthLevel.Notice, what,
                    "Диск уже заменил часть испорченных ячеек. Пока запас большой, это не опасно.",
                    "Держите копию важных файлов. " + NoRepeatAdvice));
            }
            else
            {
                c.Add(new HealthItem("Запас резервной памяти", value, HealthLevel.Good, what));
            }
        }

        AddCounter(c, "media", n.MediaErrors, "Неисправимые ошибки данных", HealthLevel.Warning,
            "Сколько раз диск не смог прочитать или проверить данные даже с исправлением ошибок. У исправного диска — 0.",
            "Часть файлов могла повредиться. Это признак износа или брака памяти.",
            "Скопируйте важные файлы в другое место. Если число растёт — меняйте диск.");

        AddTemperature(c, n.TemperatureC is > 0 and < 150 ? n.TemperatureC : null);

        long hotMinutes = n.WarningTemperatureMinutes + n.CriticalTemperatureMinutes;
        if (hotMinutes > 0)
        {
            string value = n.CriticalTemperatureMinutes > 0
                ? $"{Duration(n.WarningTemperatureMinutes)} в перегреве, из них {Duration(n.CriticalTemperatureMinutes)} — в сильном"
                : $"{Duration(n.WarningTemperatureMinutes)} в перегреве";
            c.Add(new HealthItem("Время в перегреве за всю жизнь", value, HealthLevel.Notice,
                "Сколько диск проработал горячее, чем рекомендует производитель. В это время SSD снижает скорость, чтобы остыть.",
                "Частый перегрев замедляет работу (игры дольше грузятся) и ускоряет износ.",
                "Поставьте на диск радиатор (он часто есть в комплекте материнской платы) и улучшите обдув корпуса."));
        }

        long written = n.DataUnitsWritten * 512_000;
        c.Written = written;
        c.Add(new HealthItem("Записано за всю жизнь", Formatting.DecimalBytes(written), HealthLevel.Fact,
            "Сколько данных записано на диск. Ресурс SSD тратится именно на запись: производитель обещает определённый объём (TBW)."));

        AddHours(c, n.PowerOnHours);
        AddUnsafeShutdowns(c, n.UnsafeShutdowns);

        c.RowsHaveThresholds = false;
        c.Row("", "Критические предупреждения", n.CriticalWarning == 0 ? "нет" : $"0x{n.CriticalWarning:X2}",
              serious != 0 ? HealthLevel.Bad : n.CriticalWarning != 0 ? HealthLevel.Warning : HealthLevel.Fact);
        c.Row("", "Температура", n.TemperatureC > 0 ? $"{n.TemperatureC} °C" : "—");
        c.Row("", "Запас резервной памяти", $"{n.AvailableSpare} %",
              n.AvailableSpare < n.AvailableSpareThreshold ? HealthLevel.Bad : HealthLevel.Fact);
        c.Row("", "Порог запаса", $"{n.AvailableSpareThreshold} %");
        c.Row("", "Израсходовано ресурса", $"{n.PercentageUsed} %");
        c.Row("", "Прочитано", Formatting.DecimalBytes(n.DataUnitsRead * 512_000.0));
        c.Row("", "Записано", Formatting.DecimalBytes(written));
        c.Row("", "Включений", Formatting.Number(n.PowerCycles));
        c.Row("", "Часы работы", Formatting.Number(n.PowerOnHours));
        c.Row("", "Аварийные отключения", Formatting.Number(n.UnsafeShutdowns));
        c.Row("", "Неисправимые ошибки данных", Formatting.Number(n.MediaErrors), n.MediaErrors > 0 ? HealthLevel.Warning : HealthLevel.Fact);
        c.Row("", "Записей в журнале ошибок", Formatting.Number(n.ErrorLogEntries));
        c.Row("", "Минут выше температуры предупреждения", Formatting.Number(n.WarningTemperatureMinutes));
        c.Row("", "Минут выше критической температуры", Formatting.Number(n.CriticalTemperatureMinutes));
    }

    // ---------------------------------------------------------------- SATA (ATA SMART)

    private static void EvaluateAta(Context c, List<SmartAttribute> list)
    {
        var attributes = new Dictionary<int, SmartAttribute>();
        foreach (SmartAttribute attribute in list)
            attributes.TryAdd(attribute.Id, attribute);

        // Атрибут 03 (время раскрутки) есть только у дисков с пластинами.
        bool ssd = c.Disk.IsSsd ?? !attributes.ContainsKey(0x03);
        c.Ssd = ssd;
        string units = ssd ? "блоки" : "секторы";

        List<SmartAttribute> failing = list.Where(a => a.BelowThreshold).ToList();
        if (failing.Count > 0)
        {
            c.Add(new HealthItem("Диск сам сообщает о неисправности",
                string.Join(", ", failing.Select(a => $"{AttributeName(a.Id, ssd)} ({a.Current} при пороге {a.Threshold})")),
                HealthLevel.Bad,
                "У каждого показателя SMART производитель задаёт порог. Значение опустилось до порога — значит, по мнению самого диска, он неисправен.",
                "Диск может перестать работать в любой момент вместе со всеми файлами на нём.",
                "Сразу скопируйте важные данные на другой диск или в облако и замените этот диск.")
            {
                Problem = "диск сам сообщает о неисправности",
            });
        }

        if (ssd && LifeAttribute(attributes, c.Disk.Model) is { } life)
            AddLife(c, life.Current, null);

        AddAtaCounter(c, attributes, 0x05, ssd ? "Заменённые блоки памяти" : "Переназначенные секторы", HealthLevel.Warning,
            ssd
                ? "Сколько испорченных блоков памяти диск вывел из работы и заменил запасными. У исправного диска — 0 или единицы."
                : "Сколько повреждённых участков поверхности диск нашёл и заменил запасными. У исправного диска — 0.",
            ssd
                ? "Память начала портиться. Пока запасных блоков хватает, данные целы, но если число растёт, диск деградирует."
                : "Поверхность начала портиться. Если число растёт, диск деградирует и может внезапно отказать вместе с файлами.",
            "Скопируйте важные файлы на другой диск. Если число растёт — меняйте диск. " + NoRepeatAdvice);

        AddAtaCounter(c, attributes, 0xC5, ssd ? "Нестабильные блоки" : "Нестабильные секторы", HealthLevel.Warning,
            $"Участки, которые диск не смог прочитать с первого раза; он перепроверит их при следующей записи. У исправного диска — 0.",
            "Файлы в этих местах могут не открываться. Часто это первый признак того, что диск начинает умирать.",
            $"Скопируйте важные данные. Иногда {units} после перезаписи оказываются целыми и число уходит в 0, " +
            "но если оно держится или растёт — диск лучше заменить.");

        AddAtaCounter(c, attributes, 0xC6, ssd ? "Неисправимые блоки" : "Неисправимые секторы", HealthLevel.Warning,
            "Участки, данные с которых диск не смог прочитать даже с исправлением ошибок. У исправного диска — 0.",
            "Часть данных уже потеряна или повреждена: могут не открываться файлы или не загружаться Windows.",
            "Скопируйте важные файлы на другой диск как можно скорее и замените этот.");

        AddAtaCounter(c, attributes, 0xBB, "Неисправленные ошибки", HealthLevel.Warning,
            "Сколько раз диск сообщал компьютеру о данных, которые не смог восстановить. У исправного диска — 0.",
            "Какие-то файлы могли повредиться.",
            "Скопируйте важное в другое место. Если число растёт — меняйте диск.", showZero: false);

        AddAtaCounter(c, attributes, 0xB8, "Ошибки внутри диска", HealthLevel.Warning,
            "Ошибки при передаче данных внутри самого диска — между его кешем и памятью или пластинами. У исправного диска — 0.",
            "Похоже на неисправность электроники диска: данные могут записываться с ошибками.",
            "Скопируйте важные файлы и замените диск.", showZero: false);

        if (ssd)
        {
            foreach ((int id, string title) in new[] { (0xAB, "Ошибки записи в память"), (0xB5, "Ошибки записи в память"),
                                                        (0xAC, "Ошибки стирания памяти"), (0xB6, "Ошибки стирания памяти") })
            {
                if (c.Items.Any(i => i.Title == title))
                    continue;

                AddAtaCounter(c, attributes, id, title, HealthLevel.Warning,
                    "Сколько раз ячейки памяти SSD не смогли записать или стереть данные. Диск переносит данные в запасные блоки. У исправного диска — 0.",
                    "Память изнашивается или повреждена; когда запасные блоки кончатся, начнут теряться данные.",
                    "Держите копию важных файлов. Если число растёт — меняйте диск.", showZero: false);
            }
        }
        else
        {
            AddAtaCounter(c, attributes, 0x0A, "Повторные попытки раскрутки", HealthLevel.Warning,
                "Сколько раз диску не удалось раскрутить пластины с первой попытки. У исправного диска — 0.",
                "Проблемы с мотором или питанием: однажды диск может не запуститься.",
                "Проверьте кабель питания и блок питания и скопируйте важные данные.", showZero: false);
        }

        AddCableErrors(c, attributes);
        AddTemperature(c, AtaTemperature(attributes));

        if (attributes.TryGetValue(0x09, out SmartAttribute? hours))
            AddHours(c, hours.RawLow32);

        if (attributes.TryGetValue(0xAE, out SmartAttribute? powerLoss) || attributes.TryGetValue(0xC0, out powerLoss))
            AddUnsafeShutdowns(c, powerLoss.RawLow32);

        c.RowsHaveThresholds = true;
        foreach (SmartAttribute a in list)
        {
            string key = a.Id.ToString("X2");
            HealthLevel level = a.BelowThreshold ? HealthLevel.Bad
                : WatchedCounters.ContainsKey(key) && a.RawLow32 > 0 && (ssd || key is not ("AB" or "AC" or "B5" or "B6"))
                    ? (key == "C7" ? HealthLevel.Notice : HealthLevel.Warning)
                    : HealthLevel.Fact;
            string raw = a.Id is 0xC2 or 0xBE && a.RawLowByte is > 0 and < 100 ? $"{a.RawLowByte} °C" : Formatting.Number(a.Raw);
            c.Rows.Add(new SmartRow(key, AttributeName(a.Id, ssd), a.Current.ToString(), a.Worst.ToString(),
                                    a.Threshold.ToString(), raw, level));
        }
    }

    /// <summary>
    /// Оставшийся ресурс SATA SSD: у разных производителей он в разных атрибутах (нормированное значение = %).
    /// E7 — Kingston, SandForce и многие другие; CA — Crucial/Micron; A9 — WD, ADATA; B1 — Samsung; E9 — Intel.
    /// </summary>
    private static SmartAttribute? LifeAttribute(Dictionary<int, SmartAttribute> attributes, string model)
    {
        bool intel = model.Contains("INTEL", StringComparison.OrdinalIgnoreCase);
        int[] ids = intel ? new[] { 0xE9, 0xE7 } : new[] { 0xE7, 0xCA, 0xA9, 0xB1, 0xAD };
        foreach (int id in ids)
        {
            if (attributes.TryGetValue(id, out SmartAttribute? attribute) && attribute.Current is >= 1 and <= 100)
                return attribute;
        }

        return null;
    }

    /// <summary>Температура SATA: C2, а если его нет — BE; в младшем байте «сырого» значения.</summary>
    private static int? AtaTemperature(Dictionary<int, SmartAttribute> attributes)
    {
        foreach (int id in new[] { 0xC2, 0xBE })
        {
            if (attributes.TryGetValue(id, out SmartAttribute? attribute) && attribute.RawLowByte is > 0 and < 100)
                return attribute.RawLowByte;
        }

        return null;
    }

    private static void AddAtaCounter(Context c, Dictionary<int, SmartAttribute> attributes, int id, string title, HealthLevel level,
                                      string what, string risk, string advice, bool showZero = true)
    {
        if (attributes.TryGetValue(id, out SmartAttribute? attribute))
            AddCounter(c, id.ToString("X2"), attribute.RawLow32, title, level, what, risk, advice, showZero);
    }

    private static void AddCounter(Context c, string key, long value, string title, HealthLevel level, string what, string risk,
                                   string advice, bool showZero = true)
    {
        c.Counters[key] = value;
        if (value <= 0)
        {
            if (showZero)
                c.Add(new HealthItem(title, "0", HealthLevel.Good, what));
            return;
        }

        string? growth = c.Grew(key, value);
        c.Add(new HealthItem(title, growth is null ? Formatting.Number(value) : $"{Formatting.Number(value)} ({growth})", level, what,
            growth is null ? risk : risk + " Число растёт: диск продолжает портиться.", advice));
    }

    /// <summary>Ошибки кабеля (C7): число не обнуляется, поэтому тревожно только если оно растёт.</summary>
    private static void AddCableErrors(Context c, Dictionary<int, SmartAttribute> attributes)
    {
        if (!attributes.TryGetValue(0xC7, out SmartAttribute? attribute))
            return;

        const string what = "Сбои при передаче данных между диском и материнской платой. Сам диск при этом может быть здоров; " +
                            "число не обнуляется, важно только — растёт ли оно.";
        long value = attribute.RawLow32;
        c.Counters["C7"] = value;
        if (value == 0)
        {
            c.Add(new HealthItem("Ошибки передачи по кабелю", "0", HealthLevel.Good, what));
            return;
        }

        string? growth = c.Grew("C7", value);
        c.Add(new HealthItem("Ошибки передачи по кабелю",
            growth is null ? Formatting.Number(value) : $"{Formatting.Number(value)} ({growth})",
            growth is null ? HealthLevel.Notice : HealthLevel.Warning, what,
            growth is null
                ? "Ошибки были раньше. Если число больше не растёт, всё в порядке — возможно, кабель уже меняли."
                : "Ошибки появляются прямо сейчас: возможны подтормаживания, зависания и испорченные при записи файлы.",
            "Переподключите или замените кабель SATA, попробуйте другой разъём на материнской плате. " + NoRepeatAdvice)
        {
            Problem = "растут ошибки передачи по кабелю",
        });
    }

    // ---------------------------------------------------------------- общие показатели

    private static void AddLife(Context c, int life, int? usedPercent)
    {
        c.Life = life;
        const string what = "Сколько осталось от ресурса записи, который обещает производитель. Ячейки памяти SSD изнашиваются " +
                            "от записи; у нового диска — 100 %.";
        string value = usedPercent is > 100 ? $"0 % (израсходовано {usedPercent} %)" : $"{life} %";
        if (usedPercent is >= 100)
        {
            c.Add(new HealthItem("Оставшийся ресурс", value, HealthLevel.Warning, what,
                "Ресурс исчерпан по паспорту. Многие диски работают и дальше, но изношенные ячейки хуже держат данные, " +
                "и диск может перейти в режим «только чтение» или отказать.",
                "Держите копию всех важных файлов и запланируйте замену диска.")
            {
                Problem = "ресурс записи исчерпан",
            });
        }
        else if (life < 10)
        {
            c.Add(new HealthItem("Оставшийся ресурс", value, HealthLevel.Warning, what,
                "Ресурс почти исчерпан. Изношенные ячейки хуже держат данные; со временем диск может перейти в режим «только чтение» или отказать.",
                "Скопируйте важные файлы в другое место и запланируйте замену диска.")
            {
                Problem = $"ресурс почти исчерпан ({life} %)",
            });
        }
        else if (life < 30)
        {
            c.Add(new HealthItem("Оставшийся ресурс", value, HealthLevel.Notice, what,
                "Израсходовано больше 70 % ресурса. Диск исправен, но износ уже большой.",
                "Ничего срочного: держите копию важных файлов и со временем присмотрите замену."));
        }
        else
        {
            c.Add(new HealthItem("Оставшийся ресурс", value, HealthLevel.Good, what));
        }
    }

    private static void AddTemperature(Context c, int? temperature)
    {
        if (temperature is not int t)
            return;

        c.Temperature = t;
        // Жёстким дискам вредно уже 50+ °C; SSD спокойно работают горячее, NVMe — ещё горячее.
        (int warm, int hot) = c.Disk.IsNvme ? (70, 80) : c.Ssd ? (65, 75) : (50, 60);
        string what = c.Ssd
            ? $"Температура диска сейчас. SSD нормально работают примерно до {warm} °C."
            : "Температура диска сейчас. Жёстким дискам лучше всего 30–45 °C.";
        string risk = c.Ssd
            ? "Перегретый SSD снижает скорость, чтобы остыть, а при долгом перегреве быстрее изнашивается."
            : "Перегрев ускоряет износ механики и поверхности: диск отказывает раньше.";
        string advice = c.Disk.IsNvme
            ? "Поставьте на диск радиатор (он часто есть в комплекте материнской платы) и улучшите обдув корпуса."
            : c.Ssd
                ? "Улучшите обдув корпуса и проверьте, не лежит ли диск рядом с горячей видеокартой."
                : "Проверьте обдув: перед дисками в корпусе должен стоять вентилятор, а в корпусе не должно быть толстого слоя пыли.";

        if (t >= hot)
            c.Add(new HealthItem("Температура", $"{t} °C", HealthLevel.Warning, what, risk, advice) { Problem = $"перегрев ({t} °C)" });
        else if (t >= warm)
            c.Add(new HealthItem("Температура", $"{t} °C", HealthLevel.Notice, what, "Диск горячее обычного. " + risk, advice));
        else
            c.Add(new HealthItem("Температура", $"{t} °C", HealthLevel.Good, what));
    }

    private static void AddHours(Context c, long hours)
    {
        if (hours <= 0)
            return;

        c.Hours = hours;
        c.Add(new HealthItem("Часы работы", $"{Formatting.Number(hours)} ч ({Age(hours)})", HealthLevel.Fact,
            "Сколько диск проработал за всю жизнь. Это не неисправность, а возраст: жёсткие диски обычно служат 30–50 тысяч часов, " +
            "SSD изнашиваются в основном от записи, а не от времени."));
    }

    private static void AddUnsafeShutdowns(Context c, long count)
    {
        c.Add(new HealthItem("Аварийные отключения", Formatting.Number(count), HealthLevel.Fact,
            "Сколько раз питание пропадало без нормального завершения работы Windows: выключение кнопкой, сбой, отключение света. " +
            "Обычно диску это не вредит, но файл, который записывался в этот момент, может испортиться."));
    }

    private static string Age(long hours)
    {
        double days = hours / 24.0;
        if (days >= 365)
            return $"≈ {days / 365:0.#} {YearsWord(days / 365)} работы";
        return days >= 1 ? $"≈ {days:0} {Formatting.Plural((long)Math.Round(days), "день", "дня", "дней")} работы" : "меньше суток";
    }

    // «1,5 года», «2 года», «5 лет»: дробные — всегда «года».
    private static string YearsWord(double years)
    {
        double rounded = Math.Round(years, 1);
        return rounded % 1 != 0 ? "года" : Formatting.Plural((long)rounded, "год", "года", "лет");
    }

    private static string Duration(long minutes) =>
        minutes >= 120 ? $"{Formatting.Number(minutes / 60)} ч" : $"{Formatting.Number(minutes)} мин";

    // ---------------------------------------------------------------- названия атрибутов

    private static readonly Dictionary<int, string> Names = new()
    {
        [0x01] = "Ошибки чтения (частота)",
        [0x02] = "Производительность",
        [0x03] = "Время раскрутки",
        [0x04] = "Запуски и остановки шпинделя",
        [0x05] = "Переназначенные секторы",
        [0x07] = "Ошибки позиционирования (частота)",
        [0x08] = "Скорость позиционирования",
        [0x09] = "Часы работы",
        [0x0A] = "Повторные попытки раскрутки",
        [0x0B] = "Повторные попытки калибровки",
        [0x0C] = "Включения",
        [0x0D] = "Программные ошибки чтения",
        [0x16] = "Уровень гелия",
        [0xAA] = "Запас резервных блоков",
        [0xAB] = "Ошибки записи в память",
        [0xAC] = "Ошибки стирания памяти",
        [0xAD] = "Износ (выравнивание)",
        [0xAE] = "Неожиданные отключения питания",
        [0xAF] = "Сбои защиты от отключения питания",
        [0xB1] = "Износ (выравнивание)",
        [0xB3] = "Использовано резервных блоков",
        [0xB4] = "Свободно резервных блоков",
        [0xB5] = "Ошибки записи в память (всего)",
        [0xB6] = "Ошибки стирания памяти (всего)",
        [0xB7] = "Снижения скорости SATA",
        [0xB8] = "Ошибки внутри диска (End-to-End)",
        [0xBB] = "Неисправленные ошибки",
        [0xBC] = "Тайм-ауты команд",
        [0xBD] = "Запись с неверной высоты головки",
        [0xBE] = "Температура (воздух)",
        [0xBF] = "Удары и вибрация",
        [0xC0] = "Аварийные отключения",
        [0xC1] = "Парковки головок",
        [0xC2] = "Температура",
        [0xC3] = "Исправленные ошибки (ECC)",
        [0xC4] = "События переназначения",
        [0xC5] = "Нестабильные секторы",
        [0xC6] = "Неисправимые секторы",
        [0xC7] = "Ошибки передачи по кабелю",
        [0xC8] = "Ошибки записи (частота)",
        [0xCA] = "Ошибки адресных меток",
        [0xDC] = "Смещение пластин",
        [0xDF] = "Повторы загрузки головок",
        [0xE0] = "Трение при загрузке головок",
        [0xE1] = "Циклы загрузки головок",
        [0xE2] = "Время загрузки головок",
        [0xE6] = "Износ / амплитуда головок",
        [0xE7] = "Оставшийся ресурс SSD",
        [0xE8] = "Запас резервных блоков",
        [0xE9] = "Износ / записано в память",
        [0xF0] = "Время работы головок",
        [0xF1] = "Всего записано",
        [0xF2] = "Всего прочитано",
        [0xF9] = "Записано в память",
        [0xFE] = "Защита от падения",
    };

    public static string AttributeName(int id, bool ssd)
    {
        if (ssd)
        {
            switch (id)
            {
                case 0x05: return "Заменённые блоки памяти";
                case 0xC5: return "Нестабильные блоки";
                case 0xC6: return "Неисправимые блоки";
                case 0xCA: return "Оставшийся ресурс";
            }
        }

        return Names.TryGetValue(id, out string? name) ? name : "Атрибут производителя";
    }

    // ---------------------------------------------------------------- сборка отчёта

    private sealed class Context
    {
        private readonly DiskHealthRecord? _history;

        public Context(DiskHealthData disk, DiskHealthRecord? history)
        {
            Disk = disk;
            _history = history;
            Ssd = disk.IsSsd == true || disk.IsNvme;
        }

        public DiskHealthData Disk { get; }

        public bool Ssd { get; set; }

        public int? Life { get; set; }

        public int? Temperature { get; set; }

        public long? Hours { get; set; }

        public long? Written { get; set; }

        public bool RowsHaveThresholds { get; set; }

        public List<HealthItem> Items { get; } = new();

        public List<SmartRow> Rows { get; } = new();

        public Dictionary<string, long> Counters { get; } = new();

        public void Add(HealthItem item) => Items.Add(item);

        public void Row(string id, string name, string value, HealthLevel level = HealthLevel.Fact) =>
            Rows.Add(new SmartRow(id, name, "", "", "", value, level));

        /// <summary>«+3 с 01.11.2026» — если счётчик вырос с первой проверки (или уменьшился, для запаса).</summary>
        public string? Grew(string key, long value, bool decreasing = false)
        {
            if (_history is null || !_history.FirstCounters.TryGetValue(key, out long first))
                return null;

            long change = decreasing ? first - value : value - first;
            if (change <= 0)
                return null;

            string date = _history.FirstSeen.ToLocalTime().ToString("dd.MM.yyyy");
            return decreasing ? $"−{change} % с {date}" : $"+{Formatting.Number(change)} с {date}";
        }

        public DiskHealthReport Build()
        {
            DiskVerdict verdict;
            string summary;
            if (!Disk.HasSmart)
            {
                verdict = DiskVerdict.Unknown;
                summary = Disk.SmartError ?? "Диск не отдал SMART.";
            }
            else if (Items.Any(i => i.Level == HealthLevel.Bad))
            {
                verdict = DiskVerdict.Bad;
                summary = "Диск может скоро отказать: " + Problems(HealthLevel.Bad) +
                          ". Скопируйте важные данные на другой диск или в облако как можно скорее.";
            }
            else if (Items.Any(i => i.Level == HealthLevel.Warning))
            {
                verdict = DiskVerdict.Warning;
                summary = "Диск работает, но есть тревожные признаки: " + Problems(HealthLevel.Warning) +
                          ". Держите копию важных файлов.";
            }
            else
            {
                verdict = DiskVerdict.Good;
                summary = "Признаков износа и неисправностей нет." + (Life is int life ? $" Ресурс: {life} %." : "");
            }

            string kind = Disk.Bus switch
            {
                "NVMe" => "SSD NVMe",
                "USB" => Ssd ? "SSD USB" : "USB-накопитель",
                "SD" => "Карта памяти",
                _ when Ssd => $"SSD {Disk.Bus}",
                _ when Disk.IsSsd == false || Disk.HasSmart => $"Жёсткий диск {Disk.Bus}",
                _ => Disk.Bus,
            };

            return new DiskHealthReport
            {
                Disk = Disk,
                Verdict = verdict,
                Summary = summary,
                Kind = kind.Trim(),
                LifePercent = Life,
                TemperatureC = Temperature,
                PowerOnHours = Hours,
                WrittenBytes = Written,
                // Сначала плохое, потом тревожное, заметное, хорошее и просто сведения; внутри уровня — как добавлены.
                Items = Items.Select((item, index) => (item, index))
                             .OrderBy(x => LevelOrder(x.item.Level)).ThenBy(x => x.index)
                             .Select(x => x.item).ToList(),
                Rows = Rows,
                RowsHaveThresholds = RowsHaveThresholds,
                Counters = Counters,
            };
        }

        private string Problems(HealthLevel level) =>
            string.Join(", ", Items.Where(i => i.Level == level).Select(i => i.ProblemText).Distinct());

        private static int LevelOrder(HealthLevel level) => level switch
        {
            HealthLevel.Bad => 0,
            HealthLevel.Warning => 1,
            HealthLevel.Notice => 2,
            HealthLevel.Good => 3,
            _ => 4,
        };
    }
}
