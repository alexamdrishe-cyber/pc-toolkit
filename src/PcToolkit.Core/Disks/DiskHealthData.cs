using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace PcToolkit.Core.Disks;

/// <summary>
/// Атрибут SMART диска SATA: номер, текущее и худшее нормированные значения (обычно 1–253, больше — лучше),
/// порог производителя и «сырое» значение (счётчик: часы, ошибки, секторы — смотря какой атрибут).
/// </summary>
public sealed record SmartAttribute(int Id, int Current, int Worst, int Threshold, long Raw, int Flags)
{
    /// <summary>Нормированное значение опустилось до порога: так диск сам сообщает о неисправности.</summary>
    [JsonIgnore]
    public bool BelowThreshold => Threshold > 0 && Current > 0 && Current <= Threshold;

    /// <summary>Температура хранится в младшем байте «сырого» значения (в старших — минимум и максимум).</summary>
    [JsonIgnore]
    public int RawLowByte => (int)(Raw & 0xFF);

    /// <summary>Младшие четыре байта: в старших байтах некоторые диски хранят служебные данные.</summary>
    [JsonIgnore]
    public long RawLow32 => Raw & 0xFFFF_FFFF;
}

/// <summary>Журнал здоровья NVMe (SMART / Health Information, страница журнала 02h по стандарту NVMe).</summary>
public sealed record NvmeHealth
{
    /// <summary>Критические предупреждения — битовая маска (0 — всё в порядке).</summary>
    public int CriticalWarning { get; init; }

    public int TemperatureC { get; init; }

    /// <summary>Запас резервных ячеек, % (100 — весь запас цел).</summary>
    public int AvailableSpare { get; init; }

    /// <summary>Порог запаса: ниже него диск считает себя неисправным.</summary>
    public int AvailableSpareThreshold { get; init; }

    /// <summary>Израсходовано ресурса записи, % (может быть больше 100).</summary>
    public int PercentageUsed { get; init; }

    /// <summary>Прочитано, в единицах по 512 000 байт.</summary>
    public long DataUnitsRead { get; init; }

    /// <summary>Записано, в единицах по 512 000 байт.</summary>
    public long DataUnitsWritten { get; init; }

    public long PowerCycles { get; init; }

    public long PowerOnHours { get; init; }

    public long UnsafeShutdowns { get; init; }

    /// <summary>Ошибки данных, которые диск не смог исправить.</summary>
    public long MediaErrors { get; init; }

    public long ErrorLogEntries { get; init; }

    /// <summary>Сколько минут диск работал выше температуры предупреждения.</summary>
    public long WarningTemperatureMinutes { get; init; }

    /// <summary>Сколько минут диск работал выше критической температуры.</summary>
    public long CriticalTemperatureMinutes { get; init; }

    public const int LogSize = 512;

    /// <summary>Разбирает 512 байт журнала здоровья (поля — по стандарту NVMe 1.4, числа little-endian).</summary>
    public static NvmeHealth Parse(ReadOnlySpan<byte> log)
    {
        if (log.Length < LogSize)
            throw new ArgumentException("Журнал здоровья NVMe короче 512 байт.", nameof(log));

        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        return new NvmeHealth
        {
            CriticalWarning = log[0],
            TemperatureC = kelvin > 0 ? kelvin - 273 : 0,
            AvailableSpare = log[3],
            AvailableSpareThreshold = log[4],
            PercentageUsed = log[5],
            DataUnitsRead = Counter(log, 32),
            DataUnitsWritten = Counter(log, 48),
            PowerCycles = Counter(log, 112),
            PowerOnHours = Counter(log, 128),
            UnsafeShutdowns = Counter(log, 144),
            MediaErrors = Counter(log, 160),
            ErrorLogEntries = Counter(log, 176),
            WarningTemperatureMinutes = BinaryPrimitives.ReadUInt32LittleEndian(log[192..]),
            CriticalTemperatureMinutes = BinaryPrimitives.ReadUInt32LittleEndian(log[196..]),
        };
    }

    // Счётчики 128-битные; старшая половина на практике нулевая, а long хватает на миллионы лет работы.
    private static long Counter(ReadOnlySpan<byte> log, int offset)
    {
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(log[offset..]);
        return low > long.MaxValue ? long.MaxValue : (long)low;
    }
}

/// <summary>Физический диск, как его видит Windows, и его SMART (если удалось прочитать).</summary>
public sealed record DiskHealthData
{
    /// <summary>Номер диска в Windows (\\.\PhysicalDriveN).</summary>
    public int Number { get; init; }

    public string Model { get; init; } = "";

    public string Serial { get; init; } = "";

    public string Firmware { get; init; } = "";

    /// <summary>Подключение: NVMe, SATA, USB, RAID…</summary>
    public string Bus { get; init; } = "";

    public long SizeBytes { get; init; }

    /// <summary>true — SSD, false — жёсткий диск с пластинами, null — неизвестно.</summary>
    public bool? IsSsd { get; init; }

    /// <summary>Буквы разделов на этом диске («C:», «D:»).</summary>
    public List<string> Volumes { get; init; } = new();

    /// <summary>Атрибуты SMART (SATA).</summary>
    public List<SmartAttribute>? Attributes { get; init; }

    /// <summary>Журнал здоровья (NVMe).</summary>
    public NvmeHealth? Nvme { get; init; }

    /// <summary>Почему SMART не прочитан; null — прочитан.</summary>
    public string? SmartError { get; init; }

    [JsonIgnore]
    public bool IsNvme => string.Equals(Bus, "NVMe", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasSmart => Attributes is { Count: > 0 } || Nvme is not null;

    /// <summary>Ключ для истории: серийный номер, а если его нет — модель и размер.</summary>
    [JsonIgnore]
    public string HistoryKey => Serial.Length > 0 ? Serial : $"{Model}|{SizeBytes}";
}
