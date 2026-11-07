using System.Diagnostics;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Games;

/// <summary>Что показывает счётчик кадров сейчас.</summary>
public sealed record FrameMonitorState(
    int? ProcessId,
    string? ProcessName,
    string? Api,
    double Fps,
    double FrameMs,
    double Low1Fps,
    double[] RecentFrameTimes,
    IReadOnlyList<FramePresenter> Presenters,
    string? Error,
    bool InForeground = false)
{
    public bool HasTarget => ProcessId is not null;
}

/// <summary>Получает новые кадры отслеживаемого процесса (для записи замера).</summary>
public interface IFrameListener
{
    /// <param name="frameTimesMs">Длительности новых кадров по порядку.</param>
    void OnFrames(int processId, IReadOnlyList<double> frameTimesMs);
}

/// <summary>
/// Опрашивает счётчик кадров 4 раза в секунду и следит за одним процессом: закреплённым
/// (например, при записи замера) или тем, что на переднем плане и выводит кадры (игра).
/// Пока ни оверлей, ни запись не нужны, ничего не опрашивает.
/// </summary>
public sealed class FrameMonitor : IDisposable
{
    private const double WindowSeconds = 30;   // 1% low в оверлее — за последние 30 секунд
    private const double GraphSeconds = 5;     // график времени кадра — последние 5 секунд

    private readonly IFrameSource _source;
    private readonly Func<int?> _foreground;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private readonly List<IFrameListener> _listeners = new();
    private readonly Queue<long> _recent = new();
    private readonly int _ownProcessId = Environment.ProcessId;
    private int _users;
    private int? _target;
    private string? _targetName;
    private long _since;
    private long _lastFrame;
    private int _busy;
    private bool _disposed;

    public FrameMonitor(IFrameSource source, Func<int?>? foregroundProcess = null)
    {
        _source = source;
        _foreground = foregroundProcess ?? ProcessInfo.ForegroundProcessId;
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Новое состояние. Вызывается в фоновом потоке.</summary>
    public event EventHandler<FrameMonitorState>? Updated;

    public string SourceName => _source.Name;

    /// <summary>Процесс, за которым следить, независимо от переднего плана (null — автоматически).</summary>
    public int? PinnedProcessId { get; set; }

    public FrameMonitorState? Last { get; private set; }

    /// <summary>Начать опрос (оверлей, запись, открытый экран). Каждому Acquire — свой Release.</summary>
    public void Acquire()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            if (_users++ == 0)
                _timer.Change(0, 250);
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_users == 0 || --_users > 0 || _disposed)
                return;

            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _target = null;
            _recent.Clear();
            _lastFrame = 0;
        }
    }

    public void AddListener(IFrameListener listener)
    {
        lock (_lock)
            _listeners.Add(listener);
    }

    public void RemoveListener(IFrameListener listener)
    {
        lock (_lock)
            _listeners.Remove(listener);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer.Dispose();
        }

        _source.Dispose();
    }

    private void Poll()
    {
        // Опрос не должен накладываться сам на себя, если служба ответила медленно.
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        try
        {
            int? target;
            long since;
            lock (_lock)
            {
                if (_disposed || _users == 0)
                    return;
                target = _target;
                since = _since;
            }

            FrameQueryResult? result = _source.Query(target, since, out string? error);
            if (result is null)
            {
                Publish(new FrameMonitorState(null, null, null, 0, 0, 0, Array.Empty<double>(), Array.Empty<FramePresenter>(), error));
                return;
            }

            FrameMonitorState state = Absorb(result, target);
            Publish(state);
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка счётчика кадров.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private FrameMonitorState Absorb(FrameQueryResult raw, int? queried)
    {
        long frequency = Stopwatch.Frequency;
        // Окно самого PC Toolkit (WPF рисует через Direct3D 9) и оверлей — не игра.
        FrameQueryResult result = raw with { Presenters = raw.Presenters.Where(p => p.ProcessId != _ownProcessId).ToList() };
        int? foregroundProcess = _foreground();
        List<IFrameListener> listeners;
        FrameMonitorState state;
        lock (_lock)
        {
            // Кадры, полученные для процесса, за которым следили во время запроса. Пока игра не на экране
            // (свёрнута, Alt+Tab), Windows её притормаживает: такие кадры не считаются ни в оверлее, ни в записи,
            // иначе FPS и 1% low «провалятся» не по вине игры.
            bool inForeground = queried is int q && q == foregroundProcess;
            double[] newTimes = Array.Empty<double>();
            if (queried is int pid && pid == _target && result.Frames.Length > 0)
            {
                _since = result.Frames[^1];
                if (inForeground)
                {
                    var times = new List<long>(result.Frames.Length + 1);
                    if (_lastFrame > 0)
                        times.Add(_lastFrame);
                    foreach (long t in result.Frames)
                    {
                        _recent.Enqueue(t);
                        times.Add(t);
                    }

                    _lastFrame = result.Frames[^1];
                    newTimes = FrameMath.ToFrameTimes(times, frequency);
                }
            }

            if (!inForeground)
            {
                // После возврата в игру статистика начнётся заново, без долгого «кадра» на время паузы.
                _recent.Clear();
                _lastFrame = 0;
            }

            // Кого показывать дальше.
            int? next = ChooseTarget(result.Presenters, foregroundProcess);
            if (next != _target)
            {
                _target = next;
                _targetName = next is int id ? ProcessInfo.Name(id) : null;
                _recent.Clear();
                _lastFrame = 0;
                // Для нового процесса берём кадры за последние 2 секунды, чтобы сразу было что показать.
                _since = result.Now - 2 * frequency;
                newTimes = Array.Empty<double>();
            }

            long horizon = (_lastFrame > 0 ? _lastFrame : result.Now) - (long)(WindowSeconds * frequency);
            while (_recent.Count > 0 && _recent.Peek() < horizon)
                _recent.Dequeue();

            listeners = newTimes.Length > 0 && _target is not null ? _listeners.ToList() : new List<IFrameListener>();
            state = Summarize(result, frequency) with { InForeground = _target is int shown && shown == foregroundProcess };

            if (listeners.Count > 0)
            {
                int targetId = _target!.Value;
                foreach (IFrameListener listener in listeners)
                    listener.OnFrames(targetId, newTimes);
            }
        }

        return state;
    }

    private int? ChooseTarget(IReadOnlyList<FramePresenter> presenters, int? foreground)
    {
        if (PinnedProcessId is int pinned)
            return pinned;

        if (foreground is int fg && presenters.Any(p => p.ProcessId == fg))
            return fg;

        // Игра не на переднем плане (например, открыт оверлей Steam или второй монитор): продолжаем следить,
        // пока она выводит кадры.
        if (_target is int current && presenters.Any(p => p.ProcessId == current && p.Fps > 0))
            return current;

        return null;
    }

    private FrameMonitorState Summarize(FrameQueryResult result, long frequency)
    {
        string? api = _target is int id ? result.Presenters.FirstOrDefault(p => p.ProcessId == id)?.Api : null;
        if (_target is null || _recent.Count < 2)
            return new FrameMonitorState(_target, _targetName, api, 0, 0, 0, Array.Empty<double>(), result.Presenters, null);

        long[] all = _recent.ToArray();
        long last = all[^1];

        // Игра остановилась (загрузка, зависание): кадров больше 3 секунд нет — FPS 0. События приходят с задержкой до секунды.
        if (result.Now - last > 3 * frequency)
            return new FrameMonitorState(_target, _targetName, api, 0, 0, 0, Array.Empty<double>(), result.Presenters, null);

        // FPS за последнюю секунду, считая от последнего полученного кадра: события приходят с задержкой до секунды.
        int firstInSecond = Math.Max(Array.FindIndex(all, t => t > last - frequency), 0);
        int start = Math.Max(firstInSecond - 1, 0);
        int intervals = all.Length - 1 - start;
        double span = (last - all[start]) / (double)frequency;
        double fps = span > 0 ? intervals / span : 0;
        double frameMs = fps > 0 ? 1000 / fps : 0;

        double[] times = FrameMath.ToFrameTimes(all, frequency);
        FrameStatistics? stats = FrameStatistics.FromFrameTimes(times);

        int graphStart = Array.FindIndex(all, t => t > last - (long)(GraphSeconds * frequency));
        double[] graph = graphStart > 0 ? times[(graphStart - 1)..] : times;
        if (graph.Length > 600)
            graph = graph[^600..];

        return new FrameMonitorState(_target, _targetName, api, fps, frameMs, stats?.Low1Fps ?? 0, graph, result.Presenters, null);
    }

    private void Publish(FrameMonitorState state)
    {
        Last = state;
        Updated?.Invoke(this, state);
    }
}
