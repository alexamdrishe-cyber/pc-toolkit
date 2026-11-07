using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Platform;

namespace PcToolkit.Core.Apps;

public enum AdviceLevel
{
    /// <summary>Нет рекомендации (приложение, установленное пользователем).</summary>
    None,

    /// <summary>Можно удалить: реклама и редко нужные приложения, предустановленные с Windows.</summary>
    Remove,

    /// <summary>На ваше усмотрение.</summary>
    Optional,

    /// <summary>Лучше оставить.</summary>
    Keep,

    /// <summary>Удалять нельзя: без него сломается магазин или защита.</summary>
    Blocked,
}

public sealed record AppAdvice(AdviceLevel Level, string Note);

/// <summary>Рекомендации для предустановленных приложений Microsoft Store (store-apps.json).</summary>
public static class StoreAppAdvice
{
    private static readonly Lazy<IReadOnlyList<(string Match, AppAdvice Advice)>> Entries = new(Load);

    public static AppAdvice For(string packageName)
    {
        foreach ((string match, AppAdvice advice) in Entries.Value)
        {
            bool hit = match.EndsWith('*')
                ? packageName.StartsWith(match[..^1], StringComparison.OrdinalIgnoreCase)
                : string.Equals(packageName, match, StringComparison.OrdinalIgnoreCase);
            if (hit)
                return advice;
        }

        return new AppAdvice(AdviceLevel.None, "");
    }

    private static IReadOnlyList<(string, AppAdvice)> Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PcToolkit.Core.Apps.store-apps.json")
                              ?? throw new InvalidOperationException("Нет встроенного каталога store-apps.json.");
        using JsonDocument document = JsonDocument.Parse(stream);
        var list = new List<(string, AppAdvice)>();
        foreach (JsonElement item in document.RootElement.GetProperty("apps").EnumerateArray())
        {
            AdviceLevel level = item.GetProperty("level").GetString() switch
            {
                "remove" => AdviceLevel.Remove,
                "optional" => AdviceLevel.Optional,
                "keep" => AdviceLevel.Keep,
                "blocked" => AdviceLevel.Blocked,
                _ => AdviceLevel.None,
            };
            list.Add((item.GetProperty("match").GetString() ?? "", new AppAdvice(level, item.GetProperty("note").GetString() ?? "")));
        }

        return list;
    }
}

/// <summary>
/// Приложения Microsoft Store текущего пользователя (Get-AppxPackage). Системные пакеты, библиотеки (framework)
/// и неудаляемые пакеты не показываются. Удаление — только для текущего пользователя (Remove-AppxPackage),
/// поэтому его обычно можно вернуть: повторной регистрацией или из магазина.
/// </summary>
public static class StoreAppsProvider
{
    public static async Task<IReadOnlyList<InstalledApp>> ListAsync(CancellationToken cancellation = default)
    {
        const string Script = """
            $items = Get-AppxPackage -PackageTypeFilter Main |
                Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' } |
                Select-Object Name, PackageFullName, PackageFamilyName, Version, InstallLocation
            ConvertTo-Json -InputObject @($items) -Compress
            """;
        ProcessResult result = await ProcessRunner.PowerShellAsync(Script, TimeSpan.FromSeconds(60), cancellation);
        if (result.ExitCode != 0 || result.Output.Trim().Length == 0)
        {
            AppLog.Warn($"Не удалось получить список приложений Microsoft Store: {result.Error.Trim()}");
            return Array.Empty<InstalledApp>();
        }

        var apps = new List<InstalledApp>();
        using JsonDocument document = JsonDocument.Parse(result.Output.Trim());
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string name = Get(item, "Name");
            string fullName = Get(item, "PackageFullName");
            string location = Get(item, "InstallLocation");
            if (name.Length == 0 || fullName.Length == 0)
                continue;

            (string displayName, string publisher, string logo) = ReadManifest(location, name, fullName);
            DateTime? installed = null;
            try
            {
                if (location.Length > 0 && Directory.Exists(location))
                    installed = Directory.GetCreationTime(location);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            apps.Add(new InstalledApp
            {
                Id = "store|" + fullName,
                Kind = AppKind.Store,
                Name = displayName.Length > 0 ? displayName : PrettyName(name),
                Publisher = publisher,
                Version = Get(item, "Version"),
                InstallDate = installed,
                InstallLocation = location,
                PackageFullName = fullName,
                PackageFamilyName = Get(item, "PackageFamilyName"),
                PackageName = name,
                LogoPath = logo,
            });
        }

        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Удаляет приложение для текущего пользователя. Текст ошибки или null.</summary>
    public static async Task<string?> RemoveAsync(InstalledApp app, CancellationToken cancellation = default)
    {
        if (app.Kind != AppKind.Store)
            throw new ArgumentException("Это не приложение Microsoft Store.", nameof(app));
        if (StoreAppAdvice.For(app.PackageName).Level == AdviceLevel.Blocked)
            return "Это приложение удалять нельзя.";

        ProcessResult result = await ProcessRunner.PowerShellAsync(
            $"Remove-AppxPackage -Package {ProcessRunner.PsQuote(app.PackageFullName)} -ErrorAction Stop",
            TimeSpan.FromMinutes(3), cancellation);
        if (result.ExitCode == 0)
        {
            AppLog.Info($"Удалено приложение Microsoft Store: {app.Name} ({app.PackageFullName}).");
            return null;
        }

        string error = result.Error.Trim();
        AppLog.Warn($"Не удалось удалить {app.PackageFullName}: {error}");
        return error.Length > 0 ? FirstLine(error) : $"PowerShell завершился с кодом {result.ExitCode}.";
    }

    /// <summary>
    /// Возвращает удалённое приложение: регистрирует пакет заново, если его файлы ещё на диске
    /// (так бывает, если приложение установлено для других пользователей или встроено в образ Windows).
    /// false — файлов нет: тогда остаётся страница в магазине (<see cref="StorePageUri"/>).
    /// </summary>
    public static async Task<bool> RestoreAsync(InstalledApp app, CancellationToken cancellation = default)
    {
        string manifest = Path.Combine(app.InstallLocation, "AppxManifest.xml");
        if (app.InstallLocation.Length == 0 || !File.Exists(manifest))
            return false;

        ProcessResult result = await ProcessRunner.PowerShellAsync(
            $"Add-AppxPackage -DisableDevelopmentMode -Register {ProcessRunner.PsQuote(manifest)} -ErrorAction Stop",
            TimeSpan.FromMinutes(3), cancellation);
        if (result.ExitCode != 0)
            AppLog.Warn($"Не удалось вернуть {app.PackageFullName}: {result.Error.Trim()}");
        return result.ExitCode == 0;
    }

    public static string StorePageUri(InstalledApp app) => $"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(app.PackageFamilyName)}";

    /// <summary>Имя, издатель и логотип из AppxManifest.xml; строки «ms-resource:» переводятся через Windows.</summary>
    internal static (string Name, string Publisher, string Logo) ReadManifest(string location, string packageName, string fullName)
    {
        try
        {
            string path = Path.Combine(location, "AppxManifest.xml");
            if (location.Length == 0 || !File.Exists(path))
                return ("", "", "");

            XDocument document = XDocument.Load(path);
            XElement? properties = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            string Value(string name) => properties?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";

            string displayName = Resolve(Value("DisplayName"), packageName, fullName);
            string publisher = Resolve(Value("PublisherDisplayName"), packageName, fullName);
            string logo = FindLogo(location, Value("Logo"));
            return (displayName, publisher, logo);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось прочитать манифест {packageName}.", ex);
            return ("", "", "");
        }
    }

    internal static string Resolve(string value, string packageName, string fullName)
    {
        if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return value;

        string key = value["ms-resource:".Length..];
        var candidates = new List<string>();
        if (key.StartsWith("//", StringComparison.Ordinal))
            candidates.Add("ms-resource:" + key);
        else if (key.StartsWith('/'))
            candidates.Add($"ms-resource://{packageName}{key}");
        else
        {
            candidates.Add($"ms-resource://{packageName}/Resources/{key}");
            candidates.Add($"ms-resource://{packageName}/{key}");
        }

        foreach (string candidate in candidates)
        {
            var buffer = new StringBuilder(512);
            if (SHLoadIndirectString($"@{{{fullName}?{candidate}}}", buffer, (uint)buffer.Capacity, IntPtr.Zero) == 0
                && buffer.Length > 0 && !buffer.ToString().StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                return buffer.ToString();
        }

        return "";
    }

    /// <summary>Файл логотипа: в пакете лежат варианты «StoreLogo.scale-100.png», «…targetsize-48.png».</summary>
    internal static string FindLogo(string location, string logo)
    {
        if (logo.Length == 0)
            return "";

        string path = Path.Combine(location, logo);
        if (File.Exists(path))
            return path;

        string? folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder))
            return "";

        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string[] variants = Directory.GetFiles(folder, stem + ".*" + extension);
        return variants
            .OrderBy(f => f.Contains("scale-100", StringComparison.OrdinalIgnoreCase) ? 0
                : f.Contains("targetsize-48", StringComparison.OrdinalIgnoreCase) ? 1
                : f.Contains("scale-200", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
            .FirstOrDefault() ?? "";
    }

    /// <summary>«Microsoft.BingWeather» → «Bing Weather».</summary>
    internal static string PrettyName(string packageName)
    {
        string tail = packageName.Contains('.') ? packageName[(packageName.LastIndexOf('.') + 1)..] : packageName;
        var builder = new StringBuilder();
        for (int i = 0; i < tail.Length; i++)
        {
            if (i > 0 && char.IsUpper(tail[i]) && char.IsLower(tail[i - 1]))
                builder.Append(' ');
            builder.Append(tail[i]);
        }

        return builder.ToString();
    }

    private static string Get(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint capacity, IntPtr reserved);
}
