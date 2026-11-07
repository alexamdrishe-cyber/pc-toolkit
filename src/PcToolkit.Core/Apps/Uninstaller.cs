using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Apps;

public enum UninstallOutcome
{
    /// <summary>Программа удалена: её записи в списке программ больше нет.</summary>
    Removed,

    /// <summary>Деинсталлятор закрылся, но программа осталась (отменили или нужна перезагрузка).</summary>
    StillInstalled,

    /// <summary>Отказались выдать права администратора.</summary>
    Canceled,

    /// <summary>Файла деинсталлятора нет: можно только убрать запись и следы.</summary>
    UninstallerMissing,

    Failed,
}

public sealed record UninstallCommand(string FileName, string Arguments);

/// <summary>
/// Запускает «родной» деинсталлятор программы — тот же, что «Программы и компоненты» — и ждёт его.
/// Многие деинсталляторы (Inno Setup, NSIS) копируют себя во временную папку и сразу закрываются,
/// поэтому окончание определяется по исчезновению записи программы, а не по выходу процесса.
/// </summary>
public static partial class Uninstaller
{
    /// <summary>Имена вспомогательных процессов деинсталляторов, которых тоже нужно дождаться.</summary>
    private static readonly string[] HelperNames = { "au_", "un_a", "un_b", "unins", "_iu14d2n", "uninstall", "uninst", "un.", "setup", "_un" };

    /// <summary>Разбирает строку удаления на программу и аргументы.</summary>
    public static UninstallCommand? Parse(InstalledApp app)
    {
        string command = Environment.ExpandEnvironmentVariables(app.UninstallString.Trim());
        if (command.Length == 0)
        {
            // У MSI-пакетов строки может не быть: удаляем по коду продукта.
            return app.IsMsi && app.Key is { } key && InstalledAppsProvider.IsGuid(key.KeyName)
                ? new UninstallCommand("msiexec.exe", $"/X{key.KeyName}")
                : null;
        }

        UninstallCommand parsed = Split(command);
        if (Path.GetFileName(parsed.FileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(parsed.FileName).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            // «/I{код}» открывает окно изменения программы; удаление — «/X{код}».
            string arguments = MsiInstall().Replace(parsed.Arguments, "/X$1");
            return new UninstallCommand("msiexec.exe", arguments);
        }

        return parsed;
    }

    /// <summary>«"C:\Path\un.exe" /S» или «C:\Program Files\App\uninstall.exe /S» → программа и аргументы.</summary>
    internal static UninstallCommand Split(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 1)
                return new UninstallCommand(command[1..end], command[(end + 1)..].Trim());
        }

        // Без кавычек путь может содержать пробелы: ищем самый короткий префикс, который является файлом.
        string[] parts = command.Split(' ');
        for (int i = 1; i <= parts.Length; i++)
        {
            string candidate = string.Join(' ', parts.Take(i));
            if (File.Exists(candidate) || (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate + ".exe")))
                return new UninstallCommand(candidate, string.Join(' ', parts.Skip(i)).Trim());
        }

        // Файл не нашли: всё до «.exe» — программа.
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
            return new UninstallCommand(command[..(exe + 4)], command[(exe + 4)..].Trim());

        return new UninstallCommand(parts[0], string.Join(' ', parts.Skip(1)).Trim());
    }

    public static bool UninstallerExists(InstalledApp app)
    {
        if (Parse(app) is not { } command)
            return false;
        if (command.FileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            return true;
        return Path.IsPathFullyQualified(command.FileName) ? File.Exists(command.FileName) : true;
    }

    /// <summary>
    /// Запускает деинсталлятор и ждёт, пока программа исчезнет из списка, все его процессы закроются,
    /// или пользователь нажмёт «Продолжить» (<paramref name="userDone"/>).
    /// </summary>
    public static async Task<UninstallOutcome> RunAsync(InstalledApp app, IProgress<string>? status, Func<bool> userDone,
        CancellationToken cancellation = default)
    {
        if (app.Kind != AppKind.Desktop)
            throw new ArgumentException("Это не программа с деинсталлятором.", nameof(app));
        if (Parse(app) is not { } command || !UninstallerExists(app))
            return UninstallOutcome.UninstallerMissing;

        AppLog.Info($"Удаление программы «{app.Name}»: {command.FileName} {command.Arguments}");
        DateTime started = DateTime.Now;
        Process? process;
        try
        {
            string? directory = Path.IsPathFullyQualified(command.FileName) ? Path.GetDirectoryName(command.FileName) : null;
            process = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments)
            {
                // Через оболочку: если деинсталлятору нужны права администратора, Windows сама покажет запрос.
                UseShellExecute = true,
                WorkingDirectory = directory is not null && Directory.Exists(directory) ? directory : "",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AppLog.Info($"Удаление «{app.Name}» отменено: не выданы права администратора.");
            return UninstallOutcome.Canceled;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Не удалось запустить деинсталлятор «{app.Name}».", ex);
            return UninstallOutcome.Failed;
        }

        status?.Report("Открыт деинсталлятор программы — пройдите его до конца.");
        using (process)
        {
            while (process is not null && !process.HasExited && !userDone())
                await Task.Delay(500, cancellation);
        }

        // Процесс закрылся: ждём, пока исчезнет запись программы или закончатся вспомогательные процессы.
        status?.Report("Деинсталлятор работает…");
        var quiet = Stopwatch.StartNew();
        while (!cancellation.IsCancellationRequested)
        {
            if (app.Key is { } key && !key.Exists())
                return UninstallOutcome.Removed;
            if (userDone())
                break;

            if (HelpersRunning(started))
                quiet.Restart();
            else if (quiet.Elapsed > TimeSpan.FromSeconds(8))
                break; // вспомогательных процессов нет, а запись на месте — удаление отменили

            await Task.Delay(700, cancellation);
        }

        cancellation.ThrowIfCancellationRequested();
        bool removed = app.Key is { } k && !k.Exists();
        AppLog.Info($"Удаление «{app.Name}»: {(removed ? "программа удалена" : "программа осталась в списке")}.");
        return removed ? UninstallOutcome.Removed : UninstallOutcome.StillInstalled;
    }

    /// <summary>Запущены ли после начала удаления процессы, похожие на деинсталлятор.</summary>
    private static bool HelpersRunning(DateTime since)
    {
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                string name = process.ProcessName.ToLowerInvariant();
                if (!HelperNames.Any(name.StartsWith) && !name.Contains("uninst", StringComparison.Ordinal))
                    continue;
                try
                {
                    if (process.StartTime >= since.AddSeconds(-2))
                        return true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Процесс с правами администратора: время запуска не узнать — считаем, что это он.
                    return true;
                }
            }
        }

        return false;
    }

    [GeneratedRegex(@"/I\s*(\{[0-9A-Fa-f\-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiInstall();
}
