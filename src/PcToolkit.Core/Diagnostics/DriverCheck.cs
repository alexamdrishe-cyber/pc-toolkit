using System.Globalization;
using System.Management;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Diagnostics;

/// <summary>Устройство из Win32_PnPEntity.</summary>
public sealed record PnpDeviceRaw(string Name, string PnpClass, int ErrorCode, string DeviceId, string Manufacturer, string Service);

/// <summary>Драйвер из Win32_PnPSignedDriver.</summary>
public sealed record SignedDriverRaw(string DeviceName, string DeviceClass, string DeviceId, string Provider, string Manufacturer,
                                     string Version, DateTime? Date);

/// <summary>Строка экрана «Драйверы»: проблемное устройство или важный драйвер.</summary>
/// <param name="Advice">Что делать — для проблем.</param>
public sealed record DriverItem(string Category, string Title, string Details, HealthLevel Level, string? Advice = null,
                                string? LinkTitle = null, string? LinkUrl = null);

public sealed record DriverReport
{
    /// <summary>Устройства без драйвера или с ошибкой и стандартный видеоадаптер вместо драйвера видеокарты.</summary>
    public IReadOnlyList<DriverItem> Problems { get; init; } = Array.Empty<DriverItem>();

    /// <summary>Драйверы, от которых больше всего зависит работа: видеокарта, чипсет, сеть, звук, диски.</summary>
    public IReadOnlyList<DriverItem> Drivers { get; init; } = Array.Empty<DriverItem>();

    public HealthLevel Level { get; init; } = HealthLevel.Good;

    public string Summary { get; init; } = "";

    public string? Error { get; init; }
}

/// <summary>Читает устройства и драйверы через WMI. Только чтение, права администратора не нужны.</summary>
public static class DriverReader
{
    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static (List<PnpDeviceRaw> Problems, List<PnpDeviceRaw> Displays, List<SignedDriverRaw> Drivers, string? Error) Read()
    {
        var problems = new List<PnpDeviceRaw>();
        var displays = new List<PnpDeviceRaw>();
        var drivers = new List<SignedDriverRaw>();
        var errors = new List<string>();
        if (!OperatingSystem.IsWindows())
            return (problems, displays, drivers, null);

        try
        {
            problems.AddRange(QueryDevices("SELECT Name, PNPClass, ClassGuid, ConfigManagerErrorCode, PNPDeviceID, Manufacturer, Service " +
                                           "FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"));
            displays.AddRange(QueryDevices("SELECT Name, PNPClass, ClassGuid, ConfigManagerErrorCode, PNPDeviceID, Manufacturer, Service " +
                                           $"FROM Win32_PnPEntity WHERE ClassGuid = '{DisplayClassGuid}'"));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warn("Не удалось получить список устройств.", ex);
            errors.Add("список устройств: " + ex.Message);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, DeviceClass, DeviceID, DriverProviderName, Manufacturer, DriverVersion, DriverDate FROM Win32_PnPSignedDriver " +
                "WHERE DeviceClass = 'DISPLAY' OR DeviceClass = 'NET' OR DeviceClass = 'MEDIA' OR DeviceClass = 'SYSTEM' " +
                "OR DeviceClass = 'HDC' OR DeviceClass = 'SCSIADAPTER'");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    DateTime? date = null;
                    if (item["DriverDate"] is string cim && cim.Length >= 8)
                    {
                        try
                        {
                            date = ManagementDateTimeConverter.ToDateTime(cim).Date;
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // Дата в неожиданном формате — покажем драйвер без неё.
                        }
                    }

                    drivers.Add(new SignedDriverRaw(
                        Text(item["DeviceName"]), Text(item["DeviceClass"]), Text(item["DeviceID"]), Text(item["DriverProviderName"]),
                        Text(item["Manufacturer"]), Text(item["DriverVersion"]), date));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warn("Не удалось получить список драйверов.", ex);
            errors.Add("список драйверов: " + ex.Message);
        }

        return (problems, displays, drivers, errors.Count == 0 ? null : "Не всё удалось прочитать: " + string.Join("; ", errors) + ".");
    }

    private static IEnumerable<PnpDeviceRaw> QueryDevices(string query)
    {
        using var searcher = new ManagementObjectSearcher(query);
        foreach (ManagementBaseObject item in searcher.Get())
        {
            using (item)
            {
                int code = item["ConfigManagerErrorCode"] is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
                string pnpClass = Text(item["PNPClass"]);
                if (pnpClass.Length == 0 && string.Equals(Text(item["ClassGuid"]), DisplayClassGuid, StringComparison.OrdinalIgnoreCase))
                    pnpClass = "Display";
                yield return new PnpDeviceRaw(Text(item["Name"]), pnpClass, code, Text(item["PNPDeviceID"]), Text(item["Manufacturer"]),
                                              Text(item["Service"]));
            }
        }
    }

    private static string Text(object? value) => value?.ToString()?.Trim() ?? "";
}

/// <summary>
/// Оценивает драйверы: устройства без драйвера или с ошибкой (с объяснением кода из «Диспетчера устройств»),
/// стандартный видеоадаптер вместо драйвера видеокарты, возраст драйверов видеокарты и чипсета.
/// Ничего не устанавливает — только подсказывает, где взять драйвер.
/// </summary>
public static class DriverAnalyzer
{
    public const string NvidiaUrl = "https://www.nvidia.com/ru-ru/drivers/";
    public const string AmdUrl = "https://www.amd.com/en/support/download/drivers.html";
    public const string IntelUrl = "https://www.intel.com/content/www/us/en/support/detect.html";

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static DriverReport Analyze(IReadOnlyList<PnpDeviceRaw> problemDevices, IReadOnlyList<PnpDeviceRaw> displays,
                                       IReadOnlyList<SignedDriverRaw> drivers, DateTime now, string? error = null)
    {
        var problems = new List<DriverItem>();

        // Стандартный видеоадаптер: драйвер видеокарты не установлен — игры не пойдут или будут очень медленными.
        foreach (PnpDeviceRaw display in displays.Where(d => string.Equals(d.Service, "BasicDisplay", StringComparison.OrdinalIgnoreCase)))
        {
            (string vendor, string? url) = GpuVendor(display.DeviceId, display.Manufacturer);
            problems.Add(new DriverItem("Видеокарта", "Драйвер видеокарты не установлен",
                $"Сейчас работает «{display.Name}» — стандартный драйвер Windows. Он только выводит картинку: игры не запустятся " +
                "или будут работать очень медленно, частота монитора может быть ограничена.",
                HealthLevel.Bad,
                url is null ? "Установите драйвер видеокарты с сайта её производителя." : $"Скачайте драйвер {vendor} с официального сайта и установите его.",
                url is null ? null : $"Драйверы {vendor}", url));
        }

        foreach (PnpDeviceRaw device in problemDevices.OrderBy(d => d.ErrorCode == 22 ? 1 : 0).ThenBy(d => d.Name))
        {
            // 45 — устройство сейчас не подключено (флешка, телефон): это не проблема.
            if (device.ErrorCode == 45)
                continue;

            (string title, string explanation, string advice, HealthLevel deviceLevel) = ErrorCode(device.ErrorCode);
            string name = device.Name.Length > 0 ? device.Name : "Неизвестное устройство";
            (string vendor, string? url) = device.PnpClass.Equals("Display", StringComparison.OrdinalIgnoreCase)
                ? GpuVendor(device.DeviceId, device.Manufacturer)
                : ("", null);
            problems.Add(new DriverItem(ClassName(device.PnpClass), $"{name}: {title}",
                $"{explanation} Код {device.ErrorCode} в «Диспетчере устройств»." + (device.DeviceId.Length > 0 ? $" ИД оборудования: {ShortId(device.DeviceId)}." : ""),
                deviceLevel, advice, url is null ? null : $"Драйверы {vendor}", url));
        }

        var items = new List<DriverItem>();
        AddGpuDrivers(items, drivers, now);
        AddChipset(items, drivers, now);
        AddCategory(items, drivers, now, "NET", "Сеть", d => !IsVirtual(d.DeviceId, d.DeviceName));
        AddCategory(items, drivers, now, "MEDIA", "Звук", d => !IsVirtual(d.DeviceId, d.DeviceName)
                                                                && !d.DeviceName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                                                                && !d.DeviceName.Contains("AMD High Definition", StringComparison.OrdinalIgnoreCase));
        AddCategory(items, drivers, now, "SCSIADAPTER", "Контроллер дисков", d => !IsVirtual(d.DeviceId, d.DeviceName));
        AddCategory(items, drivers, now, "HDC", "Контроллер дисков", d => !IsVirtual(d.DeviceId, d.DeviceName));

        HealthLevel level = problems.Concat(items).Select(i => i.Level).DefaultIfEmpty(HealthLevel.Good).Max();
        if (level < HealthLevel.Good)
            level = HealthLevel.Good;

        int serious = problems.Count(p => p.Level >= HealthLevel.Warning);
        string summary = problems.Count == 0
            ? "Все устройства работают с драйверами: проблемных устройств в «Диспетчере устройств» нет."
            : serious == 0
                ? $"{problems.Count} {Formatting.Plural(problems.Count, "устройство отключено", "устройства отключены", "устройств отключено")}, остальные работают с драйверами."
                : $"{serious} {Formatting.Plural(serious, "устройство", "устройства", "устройств")} без драйвера или с ошибкой.";
        if (items.Any(i => i.Category == "Видеокарта" && i.Level == HealthLevel.Notice))
            summary += " Драйвер видеокарты давно не обновлялся.";

        return new DriverReport
        {
            Problems = problems.OrderByDescending(p => p.Level).ToList(),
            Drivers = items,
            Level = level,
            Summary = summary,
            Error = error,
        };
    }

    private static void AddGpuDrivers(List<DriverItem> items, IReadOnlyList<SignedDriverRaw> drivers, DateTime now)
    {
        foreach (SignedDriverRaw gpu in drivers.Where(d => d.DeviceClass.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)
                                                           && IsPhysical(d.DeviceId)))
        {
            (string vendor, string? url) = GpuVendor(gpu.DeviceId, gpu.Manufacturer + " " + gpu.Provider);
            string version = vendor == "NVIDIA" && NvidiaVersion(gpu.Version) is { } nvidia ? $"{nvidia} ({gpu.Version})" : gpu.Version;
            int? ageDays = gpu.Date is { } date ? (int)(now - date).TotalDays : null;
            bool microsoft = gpu.Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
            HealthLevel level = ageDays > 365 ? HealthLevel.Notice : HealthLevel.Good;
            string? advice = ageDays > 365
                ? "Драйвер старше года. Игры, вышедшие позже, могут работать хуже или с ошибками — обновите драйвер с сайта производителя."
                : microsoft ? "Драйвер установлен из Центра обновления Windows. Для игр лучше поставить свежий драйвер с сайта производителя." : null;
            if (microsoft && level == HealthLevel.Good)
                level = HealthLevel.Notice;

            items.Add(new DriverItem("Видеокарта", gpu.DeviceName,
                $"Драйвер {version}" + DateText(gpu.Date, now) + (gpu.Provider.Length > 0 ? $" · {gpu.Provider}" : ""),
                level, advice, url is null ? null : $"Драйверы {vendor}", url));
        }
    }

    private static void AddChipset(List<DriverItem> items, IReadOnlyList<SignedDriverRaw> drivers, DateTime now)
    {
        List<SignedDriverRaw> system = drivers.Where(d => d.DeviceClass.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) && IsPhysical(d.DeviceId)).ToList();
        bool amdBoard = system.Any(d => IsAmd(d.Manufacturer) || d.DeviceId.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase));
        bool intelBoard = system.Any(d => d.DeviceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase));
        if (amdBoard)
        {
            SignedDriverRaw? newest = system.Where(d => IsAmd(d.Provider)).OrderByDescending(d => d.Date).FirstOrDefault();
            items.Add(newest is null
                ? new DriverItem("Чипсет", "Драйверы чипсета AMD не установлены",
                    "Устройства материнской платы работают на стандартных драйверах Windows.", HealthLevel.Notice,
                    "Для процессоров Ryzen стоит установить AMD Chipset Drivers: с ними правильнее работают план питания и распределение " +
                    "нагрузки по ядрам (особенно у процессоров X3D).", "Драйверы AMD", AmdUrl)
                : new DriverItem("Чипсет", "Драйверы чипсета AMD",
                    $"Установлены{DateText(newest.Date, now)} (самый новый — «{newest.DeviceName}», версия {newest.Version}).", HealthLevel.Good,
                    null, "Драйверы AMD", AmdUrl));
        }
        else if (intelBoard)
        {
            SignedDriverRaw? newest = system.Where(d => d.Provider.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                                            .OrderByDescending(d => d.Date).FirstOrDefault();
            items.Add(new DriverItem("Чипсет", newest is null ? "Чипсет Intel: стандартные драйверы Windows" : "Драйверы чипсета Intel",
                newest is null
                    ? "Для чипсетов Intel этого обычно достаточно."
                    : $"Установлены{DateText(newest.Date, now)} (самый новый — «{newest.DeviceName}», версия {newest.Version}).",
                HealthLevel.Good, null, "Помощник по драйверам Intel", IntelUrl));
        }
    }

    private static void AddCategory(List<DriverItem> items, IReadOnlyList<SignedDriverRaw> drivers, DateTime now, string deviceClass,
                                    string category, Func<SignedDriverRaw, bool> filter)
    {
        foreach (SignedDriverRaw driver in drivers.Where(d => d.DeviceClass.Equals(deviceClass, StringComparison.OrdinalIgnoreCase)
                                                              && IsPhysical(d.DeviceId) && filter(d))
                                                  .GroupBy(d => d.DeviceName).Select(g => g.First()))
        {
            items.Add(new DriverItem(category, driver.DeviceName,
                $"Драйвер {driver.Version}" + DateText(driver.Date, now) + (driver.Provider.Length > 0 ? $" · {driver.Provider}" : ""),
                HealthLevel.Fact));
        }
    }

    /// <summary>
    /// Версия драйвера NVIDIA, как её пишет сама NVIDIA: 32.0.15.6094 → 560.94
    /// (последние пять цифр двух последних частей версии Windows).
    /// </summary>
    public static string? NvidiaVersion(string windowsVersion)
    {
        string[] parts = windowsVersion.Split('.');
        if (parts.Length != 4)
            return null;

        string digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5 || !digits.All(char.IsAsciiDigit))
            return null;

        string last = digits[^5..];
        return $"{int.Parse(last[..3], CultureInfo.InvariantCulture)}.{last[3..]}";
    }

    private static (string Title, string Explanation, string Advice, HealthLevel Level) ErrorCode(int code) => code switch
    {
        1 => ("устройство настроено неправильно", "Windows не смогла настроить устройство.", "Обновите или переустановите драйвер устройства.", HealthLevel.Warning),
        3 => ("драйвер повреждён", "Драйвер устройства повреждён, или Windows не хватило памяти, чтобы его загрузить.",
              "Переустановите драйвер устройства с сайта производителя.", HealthLevel.Warning),
        10 => ("устройство не запускается", "Драйвер установлен, но устройство не запустилось.",
               "Обновите драйвер с сайта производителя; если это USB-устройство — переподключите его в другой разъём.", HealthLevel.Warning),
        12 => ("не хватает ресурсов", "Устройству не хватило свободных ресурсов (прерываний, адресов) — оно конфликтует с другим.",
               "Отключите лишние устройства или обновите BIOS.", HealthLevel.Warning),
        14 => ("нужна перезагрузка", "Устройство заработает после перезагрузки компьютера.", "Перезагрузите компьютер.", HealthLevel.Notice),
        18 => ("нужно переустановить драйвер", "Драйвер устройства нужно установить заново.", "Переустановите драйвер устройства.", HealthLevel.Warning),
        19 => ("повреждены настройки в реестре", "Сведения об устройстве в реестре повреждены или неполны.",
               "Удалите устройство в «Диспетчере устройств» и обновите конфигурацию оборудования — Windows установит его заново.", HealthLevel.Warning),
        21 => ("удаляется", "Windows удаляет это устройство.", "Перезагрузите компьютер.", HealthLevel.Notice),
        22 => ("отключено", "Устройство отключено вручную — вами или какой-то программой.",
               "Если устройство нужно — включите его в «Диспетчере устройств» (правой кнопкой → «Включить устройство»).", HealthLevel.Notice),
        24 => ("работает неправильно или отсутствует", "Устройство не найдено или работает неправильно; часто это значит, что для него нет драйвера.",
               "Установите драйвер устройства с сайта производителя.", HealthLevel.Warning),
        28 => ("драйвер не установлен", "Для этого устройства не установлен драйвер — оно не работает.",
               "Установите драйвер с сайта производителя устройства или материнской платы (раздел поддержки вашей модели).", HealthLevel.Warning),
        29 => ("отключено в BIOS", "Устройство выключено в настройках BIOS/UEFI.", "Если устройство нужно — включите его в BIOS.", HealthLevel.Notice),
        31 => ("драйвер не загружается", "Устройство работает неправильно: Windows не смогла загрузить для него драйвер.",
               "Обновите драйвер с сайта производителя.", HealthLevel.Warning),
        32 => ("служба драйвера отключена", "Служба, которая нужна драйверу устройства, отключена.",
               "Переустановите драйвер или включите его службу (если отключали её сами — включите обратно).", HealthLevel.Warning),
        37 => ("драйвер не запускается", "Windows не смогла запустить драйвер этого устройства.", "Переустановите драйвер с сайта производителя.", HealthLevel.Warning),
        38 => ("старый драйвер ещё в памяти", "Предыдущая копия драйвера ещё загружена.", "Перезагрузите компьютер.", HealthLevel.Notice),
        39 => ("драйвер повреждён или отсутствует", "Windows не смогла загрузить драйвер: он повреждён или удалён.",
               "Переустановите драйвер с сайта производителя.", HealthLevel.Warning),
        40 => ("повреждены сведения о драйвере", "Сведения о службе драйвера в реестре отсутствуют или повреждены.",
               "Переустановите драйвер.", HealthLevel.Warning),
        41 => ("устройство не найдено", "Драйвер загружен, но само устройство Windows не нашла.",
               "Проверьте подключение устройства; если оно отключено физически — удалите его из «Диспетчера устройств».", HealthLevel.Notice),
        43 => ("устройство сообщило о сбое", "Windows остановила устройство, потому что оно сообщило о неисправности.",
               "Переподключите устройство (USB — в другой разъём), обновите драйвер. Если это видеокарта — переустановите драйвер начисто " +
               "и проверьте питание видеокарты.", HealthLevel.Warning),
        44 => ("остановлено программой", "Программа или служба отключила это устройство.", "Перезагрузите компьютер.", HealthLevel.Notice),
        47 => ("подготовлено к извлечению", "Устройство подготовлено к безопасному извлечению.", "Отключите и снова подключите его.", HealthLevel.Notice),
        48 => ("драйвер заблокирован", "Драйвер заблокирован, потому что с ним известны проблемы.",
               "Установите более новую версию драйвера с сайта производителя.", HealthLevel.Warning),
        52 => ("драйвер без подписи", "Windows не может проверить цифровую подпись драйвера.",
               "Установите подписанный драйвер с официального сайта производителя.", HealthLevel.Warning),
        _ => ("ошибка", "Устройство работает с ошибкой.", "Обновите или переустановите драйвер устройства.", HealthLevel.Warning),
    };

    private static (string Vendor, string? Url) GpuVendor(string deviceId, string manufacturer)
    {
        string text = deviceId + " " + manufacturer;
        if (text.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) || text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return ("NVIDIA", NvidiaUrl);
        if (text.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || IsAmd(text))
            return ("AMD", AmdUrl);
        if (text.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) || text.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return ("Intel", IntelUrl);
        return ("", null);
    }

    private static bool IsAmd(string text) =>
        text.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || text.Contains("AMD", StringComparison.Ordinal);

    private static bool IsPhysical(string deviceId) =>
        deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("HDAUDIO\\", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsVirtual(string deviceId, string name) =>
        !IsPhysical(deviceId)
        || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Виртуальн", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);

    private static string ClassName(string pnpClass) => pnpClass.ToLowerInvariant() switch
    {
        "display" => "Видеокарта",
        "net" => "Сеть",
        "media" => "Звук",
        "usb" => "USB",
        "bluetooth" => "Bluetooth",
        "hdc" or "scsiadapter" => "Контроллер дисков",
        "system" => "Системное устройство",
        "camera" or "image" => "Камера",
        "hidclass" or "keyboard" or "mouse" => "Устройство ввода",
        "biometric" => "Сканер отпечатков",
        "" => "Устройство",
        _ => pnpClass,
    };

    private static string ShortId(string deviceId)
    {
        // PCI\VEN_10EC&DEV_8168&SUBSYS_...\4&... → PCI\VEN_10EC&DEV_8168: по нему драйвер ищут в интернете.
        string[] parts = deviceId.Split('\\');
        if (parts.Length < 2)
            return deviceId;

        string[] ids = parts[1].Split('&');
        return parts[0] + "\\" + string.Join("&", ids.Take(2));
    }

    private static string DateText(DateTime? date, DateTime now)
    {
        if (date is not { } d)
            return "";

        int days = (int)(now - d).TotalDays;
        string age = days switch
        {
            < 31 => "меньше месяца назад",
            < 365 => $"{days / 30} {Formatting.Plural(days / 30, "месяц", "месяца", "месяцев")} назад",
            _ => $"{days / 365} {Formatting.Plural(days / 365, "год", "года", "лет")} назад",
        };
        return $" от {d.ToString("d MMMM yyyy", Russian)} ({age})";
    }
}
