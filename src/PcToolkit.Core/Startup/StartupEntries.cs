using System.Diagnostics;
using Microsoft.Win32;
using PcToolkit.Core.Apps;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Startup;

/// <summary>Откуда программа запускается вместе с Windows.</summary>
public enum StartupLocation
{
    /// <summary>HKCU\...\Run — только для этого пользователя.</summary>
    UserRun,

    /// <summary>HKLM\...\Run — для всех пользователей (64-битная часть реестра).</summary>
    MachineRun,

    /// <summary>HKLM\...\WOW6432Node\...\Run — для всех пользователей (32-битные программы).</summary>
    MachineRun32,

    /// <summary>Папка «Автозагрузка» пользователя.</summary>
    UserFolder,

    /// <summary>Общая папка «Автозагрузка».</summary>
    CommonFolder,
}

/// <summary>Насколько программа замедляет включение компьютера (оценка по известным программам).</summary>
public enum StartupImpact
{
    Unknown,
    Low,
    Medium,
    High,
}

/// <summary>Совет: можно выключить, по желанию или лучше оставить.</summary>
public enum StartupAdvice
{
    Optional,
    CanDisable,
    Keep,
}

/// <summary>Программа в автозагрузке.</summary>
public sealed record StartupEntry(
    string Name,
    string Command,
    string FilePath,
    StartupLocation Location,
    bool Enabled,
    string Publisher,
    string Description,
    bool FileExists,
    StartupImpact Impact,
    StartupAdvice Advice,
    string Note)
{
    /// <summary>Включить или выключить её можно только с правами администратора (служба или запуск от администратора).</summary>
    public bool NeedsAdmin => Location is StartupLocation.MachineRun or StartupLocation.MachineRun32 or StartupLocation.CommonFolder;

    /// <summary>Понятное название: имя записи, а для ярлыков — без «.lnk».</summary>
    public string DisplayName =>
        Location is StartupLocation.UserFolder or StartupLocation.CommonFolder ? Path.GetFileNameWithoutExtension(Name) : Name;
}

/// <summary>
/// Включено ли — как в «Диспетчере задач»: запись остаётся на месте, а её состояние хранится в разделе
/// StartupApproved (12 байт: первый чётный — включено, нечётный — выключено, дальше время выключения).
/// Так выключенное можно включить обратно и здесь, и в «Диспетчере задач».
/// </summary>
public static class StartupApproved
{
    public const string Root = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static string SubKey(StartupLocation location) => location switch
    {
        StartupLocation.UserRun or StartupLocation.MachineRun => "Run",
        StartupLocation.MachineRun32 => "Run32",
        _ => "StartupFolder",
    };

    public static RegistryHive Hive(StartupLocation location) =>
        location is StartupLocation.UserRun or StartupLocation.UserFolder ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    public static bool IsValidSubKey(string subKey) => subKey is "Run" or "Run32" or "StartupFolder";

    public static bool IsEnabled(byte[]? data) => data is not { Length: > 0 } || (data[0] & 1) == 0;

    public static byte[] Data(bool enabled, DateTime utcNow)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled)
            BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(data, 4);
        return data;
    }

    public static bool Read(RegistryHive hive, string subKey, string name)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(Root + "\\" + subKey);
            return IsEnabled(key?.GetValue(name) as byte[]);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    /// <summary>Записывает состояние. HKLM — только с правами администратора (служба или приложение от администратора).</summary>
    public static void Write(RegistryHive hive, string subKey, string name, bool enabled)
    {
        if (!IsValidSubKey(subKey))
            throw new ArgumentException("Неизвестный раздел автозагрузки: " + subKey, nameof(subKey));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Contains('\0'))
            throw new ArgumentException("Неверное имя записи автозагрузки.", nameof(name));

        using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey key = root.CreateSubKey(Root + "\\" + subKey, writable: true);
        key.SetValue(name, Data(enabled, DateTime.UtcNow), RegistryValueKind.Binary);
        AppLog.Info($"Автозагрузка: «{name}» ({(hive == RegistryHive.CurrentUser ? "пользователь" : "компьютер")}, {subKey}) — " +
                    (enabled ? "включена." : "выключена."));
    }
}

/// <summary>Читает автозагрузку: разделы Run (пользователь и компьютер, 64 и 32 бита) и папки «Автозагрузка».</summary>
public static class StartupScanner
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static IReadOnlyList<StartupEntry> Scan()
    {
        var entries = new List<StartupEntry>();
        ReadRun(entries, RegistryHive.CurrentUser, RegistryView.Registry64, StartupLocation.UserRun);
        ReadRun(entries, RegistryHive.LocalMachine, RegistryView.Registry64, StartupLocation.MachineRun);
        ReadRun(entries, RegistryHive.LocalMachine, RegistryView.Registry32, StartupLocation.MachineRun32);
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLocation.UserFolder);
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupLocation.CommonFolder);
        return entries
            .OrderBy(e => e.Enabled ? 0 : 1)
            .ThenByDescending(e => e.Impact)
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Запись для программы: путь к файлу, издатель и описание из файла, оценка по каталогу.</summary>
    public static StartupEntry Describe(string name, string command, string filePath, StartupLocation location, bool enabled)
    {
        string publisher = "", description = "";
        bool exists = filePath.Length > 0 && File.Exists(filePath);
        if (exists)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(filePath);
                publisher = (info.CompanyName ?? "").Trim();
                description = (info.FileDescription ?? "").Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // нет сведений о версии — не страшно
            }
        }

        StartupRule rule = StartupCatalog.Match(name, filePath, command, publisher);
        string note = rule.Note;
        if (!exists && filePath.Length > 0)
            note = "Файла программы нет — запись осталась от удалённой программы, её можно выключить. " + note;
        return new StartupEntry(name, command, filePath, location, enabled, publisher, description, exists,
                                rule.Impact, exists || filePath.Length == 0 ? rule.Advice : StartupAdvice.CanDisable, note.Trim());
    }

    private static void ReadRun(List<StartupEntry> entries, RegistryHive hive, RegistryView view, StartupLocation location)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = root.OpenSubKey(RunKey);
            if (key is null)
                return;

            foreach (string name in key.GetValueNames())
            {
                if (name.Length == 0 || key.GetValue(name) is not string command || command.Trim().Length == 0)
                    continue;
                string file = Resolve(OrphanScanner.CommandTarget(command));
                bool enabled = StartupApproved.Read(StartupApproved.Hive(location), StartupApproved.SubKey(location), name);
                entries.Add(Describe(name, command, file, location, enabled));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Не удалось прочитать автозагрузку ({location}).", ex);
        }
    }

    private static void ReadFolder(List<StartupEntry> entries, string folder, StartupLocation location)
    {
        if (folder.Length == 0 || !Directory.Exists(folder))
            return;

        try
        {
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(path);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;
                string target = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? LeftoverScanner.ShortcutTarget(path) : path;
                bool enabled = StartupApproved.Read(StartupApproved.Hive(location), StartupApproved.SubKey(location), name);
                entries.Add(Describe(name, target.Length > 0 ? target : path, Resolve(target), location, enabled));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Не удалось прочитать папку автозагрузки {folder}.", ex);
        }
    }

    private static string Resolve(string path)
    {
        if (path.Length == 0)
            return "";
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (Path.IsPathRooted(path))
            return path;
        // «rundll32.exe …», «ctfmon.exe»: ищем в системной папке
        string system = Path.Combine(Environment.SystemDirectory, path);
        return File.Exists(system) ? system : path;
    }
}
