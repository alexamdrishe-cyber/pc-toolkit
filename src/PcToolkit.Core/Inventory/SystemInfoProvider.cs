using System.Globalization;
using System.Management;
using Microsoft.Win32;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Platform;

namespace PcToolkit.Core.Inventory;

/// <summary>
/// Собирает сведения о компьютере через WMI и реестр. Только чтение.
/// Вызывать в фоновом потоке: запросы WMI могут занимать несколько секунд.
/// </summary>
public static class SystemInfoProvider
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";

    public static SystemInfoReport Collect()
    {
        var sections = new List<InfoSection>
        {
            Section("Система", CollectSystem),
            Section("Процессор", CollectCpu),
            Section("Материнская плата и BIOS", CollectBoard),
            Section("Оперативная память", CollectMemory),
            Section("Видеокарты", CollectGpus),
            Section("Накопители", CollectDisks),
            Section("Разделы", CollectVolumes),
            Section("Сеть", CollectNetwork),
        };

        return new SystemInfoReport(DateTime.Now, sections);
    }

    private static InfoSection Section(string title, Func<List<InfoItem>> collect)
    {
        try
        {
            List<InfoItem> items = collect();
            if (items.Count == 0)
                items.Add(new InfoItem("Данные", "нет данных"));

            return new InfoSection(title, items);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось собрать раздел «{title}».", ex);
            return new InfoSection(title, new[] { new InfoItem("Ошибка", ex.Message) });
        }
    }

    private static List<InfoItem> CollectSystem()
    {
        var items = new List<InfoItem> { new("Имя компьютера", Environment.MachineName) };

        // У самосборных ПК здесь заглушки из BIOS вроде «System manufacturer»: их не показываем.
        Row? computer = Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault();
        if (computer is not null)
        {
            string model = Join(" ", Real(computer.Str("Manufacturer")), Real(computer.Str("Model")));
            if (model.Length > 0)
                items.Add(new InfoItem("Производитель и модель", model));
        }

        Row? os = Query("SELECT Caption, OSArchitecture, InstallDate FROM Win32_OperatingSystem").FirstOrDefault();
        if (os is not null)
            items.Add(new InfoItem("Операционная система", os.Str("Caption")));

        items.Add(new InfoItem("Версия", OsInfo.FriendlyName));

        if (os is not null)
        {
            items.Add(new InfoItem("Разрядность", os.Str("OSArchitecture")));
            if (os.Date("InstallDate") is DateTime installed)
                items.Add(new InfoItem("Установлена", installed.ToString("d MMMM yyyy")));
        }

        items.Add(new InfoItem("Время работы", Formatting.Uptime(OsInfo.Uptime)));
        return items;
    }

    private static List<InfoItem> CollectCpu()
    {
        var items = new List<InfoItem>();
        List<Row> cpus = Query(
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor");

        for (int i = 0; i < cpus.Count; i++)
        {
            Row cpu = cpus[i];
            string prefix = cpus.Count > 1 ? $"Процессор {i + 1}: " : "";

            items.Add(new InfoItem(prefix + "Модель", cpu.Str("Name")));
            items.Add(new InfoItem(prefix + "Ядра / потоки", $"{cpu.Num("NumberOfCores")} / {cpu.Num("NumberOfLogicalProcessors")}"));

            if (cpu.Num("MaxClockSpeed") is ulong clock and > 0)
                items.Add(new InfoItem(prefix + "Базовая частота", $"{clock / 1000.0:0.00} ГГц"));

            ulong l2 = cpu.Num("L2CacheSize") ?? 0;
            ulong l3 = cpu.Num("L3CacheSize") ?? 0;
            if (l2 > 0 || l3 > 0)
                items.Add(new InfoItem(prefix + "Кэш L2 / L3", $"{Formatting.Bytes(l2 * 1024)} / {Formatting.Bytes(l3 * 1024)}"));

            string socket = cpu.Str("SocketDesignation");
            if (socket.Length > 0)
                items.Add(new InfoItem(prefix + "Сокет", socket));
        }

        return items;
    }

    private static List<InfoItem> CollectBoard()
    {
        var items = new List<InfoItem>();

        Row? board = Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard").FirstOrDefault();
        if (board is not null)
        {
            items.Add(new InfoItem("Производитель", board.Str("Manufacturer")));
            items.Add(new InfoItem("Модель", Join(" ", board.Str("Product"), board.Str("Version"))));
        }

        Row? bios = Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        if (bios is not null)
        {
            items.Add(new InfoItem("BIOS", Join(" ", bios.Str("Manufacturer"), bios.Str("SMBIOSBIOSVersion"))));
            if (bios.Date("ReleaseDate") is DateTime released)
                items.Add(new InfoItem("Дата BIOS", released.ToString("d MMMM yyyy")));
        }

        return items;
    }

    /// <summary>
    /// Конфигурация оперативной памяти для теста скорости: тип, частота, число модулей и оценка числа каналов.
    /// null — WMI не ответил.
    /// </summary>
    public static MemoryConfig? ReadMemoryConfig()
    {
        try
        {
            List<Row> modules = Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            if (modules.Count == 0)
                return null;

            ulong total = modules.Aggregate(0UL, (sum, m) => sum + (m.Num("Capacity") ?? 0));
            ulong speed = modules
                .Select(m => m.Num("ConfiguredClockSpeed") is ulong c and > 0 ? c : m.Num("Speed") ?? 0)
                .Where(v => v > 0)
                .DefaultIfEmpty(0UL)
                .Min();
            string type = modules.Select(m => m.Num("SMBIOSMemoryType") ?? 0).Max() switch
            {
                24 => "DDR3",
                26 => "DDR4",
                34 => "DDR5",
                _ => "",
            };

            // В обычных настольных платформах два канала; модули ставятся по одному или по два на канал.
            int channels = modules.Count >= 2 ? 2 : 1;
            return new MemoryConfig(total, modules.Count, (int)speed, type, channels);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать конфигурацию памяти.", ex);
            return null;
        }
    }

    /// <summary>
    /// Модель и тип физического диска для каждой буквы тома: «C» → Samsung 980 (SSD, NVMe).
    /// Пусто, если класс хранилища Windows недоступен.
    /// </summary>
    public static IReadOnlyDictionary<char, DriveDetails> ReadDriveDetails()
    {
        var result = new Dictionary<char, DriveDetails>();
        try
        {
            Dictionary<string, Row> disks = Query("SELECT DeviceId, FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk", StorageScope)
                .GroupBy(d => d.Str("DeviceId"))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (Row partition in Query("SELECT DriveLetter, DiskNumber FROM MSFT_Partition", StorageScope))
            {
                string letter = partition.Str("DriveLetter");
                if (letter.Length != 1 || !char.IsLetter(letter[0]) || partition.Num("DiskNumber") is not ulong number)
                    continue;
                if (!disks.TryGetValue(number.ToString(CultureInfo.InvariantCulture), out Row? disk))
                    continue;

                string media = disk.Num("MediaType") switch
                {
                    3 => "HDD",
                    4 => "SSD",
                    _ => "",
                };
                string bus = disk.Num("BusType") switch
                {
                    17 => "NVMe",
                    11 => "SATA",
                    7 => "USB",
                    _ => "",
                };
                result[char.ToUpperInvariant(letter[0])] = new DriveDetails(disk.Str("FriendlyName"), media, bus);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сопоставить тома и диски.", ex);
        }

        return result;
    }

    private static List<InfoItem> CollectMemory()
    {
        var items = new List<InfoItem>();
        List<Row> modules = Query(
            "SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory");

        ulong total = modules.Aggregate(0UL, (sum, m) => sum + (m.Num("Capacity") ?? 0));
        ulong slots = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray")
            .Aggregate(0UL, (sum, a) => sum + (a.Num("MemoryDevices") ?? 0));

        if (total > 0)
            items.Add(new InfoItem("Всего", Formatting.Bytes(total)));

        if (modules.Count > 0)
        {
            string slotText = slots >= (ulong)modules.Count ? $"{modules.Count} из {slots} слотов" : $"{modules.Count}";
            items.Add(new InfoItem("Модули", slotText));
        }

        foreach (Row module in modules)
        {
            ulong speed = module.Num("ConfiguredClockSpeed") is ulong configured and > 0
                ? configured
                : module.Num("Speed") ?? 0;

            var parts = new List<string>();
            if (module.Num("Capacity") is ulong capacity and > 0)
                parts.Add(Formatting.Bytes(capacity));
            if (speed > 0)
                parts.Add($"{speed} МТ/с");

            string maker = Join(" ", module.Str("Manufacturer"), module.Str("PartNumber"));
            if (maker.Length > 0)
                parts.Add(maker);

            string slot = module.Str("DeviceLocator");
            items.Add(new InfoItem(slot.Length > 0 ? slot : "Модуль", string.Join(" · ", parts)));
        }

        return items;
    }

    private static List<InfoItem> CollectGpus()
    {
        var items = new List<InfoItem>();
        Dictionary<string, ulong> memoryByName = ReadVideoMemoryFromRegistry();

        foreach (Row adapter in Query(
                     "SELECT Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            string name = adapter.Str("Name");
            var parts = new List<string>();

            // Win32_VideoController.AdapterRAM ограничен 4 ГБ, поэтому объём видеопамяти берём из реестра драйвера.
            if (memoryByName.TryGetValue(name, out ulong memory) && memory > 0)
                parts.Add($"{Formatting.Bytes(memory)} видеопамяти");

            string driver = adapter.Str("DriverVersion");
            if (driver.Length > 0)
                parts.Add($"драйвер {driver}");

            ulong width = adapter.Num("CurrentHorizontalResolution") ?? 0;
            ulong height = adapter.Num("CurrentVerticalResolution") ?? 0;
            ulong refresh = adapter.Num("CurrentRefreshRate") ?? 0;
            if (width > 0 && height > 0)
                parts.Add(refresh > 1 ? $"{width}×{height} @ {refresh} Гц" : $"{width}×{height}");

            items.Add(new InfoItem(name.Length > 0 ? name : "Видеоадаптер", parts.Count > 0 ? string.Join(" · ", parts) : "нет данных"));
        }

        return items;
    }

    private static List<InfoItem> CollectDisks()
    {
        var items = new List<InfoItem>();

        // MSFT_PhysicalDisk знает тип (SSD/HDD) и шину (NVMe/SATA). Если класс недоступен, берём Win32_DiskDrive.
        List<Row> disks = Query("SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk", StorageScope);
        if (disks.Count > 0)
        {
            foreach (Row disk in disks)
            {
                string media = disk.Num("MediaType") switch
                {
                    3 => "HDD",
                    4 => "SSD",
                    5 => "SCM",
                    _ => "",
                };
                string bus = disk.Num("BusType") switch
                {
                    17 => "NVMe",
                    11 => "SATA",
                    7 => "USB",
                    8 => "RAID",
                    10 => "SAS",
                    3 => "ATA",
                    12 => "SD",
                    _ => "",
                };
                ulong size = disk.Num("Size") ?? 0;

                string details = Join(" · ", Join(" ", media, bus), size > 0 ? Formatting.Bytes(size) : "");
                items.Add(new InfoItem(disk.Str("FriendlyName"), details.Length > 0 ? details : "нет данных"));
            }

            return items;
        }

        foreach (Row disk in Query("SELECT Model, InterfaceType, Size FROM Win32_DiskDrive"))
        {
            ulong size = disk.Num("Size") ?? 0;
            items.Add(new InfoItem(disk.Str("Model"), Join(" · ", disk.Str("InterfaceType"), size > 0 ? Formatting.Bytes(size) : "")));
        }

        return items;
    }

    private static List<InfoItem> CollectVolumes()
    {
        var items = new List<InfoItem>();
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "";

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                    continue;

                string label = drive.Name.TrimEnd('\\');
                if (drive.VolumeLabel.Length > 0)
                    label += $" ({drive.VolumeLabel})";
                if (string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase))
                    label += " — системный";

                long total = drive.TotalSize;
                long free = drive.AvailableFreeSpace;
                double freePercent = total > 0 ? 100.0 * free / total : 0;

                items.Add(new InfoItem(label,
                    $"{drive.DriveFormat} · свободно {Formatting.Bytes((ulong)free)} из {Formatting.Bytes((ulong)total)} ({freePercent:0} %)"));
            }
            catch
            {
                // Диск отключили во время опроса.
            }
        }

        return items;
    }

    private static List<InfoItem> CollectNetwork()
    {
        var items = new List<InfoItem>();
        foreach (Row adapter in Query(
                     "SELECT Name, Speed FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE AND NetEnabled = TRUE"))
        {
            ulong speed = adapter.Num("Speed") ?? 0;
            string speedText = speed switch
            {
                >= 1_000_000_000 => $"{speed / 1_000_000_000.0:0.#} Гбит/с",
                >= 1_000_000 => $"{speed / 1_000_000.0:0} Мбит/с",
                > 0 => $"{speed / 1000.0:0} Кбит/с",
                _ => "подключён",
            };
            items.Add(new InfoItem(adapter.Str("Name"), speedText));
        }

        if (items.Count == 0)
            items.Add(new InfoItem("Адаптеры", "нет активных подключений"));

        return items;
    }

    private static Dictionary<string, ulong> ReadVideoMemoryFromRegistry()
    {
        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(displayClass);
            if (root is null)
                return result;

            foreach (string subKeyName in root.GetSubKeyNames())
            {
                // Нужны только подключи адаптеров вида 0000, 0001…; «Properties» закрыт для обычного пользователя.
                if (subKeyName.Length != 4 || !subKeyName.All(char.IsDigit))
                    continue;

                try
                {
                    using RegistryKey? adapter = root.OpenSubKey(subKeyName);
                    if (adapter?.GetValue("DriverDesc") is not string description)
                        continue;

                    ulong memory = ReadMemorySize(adapter.GetValue("HardwareInformation.qwMemorySize"));
                    if (memory == 0)
                        memory = ReadMemorySize(adapter.GetValue("HardwareInformation.MemorySize"));

                    if (memory > 0 && (!result.TryGetValue(description, out ulong existing) || memory > existing))
                        result[description] = memory;
                }
                catch
                {
                    // Нет доступа к конкретному адаптеру: пропускаем.
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать объём видеопамяти из реестра.", ex);
        }

        return result;
    }

    private static ulong ReadMemorySize(object? value) => value switch
    {
        long qword when qword > 0 => (ulong)qword,
        int dword when dword != 0 => unchecked((uint)dword),
        byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0),
        byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
        _ => 0,
    };

    private static List<Row> Query(string wql, string scope = @"root\cimv2")
    {
        var rows = new List<Row>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            using ManagementObjectCollection collection = searcher.Get();
            foreach (ManagementBaseObject item in collection)
            {
                using (item)
                {
                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData property in item.Properties)
                        values[property.Name] = property.Value;

                    rows.Add(new Row(values));
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"WMI-запрос не выполнен: {wql}", ex);
        }

        return rows;
    }

    private static readonly string[] PlaceholderValues =
    [
        "System manufacturer",
        "System Product Name",
        "System Version",
        "To Be Filled By O.E.M.",
        "Default string",
        "Not Applicable",
        "None",
    ];

    /// <summary>Пустая строка вместо заглушки, которую производитель платы оставил в BIOS.</summary>
    private static string Real(string value) =>
        PlaceholderValues.Any(p => string.Equals(p, value.Trim(), StringComparison.OrdinalIgnoreCase)) ? "" : value;

    private static string Join(string separator, params string[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

    /// <summary>Строка результата WMI с удобным доступом к значениям.</summary>
    private sealed class Row(Dictionary<string, object?> values)
    {
        public string Str(string name) =>
            values.TryGetValue(name, out object? value) && value is not null ? (value.ToString() ?? "").Trim() : "";

        public ulong? Num(string name)
        {
            if (!values.TryGetValue(name, out object? value) || value is null)
                return null;

            try
            {
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        public DateTime? Date(string name)
        {
            string text = Str(name);
            if (text.Length == 0)
                return null;

            try
            {
                return ManagementDateTimeConverter.ToDateTime(text);
            }
            catch
            {
                return null;
            }
        }
    }
}

/// <summary>Оперативная память: всего, модулей, частота (МТ/с), тип, каналов (оценка).</summary>
public sealed record MemoryConfig(ulong TotalBytes, int Modules, int SpeedMts, string Type, int Channels)
{
    /// <summary>Теоретический максимум скорости: МТ/с × 8 байт × каналы, ГБ/с.</summary>
    public double TheoreticalBandwidth => SpeedMts * 8.0 * Channels / 1000;

    public string Description =>
        $"{(Type.Length > 0 ? Type + "-" : "")}{SpeedMts}, {Modules} {(Modules == 1 ? "модуль" : Modules < 5 ? "модуля" : "модулей")}, " +
        $"{(Channels == 1 ? "один канал" : "два канала")}";
}

/// <summary>Физический диск под томом: модель, тип носителя (SSD/HDD) и шина (NVMe/SATA/USB).</summary>
public sealed record DriveDetails(string Model, string Media, string Bus)
{
    public string Kind => string.Join(" ", new[] { Media, Bus }.Where(p => p.Length > 0));
}
