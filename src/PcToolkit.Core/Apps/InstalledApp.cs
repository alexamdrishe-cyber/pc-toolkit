using Microsoft.Win32;

namespace PcToolkit.Core.Apps;

public enum AppKind
{
    /// <summary>Обычная программа с деинсталлятором (запись в Uninstall).</summary>
    Desktop,

    /// <summary>Приложение Microsoft Store (пакет AppX/MSIX).</summary>
    Store,
}

/// <summary>Где лежит запись программы в реестре.</summary>
public sealed record UninstallKeyLocation(RegistryHive Hive, RegistryView View, string KeyName)
{
    public const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public string SubKeyPath => $@"{UninstallPath}\{KeyName}";

    /// <summary>Путь для reg.exe: «HKLM\Software\…».</summary>
    public string RegPath => $@"{(Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{SubKeyPath}";

    public bool Exists()
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(Hive, View);
            using RegistryKey? key = root.OpenSubKey(SubKeyPath);
            return key is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return true;
        }
    }
}

/// <summary>Установленное приложение.</summary>
public sealed class InstalledApp
{
    public required string Id { get; init; }

    public required AppKind Kind { get; init; }

    public required string Name { get; init; }

    public string Publisher { get; init; } = "";

    public string Version { get; init; } = "";

    public DateTime? InstallDate { get; init; }

    /// <summary>Размер по данным установщика или посчитанный по папке; null — неизвестно.</summary>
    public long? SizeBytes { get; set; }

    public string InstallLocation { get; init; } = "";

    // ---- Программы ----

    public string UninstallString { get; init; } = "";

    public string QuietUninstallString { get; init; } = "";

    /// <summary>Значок: «C:\…\app.exe,0» или путь к .ico.</summary>
    public string DisplayIcon { get; init; } = "";

    public UninstallKeyLocation? Key { get; init; }

    /// <summary>Установлена для всех пользователей (HKLM) или только для текущего (HKCU).</summary>
    public bool ForAllUsers => Key?.Hive == RegistryHive.LocalMachine;

    public bool IsMsi { get; init; }

    // ---- Microsoft Store ----

    public string PackageFullName { get; init; } = "";

    public string PackageFamilyName { get; init; } = "";

    /// <summary>Имя пакета, например «Microsoft.BingWeather».</summary>
    public string PackageName { get; init; } = "";

    /// <summary>Файл логотипа пакета.</summary>
    public string LogoPath { get; init; } = "";

    public override string ToString() => $"{Name} {Version}".Trim();
}
