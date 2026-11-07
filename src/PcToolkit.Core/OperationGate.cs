namespace PcToolkit.Core;

/// <summary>
/// Очередь операций, которые меняют систему: одновременно выполняется только одна.
/// Например, очистка не начнётся, пока применяются настройки, и наоборот.
/// </summary>
public sealed class OperationGate
{
    private readonly object _lock = new();
    private string? _current;

    /// <summary>Название операции, которая сейчас выполняется, или null.</summary>
    public string? Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    /// <summary>Занимает очередь. Возвращает null при успехе или название уже идущей операции.</summary>
    public IDisposable? TryEnter(string operation, out string? busyWith)
    {
        lock (_lock)
        {
            if (_current is not null)
            {
                busyWith = _current;
                return null;
            }

            _current = operation;
            busyWith = null;
            return new Releaser(this);
        }
    }

    private void Release()
    {
        lock (_lock)
            _current = null;
    }

    private sealed class Releaser(OperationGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                gate.Release();
        }
    }
}
