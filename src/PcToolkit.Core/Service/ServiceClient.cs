using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Sensors;

namespace PcToolkit.Core.Service;

/// <summary>Запись системного параметра с правами администратора (через службу).</summary>
public interface IPrivilegedWriter
{
    void Write(ChangeDefinition change, ValueSnapshot value);
}

/// <summary>
/// Клиент службы PC Toolkit. Одно соединение на всё приложение, запросы идут по очереди.
/// Если служба недоступна, методы возвращают null (или бросают исключение при записи) и не ломают приложение.
/// </summary>
public sealed class ServiceClient : IPrivilegedWriter, IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _lock = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    /// <summary>Спрашивает службу о версии. null — службы нет или она не отвечает.</summary>
    public ServiceInfo? Hello(int timeoutMs = 700)
    {
        ServiceResponse? response = Send(new ServiceRequest { Op = ServiceOps.Hello }, timeoutMs);
        return response is { Ok: true, BuildId: not null } ? new ServiceInfo(response.Version, response.BuildId) : null;
    }

    public ServiceResponse? GetSensors(int timeoutMs = 2000, int? intervalMs = null) =>
        Send(new ServiceRequest { Op = ServiceOps.Sensors, IntervalMs = intervalMs }, timeoutMs);

    /// <summary>Диски и SMART от службы. null — службы нет; Ok = false — служба старая и команды не знает (или ошибка).</summary>
    public ServiceResponse? GetDisks(int timeoutMs = 20000) =>
        Send(new ServiceRequest { Op = ServiceOps.Disks }, timeoutMs);

    public void Write(ChangeDefinition change, ValueSnapshot value)
    {
        ServiceResponse response = Send(new ServiceRequest { Op = ServiceOps.Write, Change = change, Value = value }, 15000)
                                   ?? throw new IOException("Служба PC Toolkit не отвечает.");
        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "Служба отказалась выполнить изменение.");
    }

    public ServiceResponse? Send(ServiceRequest request, int timeoutMs)
    {
        lock (_lock)
        {
            if (_disposed)
                return null;

            // Вторая попытка нужна, если служба перезапускалась и старое соединение оборвалось.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    EnsureConnected(Math.Min(timeoutMs, 1000));
                    _writer!.WriteLine(JsonSerializer.Serialize(request, ServiceProtocol.Json));
                    _writer.Flush();

                    Task<string?> read = _reader!.ReadLineAsync();
                    if (!read.Wait(timeoutMs))
                    {
                        // Чтение останется висеть и завершится ошибкой, когда закроем канал: помечаем её как обработанную.
                        _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        throw new TimeoutException("Служба не ответила вовремя.");
                    }

                    string? line = read.Result;
                    if (line is null)
                        throw new IOException("Служба закрыла соединение.");

                    return JsonSerializer.Deserialize<ServiceResponse>(line, ServiceProtocol.Json);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException
                                               or InvalidOperationException or AggregateException or JsonException
                                               or UnauthorizedAccessException)
                {
                    Reset();
                    if (attempt == 1)
                        return null;
                }
            }

            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            Reset();
        }
    }

    private void EnsureConnected(int timeoutMs)
    {
        if (_pipe is { IsConnected: true })
            return;

        Reset();
        var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(timeoutMs);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, Utf8, bufferSize: 4096, leaveOpen: true) { AutoFlush = false };
    }

    private void Reset()
    {
        try
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _pipe?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ошибка при закрытии соединения со службой.", ex);
        }

        _reader = null;
        _writer = null;
        _pipe = null;
    }
}

/// <summary>Датчики, которые читает служба (с правами администратора и драйвером PawnIO).</summary>
public sealed class ServiceSensorSource : ISensorSource
{
    private readonly ServiceClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _wake = new(false);
    private long _intervalTicks;
    private DateTime _lastTimestamp = DateTime.MinValue;
    private Thread? _thread;
    private volatile SensorSourceState _state = SensorSourceState.Stopped;
    private volatile string? _lastError;

    public ServiceSensorSource(ServiceClient client, TimeSpan? interval = null)
    {
        _client = client;
        Interval = interval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Как часто спрашивать службу. Служба получает это значение с каждым запросом и опрашивает датчики так же часто
    /// (служба старой версии — раз в секунду; повторы одного снимка отбрасываются).
    /// </summary>
    public TimeSpan Interval
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));
        set
        {
            long ticks = Math.Max(value.Ticks, TimeSpan.FromMilliseconds(100).Ticks);
            if (Interlocked.Exchange(ref _intervalTicks, ticks) == ticks)
                return;

            try
            {
                _wake.Set();
            }
            catch (ObjectDisposedException)
            {
                // Источник уже остановлен.
            }
        }
    }

    public event EventHandler<HardwareSnapshot>? SnapshotReady;

    public event EventHandler? StateChanged;

    public SensorSourceState State => _state;

    public string? LastError => _lastError;

    public void Start()
    {
        if (_thread is not null)
            return;

        SetState(SensorSourceState.Starting, null);
        _thread = new Thread(Run) { IsBackground = true, Name = "PcToolkit.ServiceSensors" };
        _thread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _cts.Dispose();
        _wake.Dispose();
    }

    private void Run()
    {
        var cycle = new System.Diagnostics.Stopwatch();
        while (!_cts.IsCancellationRequested)
        {
            cycle.Restart();
            TimeSpan interval = Interval;
            ServiceResponse? response = _client.GetSensors(intervalMs: (int)interval.TotalMilliseconds);
            if (response is null)
            {
                SetState(SensorSourceState.Failed, "служба PC Toolkit не отвечает");
            }
            else if (response.Snapshot is { } snapshot)
            {
                SetState(SensorSourceState.Running, null);
                // Служба отдаёт последний готовый снимок: если он тот же, что в прошлый раз, ничего не делаем.
                if (snapshot.Timestamp != _lastTimestamp)
                {
                    _lastTimestamp = snapshot.Timestamp;
                    SnapshotReady?.Invoke(this, snapshot);
                }
            }
            else
            {
                SetState(response.SensorState ?? SensorSourceState.Starting, response.SensorError);
            }

            // Спрашиваем чуть чаще, чем служба делает снимки: так не пропускается ни один (повторы отбрасываются выше).
            TimeSpan delay = interval * 0.75 - cycle.Elapsed;
            if (delay < TimeSpan.FromMilliseconds(30))
                delay = TimeSpan.FromMilliseconds(30);
            WaitHandle.WaitAny(new[] { _cts.Token.WaitHandle, _wake }, delay);
        }
    }

    private void SetState(SensorSourceState state, string? error)
    {
        _lastError = error;
        if (_state == state)
            return;

        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
