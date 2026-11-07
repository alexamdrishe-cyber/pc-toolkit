using System.Diagnostics;
using System.Text;

namespace PcToolkit.Core.Platform;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>Запуск консольных утилит Windows (reg.exe, PowerShell) без окна, с чтением вывода.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Не удалось запустить {fileName}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellation);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(fileName)} не ответил за {timeout.TotalSeconds:0} с.");
        }

        return new ProcessResult(process.ExitCode, await output, await error);
    }

    /// <summary>Скрипт Windows PowerShell 5.1 (есть в любой Windows 10/11). Вывод — в UTF-8.</summary>
    public static Task<ProcessResult> PowerShellAsync(string script, TimeSpan timeout, CancellationToken cancellation = default)
    {
        string full = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n$ProgressPreference = 'SilentlyContinue'\n" + script;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        return RunAsync(powershell, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded },
            timeout, cancellation);
    }

    /// <summary>Строка для вставки в скрипт PowerShell в одинарных кавычках.</summary>
    public static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
}
