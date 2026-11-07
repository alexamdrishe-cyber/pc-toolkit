using Microsoft.Win32;

namespace PcToolkit.Core.Platform;

/// <summary>Понятное название Windows: «Windows 11 Pro 24H2 (сборка 26100.4061)».</summary>
public static class OsInfo
{
    private static readonly Lazy<string> Name = new(BuildFriendlyName);

    public static string FriendlyName => Name.Value;

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static string BuildFriendlyName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string build = key?.GetValue("CurrentBuildNumber") as string
                           ?? Environment.OSVersion.Version.Build.ToString();
            string displayVersion = key?.GetValue("DisplayVersion") as string ?? "";
            string edition = key?.GetValue("EditionID") as string ?? "";
            object? ubr = key?.GetValue("UBR");

            // В реестре Windows 11 до сих пор пишет «Windows 10» в ProductName, поэтому определяем по номеру сборки.
            int.TryParse(build, out int buildNumber);
            string name = buildNumber >= 22000 ? "Windows 11" : "Windows 10";

            string editionName = edition switch
            {
                "Core" or "CoreSingleLanguage" => "Домашняя",
                "Professional" => "Pro",
                "ProfessionalWorkstation" => "Pro для рабочих станций",
                "Enterprise" => "Корпоративная",
                "Education" => "Для образовательных учреждений",
                "" => "",
                _ => edition,
            };

            string fullBuild = ubr is int revision ? $"{build}.{revision}" : build;
            string result = string.Join(" ", new[] { name, editionName, displayVersion }.Where(p => p.Length > 0));
            return $"{result} (сборка {fullBuild})";
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }
}
