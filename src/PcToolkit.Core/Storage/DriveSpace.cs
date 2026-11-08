using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcToolkit.Core.Storage;

/// <summary>Раздел с буквой и место на нём.</summary>
public sealed record DriveSpaceInfo(
    string Root,
    string Label,
    DriveType Type,
    bool IsExternal,
    long TotalBytes,
    long FreeBytes,
    long AvailableBytes)
{
    /// <summary>«C:».</summary>
    public string Letter => Root.TrimEnd('\\');

    public double FreePercent => TotalBytes > 0 ? 100.0 * AvailableBytes / TotalBytes : 100;
}

/// <summary>
/// Разделы и место на них. Чтение может занимать секунды: сетевой диск, чей компьютер выключен, ждёт ответа
/// по сети, а «уснувший» внешний USB-диск раскручивается при первом обращении. Поэтому — только из фонового потока,
/// и тип каждого раздела проверяется до обращения к нему: сетевые и CD/DVD не трогаются вовсе, а внешние
/// (USB, карты памяти) можно пропустить, не разбудив их: тип шины сообщает драйвер, сам диск при этом не читается.
/// </summary>
public static class DriveSpace
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    // STORAGE_BUS_TYPE: внешние шины.
    private const int Bus1394 = 0x04;
    private const int BusUsb = 0x07;
    private const int BusSd = 0x0C;
    private const int BusMmc = 0x0D;

    /// <param name="includeExternal">false — пропустить USB-диски и карты памяти (для фоновых проверок, чтобы их не будить).</param>
    public static IReadOnlyList<DriveSpaceInfo> Read(bool includeExternal)
    {
        var result = new List<DriveSpaceInfo>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return result;
        }

        foreach (DriveInfo drive in drives)
        {
            try
            {
                // Тип известен без обращения к разделу; IsReady и размеры уже обращаются к нему.
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;

                bool external = drive.DriveType == DriveType.Removable || IsExternalBus(drive.Name);
                if (external && !includeExternal)
                    continue;
                if (!drive.IsReady)
                    continue;

                result.Add(new DriveSpaceInfo(drive.Name, drive.VolumeLabel, drive.DriveType, external,
                                              drive.TotalSize, drive.TotalFreeSpace, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Раздел отключили во время чтения — пропускаем.
            }
        }

        return result;
    }

    /// <summary>Раздел на USB, карте памяти или FireWire — по типу шины от драйвера диска.</summary>
    public static bool IsExternalBus(string root)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using SafeFileHandle handle = CreateFile(@"\\.\" + root.TrimEnd('\\'), 0, 3 /* FILE_SHARE_READ | FILE_SHARE_WRITE */,
                                                 IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return false;

        // STORAGE_PROPERTY_QUERY: StorageDeviceProperty (0), PropertyStandardQuery (0).
        byte[] query = new byte[12];
        byte[] descriptor = new byte[512];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, descriptor, descriptor.Length, out int returned, IntPtr.Zero)
            || returned < 32)
            return false;

        // STORAGE_DEVICE_DESCRIPTOR.BusType — смещение 28.
        int bus = BitConverter.ToInt32(descriptor, 28);
        return bus is Bus1394 or BusUsb or BusSd or BusMmc;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
                                                    uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[] inBuffer, int inBufferSize,
                                               byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
