using System.Diagnostics;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Service;

namespace PcToolkit.Core.Games;

/// <summary>Ответ счётчика кадров: кто выводит кадры, новые кадры выбранного процесса и текущее время QPC.</summary>
public sealed record FrameQueryResult(IReadOnlyList<FramePresenter> Presenters, long[] Frames, long Now);

/// <summary>Откуда приложение берёт кадры: из службы PC Toolkit или напрямую (когда запущено от имени администратора).</summary>
public interface IFrameSource : IDisposable
{
    /// <summary>Для подписи в интерфейсе: «служба PC Toolkit», «напрямую».</summary>
    string Name { get; }

    /// <summary>
    /// Процессы, выводящие кадры, и кадры процесса <paramref name="processId"/> новее <paramref name="since"/>.
    /// null — счётчик недоступен, причина в <paramref name="error"/>.
    /// </summary>
    FrameQueryResult? Query(int? processId, long since, out string? error);
}

/// <summary>
/// Счётчик кадров, который запускается по первому запросу и сам останавливается, если его долго не спрашивают:
/// сеанс ETW не должен работать, когда оверлей выключен и ничего не записывается.
/// Используется службой и приложением, запущенным от имени администратора.
/// </summary>
public sealed class FrameCaptureHost : IDisposable
{
    private readonly object _lock = new();
    private readonly string _sessionName;
    private readonly TimeSpan _idleTimeout;
    private readonly Timer _idleTimer;
    private EtwFrameCapture? _capture;
    private FrameCaptureException? _lastFailure;
    private long _lastFailureAt;
    private long _lastUse;
    private bool _disposed;

    public FrameCaptureHost(string sessionName, TimeSpan? idleTimeout = null)
    {
        _sessionName = sessionName;
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        _idleTimer = new Timer(_ => StopIfIdle(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public FrameQueryResult Query(int? processId, long since)
    {
        lock (_lock)
        {
            EtwFrameCapture capture = EnsureStarted();
            IReadOnlyList<FramePresenter> presenters = capture.GetPresenters();
            long[] frames = processId is int pid ? capture.GetFrames(pid, since) : Array.Empty<long>();
            return new FrameQueryResult(presenters, frames, Stopwatch.GetTimestamp());
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _idleTimer.Dispose();
            _capture?.Dispose();
            _capture = null;
        }
    }

    private EtwFrameCapture EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lastUse = Stopwatch.GetTimestamp();
        if (_capture is { IsRunning: true })
            return _capture;

        if (_capture is not null)
        {
            // Сеанс остановили снаружи (например, командой logman): запускаем заново.
            _capture.Dispose();
            _capture = null;
        }

        // После ошибки не пытаемся снова каждые 250 мс: раз в 10 секунд достаточно.
        if (_lastFailure is not null && Stopwatch.GetElapsedTime(_lastFailureAt) < TimeSpan.FromSeconds(10))
            throw _lastFailure;

        try
        {
            _capture = EtwFrameCapture.Start(_sessionName);
            _lastFailure = null;
            return _capture;
        }
        catch (FrameCaptureException ex)
        {
            AppLog.Warn("Счётчик кадров не запустился: " + ex.Message);
            _lastFailure = ex;
            _lastFailureAt = Stopwatch.GetTimestamp();
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка запуска счётчика кадров.", ex);
            _lastFailure = new FrameCaptureException("Ошибка запуска счётчика кадров: " + ex.Message, ex);
            _lastFailureAt = Stopwatch.GetTimestamp();
            throw _lastFailure;
        }
    }

    private void StopIfIdle()
    {
        lock (_lock)
        {
            if (_capture is null || _disposed || Stopwatch.GetElapsedTime(_lastUse) < _idleTimeout)
                return;

            AppLog.Info("Счётчик кадров никто не спрашивает: останавливаю сеанс ETW.");
            _capture.Dispose();
            _capture = null;
        }
    }
}

/// <summary>Кадры напрямую через ETW: приложение запущено от имени администратора.</summary>
public sealed class LocalFrameSource : IFrameSource
{
    private readonly FrameCaptureHost _host = new("PcToolkit Frames (app)");

    public string Name => "напрямую (права администратора)";

    public FrameQueryResult? Query(int? processId, long since, out string? error)
    {
        try
        {
            error = null;
            return _host.Query(processId, since);
        }
        catch (FrameCaptureException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>Кадры через службу PC Toolkit: приложению права администратора не нужны.</summary>
public sealed class ServiceFrameSource(ServiceClient client) : IFrameSource
{
    public string Name => "служба PC Toolkit";

    public FrameQueryResult? Query(int? processId, long since, out string? error)
    {
        ServiceResponse? response = client.Send(
            new ServiceRequest { Op = ServiceOps.Frames, ProcessId = processId, Since = since }, 2000);
        if (response is null)
        {
            error = "служба PC Toolkit не отвечает";
            return null;
        }

        if (!response.Ok)
        {
            error = response.Error is { } text && text.Contains("Неизвестная команда", StringComparison.Ordinal)
                ? "служба PC Toolkit устарела — обновите её в «Настройках»"
                : response.Error ?? "служба не смогла запустить счётчик кадров";
            return null;
        }

        error = null;
        return new FrameQueryResult(
            (IReadOnlyList<FramePresenter>?)response.Presenters ?? Array.Empty<FramePresenter>(),
            response.Frames ?? Array.Empty<long>(),
            response.Now ?? Stopwatch.GetTimestamp());
    }

    public void Dispose()
    {
        // Соединение со службой общее для всего приложения и закрывается вместе с ним.
    }
}

/// <summary>Счётчик кадров недоступен: нет ни службы, ни прав администратора.</summary>
public sealed class UnavailableFrameSource(string reason) : IFrameSource
{
    public string Name => "недоступен";

    public FrameQueryResult? Query(int? processId, long since, out string? error)
    {
        error = reason;
        return null;
    }

    public void Dispose()
    {
    }
}
