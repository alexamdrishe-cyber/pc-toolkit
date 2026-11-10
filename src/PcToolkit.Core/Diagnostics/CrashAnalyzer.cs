using System.Globalization;
using System.Text.RegularExpressions;
using PcToolkit.Core.Disks;

namespace PcToolkit.Core.Diagnostics;

public enum CrashKind
{
    BlueScreen,
    SuddenShutdown,
    HardwareError,
    GpuReset,
    DiskError,
    MemoryTest,
    AppCrash,
}

/// <summary>Один случай: когда и что именно.</summary>
public sealed record CrashOccurrence(DateTime Time, string Text);

/// <summary>Однотипные сбои (например, синие экраны с одним кодом или вылеты одной программы) с объяснением.</summary>
public sealed record CrashGroup
{
    public CrashKind Kind { get; init; }

    public string Key { get; init; } = "";

    public string Title { get; init; } = "";

    public HealthLevel Level { get; init; }

    /// <summary>Что это значит.</summary>
    public string What { get; init; } = "";

    /// <summary>Частые причины.</summary>
    public string? Causes { get; init; }

    /// <summary>Что делать.</summary>
    public string? Advice { get; init; }

    /// <summary>От новых к старым.</summary>
    public IReadOnlyList<CrashOccurrence> Occurrences { get; init; } = Array.Empty<CrashOccurrence>();

    public int Count => Occurrences.Count;

    public DateTime Last => Occurrences.Count > 0 ? Occurrences[0].Time : DateTime.MinValue;
}

/// <summary>Итог разбора журнала за период.</summary>
public sealed record CrashReport
{
    public DateTime From { get; init; }

    public DateTime To { get; init; }

    /// <summary>Самое серьёзное сверху, внутри уровня — самое свежее.</summary>
    public IReadOnlyList<CrashGroup> Groups { get; init; } = Array.Empty<CrashGroup>();

    public HealthLevel Level { get; init; } = HealthLevel.Good;

    /// <summary>«За 30 дней: 2 синих экрана, 1 внезапное выключение…»</summary>
    public string Summary { get; init; } = "";

    /// <summary>Догадки о причине по сочетанию сбоев («похоже на нестабильный разгон…»).</summary>
    public IReadOnlyList<string> Hints { get; init; } = Array.Empty<string>();

    public int BlueScreens { get; init; }

    public int SuddenShutdowns { get; init; }

    public int HardwareErrors { get; init; }

    public int GpuResets { get; init; }

    public int DiskErrors { get; init; }

    public int AppCrashes { get; init; }

    /// <summary>Часть журнала не прочитана.</summary>
    public string? ReadError { get; init; }
}

/// <summary>
/// Разбирает события журнала Windows о сбоях и объясняет их простыми словами: что случилось,
/// почему так бывает и что проверить. Чистая логика без обращения к системе — тестируется на готовых событиях.
/// </summary>
public static partial class CrashAnalyzer
{
    /// <summary>Сколько программ с вылетами показывать отдельно.</summary>
    private const int MaxApps = 8;

    /// <param name="diskNames">Номер диска в Windows → модель: чтобы писать «диск 1 · Samsung 980», а не просто «диск 1».</param>
    public static CrashReport Analyze(IEnumerable<RawEvent> events, DateTime from, DateTime to, string? readError = null,
                                      IReadOnlyDictionary<int, string>? diskNames = null)
    {
        List<RawEvent> list = events.Where(e => e.Time >= from && e.Time <= to).OrderByDescending(e => e.Time).ToList();
        var groups = new Dictionary<string, Builder>();

        // ---- Синие экраны: отчёт WER (1001) после перезагрузки. Kernel-Power 41 с кодом ошибки — запасной источник.
        var blueScreenTimes = new List<DateTime>();
        foreach (RawEvent e in list.Where(e => e.Id == 1001 && e.Provider is "Microsoft-Windows-WER-SystemErrorReporting" or "BugCheck"))
        {
            uint? code = ParseHex(e.DataAt(0)) ?? ParseHex(e.Message);
            if (code is null)
                continue;

            blueScreenTimes.Add(e.Time);
            string dump = e.DataAt(1);
            AddBlueScreen(groups, code.Value, new CrashOccurrence(e.Time,
                $"Код 0x{code.Value:X8}" + (dump.Length > 0 ? $", дамп памяти: {dump}" : "")));
        }

        var suddenTimes = new List<DateTime>();
        foreach (RawEvent e in list.Where(e => e.Id == 41 && e.Provider == "Microsoft-Windows-Kernel-Power"))
        {
            long bugcheck = ParseLong(e.NamedOr("BugcheckCode", 0));
            if (bugcheck != 0)
            {
                if (!blueScreenTimes.Any(t => Math.Abs((t - e.Time).TotalMinutes) <= 15))
                {
                    blueScreenTimes.Add(e.Time);
                    AddBlueScreen(groups, (uint)bugcheck, new CrashOccurrence(e.Time, $"Код 0x{bugcheck:X8} (по записи о перезагрузке)"));
                }

                continue;
            }

            suddenTimes.Add(e.Time);
            bool powerButton = ParseLong(e.NamedOr("PowerButtonTimestamp", 6)) != 0
                               || string.Equals(e.Named.GetValueOrDefault("LongPowerButtonPressDetected"), "true", StringComparison.OrdinalIgnoreCase);
            bool sleep = ParseLong(e.NamedOr("SleepInProgress", 5)) != 0;
            string text = powerButton
                ? "Выключение долгим нажатием кнопки питания — обычно так делают, когда компьютер завис"
                : sleep
                    ? "Питание пропало во время перехода в сон или выхода из него"
                    : "Компьютер выключился или перезагрузился, не завершив работу";
            Get(groups, "sudden", () => SuddenGroup()).Add(new CrashOccurrence(e.Time, text));
        }

        foreach (RawEvent e in list.Where(e => e.Id == 6008 && e.Provider == "EventLog"))
        {
            // То же событие, что Kernel-Power 41, только из старого источника: учитываем, если 41 нет.
            if (suddenTimes.Concat(blueScreenTimes).Any(t => Math.Abs((t - e.Time).TotalMinutes) <= 15))
                continue;

            suddenTimes.Add(e.Time);
            Get(groups, "sudden", () => SuddenGroup())
                .Add(new CrashOccurrence(e.Time, "Предыдущее завершение работы было неожиданным"));
        }

        // ---- Аппаратные ошибки WHEA.
        foreach (RawEvent e in list.Where(e => e.Provider == "Microsoft-Windows-WHEA-Logger"))
        {
            string details = FirstLine(e.Message) ?? $"событие {e.Id}";
            switch (e.Id)
            {
                case 1 or 18:
                    Get(groups, "whea-fatal", () => new Builder(CrashKind.HardwareError, "whea-fatal", "Неисправимая аппаратная ошибка", HealthLevel.Bad,
                            "Процессор, память или шина PCIe сообщили об ошибке, которую нельзя исправить, — после неё Windows падает в синий экран.",
                            "Нестабильный разгон или андервольт процессора (в том числе Curve Optimizer), слишком высокая частота памяти (XMP/EXPO), " +
                            "перегрев, нехватка питания, реже — неисправный процессор, память или материнская плата.",
                            "Сбросьте разгон и андервольт в BIOS, попробуйте без XMP/EXPO, проверьте температуры под нагрузкой (Тесты → Процессор) и блок питания."))
                        .Add(new CrashOccurrence(e.Time, details));
                    break;
                case 19:
                    Get(groups, "whea-cpu", () => new Builder(CrashKind.HardwareError, "whea-cpu", "Исправленные ошибки процессора", HealthLevel.Warning,
                            "Процессор сам нашёл и исправил ошибку (чаще всего в кеше). Компьютер продолжил работать, но так быть не должно.",
                            "Почти всегда — нестабильный андервольт или разгон (Curve Optimizer, PBO, снижение напряжения), реже — перегрев или слабое питание.",
                            "Верните заводские настройки процессора в BIOS и посмотрите, появятся ли ошибки снова. Если появятся — обновите BIOS."))
                        .Add(new CrashOccurrence(e.Time, details));
                    break;
                case 47:
                    Get(groups, "whea-memory", () => new Builder(CrashKind.HardwareError, "whea-memory", "Исправленные ошибки памяти", HealthLevel.Warning,
                            "Оперативная память выдала неверные данные, и система их исправила. Так быть не должно.",
                            "Слишком высокая частота или низкие тайминги памяти (XMP/EXPO, ручной разгон), плохой контакт модуля, неисправный модуль.",
                            "Попробуйте без XMP/EXPO, переставьте модули памяти и прогоните стресс-тест памяти (Тесты → Память)."))
                        .Add(new CrashOccurrence(e.Time, details));
                    break;
                case 17:
                    Get(groups, "whea-pcie", () => new Builder(CrashKind.HardwareError, "whea-pcie", "Исправленные ошибки шины PCIe", HealthLevel.Notice,
                            "Обмен данными с устройством PCIe (видеокарта, NVMe-диск, сетевая карта) прошёл с ошибкой, которую исправили повторной передачей.",
                            "Плохой контакт в слоте или райзер-кабель, режим энергосбережения PCIe (ASPM), иногда — прошивка BIOS. Единичные ошибки часто безвредны.",
                            "Если ошибок много или есть подтормаживания: переустановите видеокарту или диск в слот, обновите BIOS, уберите райзер."))
                        .Add(new CrashOccurrence(e.Time, details));
                    break;
                default:
                    if (e.Level <= 3)
                    {
                        Get(groups, "whea-other", () => new Builder(CrashKind.HardwareError, "whea-other", "Другие аппаратные ошибки", HealthLevel.Notice,
                                "Windows записала сообщение об аппаратной ошибке (WHEA).",
                                "Чаще всего — нестабильный разгон, перегрев или проблема с устройством PCIe.",
                                "Если ошибки повторяются вместе с зависаниями или синими экранами — сбросьте разгон в BIOS и обновите BIOS."))
                            .Add(new CrashOccurrence(e.Time, details));
                    }

                    break;
            }
        }

        // ---- Сброс драйвера видеокарты (TDR).
        var gpuTimes = new List<DateTime>();
        foreach (RawEvent e in list.Where(e => (e.Provider == "Display" && e.Id == 4101) || (e.Provider == "nvlddmkm" && e.Id == 153)))
        {
            if (gpuTimes.Any(t => Math.Abs((t - e.Time).TotalSeconds) <= 60))
                continue;

            gpuTimes.Add(e.Time);
            string driver = e.Provider == "nvlddmkm" ? "nvlddmkm" : e.DataAt(0);
            Get(groups, "gpu", () => new Builder(CrashKind.GpuReset, "gpu", "Драйвер видеокарты перестал отвечать и перезапустился", HealthLevel.Warning,
                    "Видеокарта не ответила вовремя, Windows перезапустила её драйвер: экран мигает или гаснет на пару секунд, игра может вылететь.",
                    "Нестабильный разгон или андервольт видеокарты, перегрев, нехватка питания (переходники, слабый блок питания), сбойный драйвер.",
                    "Уберите разгон видеокарты (MSI Afterburner и т. п.), установите драйвер начисто, проверьте температуру и кабели питания видеокарты."))
                .Add(new CrashOccurrence(e.Time, DriverName(driver)));
        }

        // ---- Ошибки дисков.
        foreach (RawEvent e in list)
        {
            string? diskName = DiskName(e, diskNames);
            string where = diskName is null ? "" : $" ({diskName})";
            switch (e.Provider, e.Id)
            {
                case ("disk", 7):
                    Get(groups, "disk-bad", () => new Builder(CrashKind.DiskError, "disk-bad", "Повреждённые блоки на диске", HealthLevel.Warning,
                            "Windows не смогла прочитать или записать участок диска.",
                            "Износ или повреждение поверхности (HDD), ячеек памяти (SSD), иногда — плохой кабель.",
                            "Откройте «Диски» → «Здоровье» и скопируйте важные файлы."))
                        .Add(new CrashOccurrence(e.Time, "Повреждённый блок" + where));
                    break;
                case ("disk", 51):
                    Get(groups, "disk-paging", () => new Builder(CrashKind.DiskError, "disk-paging", "Ошибки чтения диска при работе с памятью", HealthLevel.Warning,
                            "Windows не смогла прочитать или записать данные файла подкачки или программы.",
                            "Неисправный диск, плохой кабель SATA или разъём, сбой контроллера.",
                            "Проверьте здоровье диска («Диски» → «Здоровье»), переподключите кабель."))
                        .Add(new CrashOccurrence(e.Time, "Ошибка при страничном обмене" + where));
                    break;
                case ("disk", 153):
                    Get(groups, "disk-retry", () => new Builder(CrashKind.DiskError, "disk-retry", "Повторы операций с диском", HealthLevel.Notice,
                            "Диск ответил не с первого раза, и Windows повторила операцию. Данные не потеряны.",
                            "Диск подвисает: износ, перегрев, плохой кабель или режим энергосбережения.",
                            "Если повторов много — проверьте здоровье диска и кабель."))
                        .Add(new CrashOccurrence(e.Time, "Операция повторена" + where));
                    break;
                case ("disk", 11):
                    Get(groups, "disk-controller", () => new Builder(CrashKind.DiskError, "disk-controller", "Ошибки контроллера диска", HealthLevel.Notice,
                            "Windows получила ошибку от контроллера, к которому подключён диск.",
                            "Часто это пустой кардридер или отключённый USB-диск — тогда ошибки безвредны. Иначе — кабель, переходник или сам диск.",
                            "Если ошибка относится к внутреннему диску — проверьте кабель и здоровье диска."))
                        .Add(new CrashOccurrence(e.Time, "Ошибка контроллера" + where));
                    break;
                case (_, 129) when IsStorageDriver(e.Provider):
                    Get(groups, "disk-reset", () => new Builder(CrashKind.DiskError, "disk-reset", "Диск переставал отвечать", HealthLevel.Notice,
                            "Диск или контроллер не ответил вовремя, и Windows сбросила его. В этот момент компьютер мог подвиснуть на несколько секунд.",
                            "Перегрев SSD, устаревшая прошивка диска или драйвер контроллера, плохой кабель, режим энергосбережения.",
                            "Обновите прошивку SSD и драйвер чипсета, проверьте здоровье и температуру диска."))
                        .Add(new CrashOccurrence(e.Time, $"Сброс устройства ({e.Provider})"));
                    break;
                case ("Ntfs" or "Microsoft-Windows-Ntfs", 55 or 98) when e.Level <= 2:
                    Get(groups, "ntfs", () => new Builder(CrashKind.DiskError, "ntfs", "Повреждения файловой системы", HealthLevel.Warning,
                            "Windows нашла ошибки в структуре файловой системы диска: часть файлов или папок может не открываться.",
                            "Внезапные выключения во время записи, неисправный диск или кабель.",
                            "Запустите проверку диска: «Этот компьютер» → свойства диска → «Сервис» → «Проверить». Проверьте здоровье диска."))
                        .Add(new CrashOccurrence(e.Time, FirstLine(e.Message) ?? "Ошибка файловой системы"));
                    break;
            }
        }

        // ---- Проверка памяти Windows.
        foreach (RawEvent e in list.Where(e => e.Provider == "Microsoft-Windows-MemoryDiagnostics-Results"))
        {
            if (e.Id == 1102)
            {
                Get(groups, "memtest-bad", () => new Builder(CrashKind.MemoryTest, "memtest-bad", "Проверка памяти Windows нашла ошибки", HealthLevel.Bad,
                        "Встроенное средство проверки памяти Windows обнаружило аппаратные ошибки оперативной памяти.",
                        "Неисправный модуль памяти, слишком высокая частота (XMP/EXPO), плохой контакт.",
                        "Попробуйте без XMP/EXPO; если ошибки остаются — проверяйте модули по одному и замените неисправный."))
                    .Add(new CrashOccurrence(e.Time, "Найдены ошибки памяти"));
            }
            else if (e.Id == 1101)
            {
                Get(groups, "memtest-ok", () => new Builder(CrashKind.MemoryTest, "memtest-ok", "Проверка памяти Windows: ошибок нет", HealthLevel.Good,
                        "Встроенное средство проверки памяти Windows не нашло ошибок.", null, null))
                    .Add(new CrashOccurrence(e.Time, "Ошибок не найдено"));
            }
        }

        // ---- Вылеты и зависания программ.
        var apps = new Dictionary<string, AppStats>(StringComparer.OrdinalIgnoreCase);
        foreach (RawEvent e in list.Where(e => (e.Provider == "Application Error" && e.Id == 1000) || (e.Provider == "Application Hang" && e.Id == 1002)))
        {
            string app = e.NamedOr("AppName", 0);
            if (app.Length == 0)
                continue;

            if (!apps.TryGetValue(app, out AppStats? stats))
                apps[app] = stats = new AppStats(app);

            if (e.Id == 1002)
            {
                stats.Hangs++;
                stats.Occurrences.Add(new CrashOccurrence(e.Time, "Программа зависла, и Windows её закрыла"));
                continue;
            }

            string module = e.NamedOr("ModuleName", 3);
            // Windows пишет «unknown», когда не смогла определить модуль.
            if (module.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                module = "";
            string exception = NormalizeException(e.NamedOr("ExceptionCode", 6));
            stats.Crashes++;
            stats.Modules[module] = stats.Modules.GetValueOrDefault(module) + 1;
            stats.Occurrences.Add(new CrashOccurrence(e.Time, AppCrashText(app, module, exception)));
        }

        int appCrashTotal = apps.Values.Sum(a => a.Crashes + a.Hangs);
        IEnumerable<CrashGroup> appGroups = apps.Values
            .OrderByDescending(a => a.Crashes + a.Hangs)
            .ThenByDescending(a => a.Occurrences.Max(o => o.Time))
            .Take(MaxApps)
            .Select(AppGroup);

        int hiddenApps = Math.Max(0, apps.Count - MaxApps);

        List<CrashGroup> result = groups.Values.Select(b => b.Build())
            .Concat(appGroups)
            .OrderBy(g => LevelOrder(g.Level))
            .ThenByDescending(g => g.Last)
            .ToList();

        int blueScreens = result.Where(g => g.Kind == CrashKind.BlueScreen).Sum(g => g.Count);
        int sudden = result.Where(g => g.Kind == CrashKind.SuddenShutdown).Sum(g => g.Count);
        int hardware = result.Where(g => g.Kind == CrashKind.HardwareError).Sum(g => g.Count);
        int gpu = result.Where(g => g.Kind == CrashKind.GpuReset).Sum(g => g.Count);
        int disk = result.Where(g => g.Kind == CrashKind.DiskError).Sum(g => g.Count);

        HealthLevel level = result.Count == 0 ? HealthLevel.Good : result.Max(g => g.Level is HealthLevel.Fact ? HealthLevel.Good : g.Level);
        if (level < HealthLevel.Good)
            level = HealthLevel.Good;

        return new CrashReport
        {
            From = from,
            To = to,
            Groups = result,
            Level = level,
            Summary = Summary(from, to, blueScreens, sudden, hardware, gpu, disk, appCrashTotal, apps.Count, hiddenApps),
            Hints = Hints(result, blueScreens, sudden, hardware, gpu, disk),
            BlueScreens = blueScreens,
            SuddenShutdowns = sudden,
            HardwareErrors = hardware,
            GpuResets = gpu,
            DiskErrors = disk,
            AppCrashes = appCrashTotal,
            ReadError = readError,
        };
    }

    // ---------------------------------------------------------------- синие экраны

    private static void AddBlueScreen(Dictionary<string, Builder> groups, uint rawCode, CrashOccurrence occurrence)
    {
        // Коды 0x1000xxxx — те же ошибки, что 0x0000xxxx, только с другими параметрами.
        uint code = rawCode & 0x0FFFFFFF;
        BugCheckInfo info = BugCheck(code);
        Get(groups, $"bsod:{code:X}", () => new Builder(CrashKind.BlueScreen, $"bsod:{code:X}",
                $"Синий экран: {info.Name}", HealthLevel.Warning, ("Windows аварийно остановилась (синий экран). " + info.What).Trim(),
                info.Causes, info.Advice))
            .Add(occurrence);
    }

    private sealed record BugCheckInfo(string Name, string What, string Causes, string Advice);

    /// <summary>Название ошибки синего экрана и что она значит (для разбора дампов).</summary>
    public static (string Name, string What, string Advice) DescribeBugCheck(uint rawCode)
    {
        BugCheckInfo info = BugCheck(rawCode & 0x0FFFFFFF);
        return (info.Name, info.What, info.Advice);
    }

    private const string DriverAdvice = "Обновите драйверы видеокарты, чипсета и сети с сайтов производителей; вспомните, что устанавливали перед первым сбоем.";
    private const string MemoryAdvice = "Прогоните стресс-тест памяти (Тесты → Память), попробуйте без XMP/EXPO и разгона.";
    private const string DiskAdvice = "Откройте «Диски» → «Здоровье», проверьте диск и кабель; запустите проверку системных файлов (sfc /scannow).";
    private const string GpuAdvice = "Установите драйвер видеокарты начисто, уберите разгон видеокарты, проверьте её температуру и питание.";
    private const string CpuAdvice = "Сбросьте разгон и андервольт процессора в BIOS (PBO, Curve Optimizer), обновите BIOS, проверьте температуру под нагрузкой.";

    private static BugCheckInfo BugCheck(uint code) => code switch
    {
        0x0A => new("IRQL_NOT_LESS_OR_EQUAL", "Драйвер обратился к памяти, к которой в этот момент обращаться нельзя.",
                    "Сбойный драйвер (сеть, звук, видео, антивирус) или нестабильная память.", DriverAdvice + " " + MemoryAdvice),
        0x19 or 0xC2 or 0xC5 => new(code switch { 0x19 => "BAD_POOL_HEADER", 0xC2 => "BAD_POOL_CALLER", _ => "DRIVER_CORRUPTED_EXPOOL" },
                    "Драйвер испортил служебную память системы.",
                    "Сбойный драйвер или антивирус, реже — неисправная память.", DriverAdvice + " " + MemoryAdvice),
        0x1A => new("MEMORY_MANAGEMENT", "Сбой управления памятью.",
                    "Очень часто — неисправная или нестабильная оперативная память (XMP/EXPO, разгон), реже драйвер.", MemoryAdvice),
        0x1E => new("KMODE_EXCEPTION_NOT_HANDLED", "Драйвер вызвал ошибку, которую система не смогла обработать.",
                    "Сбойный или несовместимый драйвер, иногда нестабильный разгон.", DriverAdvice),
        0x24 => new("NTFS_FILE_SYSTEM", "Сбой файловой системы NTFS.",
                    "Повреждена файловая система или неисправен диск.", DiskAdvice),
        0x3B => new("SYSTEM_SERVICE_EXCEPTION", "Ошибка при выполнении системного вызова.",
                    "Чаще всего драйвер (нередко видеокарты) или повреждённые системные файлы.", DriverAdvice + " Проверьте системные файлы командой sfc /scannow."),
        0x50 => new("PAGE_FAULT_IN_NONPAGED_AREA", "Обращение к памяти, которой нет.",
                    "Драйвер, антивирус или неисправная/нестабильная оперативная память.", MemoryAdvice + " " + DriverAdvice),
        0x7A => new("KERNEL_DATA_INPAGE_ERROR", "Windows не смогла прочитать нужные данные с диска.",
                    "Неисправный диск, плохой кабель SATA, сбой контроллера или файл подкачки на сбойном диске.", DiskAdvice),
        0x7E => new("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Системный поток (обычно драйвера) вызвал необработанную ошибку.",
                    "Сбойный драйвер — часто видеокарты.", DriverAdvice),
        0x9C => new("MACHINE_CHECK_EXCEPTION", "Процессор сообщил о неисправимой аппаратной ошибке.",
                    "Нестабильный разгон, перегрев, нехватка питания, неисправное железо.", CpuAdvice),
        0x9F => new("DRIVER_POWER_STATE_FAILURE", "Драйвер не справился с переходом в сон или выходом из сна.",
                    "Драйверы сети, Wi-Fi, видео или USB-устройств.", DriverAdvice + " Если сбой при выходе из сна — попробуйте выключить быстрый запуск."),
        0xBE => new("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "Драйвер попытался записать в защищённую память.",
                    "Сбойный драйвер или неисправная память.", DriverAdvice + " " + MemoryAdvice),
        0xC4 => new("DRIVER_VERIFIER_DETECTED_VIOLATION", "Проверка драйверов (Driver Verifier) поймала ошибку драйвера.",
                    "Включена проверка драйверов, и один из драйверов работает неправильно.", "Если вы не включали Driver Verifier сами — выключите его командой verifier /reset и обновите драйверы."),
        0xD1 => new("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Драйвер обратился к недопустимой памяти.",
                    "Очень часто — драйвер сети или Wi-Fi, звука или видеокарты.", DriverAdvice),
        0xEA => new("THREAD_STUCK_IN_DEVICE_DRIVER", "Драйвер видеокарты завис, ожидая ответа видеокарты.",
                    "Видеокарта перестала отвечать: разгон, перегрев, питание или драйвер.", GpuAdvice),
        0xEF => new("CRITICAL_PROCESS_DIED", "Аварийно завершился важный процесс Windows.",
                    "Повреждённые системные файлы, сбойный диск или драйвер.", "Проверьте системные файлы (sfc /scannow) и здоровье диска («Диски» → «Здоровье»)."),
        0xF4 => new("CRITICAL_OBJECT_TERMINATION", "Неожиданно завершился критический процесс Windows.",
                    "Чаще всего — ошибки чтения с диска или его кабель.", DiskAdvice),
        0xF7 => new("DRIVER_OVERRAN_STACK_BUFFER", "Драйвер повредил свою память.",
                    "Сбойный драйвер.", DriverAdvice),
        0x101 => new("CLOCK_WATCHDOG_TIMEOUT", "Одно из ядер процессора перестало отвечать.",
                    "Нестабильный разгон или андервольт процессора, перегрев, иногда устаревший BIOS.", CpuAdvice),
        0x109 => new("CRITICAL_STRUCTURE_CORRUPTION", "Повреждены защищённые структуры ядра Windows.",
                    "Сбойный драйвер, программы обхода защиты (читы, взломы), неисправная память.", DriverAdvice + " " + MemoryAdvice),
        0x10E or 0x113 or 0x119 => new(code switch { 0x10E => "VIDEO_MEMORY_MANAGEMENT_INTERNAL", 0x113 => "VIDEO_DXGKRNL_FATAL_ERROR", _ => "VIDEO_SCHEDULER_INTERNAL_ERROR" },
                    "Сбой графической подсистемы.",
                    "Драйвер видеокарты, разгон видеокарты, иногда сама видеокарта.", GpuAdvice),
        0x116 => new("VIDEO_TDR_FAILURE", "Видеокарта перестала отвечать, и драйвер не смог её перезапустить.",
                    "Разгон или андервольт видеокарты, перегрев, нехватка питания, сбойный драйвер.", GpuAdvice),
        0x124 => new("WHEA_UNCORRECTABLE_ERROR", "Процессор, память или шина PCIe сообщили о неисправимой аппаратной ошибке.",
                    "Нестабильный разгон или андервольт (Curve Optimizer, PBO), слишком высокий XMP/EXPO, перегрев, нехватка питания.",
                    CpuAdvice + " Попробуйте без XMP/EXPO."),
        0x127 or 0x12B => new(code == 0x127 ? "PAGE_NOT_ZERO" : "FAULTY_HARDWARE_CORRUPTED_PAGE", "Данные в оперативной памяти оказались испорчены.",
                    "Почти всегда — неисправная или нестабильная оперативная память.", MemoryAdvice),
        0x133 => new("DPC_WATCHDOG_VIOLATION", "Драйвер слишком долго занимал процессор.",
                    "Драйвер контроллера дисков (SATA/NVMe), устаревшая прошивка SSD или другой драйвер.",
                    "Обновите драйвер чипсета и прошивку SSD; проверьте здоровье диска. " + DriverAdvice),
        0x139 => new("KERNEL_SECURITY_CHECK_FAILURE", "Система обнаружила повреждение важных данных.",
                    "Сбойный драйвер, повреждённые системные файлы или нестабильная память.", DriverAdvice + " " + MemoryAdvice),
        0x144 => new("BUGCODE_USB3_DRIVER", "Сбой драйвера USB 3.",
                    "USB-устройство, хаб или драйвер USB-контроллера.", "Отключите лишние USB-устройства и хабы, обновите драйвер чипсета."),
        0x154 => new("UNEXPECTED_STORE_EXCEPTION", "Сбой в хранилище памяти Windows.",
                    "Часто — проблемы диска или его драйвера, иногда антивирус.", DiskAdvice),
        _ => new($"код 0x{code:X}", "",
                    "Чаще всего — сбойный драйвер или нестабильное железо (разгон, память, перегрев).",
                    DriverAdvice + " Если сбои продолжаются — проверьте память и сбросьте разгон."),
    };

    // ---------------------------------------------------------------- вылеты программ

    private sealed class AppStats(string app)
    {
        public string App { get; } = app;

        public int Crashes { get; set; }

        public int Hangs { get; set; }

        public Dictionary<string, int> Modules { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<CrashOccurrence> Occurrences { get; } = new();
    }

    private static CrashGroup AppGroup(AppStats stats)
    {
        string name = Path.GetFileNameWithoutExtension(stats.App);
        int total = stats.Crashes + stats.Hangs;
        string title = stats.Crashes > 0 && stats.Hangs > 0
            ? $"{name}: вылеты и зависания"
            : stats.Hangs > 0 ? $"{name}: зависания" : $"{name}: вылеты";

        string? mainModule = stats.Modules.OrderByDescending(m => m.Value).Select(m => m.Key).FirstOrDefault();
        ModuleKind kind = mainModule is null ? ModuleKind.Unknown : ClassifyModule(mainModule, stats.App);
        string what = stats.Crashes == 0
            ? "Программа переставала отвечать, и Windows закрывала её."
            : kind switch
            {
                ModuleKind.GpuDriver => $"Программа аварийно закрывалась в драйвере видеокарты ({mainModule}).",
                ModuleKind.DirectX => $"Программа аварийно закрывалась в DirectX ({mainModule}).",
                ModuleKind.System => $"Программа аварийно закрывалась в системной библиотеке ({mainModule}) — обычно это ошибка самой программы.",
                ModuleKind.Self => "Программа аварийно закрывалась из-за ошибки в собственном коде.",
                ModuleKind.DotNet => "Программа на .NET аварийно закрывалась из-за необработанной ошибки.",
                ModuleKind.GameEngine => $"Игра аварийно закрывалась в своём движке ({mainModule}) — обычно это ошибка самой игры, модов или её файлов.",
                _ => mainModule is { Length: > 0 }
                    ? $"Программа аварийно закрывалась в модуле {mainModule}."
                    : "Программа аварийно закрывалась.",
            };
        string causes = kind switch
        {
            ModuleKind.GpuDriver or ModuleKind.DirectX =>
                "Сбойный или устаревший драйвер видеокарты, разгон или андервольт видеокарты, перегрев.",
            ModuleKind.System or ModuleKind.Self or ModuleKind.DotNet or ModuleKind.GameEngine =>
                "Ошибка в самой программе, повреждённые файлы игры, моды и оверлеи. Если вылетают сразу многие программы — нестабильная память или разгон.",
            _ => "Ошибка в программе, повреждённые файлы, оверлеи и сторонние модули, нестабильный разгон.",
        };
        string advice = kind switch
        {
            ModuleKind.GpuDriver or ModuleKind.DirectX =>
                "Установите драйвер видеокарты начисто, уберите разгон видеокарты, проверьте температуру.",
            _ => "Обновите программу, проверьте целостность файлов игры (Steam, Epic и т. п.), отключите оверлеи и моды.",
        };

        return new CrashGroup
        {
            Kind = CrashKind.AppCrash,
            Key = "app:" + stats.App,
            Title = $"{title} — {total} {Formatting.Plural(total, "раз", "раза", "раз")}",
            Level = total >= 5 ? HealthLevel.Warning : HealthLevel.Notice,
            What = what,
            Causes = causes,
            Advice = advice,
            Occurrences = stats.Occurrences.OrderByDescending(o => o.Time).ToList(),
        };
    }

    private enum ModuleKind
    {
        Unknown,
        GpuDriver,
        DirectX,
        System,
        Self,
        DotNet,
        GameEngine,
    }

    private static ModuleKind ClassifyModule(string module, string app)
    {
        string m = module.ToLowerInvariant();
        if (m.StartsWith("nvwgf2um") || m.StartsWith("nvoglv") || m.StartsWith("nvd3dum") || m.StartsWith("nvldumd") || m.StartsWith("nvapi")
            || m.StartsWith("nvgpucomp") || m.StartsWith("atidxx") || m.StartsWith("atiumd") || m.StartsWith("amdxx") || m.StartsWith("amdxc")
            || m.StartsWith("atig6") || m.StartsWith("aticfx") || m.StartsWith("amdvlk") || m.StartsWith("igd10") || m.StartsWith("igd12")
            || m.StartsWith("igxel") || m.StartsWith("ig9icd") || m.StartsWith("ig11") || m.StartsWith("igc64"))
            return ModuleKind.GpuDriver;
        if (m is "d3d11.dll" or "d3d12.dll" or "d3d12core.dll" or "dxgi.dll" or "d3d9.dll" or "vulkan-1.dll" or "opengl32.dll")
            return ModuleKind.DirectX;
        if (m is "ntdll.dll" or "kernelbase.dll" or "kernel32.dll" or "ucrtbase.dll" or "msvcrt.dll" or "user32.dll"
            || m.StartsWith("msvcp") || m.StartsWith("vcruntime"))
            return ModuleKind.System;
        if (m is "clr.dll" or "coreclr.dll" or "clrjit.dll" || m.StartsWith("mscorlib") || m.StartsWith("system.private.corelib"))
            return ModuleKind.DotNet;
        if (m is "unityplayer.dll" or "gameassembly.dll" or "mono-2.0-bdwgc.dll" or "mono.dll" || m.StartsWith("cry") && m.EndsWith(".dll"))
            return ModuleKind.GameEngine;
        if (string.Equals(m, app, StringComparison.OrdinalIgnoreCase))
            return ModuleKind.Self;
        return ModuleKind.Unknown;
    }

    private static string AppCrashText(string app, string module, string exception)
    {
        string where = module.Length == 0 ? "" : ClassifyModule(module, app) switch
        {
            ModuleKind.GpuDriver => $"в драйвере видеокарты ({module})",
            ModuleKind.DirectX => $"в DirectX ({module})",
            ModuleKind.Self => "в коде самой программы",
            ModuleKind.GameEngine => $"в движке игры ({module})",
            _ => $"в модуле {module}",
        };
        string? reason = ExceptionText(exception);
        return "Вылет" + (where.Length > 0 ? " " + where : "") + (exception.Length > 0 ? $", код {exception}" + (reason is null ? "" : $" ({reason})") : "");
    }

    private static string NormalizeException(string code)
    {
        if (code.Length == 0)
            return "";
        string hex = code.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? code[2..] : code;
        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? $"0x{value:x8}" : code;
    }

    private static string? ExceptionText(string code) => code switch
    {
        "0xc0000005" => "обращение к недопустимой памяти",
        "0xc0000409" => "программа сама аварийно завершилась при проверке",
        "0xc0000374" => "повреждение памяти программы",
        "0xc00000fd" => "переполнение стека",
        "0xc000001d" => "процессор не смог выполнить команду — нестабильный разгон или несовместимый процессор",
        "0xc0000096" => "запрещённая команда процессора",
        "0xe0434352" => "необработанная ошибка .NET",
        "0xe06d7363" => "необработанное исключение C++",
        "0x80000003" => "точка останова",
        "0xc0000142" => "не удалось загрузить библиотеку",
        "0x40000015" => "аварийное завершение",
        "0xc0000417" => "недопустимый параметр в библиотеке C",
        _ => null,
    };

    private static string DriverName(string driver) => driver.ToLowerInvariant() switch
    {
        "nvlddmkm" => "Драйвер NVIDIA (nvlddmkm)",
        "amdkmdag" or "amdkmdap" or "amdwddmg" => $"Драйвер AMD ({driver})",
        "igfx" or "igdkmd64" or "igdkmdn64" or "igdkmdnd64" => $"Драйвер Intel ({driver})",
        "" => "Драйвер видеокарты",
        _ => $"Драйвер {driver}",
    };

    // ---------------------------------------------------------------- итог

    private static string Summary(DateTime from, DateTime to, int blueScreens, int sudden, int hardware, int gpu, int disk,
                                  int appCrashes, int appCount, int hiddenApps)
    {
        int days = Math.Max(1, (int)Math.Round((to - from).TotalDays));
        string period = $"За {days} {Formatting.Plural(days, "день", "дня", "дней")}";
        var parts = new List<string>();
        if (blueScreens > 0)
            parts.Add($"{blueScreens} {Formatting.Plural(blueScreens, "синий экран", "синих экрана", "синих экранов")}");
        if (sudden > 0)
            parts.Add($"{sudden} {Formatting.Plural(sudden, "внезапное выключение", "внезапных выключения", "внезапных выключений")}");
        if (hardware > 0)
            parts.Add($"{hardware} {Formatting.Plural(hardware, "аппаратная ошибка", "аппаратные ошибки", "аппаратных ошибок")}");
        if (gpu > 0)
            parts.Add($"{gpu} {Formatting.Plural(gpu, "сброс", "сброса", "сбросов")} драйвера видеокарты");
        if (disk > 0)
            parts.Add($"{disk} {Formatting.Plural(disk, "ошибка", "ошибки", "ошибок")} дисков");
        if (appCrashes > 0)
            parts.Add($"{appCrashes} {Formatting.Plural(appCrashes, "вылет", "вылета", "вылетов")} программ ({appCount} {Formatting.Plural(appCount, "программа", "программы", "программ")}" +
                      (hiddenApps > 0 ? $", показаны {appCount - hiddenApps} самых частых" : "") + ")");

        return parts.Count == 0
            ? $"{period} сбоев не найдено: синих экранов, внезапных выключений и ошибок железа нет."
            : $"{period}: {string.Join(", ", parts)}.";
    }

    private static IReadOnlyList<string> Hints(List<CrashGroup> groups, int blueScreens, int sudden, int hardware, int gpu, int disk)
    {
        var hints = new List<string>();
        bool cpuCodes = groups.Any(g => g.Key is "bsod:124" or "bsod:101" or "bsod:9C" or "whea-cpu" or "whea-fatal");
        bool memoryCodes = groups.Any(g => g.Key is "bsod:1A" or "bsod:50" or "bsod:127" or "bsod:12B" or "whea-memory" or "memtest-bad");
        bool diskCodes = groups.Any(g => g.Key is "bsod:7A" or "bsod:F4" or "bsod:24" or "bsod:154" or "disk-bad" or "disk-paging" or "ntfs");
        bool gpuCodes = gpu > 0 || groups.Any(g => g.Key is "bsod:116" or "bsod:EA" or "bsod:10E" or "bsod:113" or "bsod:119");
        int gpuDriverAppCrashes = groups.Count(g => g.Kind == CrashKind.AppCrash && g.What.Contains("драйвере видеокарты", StringComparison.Ordinal));

        if (cpuCodes)
            hints.Add("Сбои похожи на нестабильную работу процессора: если в BIOS включены разгон, андервольт, PBO или Curve Optimizer — верните заводские настройки.");
        if (memoryCodes)
            hints.Add("Сбои похожи на проблемы с оперативной памятью: попробуйте без XMP/EXPO и прогоните стресс-тест памяти (Тесты → Память).");
        if (diskCodes)
            hints.Add("Сбои похожи на проблемы с диском: проверьте его здоровье на экране «Диски».");
        if (gpuCodes || gpuDriverAppCrashes >= 2)
            hints.Add("Сбои похожи на проблемы видеокарты или её драйвера: установите драйвер начисто и уберите разгон видеокарты.");
        if (sudden > 0 && blueScreens == 0 && hardware == 0)
            hints.Add("Компьютер выключается без синего экрана — так бывает при перегреве, проблемах с блоком питания или розеткой, а также если компьютер завис и его выключили кнопкой.");
        return hints;
    }

    // ---------------------------------------------------------------- вспомогательное

    /// <summary>Записи, между которыми меньше 10 минут, — один случай.</summary>
    private static readonly TimeSpan BurstGap = TimeSpan.FromMinutes(10);

    private sealed class Builder
    {
        private readonly List<CrashOccurrence> _occurrences = new();
        private readonly CrashGroup _template;

        public Builder(CrashKind kind, string key, string title, HealthLevel level, string what, string? causes, string? advice) =>
            _template = new CrashGroup { Kind = kind, Key = key, Title = title, Level = level, What = what, Causes = causes, Advice = advice };

        public void Add(CrashOccurrence occurrence) => _occurrences.Add(occurrence);

        public CrashGroup Build()
        {
            List<CrashOccurrence> sorted = _occurrences.OrderByDescending(o => o.Time).ToList();
            int records = sorted.Count;

            // Ошибки дисков и исправленные аппаратные ошибки приходят пачками: сотни записей за минуту — это один случай.
            if (_template.Kind is CrashKind.DiskError or CrashKind.HardwareError)
                sorted = Cluster(sorted, BurstGap);

            // Синих экранов три и больше — уже не случайность.
            HealthLevel level = _template.Kind == CrashKind.BlueScreen && sorted.Count >= 3 ? HealthLevel.Bad : _template.Level;
            string title = _template.Kind is CrashKind.AppCrash or CrashKind.MemoryTest || (sorted.Count <= 1 && records <= 1)
                ? _template.Title
                : $"{_template.Title} — {sorted.Count} {Formatting.Plural(sorted.Count, "раз", "раза", "раз")}" +
                  (records > sorted.Count ? $" ({Formatting.Number(records)} {Formatting.Plural(records, "запись", "записи", "записей")} в журнале)" : "");
            return _template with { Title = title, Level = level, Occurrences = sorted };
        }

        /// <summary>
        /// Склеивает однотипные записи, идущие подряд с промежутком не больше <paramref name="gap"/>, в один случай
        /// («… — 398 записей за 2 мин»). На входе и выходе — от новых к старым.
        /// </summary>
        private static List<CrashOccurrence> Cluster(List<CrashOccurrence> newestFirst, TimeSpan gap)
        {
            var result = new List<CrashOccurrence>();
            foreach (IGrouping<string, CrashOccurrence> sameText in newestFirst.GroupBy(o => o.Text))
            {
                List<CrashOccurrence> items = sameText.ToList();
                int i = 0;
                while (i < items.Count)
                {
                    int j = i + 1;
                    while (j < items.Count && items[j - 1].Time - items[j].Time <= gap)
                        j++;

                    int count = j - i;
                    CrashOccurrence latest = items[i];
                    TimeSpan span = latest.Time - items[j - 1].Time;
                    result.Add(count == 1
                        ? latest
                        : latest with { Text = $"{latest.Text} — {Formatting.Number(count)} {Formatting.Plural(count, "запись", "записи", "записей")} за {SpanText(span)}" });
                    i = j;
                }
            }

            return result.OrderByDescending(o => o.Time).ToList();
        }

        private static string SpanText(TimeSpan span) => span.TotalMinutes switch
        {
            < 1 => "меньше минуты",
            < 60 => $"{(int)Math.Ceiling(span.TotalMinutes)} мин",
            _ => $"{span.TotalHours:0.#} ч",
        };
    }

    private static Builder SuddenGroup() => new(CrashKind.SuddenShutdown, "sudden", "Внезапные выключения и перезагрузки", HealthLevel.Warning,
        "Компьютер выключился или перезагрузился, не завершив работу Windows, и без синего экрана.",
        "Перегрев (процессора или видеокарты), блок питания не тянет нагрузку или неисправен, проблемы с розеткой или удлинителем, " +
        "зависание, после которого компьютер выключили кнопкой, нестабильный разгон.",
        "Проверьте температуры под нагрузкой (Мониторинг, Тесты), почистите пыль. Если выключается в играх — скорее всего блок питания. " +
        "Если вы сами выключали кнопкой из-за зависания — ищите причину зависаний.");

    private static Builder Get(Dictionary<string, Builder> groups, string key, Func<Builder> create)
    {
        if (!groups.TryGetValue(key, out Builder? builder))
            groups[key] = builder = create();
        return builder;
    }

    private static int LevelOrder(HealthLevel level) => level switch
    {
        HealthLevel.Bad => 0,
        HealthLevel.Warning => 1,
        HealthLevel.Notice => 2,
        HealthLevel.Good => 3,
        _ => 4,
    };

    private static bool IsStorageDriver(string provider)
    {
        string p = provider.ToLowerInvariant();
        return p.Contains("stor") || p.Contains("nvme") || p.Contains("ahci") || p.Contains("iastor") || p.Contains("raid");
    }

    private static string? DiskName(RawEvent e, IReadOnlyDictionary<int, string>? diskNames)
    {
        foreach (string value in e.Data.Append(e.Message ?? ""))
        {
            Match match = HarddiskRegex().Match(value);
            if (!match.Success)
                continue;

            int number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return diskNames is not null && diskNames.TryGetValue(number, out string? model)
                ? $"диск {number} · {model}"
                : $"диск {number}";
        }

        return null;
    }

    private static uint? ParseHex(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        Match match = HexRegex().Match(text);
        return match.Success && uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
            ? value
            : null;
    }

    private static long ParseLong(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex) ? hex : 0;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > 220 ? line[..220] + "…" : line;
    }

    [GeneratedRegex(@"0x([0-9a-fA-F]{1,8})\b")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"Harddisk(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex HarddiskRegex();
}
