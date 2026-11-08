using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcToolkit.Core.Cleanup;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Apps;

public enum LeftoverKind
{
    Folder,
    Shortcut,
    RegistryKey,

    /// <summary>Запись автозапуска (значение в разделе Run): <see cref="Leftover.ValueName"/> — её имя.</summary>
    RegistryValue,

    /// <summary>Задание Планировщика: <see cref="Leftover.Path"/> — «\Папка\Имя».</summary>
    ScheduledTask,
}

/// <summary>
/// Найденный след программы. Для реестра <see cref="Path"/> — «HKCU\Software\…» или «HKLM\SOFTWARE\…»,
/// <see cref="View"/> — какая часть реестра (64 или 32 бита), <see cref="ValueName"/> — имя значения (для автозапуска).
/// </summary>
public sealed record Leftover(LeftoverKind Kind, string Path, RegistryView View, long SizeBytes, bool NeedsAdmin, string Reason,
                              string? ValueName = null);

public sealed record LeftoverResult(Leftover Item, bool Removed, string? Error);

/// <summary>
/// Ищет то, что программа оставила после удаления: папку установки, папки с её именем в Program Files,
/// ProgramData и AppData, ярлыки, ключи реестра в Software. Правила осторожные:
/// только точное совпадение имени (без «похожих»), ничего общего с другими установленными программами,
/// никаких системных и личных папок (Документы, Рабочий стол целиком и т. п.) и ссылок на другие папки.
/// </summary>
public static partial class LeftoverScanner
{
    /// <summary>Слишком общие имена: по ним ничего не ищем, даже если программа так называется.</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "microsoft corporation", "windows", "common files", "programs", "packages", "temp", "cache",
        "update", "updater", "data", "config", "settings", "app", "apps", "games", "bin", "tools", "drivers",
        "intel", "intel corporation", "amd", "advanced micro devices", "nvidia", "nvidia corporation", "realtek",
        "google", "google llc", "mozilla", "oracle", "adobe", "apple", "classes", "clients", "policies", "software",
        "wow6432node", "odbc", "system", "default", "public", "users", "program files", "programdata",
    };

    /// <summary>Ключи первого уровня в Software, которые не трогаем никогда.</summary>
    private static readonly HashSet<string> ProtectedRegistryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Clients", "Policies", "RegisteredApplications", "Wow6432Node", "ODBC", "Khronos",
        "Intel", "NVIDIA Corporation", "Realtek", "AMD", "ATI Technologies", "Windows", "Partner", "OEM",
    };

    /// <param name="app">Программа, которую удалили (или удаляем принудительно).</param>
    /// <param name="others">Программы, которые остались установленными: их следы не трогаем.</param>
    /// <param name="includeUninstallEntry">Добавить и саму запись программы в списке (если деинсталлятора нет).</param>
    /// <param name="elevated">Приложение уже запущено с правами администратора.</param>
    public static IReadOnlyList<Leftover> Find(InstalledApp app, IReadOnlyList<InstalledApp> others, bool includeUninstallEntry,
        bool elevated)
    {
        var names = NameKeys(app);
        if (names.Count == 0)
            return Array.Empty<Leftover>();

        string publisher = Normalize(app.Publisher);
        var otherNames = others.Where(o => o.Id != app.Id).SelectMany(NameKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var otherLocations = others.Where(o => o.Id != app.Id && o.InstallLocation.Length > 3)
            .Select(o => SafeFull(o.InstallLocation)).Where(p => p.Length > 0).ToList();

        var result = new List<Leftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool SharedWithOthers(string folder) => otherLocations.Any(location =>
            string.Equals(location, folder, StringComparison.OrdinalIgnoreCase)
            || CleanupPaths.IsUnder(location, folder)
            || CleanupPaths.IsUnder(folder, location));

        void AddFolder(string folder, string reason)
        {
            string full = SafeFull(folder);
            if (full.Length == 0 || !seen.Add(full) || !Directory.Exists(full) || CleanupPaths.IsReparsePoint(full))
                return;
            if (!LeftoverRemover.IsAllowedLocation(full, shortcut: false) || SharedWithOthers(full))
                return;
            if (result.Any(r => r.Kind == LeftoverKind.Folder && CleanupPaths.IsUnder(full, r.Path)))
                return; // уже удаляется родительская папка
            result.RemoveAll(r => r.Kind == LeftoverKind.Folder && CleanupPaths.IsUnder(r.Path, full));
            result.Add(new Leftover(LeftoverKind.Folder, full, default, FolderSize(full), !elevated && NeedsAdminPath(full), reason));
        }

        // 1. Папка установки.
        if (app.InstallLocation.Length > 3)
            AddFolder(app.InstallLocation, "папка установки");

        // 2. Папки с именем программы (и «Издатель\Программа») в типичных местах.
        foreach ((string root, string place) in FolderRoots())
        {
            foreach (string directory in SafeDirectories(root))
            {
                string name = Normalize(System.IO.Path.GetFileName(directory));
                if (names.Contains(name) && !otherNames.Contains(name))
                {
                    AddFolder(directory, $"папка программы в {place}");
                }
                else if (publisher.Length > 0 && name == publisher && !GenericNames.Contains(name))
                {
                    var children = SafeDirectories(directory).ToList();
                    var matched = children.Where(c => names.Contains(Normalize(System.IO.Path.GetFileName(c)))).ToList();
                    bool hasFiles = SafeFiles(directory).Any();
                    if (matched.Count > 0 && matched.Count == children.Count && !hasFiles && !otherNames.Contains(name))
                        AddFolder(directory, $"папка издателя в {place}: в ней только эта программа");
                    else
                        foreach (string child in matched)
                            AddFolder(child, $"папка программы в {place}");
                }
            }
        }

        // 3. Ярлыки, которые ведут в папку программы или называются её именем и никуда не ведут.
        foreach (string shortcut in Shortcuts())
        {
            string name = Normalize(System.IO.Path.GetFileNameWithoutExtension(shortcut));
            string target = ShortcutTarget(shortcut);
            bool intoApp = app.InstallLocation.Length > 3 && target.Length > 0
                           && (CleanupPaths.IsUnder(target, app.InstallLocation) ||
                               string.Equals(SafeFull(target), SafeFull(app.InstallLocation), StringComparison.OrdinalIgnoreCase));
            bool broken = target.Length > 0 && !File.Exists(target) && !Directory.Exists(target);
            if ((intoApp || (names.Contains(name) && broken)) && !otherNames.Contains(name)
                && LeftoverRemover.IsAllowedLocation(shortcut, shortcut: true) && seen.Add(shortcut))
                result.Add(new Leftover(LeftoverKind.Shortcut, shortcut, default, 0, !elevated && NeedsAdminPath(shortcut),
                    intoApp ? "ярлык на программу" : "ярлык, который никуда не ведёт"));
        }

        // 4. Реестр: Software\Программа и Software\Издатель\Программа.
        foreach ((RegistryHive hive, RegistryView view, string prefix) in RegistryRoots())
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? software = root.OpenSubKey("Software");
                if (software is null)
                    continue;

                foreach (string keyName in software.GetSubKeyNames())
                {
                    if (ProtectedRegistryNames.Contains(keyName))
                        continue;
                    string name = Normalize(keyName);
                    bool needsAdmin = hive == RegistryHive.LocalMachine && !elevated;
                    if (names.Contains(name) && !otherNames.Contains(name))
                    {
                        AddKey(result, seen, $@"{prefix}\Software\{keyName}", view, needsAdmin, "настройки программы в реестре");
                    }
                    else if (publisher.Length > 0 && name == publisher && !GenericNames.Contains(name))
                    {
                        using RegistryKey? publisherKey = software.OpenSubKey(keyName);
                        if (publisherKey is null)
                            continue;
                        string[] children = publisherKey.GetSubKeyNames();
                        var matched = children.Where(c => names.Contains(Normalize(c))).ToList();
                        if (matched.Count > 0 && matched.Count == children.Length && publisherKey.ValueCount == 0 && !otherNames.Contains(name))
                            AddKey(result, seen, $@"{prefix}\Software\{keyName}", view, needsAdmin, "ключ издателя: в нём только эта программа");
                        else
                            foreach (string child in matched)
                                AddKey(result, seen, $@"{prefix}\Software\{keyName}\{child}", view, needsAdmin, "настройки программы в реестре");
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                AppLog.Warn($"Нет доступа к {prefix}\\Software при поиске следов.", ex);
            }
        }

        // 5. Запись в списке программ, если деинсталлятор не убрал её сам.
        if (includeUninstallEntry && app.Key is { } key && key.Exists())
            AddKey(result, seen, key.RegPath, key.View, key.Hive == RegistryHive.LocalMachine && !elevated, "запись в списке программ");

        // 6. Автозапуск и задания Планировщика, которые запускали файлы из папок программы, а этих файлов уже нет.
        foreach (string folder in AppFolders(app))
        {
            bool Inside(string target) => CleanupPaths.IsUnder(target, folder);
            IEnumerable<Orphan> broken = OrphanScanner.BrokenAutoruns(elevated, Inside);
            if (OperatingSystem.IsWindows())
                broken = broken.Concat(OrphanScanner.BrokenTasks(elevated, Inside));
            foreach (Orphan orphan in broken)
            {
                if (seen.Add($"{orphan.Item.Kind}|{orphan.Item.View}|{orphan.Item.Path}|{orphan.Item.ValueName}"))
                    result.Add(orphan.Item);
            }
        }

        return result;
    }

    /// <summary>Папки программы: папка установки и папка её деинсталлятора (если это не системная папка и не корень).</summary>
    internal static IEnumerable<string> AppFolders(InstalledApp app)
    {
        var folders = new List<string>();
        if (app.InstallLocation.Length > 3 && SafeFull(app.InstallLocation) is { Length: > 0 } location && CleanupPaths.IsSafeRoot(location, out _))
            folders.Add(location);
        if (Uninstaller.Parse(app) is { } command && System.IO.Path.IsPathFullyQualified(command.FileName)
            && System.IO.Path.GetDirectoryName(SafeFull(command.FileName)) is { Length: > 3 } directory
            && CleanupPaths.IsSafeRoot(directory, out _)
            && !folders.Any(f => f.Equals(directory, StringComparison.OrdinalIgnoreCase) || CleanupPaths.IsUnder(directory, f)))
            folders.Add(directory);
        return folders;
    }

    private static void AddKey(List<Leftover> result, HashSet<string> seen, string path, RegistryView view, bool needsAdmin, string reason)
    {
        if (seen.Add($"{view}|{path}"))
            result.Add(new Leftover(LeftoverKind.RegistryKey, path, view, 0, needsAdmin, reason));
    }

    /// <summary>Имена, по которым ищем: название программы и имя папки установки, в обычном виде.</summary>
    internal static HashSet<string> NameKeys(InstalledApp app)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string value)
        {
            string key = Normalize(value);
            if (key.Replace(" ", "").Length >= 4 && !GenericNames.Contains(key))
                keys.Add(key);
        }

        Add(app.Name);
        if (app.InstallLocation.Length > 3)
            Add(System.IO.Path.GetFileName(app.InstallLocation.TrimEnd('\\')));
        return keys;
    }

    /// <summary>«Steam (x64) 2.10.91.91» → «steam»; «Discord®» → «discord».</summary>
    internal static string Normalize(string value)
    {
        string text = value.ToLowerInvariant();
        text = Parentheses().Replace(text, " ");
        text = text.Replace("®", "").Replace("™", "").Replace("©", "");
        text = Versions().Replace(text, " ");
        text = Architecture().Replace(text, " ");
        text = NonWord().Replace(text, " ");
        return Spaces().Replace(text, " ").Trim();
    }

    internal static IEnumerable<(string Root, string Place)> FolderRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<(string, string)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppData\\Roaming"),
            (local, "AppData\\Local"),
            (System.IO.Path.Combine(local, "Programs"), "AppData\\Local\\Programs"),
            (System.IO.Path.Combine(System.IO.Path.GetDirectoryName(local) ?? local, "LocalLow"), "AppData\\LocalLow"),
            (Environment.GetFolderPath(Environment.SpecialFolder.Programs), "меню «Пуск»"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "меню «Пуск»"),
        };
        return roots.Where(r => r.Item1.Length > 0 && Directory.Exists(r.Item1)).Distinct();
    }

    private static IEnumerable<(RegistryHive, RegistryView, string)> RegistryRoots() => new[]
    {
        (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM"),
    };

    private static IEnumerable<string> Shortcuts()
    {
        var places = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.Programs), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true),
        };
        foreach ((string folder, bool recursive) in places)
        {
            if (folder.Length == 0 || !Directory.Exists(folder))
                continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MaxRecursionDepth = 3,
                }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
                yield return file;
        }
    }

    /// <summary>Куда ведёт ярлык (через WScript.Shell, он есть в любой Windows). Пусто — не удалось прочитать.</summary>
    internal static string ShortcutTarget(string shortcut)
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
                return "";
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic link = shell.CreateShortcut(shortcut);
                return (string)(link.TargetPath ?? "");
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception)
        {
            return "";
        }
    }

    internal static bool NeedsAdminPath(string path)
    {
        foreach (Environment.SpecialFolder folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.CommonPrograms,
                     Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonStartMenu,
                 })
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length > 0 && (CleanupPaths.IsUnder(path, root) || string.Equals(SafeFull(path), SafeFull(root), StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>Размер папки со всем содержимым (ссылки на другие папки не считаются). Не больше 200 000 файлов.</summary>
    public static long FolderSize(string folder)
    {
        long total = 0;
        int count = 0;
        try
        {
            foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                total += file.Length;
                if (++count > 200_000)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return total;
    }

    internal static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder).Where(d => !CleanupPaths.IsReparsePoint(d)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try
        {
            return Directory.GetFiles(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new[] { "?" }; // не смогли прочитать — считаем, что там что-то есть
        }
    }

    internal static string SafeFull(string path)
    {
        try
        {
            return CleanupPaths.Normalize(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Parentheses();

    [GeneratedRegex(@"\bv?\d+(\.\d+)+\w*\b|\b\d{4,}\b")]
    private static partial Regex Versions();

    [GeneratedRegex(@"\b(x64|x86|amd64|arm64|64-bit|32-bit|64 bit|32 bit|64bit|32bit)\b")]
    private static partial Regex Architecture();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// Удаляет найденные следы: папки и ярлыки — в Корзину (их можно вернуть оттуда), ключи реестра —
/// после резервной копии в .reg (вернуть — двойной щелчок по файлу). Перед удалением каждый пункт проверяется ещё раз.
/// </summary>
public static class LeftoverRemover
{
    public static IReadOnlyList<LeftoverResult> Remove(IEnumerable<Leftover> items, string backupFolder)
    {
        var results = new List<LeftoverResult>();
        int backupIndex = 0;
        // Сначала файлы (пока программа видна в реестре, её файлы проще опознать), потом автозапуск, задания и реестр.
        foreach (Leftover item in items.OrderBy(i => i.Kind switch
                 {
                     LeftoverKind.Folder or LeftoverKind.Shortcut => 0,
                     LeftoverKind.RegistryValue or LeftoverKind.ScheduledTask => 1,
                     _ => 2,
                 }))
        {
            try
            {
                string? problem = Validate(item);
                if (problem is not null)
                {
                    results.Add(new LeftoverResult(item, false, problem));
                    continue;
                }

                switch (item.Kind)
                {
                    case LeftoverKind.RegistryKey:
                        Directory.CreateDirectory(backupFolder);
                        RemoveKey(item, System.IO.Path.Combine(backupFolder, $"{++backupIndex:00}.reg"));
                        break;
                    case LeftoverKind.RegistryValue:
                        Directory.CreateDirectory(backupFolder);
                        RemoveValue(item, System.IO.Path.Combine(backupFolder, $"{++backupIndex:00}-autorun.reg"));
                        break;
                    case LeftoverKind.ScheduledTask:
                        Directory.CreateDirectory(backupFolder);
                        OrphanScanner.RemoveTask(item.Path, System.IO.Path.Combine(backupFolder, $"{++backupIndex:00}-task.xml"));
                        break;
                    default:
                        MoveToRecycleBin(item.Path);
                        break;
                }

                AppLog.Info($"Удалён след программы: {item.Path}");
                results.Add(new LeftoverResult(item, true, null));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось удалить {item.Path}.", ex);
                results.Add(new LeftoverResult(item, false, ex.Message));
            }
        }

        return results;
    }

    /// <summary>Повторная проверка перед удалением. Текст причины отказа или null.</summary>
    public static string? Validate(Leftover item)
    {
        switch (item.Kind)
        {
            case LeftoverKind.Folder:
                if (!Directory.Exists(item.Path))
                    return "папки уже нет";
                if (CleanupPaths.IsReparsePoint(item.Path))
                    return "это ссылка на другую папку";
                return IsAllowedLocation(item.Path, shortcut: false) ? null : "папка в системном или личном месте";

            case LeftoverKind.Shortcut:
                if (!item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    return "это не ярлык";
                if (!IsAllowedLocation(item.Path, shortcut: true))
                    return "ярлык не на рабочем столе и не в меню «Пуск»";
                return File.Exists(item.Path) ? null : "ярлыка уже нет";

            case LeftoverKind.RegistryKey:
                (RegistryHive hive, string sub) = SplitKey(item.Path);
                string[] parts = sub.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !parts[0].Equals("Software", StringComparison.OrdinalIgnoreCase))
                    return "ключ вне раздела Software";
                if (parts[1].Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
                    && !sub.StartsWith(UninstallKeyLocation.UninstallPath + "\\", StringComparison.OrdinalIgnoreCase))
                    return "системный ключ Microsoft";
                if (parts[1] is "Classes" or "Policies" or "Clients" or "Wow6432Node" or "WOW6432Node")
                    return "системный ключ";
                if (sub.Equals(UninstallKeyLocation.UninstallPath, StringComparison.OrdinalIgnoreCase))
                    return "весь список программ удалять нельзя";
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, item.View))
                using (RegistryKey? key = root.OpenSubKey(sub))
                    return key is null ? "ключа уже нет" : null;

            case LeftoverKind.RegistryValue:
            {
                (RegistryHive valueHive, string valueKey) = SplitKey(item.Path);
                if (!valueKey.Equals(OrphanScanner.RunKeyPath, StringComparison.OrdinalIgnoreCase))
                    return "это не раздел автозапуска";
                if (string.IsNullOrEmpty(item.ValueName))
                    return "нет имени записи";
                using RegistryKey root = RegistryKey.OpenBaseKey(valueHive, item.View);
                using RegistryKey? key = root.OpenSubKey(valueKey);
                if (key?.GetValue(item.ValueName) is not { } value)
                    return "записи уже нет";
                if (key.GetValueKind(item.ValueName) is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                    return "необычный тип записи";
                return OrphanScanner.IsMissingLocalPath(OrphanScanner.CommandTarget(value.ToString() ?? "")) ? null : "файл автозапуска снова на месте";
            }

            case LeftoverKind.ScheduledTask:
                return OrphanScanner.ValidateTask(item.Path);

            default:
                return "неизвестный тип";
        }
    }

    /// <summary>
    /// Можно ли удалять здесь. Никогда — в папке Windows, в корнях системных папок и выше них;
    /// в профиле пользователя — только в AppData и меню «Пуск» (ярлыки — ещё и на рабочем столе), но не в Документах и т. п.
    /// </summary>
    public static bool IsAllowedLocation(string path, bool shortcut)
    {
        string full;
        try
        {
            full = CleanupPaths.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && (CleanupPaths.IsUnder(full, windows) || full.Equals(CleanupPaths.Normalize(windows), StringComparison.OrdinalIgnoreCase)))
            return false;

        string container = shortcut ? System.IO.Path.GetDirectoryName(full) ?? full : full;
        if (!shortcut && !CleanupPaths.IsSafeRoot(full, out _))
            return false;

        string[] shortcutRoots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        if (shortcut)
            return shortcutRoots.Any(r => r.Length > 0 && (CleanupPaths.IsUnder(full, r)));

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string usersRoot = System.IO.Path.GetDirectoryName(profile) ?? profile;
        if (profile.Length > 0 && (CleanupPaths.IsUnder(container, usersRoot)))
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] allowedInProfile =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                local,
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(local) ?? local, "LocalLow"),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            };
            return allowedInProfile.Any(r => r.Length > 0 && CleanupPaths.IsUnder(container, r));
        }

        return true;
    }

    private static void RemoveKey(Leftover item, string backup)
    {
        (RegistryHive hive, string sub) = SplitKey(item.Path);
        string view = item.View == RegistryView.Registry32 ? "/reg:32" : "/reg:64";
        var export = Platform.ProcessRunner.RunAsync(System.IO.Path.Combine(Environment.SystemDirectory, "reg.exe"),
            new[] { "export", item.Path, backup, "/y", view }, TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        if (export.ExitCode != 0 || !File.Exists(backup))
            throw new IOException($"Не удалось сделать резервную копию ключа: {export.Error.Trim()}");

        using RegistryKey root = RegistryKey.OpenBaseKey(hive, item.View);
        root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
    }

    /// <summary>Убирает запись автозапуска, сохранив её в .reg-файл (двойной щелчок по нему вернёт запись).</summary>
    private static void RemoveValue(Leftover item, string backup)
    {
        (RegistryHive hive, string sub) = SplitKey(item.Path);
        using RegistryKey root = RegistryKey.OpenBaseKey(hive, item.View);
        using RegistryKey key = root.OpenSubKey(sub, writable: true) ?? throw new IOException("Раздела автозапуска нет.");
        string name = item.ValueName!;
        object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? throw new IOException("Записи уже нет.");
        File.WriteAllText(backup, RegFile(RegKeyName(hive, item.View, sub), name, value.ToString() ?? "", key.GetValueKind(name)), Encoding.Unicode);
        key.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>Полное имя раздела для .reg-файла; у 32-битной части HKLM — через WOW6432Node.</summary>
    internal static string RegKeyName(RegistryHive hive, RegistryView view, string sub)
    {
        if (hive == RegistryHive.LocalMachine && view == RegistryView.Registry32 && sub.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase))
            sub = "SOFTWARE\\WOW6432Node\\" + sub["Software\\".Length..];
        return (hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER") + "\\" + sub;
    }

    /// <summary>Текст .reg-файла с одним строковым значением (REG_SZ или REG_EXPAND_SZ).</summary>
    public static string RegFile(string keyName, string valueName, string value, RegistryValueKind kind)
    {
        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
        text.Append('[').Append(keyName).Append("]\r\n");
        text.Append('"').Append(Escape(valueName)).Append("\"=");
        if (kind == RegistryValueKind.ExpandString)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(value + "\0");
            text.Append("hex(2):").Append(string.Join(",", bytes.Select(b => b.ToString("x2"))));
        }
        else
        {
            text.Append('"').Append(Escape(value)).Append('"');
        }

        return text.Append("\r\n").ToString();
    }

    internal static (RegistryHive Hive, string SubKey) SplitKey(string path)
    {
        int slash = path.IndexOf('\\');
        string hive = slash > 0 ? path[..slash] : path;
        string sub = slash > 0 ? path[(slash + 1)..] : "";
        return hive.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => (RegistryHive.CurrentUser, sub),
            "HKLM" or "HKEY_LOCAL_MACHINE" => (RegistryHive.LocalMachine, sub),
            _ => throw new ArgumentException($"Неподдерживаемый раздел реестра: {hive}"),
        };
    }

    /// <summary>Перемещает файл или папку в Корзину (без окон подтверждения).</summary>
    internal static void MoveToRecycleBin(string path)
    {
        var operation = new ShFileOperation
        {
            Func = 3, // FO_DELETE
            From = path + "\0\0",
            // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR
            Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400 | 0x0200,
        };
        int code = SHFileOperation(ref operation);
        if (code != 0 || operation.AnyOperationsAborted)
            throw new IOException($"Windows не смогла переместить в Корзину (код {code}).");
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("Файл или папка заняты другой программой.");
    }

    /// <summary>Запрос на удаление для копии приложения с правами администратора (JSON-файл).</summary>
    public sealed class Request
    {
        public List<Leftover> Items { get; set; } = new();

        public string BackupFolder { get; set; } = "";
    }

    public static void WriteRequest(string path, IEnumerable<Leftover> items, string backupFolder) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new Request { Items = items.ToList(), BackupFolder = backupFolder }));

    /// <summary>Выполняется в копии с правами администратора: читает запрос, удаляет, пишет результат рядом.</summary>
    public static int ExecuteRequest(string requestPath)
    {
        try
        {
            Request request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath)) ?? new Request();
            string backup = request.BackupFolder.Length > 0 && System.IO.Path.IsPathFullyQualified(request.BackupFolder)
                ? request.BackupFolder
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "backups", "apps");
            IReadOnlyList<LeftoverResult> results = Remove(request.Items, backup);
            File.WriteAllText(requestPath + ".result.json", JsonSerializer.Serialize(results));
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось выполнить удаление следов с правами администратора.", ex);
            return 1;
        }
    }

    public static IReadOnlyList<LeftoverResult> ReadResults(string requestPath)
    {
        string path = requestPath + ".result.json";
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<LeftoverResult>>(File.ReadAllText(path)) ?? new List<LeftoverResult>()
            : new List<LeftoverResult>();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOperation operation);
}
