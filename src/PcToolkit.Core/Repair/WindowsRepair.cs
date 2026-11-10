using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Repair;

/// <summary>Чем закончилась проверка системных файлов (sfc).</summary>
public enum SfcVerdict
{
    Unknown,

    /// <summary>Повреждений нет.</summary>
    Clean,

    /// <summary>Нашла повреждённые файлы и восстановила.</summary>
    Repaired,

    /// <summary>Нашла, но восстановила не всё.</summary>
    Partial,

    /// <summary>Не смогла выполнить проверку.</summary>
    Failed,
}

/// <summary>Ход и итог восстановления (файл, который пишет процесс с правами администратора, а читает окно).</summary>
public sealed class RepairStatus
{
    /// <summary>starting, dism, sfc, done.</summary>
    public string Stage { get; set; } = "starting";

    /// <summary>Общий ход, 0–100: DISM — первая половина, sfc — вторая.</summary>
    public double Percent { get; set; }

    public int? DismExitCode { get; set; }

    public int? SfcExitCode { get; set; }

    public SfcVerdict Sfc { get; set; }

    public string? Error { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public DateTime? FinishedAt { get; set; }

    /// <summary>Идёт сейчас: не закончено и процесс недавно отчитывался.</summary>
    public bool IsRunning => FinishedAt is null && DateTime.Now - UpdatedAt < TimeSpan.FromMinutes(3);
}

/// <summary>
/// «Восстановление Windows»: DISM /RestoreHealth (чинит хранилище компонентов, при необходимости скачивает
/// исходные файлы из Центра обновления), затем sfc /scannow (проверяет и восстанавливает системные файлы).
/// Нужны права администратора: запускается отдельным процессом приложения после согласия в окне Windows.
/// </summary>
public static partial class WindowsRepair
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultStatusPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "repair-status.json");

    public static RepairStatus? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<RepairStatus>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Выполняет DISM и sfc, записывая ход в <paramref name="statusPath"/>. Код выхода 0 — дошло до конца.</summary>
    public static int Run(string statusPath)
    {
        var status = new RepairStatus();
        Save(statusPath, status);
        AppLog.Info("Восстановление Windows: начато (DISM, затем sfc).");
        try
        {
            status.Stage = "dism";
            Save(statusPath, status);
            string system = Environment.SystemDirectory;
            status.DismExitCode = RunTool(Path.Combine(system, "dism.exe"), "/Online /Cleanup-Image /RestoreHealth /English",
                Encoding.UTF8, percent => Report(statusPath, status, percent / 2), out _);
            AppLog.Info($"Восстановление Windows: DISM завершился с кодом {status.DismExitCode}.");

            status.Stage = "sfc";
            Report(statusPath, status, 50);
            // sfc пишет в перенаправленный вывод UTF-16, а прогресс — как «Verification 45% complete.» / «Проверка 45% завершена.»
            status.SfcExitCode = RunTool(Path.Combine(system, "sfc.exe"), "/scannow", Encoding.Unicode,
                percent => Report(statusPath, status, 50 + percent / 2), out string sfcOutput);
            status.Sfc = ParseSfc(sfcOutput);
            AppLog.Info($"Восстановление Windows: sfc — {status.Sfc} (код {status.SfcExitCode}).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            status.Error = ex.Message;
            AppLog.Error("Восстановление Windows прервалось.", ex);
        }

        status.Stage = "done";
        status.Percent = 100;
        status.FinishedAt = DateTime.Now;
        Save(statusPath, status);
        return status.Error is null ? 0 : 1;
    }

    /// <summary>Итог sfc по её последнему сообщению (английская и русская Windows).</summary>
    public static SfcVerdict ParseSfc(string output)
    {
        string text = output.Replace("\0", "", StringComparison.Ordinal).ToLowerInvariant().Replace('ё', 'е');
        if (text.Contains("did not find any integrity violations", StringComparison.Ordinal) ||
            text.Contains("не обнаружила нарушений целостности", StringComparison.Ordinal))
            return SfcVerdict.Clean;
        if (text.Contains("unable to fix some", StringComparison.Ordinal) ||
            text.Contains("не может восстановить некоторые", StringComparison.Ordinal) ||
            text.Contains("не удалось восстановить некоторые", StringComparison.Ordinal))
            return SfcVerdict.Partial;
        if (text.Contains("successfully repaired", StringComparison.Ordinal) ||
            text.Contains("успешно их восстановила", StringComparison.Ordinal) ||
            text.Contains("успешно восстановила", StringComparison.Ordinal))
            return SfcVerdict.Repaired;
        if (text.Contains("could not perform the requested operation", StringComparison.Ordinal) ||
            text.Contains("не может выполнить запрошенную операцию", StringComparison.Ordinal) ||
            text.Contains("не удается выполнить запрошенную операцию", StringComparison.Ordinal))
            return SfcVerdict.Failed;
        return SfcVerdict.Unknown;
    }

    /// <summary>Процент из строки хода: «[==== 25.3% ====]», «Verification 45% complete.», «Проверка 45% завершена.».</summary>
    public static double? Percent(string line)
    {
        Match match = PercentPattern().Match(line);
        return match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) && value is >= 0 and <= 100
            ? value
            : null;
    }

    private static int RunTool(string file, string arguments, Encoding encoding, Action<double> progress, out string output)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
        };
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить " + Path.GetFileName(file));
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();

        // Ход выводится с возвратом каретки на ту же строку: читаем посимвольно и режем по \r и \n.
        var all = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[256];
        int read;
        while ((read = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                all.Append(c);
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0 && Percent(line.ToString()) is double percent)
                        progress(percent);
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }
        }

        process.WaitForExit();
        output = all.ToString();
        return process.ExitCode;
    }

    private static void Report(string path, RepairStatus status, double percent)
    {
        status.Percent = Math.Round(Math.Clamp(percent, 0, 100), 1);
        // Не чаще раза в секунду: файл читает окно приложения.
        if (DateTime.Now - status.UpdatedAt < TimeSpan.FromSeconds(1) && percent < 100)
            return;
        Save(path, status);
    }

    private static void Save(string path, RepairStatus status)
    {
        status.UpdatedAt = DateTime.Now;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Не удалось записать ход восстановления Windows.", ex);
        }
    }

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentPattern();
}
