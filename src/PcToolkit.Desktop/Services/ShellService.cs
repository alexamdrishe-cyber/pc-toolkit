using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PcToolkit.Core.Logging;

namespace PcToolkit.Desktop.Services;

/// <summary>Действия с оболочкой Windows: открыть ссылку, папку, перезапустить приложение.</summary>
public static class ShellService
{
    private const int ErrorCancelled = 1223;

    public static void OpenUrl(string url) =>
        TryStart(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось создать папку {path}.", ex);
        }

        TryStart(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void ShowFileInExplorer(string path) =>
        TryStart(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    /// <summary>Открывает оснастку Windows: «Просмотр событий» (eventvwr.msc), «Диспетчер устройств» (devmgmt.msc).</summary>
    public static void OpenSystemTool(string name) =>
        TryStart(new ProcessStartInfo(name) { UseShellExecute = true });

    /// <summary>
    /// Запускает новую копию приложения с запросом прав администратора (окно UAC).
    /// Возвращает false, если пользователь отказался или запуск не удался.
    /// </summary>
    public static bool RestartAsAdministrator(string? extraArguments = null) => Restart(elevate: true, extraArguments);

    /// <summary>Запускает новую копию приложения с теми же правами, что у текущей.</summary>
    public static bool RestartApplication(string? extraArguments = null) => Restart(elevate: false, extraArguments);

    private static bool Restart(bool elevate, string? extraArguments)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = $"{LaunchArguments.Restarted} {extraArguments}".Trim(),
                UseShellExecute = true,
                Verb = elevate ? "runas" : "",
                WorkingDirectory = AppContext.BaseDirectory,
            })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось перезапустить приложение.", ex);
            return false;
        }
    }

    private static void TryStart(ProcessStartInfo startInfo)
    {
        try
        {
            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось открыть {startInfo.FileName} {startInfo.Arguments}.", ex);
        }
    }
}
