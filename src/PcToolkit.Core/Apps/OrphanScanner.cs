using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PcToolkit.Core.Cleanup;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Apps;

/// <summary>Остаток удалённой программы: найденный след и чей он (имя ярлыка, программы, записи автозапуска или задания).</summary>
public sealed record Orphan(string Name, Leftover Item);

/// <summary>
/// Ищет остатки программ, удалённых раньше (не через PC Toolkit или без поиска следов):
/// ярлыки, автозапуск и задания Планировщика, которые ведут на несуществующие файлы; записи в списке программ,
/// у которых нет ни программы, ни деинсталлятора (вместе с их следами); пустые папки программ.
/// Только надёжные признаки: файл должен отсутствовать на подключённом несъёмном диске — ярлык на флешку или
/// отключённый диск сломанным не считается. Непустые папки «без хозяина» не предлагаются: по имени не отличить
/// остаток от сохранений игры или портативной программы.
/// </summary>
public static class OrphanScanner
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Папки Windows и драйверов, которые бывают пустыми и нужны пустыми.</summary>
    private static readonly HashSet<string> KeepEmpty = new(StringComparer.OrdinalIgnoreCase)
    {
        "CrashDumps", "D3DSCache", "VirtualStore", "PeerDistRepub", "ConnectedDevicesPlatform", "Comms", "History", "Publishers",
        "ElevatedDiagnostics", "PlaceholderTileLogoFolder", "Temp", "Packages", "Programs", "Package Cache", "ssh", "USOPrivate",
        "USOShared", "SoftwareDistribution", "Common Files", "Reference Assemblies", "Uninstall Information", "Internet Explorer",
        "ModifiableWindowsApps", "MSBuild", "dotnet", "Startup", "Accessibility", "Accessories", "Administrative Tools",
        "Maintenance", "System Tools", "regid.1991-06.com.microsoft", "Application Data", "Desktop", "Documents", "Start Menu",
        "Templates", "Favorites", "NVIDIA", "NVIDIA Corporation", "AMD", "ATI", "Intel", "Realtek", "IsolatedStorage", "assembly",
        "Apps", "Deployment", "GroupPolicy", "SquirrelTemp", "PcToolkit", "PC Toolkit", "PawnIO",
    };

    public static IReadOnlyList<Orphan> Find(IReadOnlyList<InstalledApp> installed, bool elevated, IProgress<string>? progress = null)
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<Orphan>();

        var found = new List<Orphan>();
        void Step(string text, Func<IEnumerable<Orphan>> scan)
        {
            progress?.Report(text);
            try
            {
                found.AddRange(scan());
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Поиск остатков: ошибка на шаге «{text}».", ex);
            }
        }

        Step("Проверяю ярлыки на рабочем столе и в меню «Пуск»…", () => BrokenShortcuts(elevated));
        Step("Проверяю автозапуск…", () => BrokenAutoruns(elevated, null));
        Step("Проверяю задания Планировщика…", () => BrokenTasks(elevated, null));
        Step("Проверяю список программ…", () => BrokenUninstallEntries(installed, elevated));
        Step("Ищу пустые папки программ…", () => EmptyFolders(installed, elevated));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Orphan>();
        foreach (Orphan orphan in found)
        {
            Leftover item = orphan.Item;
            if (seen.Add($"{item.Kind}|{item.View}|{item.Path}|{item.ValueName}"))
                result.Add(orphan);
        }

        // Папка, которая уходит целиком, забирает и то, что внутри неё.
        var folders = result.Where(o => o.Item.Kind == LeftoverKind.Folder).Select(o => o.Item.Path).ToList();
        result.RemoveAll(o => o.Item.Kind is LeftoverKind.Folder or LeftoverKind.Shortcut
                              && folders.Any(f => CleanupPaths.IsUnder(o.Item.Path, f)));
        AppLog.Info($"Поиск остатков удалённых программ: найдено {result.Count} " +
                    $"(ярлыков {result.Count(o => o.Item.Kind == LeftoverKind.Shortcut)}, автозапуска {result.Count(o => o.Item.Kind == LeftoverKind.RegistryValue)}, " +
                    $"заданий {result.Count(o => o.Item.Kind == LeftoverKind.ScheduledTask)}, папок {result.Count(o => o.Item.Kind == LeftoverKind.Folder)}, " +
                    $"записей реестра {result.Count(o => o.Item.Kind == LeftoverKind.RegistryKey)}).");
        return result;
    }

    // ---- Ярлыки ----

    private static List<Orphan> BrokenShortcuts(bool elevated)
    {
        var result = new List<Orphan>();
        foreach (string desktop in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                 })
        {
            foreach (string file in Shortcuts(desktop))
            {
                if (BrokenTarget(file) is { } target)
                    result.Add(ShortcutOrphan(file, target, elevated));
            }
        }

        foreach (string programs in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                 })
        {
            if (programs.Length == 0 || !Directory.Exists(programs))
                continue;

            foreach (string file in Shortcuts(programs))
            {
                if (BrokenTarget(file) is { } target)
                    result.Add(ShortcutOrphan(file, target, elevated));
            }

            // Папка программы в меню «Пуск», где все ярлыки сломаны, предлагается целиком.
            foreach (string folder in LeftoverScanner.SafeDirectories(programs))
            {
                List<string>? files = FilesIn(folder);
                if (files is null)
                    continue;

                var shortcuts = files.Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)).ToList();
                var broken = shortcuts.Select(f => (File: f, Target: BrokenTarget(f))).Where(x => x.Target is not null).ToList();
                if (broken.Count == 0)
                    continue;

                bool onlyBroken = broken.Count == shortcuts.Count
                                  && files.All(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                                                    || Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));
                if (onlyBroken && !KeepEmpty.Contains(Path.GetFileName(folder)) && LeftoverRemover.IsAllowedLocation(folder, shortcut: false))
                {
                    result.Add(new Orphan(Path.GetFileName(folder), new Leftover(LeftoverKind.Folder, folder, default, 0,
                        !elevated && LeftoverScanner.NeedsAdminPath(folder),
                        $"папка в меню «Пуск»: все ярлыки в ней ведут на удалённые файлы (например, {broken[0].Target})")));
                }
                else
                {
                    foreach ((string file, string? target) in broken)
                        result.Add(ShortcutOrphan(file, target!, elevated));
                }
            }
        }

        return result;
    }

    private static Orphan ShortcutOrphan(string file, string target, bool elevated) =>
        new(Path.GetFileNameWithoutExtension(file), new Leftover(LeftoverKind.Shortcut, file, default, 0,
            !elevated && LeftoverScanner.NeedsAdminPath(file), $"ярлык ведёт на {target} — такого файла уже нет"));

    /// <summary>Куда вёл ярлык, если этого файла уже нет; null — ярлык рабочий или проверить нельзя.</summary>
    private static string? BrokenTarget(string shortcut)
    {
        string target = LeftoverScanner.ShortcutTarget(shortcut);
        return IsMissingLocalPath(target) ? target : null;
    }

    private static IEnumerable<string> Shortcuts(string folder)
    {
        if (folder.Length == 0 || !Directory.Exists(folder))
            return Array.Empty<string>();
        try
        {
            return Directory.GetFiles(folder, "*.lnk");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Все файлы папки до трёх уровней вглубь; null — что-то не прочитать или есть ссылки на другие папки.</summary>
    private static List<string>? FilesIn(string folder)
    {
        try
        {
            var files = new List<string>();
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         MaxRecursionDepth = 3,
                         IgnoreInaccessible = false,
                         AttributesToSkip = 0,
                     }))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return null;
                if ((attributes & FileAttributes.Directory) == 0)
                    files.Add(entry);
            }

            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Автозапуск ----

    /// <summary>
    /// Записи автозапуска (HKCU и HKLM, 64 и 32 бита), которые запускают несуществующий файл.
    /// <paramref name="filter"/> — дополнительное условие на этот файл (например, «внутри папки программы»).
    /// </summary>
    internal static List<Orphan> BrokenAutoruns(bool elevated, Func<string, bool>? filter)
    {
        var result = new List<Orphan>();
        if (!OperatingSystem.IsWindows())
            return result;

        foreach ((RegistryHive hive, RegistryView view, string prefix) in new[]
                 {
                     (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU"),
                     (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"),
                     (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM"),
                 })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? run = root.OpenSubKey(RunKeyPath);
                if (run is null)
                    continue;

                foreach (string name in run.GetValueNames())
                {
                    if (name.Length == 0 || run.GetValueKind(name) is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                        continue;
                    string target = CommandTarget(run.GetValue(name)?.ToString() ?? "");
                    if (!IsMissingLocalPath(target) || (filter is not null && !filter(target)))
                        continue;
                    result.Add(new Orphan(name, new Leftover(LeftoverKind.RegistryValue, $@"{prefix}\{RunKeyPath}", view, 0,
                        hive == RegistryHive.LocalMachine && !elevated, $"автозапуск {target} — такого файла уже нет", name)));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                AppLog.Warn($"Нет доступа к автозапуску {prefix} ({view}).", ex);
            }
        }

        return result;
    }

    /// <summary>
    /// Файл, который запускает команда: «"C:\a b\x.exe" -min» → «C:\a b\x.exe», «C:\a b\x.exe /s» → «C:\a b\x.exe»;
    /// у rundll32 — библиотека из первого аргумента. Пусто — понять нельзя (тогда запись не трогаем).
    /// </summary>
    public static string CommandTarget(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command).Trim();
        if (command.Length == 0)
            return "";

        string program;
        string rest;
        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            if (end < 0)
                return "";
            program = command[1..end];
            rest = command[(end + 1)..].Trim();
        }
        else
        {
            int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0 && (exe + 4 == command.Length || command[exe + 4] == ' '))
            {
                program = command[..(exe + 4)];
                rest = command[(exe + 4)..].Trim();
            }
            else
            {
                // Без кавычек и без «.exe» путь с пробелами не разобрать надёжно — берём только путь без пробелов с расширением.
                int space = command.IndexOf(' ');
                program = space > 0 ? command[..space] : command;
                rest = space > 0 ? command[(space + 1)..].Trim() : "";
                if (!Path.HasExtension(program))
                    return "";
            }
        }

        string file = program[(program.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        if (file.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            if (rest.StartsWith('"'))
            {
                int end = rest.IndexOf('"', 1);
                return end > 1 ? rest[1..end] : "";
            }

            string library = rest.Split(',', ' ')[0];
            return Path.HasExtension(library) ? library : "";
        }

        return program;
    }

    // ---- Планировщик заданий ----

    /// <summary>Задания Планировщика (кроме системных в \Microsoft), у которых все запускаемые программы исчезли.</summary>
    internal static List<Orphan> BrokenTasks(bool elevated, Func<string, bool>? filter)
    {
        var result = new List<Orphan>();
        if (!OperatingSystem.IsWindows())
            return result;

        dynamic? service = null;
        int unreadable = 0;
        try
        {
            service = Scheduler();
            if (service is null)
                return result;
            Walk(service.GetFolder(@"\"), 0);
            if (unreadable > 0)
                AppLog.Info($"Поиск остатков: не прочитано заданий Планировщика — {unreadable} (нет доступа).");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать задания Планировщика.", ex);
        }
        finally
        {
            if (service is not null)
                Marshal.FinalReleaseComObject(service);
        }

        return result;

        void Walk(dynamic folder, int depth)
        {
            string folderPath = (string)folder.Path;
            if (depth > 5 || folderPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase))
                return;

            dynamic tasks = folder.GetTasks(1); // TASK_ENUM_HIDDEN: и скрытые тоже
            int count = tasks.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    dynamic task = tasks.Item(i);
                    string? target = MissingTaskTarget(task.Definition);
                    if (target is null || (filter is not null && !filter(target)))
                        continue;
                    result.Add(new Orphan((string)task.Name, new Leftover(LeftoverKind.ScheduledTask, (string)task.Path, default, 0, !elevated,
                        $"задание Планировщика запускает {target} — такого файла уже нет")));
                }
                catch (Exception)
                {
                    unreadable++; // задание без доступа на чтение — пропускаем
                }
            }

            dynamic folders = folder.GetFolders(0);
            int subCount = folders.Count;
            for (int i = 1; i <= subCount; i++)
            {
                try
                {
                    Walk(folders.Item(i), depth + 1);
                }
                catch (Exception)
                {
                    unreadable++;
                }
            }
        }
    }

    /// <summary>Файл первой программы задания, если все его действия «запуск программы» ведут на несуществующие файлы.</summary>
    private static string? MissingTaskTarget(dynamic definition)
    {
        dynamic actions = definition.Actions;
        int count = actions.Count;
        string? first = null;
        int programs = 0;
        for (int i = 1; i <= count; i++)
        {
            dynamic action = actions.Item(i);
            if ((int)action.Type != 0) // TASK_ACTION_EXEC
                continue;

            programs++;
            string file = Environment.ExpandEnvironmentVariables(((string?)action.Path ?? "").Trim().Trim('"'));
            string directory = Environment.ExpandEnvironmentVariables(((string?)action.WorkingDirectory ?? "").Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(file) && directory.Length > 0 && Path.IsPathFullyQualified(directory))
                file = Path.Combine(directory, file);
            if (!IsMissingLocalPath(file))
                return null; // хотя бы одна программа на месте — задание живое
            first ??= file;
        }

        return programs > 0 ? first : null;
    }

    /// <summary>Повторная проверка задания перед удалением: текст причины отказа или null.</summary>
    internal static string? ValidateTask(string path)
    {
        if (!path.StartsWith('\\') || path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
            return "системное задание";

        dynamic? service = null;
        try
        {
            service = Scheduler();
            if (service is null)
                return "Планировщик заданий недоступен";
            (string folderPath, string name) = SplitTaskPath(path);
            dynamic task = service.GetFolder(folderPath).GetTask(name);
            return MissingTaskTarget(task.Definition) is null ? "программа задания снова на месте" : null;
        }
        catch (Exception)
        {
            return "задания уже нет";
        }
        finally
        {
            if (service is not null)
                Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>Удаляет задание, сохранив его описание в XML (вернуть — «Импортировать задачу» в Планировщике).</summary>
    internal static void RemoveTask(string path, string backup)
    {
        dynamic service = Scheduler() ?? throw new IOException("Планировщик заданий недоступен.");
        try
        {
            (string folderPath, string name) = SplitTaskPath(path);
            dynamic folder = service.GetFolder(folderPath);
            dynamic task = folder.GetTask(name);
            File.WriteAllText(backup, (string)task.Xml, Encoding.Unicode);
            folder.DeleteTask(name, 0);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>«\Папка\Имя» → («\Папка», «Имя»); «\Имя» → («\», «Имя»).</summary>
    public static (string Folder, string Name) SplitTaskPath(string path)
    {
        int slash = path.LastIndexOf('\\');
        string folder = slash > 0 ? path[..slash] : @"\";
        return (folder, path[(slash + 1)..]);
    }

    private static dynamic? Scheduler()
    {
        Type? type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null)
            return null;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    // ---- Список программ ----

    /// <summary>
    /// Записи в списке программ, у которых нет ни деинсталлятора, ни папки программы: программу удалили вручную.
    /// Вместе с записью — её следы (папки, ярлыки, ключи реестра), как после обычного удаления.
    /// </summary>
    private static List<Orphan> BrokenUninstallEntries(IReadOnlyList<InstalledApp> installed, bool elevated)
    {
        var result = new List<Orphan>();
        foreach (InstalledApp app in installed.Where(a => a.Kind == AppKind.Desktop && !a.IsMsi && a.Key is not null))
        {
            if (Uninstaller.Parse(app) is not { } command || !IsMissingLocalPath(command.FileName))
                continue; // деинсталлятор на месте (или на отключённом диске) — программу удаляют обычным путём
            if (app.InstallLocation.Length > 3 && !IsMissingLocalPath(app.InstallLocation))
                continue;
            string icon = IconFile(app.DisplayIcon);
            if (icon.Length > 0 && !IsMissingLocalPath(icon))
                continue;

            var others = installed.Where(o => o.Id != app.Id).ToList();
            foreach (Leftover item in LeftoverScanner.Find(app, others, includeUninstallEntry: true, elevated))
            {
                Leftover described = item.Reason == "запись в списке программ"
                    ? item with { Reason = $"запись в списке программ: деинсталлятора {command.FileName} уже нет" }
                    : item;
                result.Add(new Orphan(app.Name, described));
            }
        }

        return result;
    }

    /// <summary>«"C:\App\app.exe",0» → «C:\App\app.exe».</summary>
    public static string IconFile(string displayIcon)
    {
        string icon = displayIcon.Trim();
        int comma = icon.LastIndexOf(',');
        if (comma > 0 && int.TryParse(icon[(comma + 1)..].Trim(), out _))
            icon = icon[..comma];
        return icon.Trim().Trim('"');
    }

    // ---- Пустые папки ----

    private static List<Orphan> EmptyFolders(IReadOnlyList<InstalledApp> installed, bool elevated)
    {
        var result = new List<Orphan>();
        var names = installed.SelectMany(LeftoverScanner.NameKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var publishers = installed.Select(a => LeftoverScanner.Normalize(a.Publisher)).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var locations = installed.Where(a => a.InstallLocation.Length > 3).Select(a => LeftoverScanner.SafeFull(a.InstallLocation))
                                 .Where(p => p.Length > 0).ToList();

        foreach ((string root, string place) in LeftoverScanner.FolderRoots())
        {
            foreach (string folder in LeftoverScanner.SafeDirectories(root))
            {
                string name = Path.GetFileName(folder);
                if (KeepEmpty.Contains(name) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                                             || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase))
                    continue;

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                    continue;

                // Папка установленной программы может быть пустой и заполниться позже — не трогаем.
                string key = LeftoverScanner.Normalize(name);
                if (names.Contains(key) || publishers.Contains(key))
                    continue;
                if (locations.Any(l => l.Equals(folder, StringComparison.OrdinalIgnoreCase) || CleanupPaths.IsUnder(l, folder)))
                    continue;
                if (!IsEmptyTree(folder) || !LeftoverRemover.IsAllowedLocation(folder, shortcut: false))
                    continue;

                result.Add(new Orphan(name, new Leftover(LeftoverKind.Folder, folder, default, 0,
                    !elevated && LeftoverScanner.NeedsAdminPath(folder), $"пустая папка в {place}", MustBeEmpty: true)));
            }
        }

        return result;
    }

    /// <summary>
    /// В папке нет ни одного файла — только пустые папки. Не смогли прочитать, есть ссылки на другие папки или слишком
    /// глубоко — считаем, что не пустая.
    /// </summary>
    public static bool IsEmptyTree(string folder, int depth = 0)
    {
        if (depth > 8)
            return false;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (!IsEmptyTree(entry, depth + 1))
                    return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- Общее ----

    /// <summary>
    /// Путь ведёт на подключённый несъёмный диск, и там нет такого файла или папки (с поправкой на «Program Files (x86)»
    /// и SysWOW64, куда 32-битные установщики кладут файлы). Если ближайшую существующую папку не прочитать —
    /// считаем, что файл может быть на месте.
    /// </summary>
    public static bool IsMissingLocalPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim());
        if (path.Length < 4 || path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
            return false;
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return false;
            var drive = new DriveInfo(root);
            // Сначала тип (без обращения к диску): сетевой раздел выключенного компьютера ответил бы на IsReady через десяток секунд.
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                return false;
            if (File.Exists(path) || Directory.Exists(path) || AlternatePaths(path).Any(p => File.Exists(p) || Directory.Exists(p)))
                return false;

            string? existing = Path.GetDirectoryName(path);
            while (existing is not null && !Directory.Exists(existing))
                existing = Path.GetDirectoryName(existing);
            if (existing is null)
                return false;
            using IEnumerator<string> probe = Directory.EnumerateFileSystemEntries(existing).GetEnumerator();
            probe.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Тот же путь в соседней папке: Program Files ↔ Program Files (x86), System32 ↔ SysWOW64.</summary>
    public static IEnumerable<string> AlternatePaths(string path)
    {
        (string From, string To)[] pairs =
        {
            (@"\Program Files\", @"\Program Files (x86)\"),
            (@"\Program Files (x86)\", @"\Program Files\"),
            (@"\Windows\System32\", @"\Windows\SysWOW64\"),
            (@"\Windows\SysWOW64\", @"\Windows\System32\"),
        };
        foreach ((string from, string to) in pairs)
        {
            if (path.Length > 2 && path[1] == ':' && path.IndexOf(from, StringComparison.OrdinalIgnoreCase) == 2)
                yield return path[..2] + to + path[(2 + from.Length)..];
        }
    }
}
