using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using PcToolkit.Core.Logging;

namespace PcToolkit.Desktop.Services;

public sealed record PawnIoInstallResult(bool Success, string Message);

/// <summary>
/// Устанавливает драйвер PawnIO из официального установщика, встроенного в приложение.
/// Перед запуском проверяет контрольную сумму и цифровую подпись установщика.
/// Вызывать только из процесса с правами администратора и в фоновом потоке.
/// </summary>
public static class PawnIoInstaller
{
    public const string BundledVersion = "2.2.0";

    private const string ResourceName = "PcToolkit.PawnIO_setup.exe";

    // SHA-256 официального PawnIO_setup.exe 2.2.0 (github.com/namazso/PawnIO.Setup).
    // При обновлении файла в third_party/PawnIO обновить и это значение.
    private const string ExpectedSha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(2);

    public static PawnIoInstallResult Install() => Run(execute: true);

    /// <summary>
    /// Переустановка: сначала удалить прежнюю установку, потом поставить заново.
    /// Помогает, когда драйвер числится установленным, но Windows не может его запустить (ошибка в «Диспетчере устройств»).
    /// </summary>
    public static PawnIoInstallResult Repair() => Run(execute: true, repair: true);

    /// <summary>
    /// Распаковывает установщик и проверяет хэш и подпись, ничего не устанавливая.
    /// Используется для самопроверки в отладочной сборке.
    /// </summary>
    public static PawnIoInstallResult Verify() => Run(execute: false);

    private static PawnIoInstallResult Run(bool execute, bool repair = false)
    {
        string folder = Path.Combine(Path.GetTempPath(), "PcToolkit", Guid.NewGuid().ToString("N"));
        string setupPath = Path.Combine(folder, "PawnIO_setup.exe");

        try
        {
            Directory.CreateDirectory(folder);

            using (Stream? resource = typeof(PawnIoInstaller).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (resource is null)
                    return Fail("Установщик PawnIO не найден внутри приложения.");

                using var output = new FileStream(setupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                resource.CopyTo(output);
            }

            // Файл остаётся открытым только на чтение до конца установки: подменить его после проверки нельзя.
            using var guard = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            string hash = Convert.ToHexString(SHA256.HashData(guard));
            if (!hash.Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return Fail($"Контрольная сумма установщика PawnIO не совпадает ({hash}). Установка отменена.");

            if (!Authenticode.IsTrusted(setupPath, out int signatureStatus))
                return Fail($"Цифровая подпись установщика PawnIO не прошла проверку (код 0x{signatureStatus:X8}). Установка отменена.");

            if (!execute)
                return new PawnIoInstallResult(true, $"Встроенный установщик PawnIO {BundledVersion}: контрольная сумма и подпись в порядке.");

            if (repair)
            {
                // Ошибку удаления не считаем провалом: если удалять было нечего, установка всё равно поможет.
                AppLog.Info($"Переустановка PawnIO {BundledVersion}: удаляю прежнюю установку.");
                int? removed = RunSetup(setupPath, folder, "-uninstall -silent");
                AppLog.Info(removed is null
                    ? "Удаление PawnIO не завершилось вовремя или не запустилось."
                    : $"Удаление PawnIO завершилось с кодом {removed}.");
            }

            AppLog.Info($"Запускаю установщик PawnIO {BundledVersion}.");
            int? exitCode = RunSetup(setupPath, folder, "-install -silent");
            if (exitCode is null)
                return Fail("Установщик PawnIO не запустился или не завершился за 2 минуты.");

            AppLog.Info($"Установщик PawnIO завершился с кодом {exitCode}.");
            return exitCode == 0
                ? new PawnIoInstallResult(true, repair ? "Драйвер PawnIO переустановлен." : "Драйвер PawnIO установлен.")
                : Fail($"Установщик PawnIO завершился с ошибкой (код {exitCode}).");
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка при установке PawnIO.", ex);
            return new PawnIoInstallResult(false, $"Ошибка при установке PawnIO: {ex.Message}");
        }
        finally
        {
            TryDelete(folder);
        }
    }

    /// <summary>Код завершения установщика или null, если он не запустился или не уложился во время.</summary>
    private static int? RunSetup(string setupPath, string folder, string arguments)
    {
        using Process? process = Process.Start(new ProcessStartInfo(setupPath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder,
        });

        if (process is null)
            return null;

        if (!process.WaitForExit(InstallTimeout))
            return null;

        return process.ExitCode;
    }

    private static PawnIoInstallResult Fail(string message)
    {
        AppLog.Warn(message);
        return new PawnIoInstallResult(false, message);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось удалить временную папку {folder}.", ex);
        }
    }
}
