using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Disks;

/// <summary>
/// Читает список физических дисков и их SMART напрямую через Windows (DeviceIoControl), без сторонних программ:
/// SATA — командой ATA SMART READ DATA через ATA pass-through, NVMe — журналом здоровья через запрос протокола NVMe.
/// SMART читается только с правами администратора, поэтому без них это делает служба PC Toolkit.
/// </summary>
public static class DiskHealthReader
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint IoctlAtaPassThrough = 0x0004D02C;

    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int StorageAdapterProtocolSpecificProperty = 49;
    private const int StorageDeviceProtocolSpecificProperty = 50;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;

    /// <summary>Диски с номерами 0–31: этого хватает с запасом, отсутствующие номера пропускаются.</summary>
    private const int MaxDisks = 32;

    /// <summary>
    /// Все физические диски. <paramref name="readSmart"/> = false — только сведения, без SMART
    /// (так можно и без прав администратора).
    /// </summary>
    public static List<DiskHealthData> ReadAll(bool readSmart = true)
    {
        var disks = new List<DiskHealthData>();
        if (!OperatingSystem.IsWindows())
            return disks;

        Dictionary<int, List<string>> volumes = MapVolumes();
        for (int number = 0; number < MaxDisks; number++)
        {
            try
            {
                DiskHealthData? disk = Read(number, readSmart, volumes);
                if (disk is not null)
                    disks.Add(disk);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось прочитать диск {number}.", ex);
            }
        }

        return disks;
    }

    private const string NeedAdmin = "Чтобы прочитать SMART, нужны права администратора или служба PC Toolkit.";

    private static DiskHealthData? Read(int number, bool readSmart, Dictionary<int, List<string>> volumes)
    {
        string path = @"\\.\PhysicalDrive" + number;

        // Полный доступ (чтение и запись) нужен для команд SMART и есть только у администратора.
        // Без него диск открывается с нулевым доступом: этого хватает для сведений о модели и размере.
        bool fullAccess = false;
        SafeFileHandle handle;
        if (readSmart)
        {
            handle = Open(path, GenericRead | GenericWrite);
            if (!handle.IsInvalid)
            {
                fullAccess = true;
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error is ErrorFileNotFound or ErrorPathNotFound)
                    return null;

                if (error != ErrorAccessDenied)
                    AppLog.Warn($"Диск {number} не открылся для чтения SMART: {new Win32Exception(error).Message}");
                handle = Open(path, 0);
            }
        }
        else
        {
            handle = Open(path, 0);
        }

        using (handle)
        {
            if (handle.IsInvalid)
                return null;

            Descriptor? descriptor = QueryDescriptor(handle);
            if (descriptor is null)
                return null;

            // Виртуальные диски (подключённые VHD, образы) — не железо.
            if (descriptor.BusType is 0x0E or 0x0F)
                return null;

            List<SmartAttribute>? attributes = null;
            NvmeHealth? nvme = null;
            string? smartError;
            if (!readSmart)
            {
                smartError = "SMART не запрашивался.";
            }
            else if (descriptor.BusType == 0x11)
            {
                // Журнал здоровья NVMe иногда отдаётся и без прав администратора: пробуем в любом случае.
                nvme = ReadNvmeHealth(handle, out smartError);
                if (nvme is null && !fullAccess)
                    smartError = NeedAdmin;
            }
            else if (descriptor.BusType == 0x07)
            {
                smartError = "SMART внешних USB-дисков и флешек обычно недоступен через переходник USB.";
            }
            else if (descriptor.BusType == 0x08)
            {
                smartError = "Диск подключён через RAID-контроллер (часто это режим Intel RST в BIOS): SMART скрыт контроллером.";
            }
            else if (descriptor.BusType is 0x0C or 0x0D)
            {
                smartError = "Карты памяти не сообщают SMART.";
            }
            else if (!fullAccess)
            {
                smartError = NeedAdmin;
            }
            else
            {
                attributes = ReadAtaSmart(handle, out smartError);
            }

            return new DiskHealthData
            {
                Number = number,
                Model = Clean(descriptor.Vendor is { Length: > 0 } vendor && !IsGenericVendor(vendor)
                              && !descriptor.Product.StartsWith(vendor.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? $"{vendor.Trim()} {descriptor.Product}"
                    : descriptor.Product),
                Serial = Clean(descriptor.Serial),
                Firmware = Clean(descriptor.Revision),
                Bus = BusName(descriptor.BusType),
                SizeBytes = QuerySize(handle) is > 0 and long size ? size : VolumesSize(volumes.GetValueOrDefault(number)),
                IsSsd = QuerySeekPenalty(handle) is bool seek ? !seek : null,
                Volumes = volumes.TryGetValue(number, out List<string>? letters) ? letters : new List<string>(),
                Attributes = attributes,
                Nvme = nvme,
                SmartError = attributes is { Count: > 0 } || nvme is not null ? null : smartError ?? "Диск не отдал SMART.",
            };
        }
    }

    private static SafeFileHandle Open(string path, uint access) =>
        CreateFile(path, access, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    private sealed record Descriptor(int BusType, string Vendor, string Product, string Revision, string Serial);

    private static Descriptor? QueryDescriptor(SafeFileHandle handle)
    {
        byte[] query = PropertyQuery(StorageDeviceProperty);
        byte[] output = new byte[1024];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, output, output.Length, out int returned, IntPtr.Zero)
            || returned < 36)
            return null;

        return new Descriptor(
            BusType: BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(28)),
            Vendor: AsciiAt(output, returned, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(12))),
            Product: AsciiAt(output, returned, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(16))),
            Revision: AsciiAt(output, returned, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(20))),
            Serial: AsciiAt(output, returned, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(24))));
    }

    private static bool? QuerySeekPenalty(SafeFileHandle handle)
    {
        byte[] query = PropertyQuery(StorageDeviceSeekPenaltyProperty);
        byte[] output = new byte[16];
        return DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, output, output.Length, out int returned, IntPtr.Zero)
               && returned >= 9
            ? output[8] != 0
            : null;
    }

    private static long QuerySize(SafeFileHandle handle)
    {
        byte[] output = new byte[256];
        return DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, null, 0, output, output.Length, out int returned, IntPtr.Zero)
               && returned >= 32
            ? BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24))
            : 0;
    }

    /// <summary>
    /// Если диск не сообщил размер (так бывает у кардридеров и переходников USB), — хотя бы сумма размеров его разделов.
    /// </summary>
    private static long VolumesSize(List<string>? letters)
    {
        long total = 0;
        foreach (string letter in letters ?? new List<string>())
        {
            try
            {
                var drive = new DriveInfo(letter);
                if (drive.IsReady)
                    total += drive.TotalSize;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Раздел пропал во время проверки — без размера.
            }
        }

        return total;
    }

    /// <summary>STORAGE_PROPERTY_QUERY: PropertyId, QueryType = PropertyStandardQuery, AdditionalParameters.</summary>
    private static byte[] PropertyQuery(int propertyId)
    {
        byte[] query = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(query, propertyId);
        return query;
    }

    /// <summary>
    /// Журнал здоровья NVMe через IOCTL_STORAGE_QUERY_PROPERTY с данными протокола (так делает и сама Windows:
    /// «Параметры → Диски → Работоспособность»). Сначала запрос к устройству, потом к контроллеру.
    /// </summary>
    private static NvmeHealth? ReadNvmeHealth(SafeFileHandle handle, out string? error)
    {
        // STORAGE_PROPERTY_QUERY до AdditionalParameters (8 байт) + STORAGE_PROTOCOL_SPECIFIC_DATA (40 байт) + журнал.
        const int header = 8;
        const int protocolData = 40;
        const int descriptorSize = header + protocolData;
        int lastError = 0;
        foreach (int property in new[] { StorageDeviceProtocolSpecificProperty, StorageAdapterProtocolSpecificProperty })
        {
            byte[] buffer = new byte[descriptorSize + NvmeHealth.LogSize];
            Span<byte> span = buffer;
            BinaryPrimitives.WriteInt32LittleEndian(span, property);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], 0);                      // PropertyStandardQuery
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], 3);                      // ProtocolTypeNvme
            BinaryPrimitives.WriteInt32LittleEndian(span[12..], 2);                     // NVMeDataTypeLogPage
            BinaryPrimitives.WriteInt32LittleEndian(span[16..], 2);                     // NVME_LOG_PAGE_HEALTH_INFO
            // ProtocolDataRequestSubValue (смещение в журнале) = 0; пространство имён драйвер подставляет сам.
            BinaryPrimitives.WriteInt32LittleEndian(span[24..], protocolData);          // ProtocolDataOffset
            BinaryPrimitives.WriteInt32LittleEndian(span[28..], NvmeHealth.LogSize);    // ProtocolDataLength

            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
            {
                lastError = Marshal.GetLastWin32Error();
                continue;
            }

            // Ответ — STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version, Size и те же 40 байт описания; данные — по смещению от описания.
            int version = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            int size = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4));
            int offset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(header + 16));
            int length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(header + 20));
            int start = header + offset;
            if (version != descriptorSize || size != descriptorSize || offset < protocolData || length < NvmeHealth.LogSize
                || start + NvmeHealth.LogSize > buffer.Length)
                continue;

            error = null;
            return NvmeHealth.Parse(buffer.AsSpan(start, NvmeHealth.LogSize));
        }

        error = lastError == ErrorAccessDenied
            ? NeedAdmin
            : "Диск NVMe не отдал журнал здоровья" + (lastError != 0 ? ": " + new Win32Exception(lastError).Message : ".");
        return null;
    }

    /// <summary>SMART диска SATA: таблица атрибутов (SMART READ DATA) и порогов (SMART READ THRESHOLDS).</summary>
    private static List<SmartAttribute>? ReadAtaSmart(SafeFileHandle handle, out string? error)
    {
        byte[]? data = AtaSmartCommand(handle, 0xD0);
        if (data is null)
        {
            error = "Диск не ответил на запрос SMART: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }

        byte[]? thresholds = AtaSmartCommand(handle, 0xD1);
        var limits = new Dictionary<int, int>();
        if (thresholds is not null)
        {
            for (int i = 0; i < 30; i++)
            {
                int o = 2 + i * 12;
                if (thresholds[o] != 0)
                    limits[thresholds[o]] = thresholds[o + 1];
            }
        }

        var attributes = new List<SmartAttribute>();
        for (int i = 0; i < 30; i++)
        {
            int o = 2 + i * 12;
            int id = data[o];
            if (id == 0)
                continue;

            long raw = 0;
            for (int b = 5; b >= 0; b--)
                raw = (raw << 8) | data[o + 5 + b];

            attributes.Add(new SmartAttribute(id, data[o + 3], data[o + 4], limits.GetValueOrDefault(id), raw,
                                              data[o + 1] | (data[o + 2] << 8)));
        }

        error = attributes.Count == 0 ? "Таблица SMART пуста." : null;
        return attributes.Count == 0 ? null : attributes;
    }

    /// <summary>Команда ATA SMART (B0h) с подкомандой <paramref name="feature"/>: 512 байт данных или null.</summary>
    private static byte[]? AtaSmartCommand(SafeFileHandle handle, byte feature)
    {
        // ATA_PASS_THROUGH_EX: на 64-битной Windows 48 байт (DataBufferOffset — 8-байтовый ULONG_PTR после выравнивания).
        bool is64 = IntPtr.Size == 8;
        int headerSize = is64 ? 48 : 40;
        int taskFile = is64 ? 40 : 32;
        byte[] buffer = new byte[headerSize + 512];
        Span<byte> span = buffer;
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)headerSize);   // Length
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 0x03);            // ATA_FLAGS_DRDY_REQUIRED | ATA_FLAGS_DATA_IN
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 512);             // DataTransferLength
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 3);              // TimeOutValue, с
        if (is64)
            BinaryPrimitives.WriteUInt64LittleEndian(span[24..], (ulong)headerSize);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)headerSize);

        span[taskFile + 0] = feature;   // Features
        span[taskFile + 1] = 1;         // Sector Count
        span[taskFile + 2] = 1;         // LBA Low
        span[taskFile + 3] = 0x4F;      // LBA Mid — подпись SMART
        span[taskFile + 4] = 0xC2;      // LBA High — подпись SMART
        span[taskFile + 5] = 0xA0;      // Device
        span[taskFile + 6] = 0xB0;      // Command: SMART

        if (!DeviceIoControl(handle, IoctlAtaPassThrough, buffer, buffer.Length, buffer, buffer.Length, out int returned, IntPtr.Zero)
            || returned < headerSize + 2)
            return null;

        // После выполнения в CurrentTaskFile — регистр состояния: бит 0 — ошибка.
        if ((buffer[taskFile + 6] & 0x01) != 0)
            return null;

        return buffer.AsSpan(headerSize, 512).ToArray();
    }

    /// <summary>Какие буквы разделов на каком диске: IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS для каждой буквы.</summary>
    private static Dictionary<int, List<string>> MapVolumes()
    {
        var map = new Dictionary<int, List<string>>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;

            string letter = drive.Name.TrimEnd('\\');
            try
            {
                using SafeFileHandle handle = Open(@"\\.\" + letter, 0);
                if (handle.IsInvalid)
                    continue;

                byte[] output = new byte[512];
                if (!DeviceIoControl(handle, IoctlVolumeGetVolumeDiskExtents, null, 0, output, output.Length, out int returned, IntPtr.Zero))
                    continue;

                int count = BinaryPrimitives.ReadInt32LittleEndian(output);
                for (int e = 0; e < count && 8 + e * 24 + 4 <= returned; e++)
                {
                    int disk = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8 + e * 24));
                    if (!map.TryGetValue(disk, out List<string>? letters))
                        map[disk] = letters = new List<string>();
                    if (!letters.Contains(letter))
                        letters.Add(letter);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось узнать, на каком диске раздел {letter}.", ex);
            }
        }

        return map;
    }

    private static string AsciiAt(byte[] buffer, int length, int offset)
    {
        if (offset <= 0 || offset >= length)
            return "";

        int end = Array.IndexOf(buffer, (byte)0, offset, length - offset);
        if (end < 0)
            end = length;
        return Encoding.ASCII.GetString(buffer, offset, end - offset);
    }

    private static string Clean(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', '_', '-');

    // Многие диски SATA сообщают вместо производителя «ATA»: такое в название не добавляем.
    private static bool IsGenericVendor(string vendor) =>
        vendor.Trim() is "ATA" or "NVMe" or "SATA" or "SCSI" or "USB" or "";

    private static string BusName(int bus) => bus switch
    {
        0x01 => "SCSI",
        0x03 or 0x0B => "SATA",
        0x07 => "USB",
        0x08 => "RAID",
        0x0A => "SAS",
        0x0C or 0x0D => "SD",
        0x10 => "Storage Spaces",
        0x11 => "NVMe",
        0x13 => "UFS",
        _ => "другое",
    };

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation,
                                                    uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[]? output,
                                               int outputSize, out int returned, IntPtr overlapped);
}
