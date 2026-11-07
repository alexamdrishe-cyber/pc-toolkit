namespace PcToolkit.Core.Logging;

/// <summary>
/// Простой журнал в файл %LocalAppData%\PcToolkit\logs\app.log.
/// Никогда не бросает исключения: ошибка записи журнала не должна ронять приложение.
/// </summary>
public static class AppLog
{
    private const long MaxFileSize = 5 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string LogDirectory { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "logs");

    public static string LogFile { get; private set; } = Path.Combine(LogDirectory, "app.log");

    /// <summary>Другое место журнала. Служба пишет в %ProgramData%\PcToolkit\logs\service.log.</summary>
    public static void UseFile(string directory, string fileName)
    {
        lock (Gate)
        {
            LogDirectory = directory;
            LogFile = Path.Combine(directory, fileName);
        }
    }

    /// <summary>Тесты выключают запись, чтобы не засорять журнал настоящего приложения.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        if (!Enabled)
            return;

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (exception is not null)
                    line += Environment.NewLine + exception;

                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Журнал недоступен: молча продолжаем работу.
        }
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(LogFile);
        if (!info.Exists || info.Length < MaxFileSize)
            return;

        string old = Path.ChangeExtension(LogFile, ".old.log");
        File.Delete(old);
        File.Move(LogFile, old);
    }
}
