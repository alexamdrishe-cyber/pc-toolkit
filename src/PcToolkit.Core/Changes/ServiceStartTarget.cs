using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PcToolkit.Core.Changes;

/// <summary>
/// Тип запуска службы Windows. Читается из реестра (там его хранит диспетчер служб),
/// меняется через официальный API диспетчера служб. Работающую службу не останавливает:
/// новый тип запуска вступает в силу после перезагрузки.
/// </summary>
public sealed class ServiceStartTarget : ISettingTarget
{
    public const string KindName = "ServiceStart";

    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;

    private readonly string _serviceName;

    public ServiceStartTarget(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        _serviceName = serviceName;
        Key = $"service:{serviceName}".ToLowerInvariant();
        DisplayName = $"Служба {serviceName} → тип запуска";
    }

    public string Key { get; }

    public string DisplayName { get; }

    public bool RequiresAdmin => true;

    public static bool IsValidStartMode(string? mode) =>
        mode is "auto" or "auto-delayed" or "manual" or "disabled";

    public bool IsAvailable(out string? reason)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{_serviceName}");
        bool exists = key?.GetValue("Start") is int;
        reason = exists ? null : $"служба {_serviceName} не найдена на этом компьютере";
        return exists;
    }

    public ValueSnapshot Read()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{_serviceName}");
        if (key?.GetValue("Start") is not int start)
            return ValueSnapshot.Missing(KindName);

        bool delayed = key.GetValue("DelayedAutostart") is int flag && flag != 0;
        string mode = start switch
        {
            2 => delayed ? "auto-delayed" : "auto",
            3 => "manual",
            4 => "disabled",
            0 => "boot",
            1 => "system",
            _ => start.ToString(),
        };
        return new ValueSnapshot(true, KindName, mode);
    }

    public void Write(ValueSnapshot value)
    {
        if (!value.Exists)
            throw new InvalidOperationException($"Нельзя удалить тип запуска службы {_serviceName}.");

        (uint startType, bool? delayed) = value.Data switch
        {
            "auto" => (2u, (bool?)false),
            "auto-delayed" => (2u, (bool?)true),
            "manual" => (3u, (bool?)null),
            "disabled" => (4u, (bool?)null),
            _ => throw new InvalidOperationException($"Неизвестный тип запуска «{value.Data}»."),
        };

        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось подключиться к диспетчеру служб.");

        try
        {
            IntPtr service = OpenServiceW(manager, _serviceName, ServiceQueryConfig | ServiceChangeConfig);
            if (service == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Не удалось открыть службу {_serviceName}.");

            try
            {
                if (!ChangeServiceConfigW(service, ServiceNoChange, startType, ServiceNoChange,
                        null, null, IntPtr.Zero, null, null, null, null))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Не удалось изменить тип запуска службы {_serviceName}.");
                }

                if (delayed is bool isDelayed)
                {
                    var info = new DelayedAutoStartInfo { DelayedAutostart = isDelayed };
                    if (!ChangeServiceConfig2W(service, ServiceConfigDelayedAutoStartInfo, ref info))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Не удалось изменить отложенный запуск службы {_serviceName}.");
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public string Describe(ValueSnapshot value)
    {
        if (!value.Exists)
            return "служба не найдена";

        return value.Data switch
        {
            "auto" => "автоматически",
            "auto-delayed" => "автоматически (отложенный запуск)",
            "manual" => "вручную",
            "disabled" => "отключена",
            "boot" or "system" => "системная",
            _ => value.Data,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DelayedAutoStartInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DelayedAutostart;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, ref DelayedAutoStartInfo info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
