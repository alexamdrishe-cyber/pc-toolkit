using System.Diagnostics;
using System.Text;
using PcToolkit.Core.Logging;

namespace PcToolkit.Desktop.Services;

/// <summary>
/// Пишет ошибки привязок WPF в журнал приложения. Без этого сломанная привязка
/// молчит: кнопка просто ничего не делает, и причину не найти.
/// </summary>
public sealed class BindingErrorListener : TraceListener
{
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    public static void Install()
    {
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
    }

    public override void Write(string? message)
    {
        lock (_gate)
            _buffer.Append(message);
    }

    public override void WriteLine(string? message)
    {
        string text;
        lock (_gate)
        {
            _buffer.Append(message);
            text = _buffer.ToString();
            _buffer.Clear();
        }

        if (text.Length > 0)
            AppLog.Warn("Ошибка привязки: " + text);
    }
}
