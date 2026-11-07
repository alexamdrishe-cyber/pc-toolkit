using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Apps;

/// <summary>
/// Список установленных программ — тот же, что в «Программы и компоненты»: записи Uninstall
/// из HKLM (64- и 32-битной частей реестра) и HKCU. Обновления Windows, системные компоненты
/// и записи без деинсталлятора не показываются. Только чтение.
/// </summary>
public static partial class InstalledAppsProvider
{
    public static IReadOnlyList<InstalledApp> ListDesktop()
    {
        var apps = new List<InstalledApp>();
        foreach ((RegistryHive hive, RegistryView view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                     (RegistryHive.CurrentUser, RegistryView.Registry32),
                 })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? uninstall = root.OpenSubKey(UninstallKeyLocation.UninstallPath);
                if (uninstall is null)
                    continue;

                foreach (string name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? key = uninstall.OpenSubKey(name);
                        if (key is not null && Read(key, new UninstallKeyLocation(hive, view, name)) is { } app)
                            apps.Add(app);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                    {
                        // Нет доступа к одной записи — пропускаем её.
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось прочитать список программ ({hive}, {view}).", ex);
            }
        }

        // В HKCU 32- и 64-битная части обычно совпадают, а некоторые программы пишут себя в обе части HKLM.
        return apps
            .GroupBy(a => (a.Name.ToLowerInvariant(), a.Version, a.Key!.Hive, a.Key.KeyName.ToLowerInvariant()))
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal static InstalledApp? Read(RegistryKey key, UninstallKeyLocation location)
    {
        string name = Str(key, "DisplayName");
        if (name.Length == 0)
            return null;
        if (Num(key, "SystemComponent") == 1)
            return null;
        if (Str(key, "ParentKeyName").Length > 0)
            return null;
        string releaseType = Str(key, "ReleaseType");
        if (releaseType is "Update" or "Hotfix" or "Security Update" or "Service Pack")
            return null;
        if (KbPattern().IsMatch(location.KeyName))
            return null;

        string uninstall = Str(key, "UninstallString");
        bool isMsi = Num(key, "WindowsInstaller") == 1 || uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase);
        if (uninstall.Length == 0 && !(isMsi && IsGuid(location.KeyName)))
            return null;

        long? size = Num(key, "EstimatedSize") is long kb and > 0 ? kb * 1024 : null;
        return new InstalledApp
        {
            Id = $"{location.Hive}|{location.View}|{location.KeyName}",
            Kind = AppKind.Desktop,
            Name = name.Trim(),
            Publisher = Str(key, "Publisher"),
            Version = Str(key, "DisplayVersion"),
            InstallDate = ParseDate(Str(key, "InstallDate")),
            SizeBytes = size,
            InstallLocation = CleanPath(Str(key, "InstallLocation")),
            UninstallString = uninstall,
            QuietUninstallString = Str(key, "QuietUninstallString"),
            DisplayIcon = Str(key, "DisplayIcon"),
            Key = location,
            IsMsi = isMsi,
        };
    }

    /// <summary>Дата установки из реестра: «20241015».</summary>
    internal static DateTime? ParseDate(string value) =>
        DateTime.TryParseExact(value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            ? date
            : null;

    internal static bool IsGuid(string value) => Guid.TryParse(value.Trim(), out _);

    /// <summary>Путь без кавычек и завершающего «\».</summary>
    internal static string CleanPath(string path)
    {
        string trimmed = path.Trim().Trim('"').Trim();
        if (trimmed.Length == 0)
            return "";
        trimmed = Environment.ExpandEnvironmentVariables(trimmed);
        return trimmed.Length > 3 ? trimmed.TrimEnd('\\', '/') : trimmed;
    }

    private static string Str(RegistryKey key, string name) =>
        key.GetValue(name) is { } value ? (value.ToString() ?? "").Trim() : "";

    private static long? Num(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int i => (uint)i,
        long l => l,
        string s when long.TryParse(s, out long parsed) => parsed,
        _ => null,
    };

    [GeneratedRegex(@"^KB\d{5,}", RegexOptions.IgnoreCase)]
    private static partial Regex KbPattern();
}
