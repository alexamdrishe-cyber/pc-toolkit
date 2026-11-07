using System.Security.Principal;
using Microsoft.Win32;

namespace PcToolkit.Core.Platform;

/// <summary>
/// Условия, от которых зависит полнота данных датчиков:
/// права администратора и драйвер PawnIO, через который LibreHardwareMonitor читает процессор и материнскую плату.
/// </summary>
public sealed record EnvironmentStatus(bool IsElevated, Version? PawnIoVersion, bool ServiceConnected = false)
{
    /// <summary>Полный набор датчиков: приложение с правами администратора или данные идут от службы.</summary>
    public bool HasFullSensors => IsElevated || ServiceConnected;

    /// <summary>Официальные выпуски установщика PawnIO.</summary>
    public const string PawnIoDownloadUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest";

    public bool PawnIoInstalled => PawnIoVersion is not null;

    /// <summary>LibreHardwareMonitor 0.9.6 требует PawnIO версии 2.0 или новее.</summary>
    public bool PawnIoOutdated => PawnIoVersion is not null && PawnIoVersion < new Version(2, 0);

    public static EnvironmentStatus Detect() => new(IsProcessElevated(), ReadPawnIoVersion());

    /// <summary>Запущен ли текущий процесс с правами администратора.</summary>
    public static bool CurrentProcessElevated => IsProcessElevated();

    private static bool IsProcessElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // Проверка такая же, как в самом LibreHardwareMonitor: по записи установщика PawnIO в реестре.
    private static Version? ReadPawnIoVersion()
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = baseKey.OpenSubKey(keyPath);
                if (Version.TryParse(key?.GetValue("DisplayVersion") as string, out Version? version))
                    return version;
            }
            catch
            {
                // Нет доступа к ветке реестра: пробуем следующую.
            }
        }

        return null;
    }
}
