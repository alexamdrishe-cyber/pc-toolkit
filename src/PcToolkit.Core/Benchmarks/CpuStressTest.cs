using System.Diagnostics;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Benchmarks;

/// <summary>Одна найденная ошибка вычислений.</summary>
public sealed record CpuTestError(TimeSpan At, CpuWorkload Workload, int Thread, string Detail);

/// <summary>Параметры нагрузки одного вида: сколько раундов в одном вызове и какой результат правильный.</summary>
internal sealed class CalibratedWorkload
{
    public required CpuWorkload Workload { get; init; }

    public required long Rounds { get; init; }

    public required ulong ExpectedBits { get; init; }
}

/// <summary>
/// Стресс-тест процессора: по потоку на каждое логическое ядро, нагрузки выбранных видов по очереди.
/// Каждый результат сверяется с эталоном, посчитанным до начала нагрева; расхождение — ошибка.
/// Останавливается по времени, по кнопке или по команде извне (например, при перегреве).
/// </summary>
public sealed class CpuStressTest : IDisposable
{
    /// <summary>Сколько длится один вызов нагрузки: достаточно коротко, чтобы быстро останавливаться.</summary>
    private static readonly TimeSpan SliceTarget = TimeSpan.FromMilliseconds(40);

    private const int MaxStoredErrors = 50;

    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _threads = new();
    private readonly List<CpuTestError> _errors = new();
    private readonly object _errorsLock = new();
    private readonly Stopwatch _watch = new();
    private IReadOnlyList<CalibratedWorkload> _workloads = Array.Empty<CalibratedWorkload>();
    private long _slices;
    private long _errorCount;
    private int _running;

    public CpuStressTest(IReadOnlyCollection<CpuWorkload> workloads, int? threads = null, long memoryBytes = 0)
    {
        if (workloads.Count == 0)
            throw new ArgumentException("Нужно выбрать хотя бы один вид нагрузки.", nameof(workloads));

        Workloads = workloads.Distinct().ToList();
        ThreadCount = Math.Max(1, threads ?? Environment.ProcessorCount);
        MemoryBytes = memoryBytes > 0 ? memoryBytes : DefaultMemoryBytes();
    }

    public IReadOnlyList<CpuWorkload> Workloads { get; }

    public int ThreadCount { get; }

    /// <summary>Сколько памяти всего занимает нагрузка «Память» (делится между потоками).</summary>
    public long MemoryBytes { get; }

    public TimeSpan Elapsed => _watch.Elapsed;

    public long Slices => Interlocked.Read(ref _slices);

    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public bool IsRunning => Volatile.Read(ref _running) > 0;

    public IReadOnlyList<CpuTestError> Errors
    {
        get
        {
            lock (_errorsLock)
                return _errors.ToList();
        }
    }

    /// <summary>Четверть оперативной памяти, но не больше 4 ГБ: систему не должно выдавливать в файл подкачки.</summary>
    public static long DefaultMemoryBytes()
    {
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return Math.Clamp(total / 4, 256L * 1024 * 1024, 4L * 1024 * 1024 * 1024);
    }

    /// <summary>Подбирает размер вызовов и считает эталонные результаты. Занимает около секунды.</summary>
    public void Prepare()
    {
        _workloads = Workloads.Where(w => w != CpuWorkload.Memory).Select(Calibrate).ToList();
    }

    public void Start()
    {
        if (_threads.Count > 0)
            throw new InvalidOperationException("Тест уже запущен.");

        if (_workloads.Count == 0 && Workloads.Any(w => w != CpuWorkload.Memory))
            Prepare();

        long wordsPerThread = Workloads.Contains(CpuWorkload.Memory)
            ? Math.Max(1024, MemoryBytes / ThreadCount / sizeof(ulong))
            : 0;

        _watch.Restart();
        for (int i = 0; i < ThreadCount; i++)
        {
            int index = i;
            var thread = new Thread(() => Work(index, wordsPerThread))
            {
                IsBackground = true,
                Name = $"PcToolkit.CpuStress.{i}",
                // Чуть ниже обычного: интерфейс остаётся отзывчивым, а процессор всё равно загружен на 100 %.
                Priority = ThreadPriority.BelowNormal,
            };
            _threads.Add(thread);
        }

        Interlocked.Exchange(ref _running, _threads.Count);
        foreach (Thread thread in _threads)
            thread.Start();

        AppLog.Info($"Стресс-тест процессора запущен: {string.Join(", ", Workloads)}, потоков {ThreadCount}.");
    }

    public void Stop(string reason = "остановлен")
    {
        if (_cts.IsCancellationRequested)
            return;

        _cts.Cancel();
        foreach (Thread thread in _threads)
            thread.Join(TimeSpan.FromSeconds(5));
        _watch.Stop();
        AppLog.Info($"Стресс-тест процессора {reason}: {Elapsed:hh\\:mm\\:ss}, ошибок {ErrorCount}.");
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    private void Work(int threadIndex, long memoryWords)
    {
        try
        {
            ulong[]? cache = Workloads.Contains(CpuWorkload.Cache) ? new ulong[CpuKernels.CacheWords] : null;
            ulong[]? memory = memoryWords > 0 ? GC.AllocateUninitializedArray<ulong>((int)Math.Min(memoryWords, int.MaxValue)) : null;
            ulong pass = (ulong)threadIndex * 1000;
            int next = threadIndex;

            while (!_cts.IsCancellationRequested)
            {
                CpuWorkload workload = Workloads[next++ % Workloads.Count];
                if (workload == CpuWorkload.Memory)
                {
                    long bad = CpuKernels.Memory(memory!, pass++);
                    if (bad > 0)
                        AddError(workload, threadIndex, $"{bad} слов памяти прочитались не так, как записались");
                }
                else
                {
                    CalibratedWorkload calibrated = _workloads.First(w => w.Workload == workload);
                    ulong bits = Run(calibrated.Workload, calibrated.Rounds, cache);
                    if (bits != calibrated.ExpectedBits)
                        AddError(workload, threadIndex, "результат вычислений не совпал с эталоном");
                }

                Interlocked.Increment(ref _slices);
            }
        }
        catch (OutOfMemoryException)
        {
            AddError(CpuWorkload.Memory, threadIndex, "не хватило памяти для теста");
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private void AddError(CpuWorkload workload, int thread, string detail)
    {
        Interlocked.Increment(ref _errorCount);
        lock (_errorsLock)
        {
            if (_errors.Count < MaxStoredErrors)
                _errors.Add(new CpuTestError(_watch.Elapsed, workload, thread, detail));
        }
    }

    internal static ulong Run(CpuWorkload workload, long rounds, ulong[]? cache) => workload switch
    {
        CpuWorkload.Integer => CpuKernels.Integer(rounds),
        CpuWorkload.Float => (ulong)BitConverter.DoubleToInt64Bits(CpuKernels.Float(rounds)),
        CpuWorkload.Cache => CpuKernels.Cache(cache ?? new ulong[CpuKernels.CacheWords], rounds),
        _ => throw new ArgumentOutOfRangeException(nameof(workload)),
    };

    /// <summary>Подбирает число раундов на ~40 мс и считает эталон на холодном процессоре.</summary>
    internal static CalibratedWorkload Calibrate(CpuWorkload workload)
    {
        ulong[] cache = new ulong[CpuKernels.CacheWords];
        long rounds = 10_000;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            watch.Restart();
            Run(workload, rounds, cache);
            watch.Stop();
            if (watch.Elapsed >= TimeSpan.FromMilliseconds(5) || rounds > 1_000_000_000)
                break;
            rounds *= 4;
        }

        double scale = SliceTarget.TotalMilliseconds / Math.Max(watch.Elapsed.TotalMilliseconds, 0.001);
        long target = Math.Max(1000, (long)(rounds * scale));
        ulong expected = Run(workload, target, cache);
        return new CalibratedWorkload { Workload = workload, Rounds = target, ExpectedBits = expected };
    }
}
