using PcToolkit.Core.Disks;
using PcToolkit.Core.Inventory;

namespace PcToolkit.Core.Diagnostics;

/// <summary>Уведомление в трее.</summary>
public sealed record HealthNotification(string Title, string Text, string Area);

/// <summary>
/// Превращает результаты проверок дисков, журнала сбоев, драйверов и памяти в пункты «Проверки системы» на главной
/// и в тексты уведомлений. Чистая логика — тестируется без Windows.
/// </summary>
public static class HealthFindings
{
    public static IEnumerable<Finding> FromDisks(IEnumerable<DiskHealthReport> reports)
    {
        foreach (DiskHealthReport report in reports)
        {
            if (report.Verdict == DiskVerdict.Bad)
            {
                yield return new Finding(FindingSeverity.Critical, $"Диск {report.Title} может скоро отказать", report.Summary)
                {
                    Area = FindingAreas.Disks,
                };
            }
            else if (report.Verdict == DiskVerdict.Warning)
            {
                yield return new Finding(FindingSeverity.Warning, $"Тревожные признаки у диска {report.Title}", report.Summary)
                {
                    Area = FindingAreas.Disks,
                };
            }
        }
    }

    /// <summary>Сбои за неделю: синие экраны, внезапные выключения, аппаратные ошибки, сбросы видеодрайвера, ошибки дисков.</summary>
    public static IEnumerable<Finding> FromCrashes(CrashReport report)
    {
        string? hint = report.Hints.FirstOrDefault();
        string period = PeriodText(report);

        if (report.BlueScreens > 0)
        {
            string names = string.Join(", ", report.Groups.Where(g => g.Kind == CrashKind.BlueScreen)
                                                    .Select(g => g.Title.Replace("Синий экран: ", "").Split(" — ")[0]));
            yield return new Finding(report.BlueScreens >= 3 ? FindingSeverity.Critical : FindingSeverity.Warning,
                $"{report.BlueScreens} {Formatting.Plural(report.BlueScreens, "синий экран", "синих экрана", "синих экранов")} {period}",
                $"{names}. {hint ?? "Подробности и частые причины — на экране «Сбои»."}")
            {
                Area = FindingAreas.Crashes,
            };
        }

        if (report.SuddenShutdowns > 0)
        {
            yield return new Finding(FindingSeverity.Warning,
                $"Компьютер {report.SuddenShutdowns} {Formatting.Plural(report.SuddenShutdowns, "раз", "раза", "раз")} выключался внезапно {period}",
                "Без синего экрана: так бывает при перегреве, проблемах с питанием или если компьютер завис и его выключили кнопкой.")
            {
                Area = FindingAreas.Crashes,
            };
        }

        foreach (CrashGroup group in report.Groups.Where(g => g.Key is "whea-fatal" or "memtest-bad" or "whea-cpu" or "whea-memory"))
        {
            yield return new Finding(group.Level == HealthLevel.Bad ? FindingSeverity.Critical : FindingSeverity.Warning,
                group.Title, group.Advice ?? group.What)
            {
                Area = FindingAreas.Crashes,
            };
        }

        if (report.GpuResets > 0)
        {
            yield return new Finding(FindingSeverity.Warning,
                $"Драйвер видеокарты перезапускался {report.GpuResets} {Formatting.Plural(report.GpuResets, "раз", "раза", "раз")} {period}",
                "Экран мигал или гас, игры могли вылетать. Частые причины — разгон видеокарты, перегрев, питание или сбойный драйвер.")
            {
                Area = FindingAreas.Crashes,
            };
        }

        foreach (CrashGroup group in report.Groups.Where(g => g.Key is "disk-bad" or "disk-paging" or "ntfs"))
        {
            yield return new Finding(FindingSeverity.Warning, group.Title, group.What + " " + group.Advice)
            {
                Area = FindingAreas.Crashes,
            };
        }
    }

    public static IEnumerable<Finding> FromDrivers(DriverReport report)
    {
        foreach (DriverItem item in report.Problems.Where(p => p.Level == HealthLevel.Bad))
        {
            yield return new Finding(FindingSeverity.Critical, item.Title, item.Details) { Area = FindingAreas.Drivers };
        }

        foreach (DriverItem item in report.Problems.Where(p => p.Action == DriverActions.ReinstallPawnIo))
        {
            yield return new Finding(FindingSeverity.Warning, "Драйвер датчиков PawnIO не работает",
                "Поэтому температура, частоты и мощность процессора недоступны. Переустановить его можно на экране «Драйверы».")
            {
                Area = FindingAreas.Drivers,
            };
        }

        List<DriverItem> broken = report.Problems.Where(p => p.Level == HealthLevel.Warning && p.Action is null).ToList();
        if (broken.Count > 0)
        {
            string names = string.Join(", ", broken.Take(3).Select(p => p.Title.Split(':')[0]));
            yield return new Finding(FindingSeverity.Warning,
                $"{broken.Count} {Formatting.Plural(broken.Count, "устройство", "устройства", "устройств")} без драйвера или с ошибкой",
                names + (broken.Count > 3 ? " и другие." : "."))
            {
                Area = FindingAreas.Drivers,
            };
        }

        foreach (DriverItem item in report.Drivers.Where(d => d.Level == HealthLevel.Notice))
        {
            yield return new Finding(FindingSeverity.Info,
                item.Category == "Видеокарта" ? "Драйвер видеокарты давно не обновлялся" : item.Title,
                item.Advice ?? item.Details)
            {
                Area = FindingAreas.Drivers,
                IsTip = true,
            };
        }
    }

    /// <summary>Память на базовой частоте (не включён XMP/EXPO) или один модуль (одноканальный режим).</summary>
    /// <summary>Мало места на внутренних дисках (внешние в фоновой проверке не читаются, чтобы их не будить).</summary>
    public static IEnumerable<Finding> FromDriveSpace(IEnumerable<Storage.DriveSpaceInfo> drives)
    {
        foreach (Storage.DriveSpaceInfo drive in drives)
        {
            if (drive.TotalBytes <= 0 || drive.FreePercent >= Thresholds.DiskFreeWarningPercent)
                continue;

            yield return new Finding(FindingSeverity.Warning,
                $"Мало места на диске {drive.Letter}: свободно {drive.FreePercent:0} %",
                $"Свободно {Formatting.Bytes((ulong)drive.AvailableBytes)} из {Formatting.Bytes((ulong)drive.TotalBytes)}. " +
                "Windows и игры работают хуже, когда диск почти заполнен.")
            {
                Area = FindingAreas.Space,
            };
        }
    }

    public static IEnumerable<Finding> FromMemory(MemoryConfig? config)
    {
        if (config is not { SpeedMts: > 0 } c)
            yield break;

        bool baseSpeed = (c.Type == "DDR4" && c.SpeedMts <= 2666) || (c.Type == "DDR5" && c.SpeedMts <= 4800);
        if (baseSpeed)
        {
            yield return new Finding(FindingSeverity.Info, $"Память работает на базовой частоте {c.SpeedMts} МТ/с",
                "Если на модулях указана большая частота (например, " + (c.Type == "DDR4" ? "3200 или 3600" : "6000") +
                "), включите профиль XMP/EXPO в BIOS — игры станут работать быстрее. PC Toolkit сам BIOS не меняет.")
            {
                Area = FindingAreas.Memory,
                IsTip = true,
            };
        }

        if (c.Modules == 1)
        {
            yield return new Finding(FindingSeverity.Info, "Стоит один модуль памяти",
                "С одним модулем память работает в одноканальном режиме — почти вдвое медленнее. Два одинаковых модуля дают заметный прирост в играх.")
            {
                Area = FindingAreas.Memory,
                IsTip = true,
            };
        }
    }

    /// <summary>
    /// Уведомление о серьёзных сбоях, случившихся после <paramref name="since"/>: синие экраны, внезапные выключения,
    /// аппаратные ошибки, сбросы драйвера видеокарты. null — сообщать не о чем.
    /// </summary>
    public static HealthNotification? CrashNotification(CrashReport report, DateTime since)
    {
        var parts = new List<string>();
        foreach (CrashGroup group in report.Groups)
        {
            int fresh = group.Occurrences.Count(o => o.Time > since);
            if (fresh == 0)
                continue;

            string? part = group.Kind switch
            {
                CrashKind.BlueScreen => "синий экран " + group.Title.Replace("Синий экран: ", "").Split(" — ")[0],
                CrashKind.SuddenShutdown => fresh == 1 ? "внезапное выключение" : $"{fresh} внезапных выключения",
                CrashKind.GpuReset => "перезапуск драйвера видеокарты",
                CrashKind.HardwareError when group.Level >= HealthLevel.Warning => group.Title.Split(" — ")[0].ToLowerInvariant(),
                CrashKind.MemoryTest when group.Level == HealthLevel.Bad => "ошибки памяти",
                CrashKind.DiskError when group.Level >= HealthLevel.Warning => group.Title.Split(" — ")[0].ToLowerInvariant(),
                _ => null,
            };
            if (part is not null && !parts.Contains(part))
                parts.Add(part);
        }

        if (parts.Count == 0)
            return null;

        string text = char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..] + ". Подробности и возможные причины — раздел «Сбои».";
        return new HealthNotification("PC Toolkit: были сбои", text, FindingAreas.Crashes);
    }

    public static HealthNotification DiskNotification(DiskHealthNews news) =>
        new(news.Verdict == DiskVerdict.Bad ? $"Диск {news.Disk} может отказать" : $"Диск {news.Disk}: тревожные признаки",
            news.Text, FindingAreas.Disks);

    private static string PeriodText(CrashReport report)
    {
        int days = Math.Max(1, (int)Math.Round((report.To - report.From).TotalDays));
        return days == 7 ? "за неделю" : $"за {days} {Formatting.Plural(days, "день", "дня", "дней")}";
    }
}
