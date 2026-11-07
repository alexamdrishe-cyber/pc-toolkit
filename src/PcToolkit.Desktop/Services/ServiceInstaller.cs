using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Service;

namespace PcToolkit.Desktop.Services;

public enum ServiceInstallState
{
    NotInstalled,
    Stopped,
    Running,
    Unknown,
}

/// <summary>
/// Установка, обновление и удаление службы PC Toolkit.
/// Файлы службы копируются в Program Files: туда без прав администратора ничего не записать,
/// поэтому подменить программу, которая работает от имени SYSTEM, нельзя.
/// Сами действия выполняет отдельная копия приложения с правами администратора (один запрос UAC).
/// </summary>
public static class ServiceInstaller
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;

    public static string SourceDirectory => Path.Combine(AppContext.BaseDirectory, "service");

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PcToolkit", "Service");

    public static bool PackageAvailable => File.Exists(Path.Combine(SourceDirectory, ServiceProtocol.ServiceExecutable));

    public static ServiceInstallState QueryState()
    {
        try
        {
            using ServiceController? service = Find();
            if (service is null)
                return ServiceInstallState.NotInstalled;

            return service.Status == ServiceControllerStatus.Running ? ServiceInstallState.Running : ServiceInstallState.Stopped;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось узнать состояние службы.", ex);
            return ServiceInstallState.Unknown;
        }
    }

    /// <summary>
    /// Запускает копию приложения с правами администратора, которая выполнит действие, и ждёт её.
    /// false — пользователь отказался в окне UAC или действие не удалось.
    /// </summary>
    public static async Task<bool> RunElevatedAsync(string argument)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
            return false;

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = argument,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
            if (process is null)
                return false;

            await process.WaitForExitAsync();
            return process.ExitCode == ExitOk;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AppLog.Info("Пользователь отказался выдать права администратора для службы.");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось запустить установку службы.", ex);
            return false;
        }
    }

    /// <summary>Установка или обновление. Вызывается в копии приложения с правами администратора.</summary>
    public static int Install()
    {
        try
        {
            if (!PackageAvailable)
                throw new FileNotFoundException("Рядом с приложением нет папки service с файлами службы.", SourceDirectory);

            bool exists;
            using (ServiceController? existing = Find())
            {
                exists = existing is not null;
                if (existing is not null)
                    Stop(existing);
            }

            // Если приложение само лежит в папке службы, копировать нечего (и копирование файла в себя упадёт).
            if (!string.Equals(Path.GetFullPath(SourceDirectory).TrimEnd('\\'), Path.GetFullPath(InstallDirectory).TrimEnd('\\'),
                               StringComparison.OrdinalIgnoreCase))
                CopyDirectory(SourceDirectory, InstallDirectory);
            string executable = $"\"{Path.Combine(InstallDirectory, ServiceProtocol.ServiceExecutable)}\"";

            if (exists)
            {
                Sc("config", ServiceProtocol.ServiceName, "binPath=", executable, "start=", "auto");
            }
            else
            {
                Sc("create", ServiceProtocol.ServiceName, "binPath=", executable, "start=", "auto",
                   "DisplayName=", ServiceProtocol.DisplayName);
            }

            Sc("description", ServiceProtocol.ServiceName, ServiceProtocol.Description);
            // Если служба упадёт, Windows перезапустит её через 5 секунд.
            Sc("failure", ServiceProtocol.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");

            using ServiceController service = Find() ?? throw new InvalidOperationException("Служба не появилась после установки.");
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));

            AppLog.Info($"Служба PC Toolkit установлена в {InstallDirectory} и запущена.");
            return ExitOk;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось установить службу PC Toolkit.", ex);
            return ExitFailed;
        }
    }

    /// <summary>Удаление. Вызывается в копии приложения с правами администратора.</summary>
    public static int Uninstall()
    {
        try
        {
            using (ServiceController? existing = Find())
            {
                if (existing is not null)
                {
                    Stop(existing);
                    Sc("delete", ServiceProtocol.ServiceName);
                }
            }

            try
            {
                if (Directory.Exists(InstallDirectory))
                    Directory.Delete(InstallDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                // Windows отпускает файлы службы не сразу; папка удалится при следующем обновлении или вручную.
                AppLog.Warn("Папку службы удалить не удалось.", ex);
            }

            AppLog.Info("Служба PC Toolkit удалена.");
            return ExitOk;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось удалить службу PC Toolkit.", ex);
            return ExitFailed;
        }
    }

    private static ServiceController? Find() =>
        ServiceController.GetServices().FirstOrDefault(s =>
            string.Equals(s.ServiceName, ServiceProtocol.ServiceName, StringComparison.OrdinalIgnoreCase));

    private static void Stop(ServiceController service)
    {
        service.Refresh();
        if (service.Status is ServiceControllerStatus.Stopped)
            return;

        if (service.Status is not ServiceControllerStatus.StopPending)
            service.Stop();

        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));

        // Процесс службы может завершаться ещё долю секунды после остановки и держать файлы.
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ServiceProtocol.ServiceExecutable)))
        {
            using (process)
                process.WaitForExit(10000);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void Sc(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить sc.exe.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"sc {arguments[0]} завершился с кодом {process.ExitCode}: {output.Trim()}");
    }
}
