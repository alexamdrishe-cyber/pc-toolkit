using System.Runtime.InteropServices;
using System.Text;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Network;

/// <summary>
/// Сигнал, стандарт и канал текущего Wi-Fi-подключения через Native Wifi API (wlanapi.dll) — то же, что показывает
/// «netsh wlan show interfaces», но без разбора локализованного текста. Права администратора не нужны.
/// </summary>
public static class WifiReader
{
    private const int ClientVersion = 2;
    private const int OpcodeCurrentConnection = 7;
    private const int OpcodeChannelNumber = 8;
    private const int StateConnected = 1;

    // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] (512) + состояние (4).
    private const int InterfaceInfoSize = 532;

    // WLAN_CONNECTION_ATTRIBUTES: состояние (4), режим (4), имя профиля WCHAR[256] (512), затем WLAN_ASSOCIATION_ATTRIBUTES.
    private const int AssociationOffset = 520;

    public static WifiInfo? Read()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        IntPtr client = IntPtr.Zero;
        IntPtr list = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out client) != 0)
                return null;
            if (WlanEnumInterfaces(client, IntPtr.Zero, out list) != 0)
                return null;

            int count = Marshal.ReadInt32(list);
            for (int i = 0; i < count; i++)
            {
                IntPtr info = list + 8 + i * InterfaceInfoSize;
                if (Marshal.ReadInt32(info + 16 + 512) != StateConnected)
                    continue;

                Guid guid = Marshal.PtrToStructure<Guid>(info);
                if (WlanQueryInterface(client, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out _, out IntPtr data, out _) != 0)
                    continue;

                try
                {
                    IntPtr assoc = data + AssociationOffset;
                    int ssidLength = Math.Clamp(Marshal.ReadInt32(assoc), 0, 32);
                    byte[] ssid = new byte[ssidLength];
                    Marshal.Copy(assoc + 4, ssid, 0, ssidLength);
                    // После SSID (4 + 32): тип сети (4), BSSID (6 + 2 выравнивания), тип PHY (4), индекс PHY (4),
                    // качество сигнала 0–100 (4), скорость приёма и передачи в кбит/с (по 4).
                    int phy = Marshal.ReadInt32(assoc + 48);
                    int signal = Marshal.ReadInt32(assoc + 56);
                    int rx = Marshal.ReadInt32(assoc + 60);
                    int tx = Marshal.ReadInt32(assoc + 64);

                    int channel = 0;
                    if (WlanQueryInterface(client, ref guid, OpcodeChannelNumber, IntPtr.Zero, out _, out IntPtr channelData, out _) == 0)
                    {
                        channel = Marshal.ReadInt32(channelData);
                        WlanFreeMemory(channelData);
                    }

                    return new WifiInfo(Encoding.UTF8.GetString(ssid), Math.Clamp(signal, 0, 100), PhyName(phy), channel, rx / 1000, tx / 1000);
                }
                finally
                {
                    WlanFreeMemory(data);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or AccessViolationException)
        {
            // Нет службы WLAN (компьютер без Wi-Fi) — просто без сведений о сигнале.
            AppLog.Warn("Не удалось прочитать сведения о Wi-Fi.", ex);
            return null;
        }
        finally
        {
            if (list != IntPtr.Zero)
                WlanFreeMemory(list);
            if (client != IntPtr.Zero)
                WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    private static string PhyName(int phy) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)",
        10 => "Wi-Fi 6 (802.11ax)",
        11 => "Wi-Fi 7 (802.11be)",
        _ => "",
    };

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
                                                 out int dataSize, out IntPtr data, out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
