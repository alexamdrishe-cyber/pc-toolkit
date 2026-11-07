using System.Diagnostics;
using System.Runtime.InteropServices;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Benchmarks.Memory;

public enum MemoryTestPhase
{
    NotStarted,
    Preparing,
    Running,
    Finished,
}

/// <summary>Одна найденная ошибка памяти: где, что ожидали, что прочитали.</summary>
public sealed record MemoryTestError(TimeSpan At, string Pattern, long Offset, ulong Expected, ulong Actual)
{
    public ulong FlippedBits => Expected ^ Actual;
}

/// <summary>
/// Тест оперативной памяти в духе MemTest86 / TestMem5: занимает большую часть свободной памяти,
/// закрепляет её в RAM (чтобы Windows не унесла в файл подкачки) и гоняет по кругу классические алгоритмы.
/// Каждый поток проверяет свой участок; все потоки работают одновременно — это нагружает контроллер памяти
/// так же, как игры и тяжёлые программы, и ловит нестабильный разгон (XMP/EXPO, тайминги, напряжение).
/// Память программы обычная, без прав администратора; проверяется только то, что Windows смогла выделить.
/// </summary>
public sealed unsafe class MemoryTest : IDisposable
{
    /// <summary>Алгоритмы одного цикла по порядку.</summary>
    public static readonly IReadOnlyList<string> Patterns = new[]
    {
        "Свой адрес",
        "Бегущие инверсии: нули и единицы",
        "Бегущие инверсии: единица в каждом байте",
        "Случайные числа",
        "Перемещение блоков",
        "Модуль 20",
    };

    private const int MaxStoredErrors = 100;
    private const long SegmentWords = 1 << 20; // 8 МБ — шаг, на котором проверяется остановка и считается прогресс

    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _watch = new();
    private readonly List<MemoryTestError> _errors = new();
    private readonly object _errorsLock = new();
    private readonly List<Thread> _threads = new();
    private Block[] _blocks = Array.Empty<Block>();
    private long _errorCount;
    private long _flippedBits;
    private long _processedBytes;
    private int _running;
    private volatile MemoryTestPhase _phase;
    private volatile string? _failure;
    private nuint _oldMinWorkingSet;
    private nuint _oldMaxWorkingSet;
    private bool _workingSetChanged;

    /// <param name="bytes">Сколько памяти проверять (будет поделено между потоками).</param>
    public MemoryTest(long bytes, int? threads = null)
    {
        ThreadCount = Math.Max(1, threads ?? Environment.ProcessorCount);
        long perThread = Math.Max(16L << 20, bytes / ThreadCount) / 4096 * 4096;
        BlockBytes = perThread;
        RequestedBytes = perThread * ThreadCount;
    }

    public int ThreadCount { get; }

    public long BlockBytes { get; }

    public long RequestedBytes { get; }

    /// <summary>Сколько памяти реально выделено.</summary>
    public long AllocatedBytes => _blocks.Sum(b => b.Bytes);

    /// <summary>Сколько из неё закреплено в RAM (не может уйти в файл подкачки).</summary>
    public long LockedBytes => _blocks.Where(b => b.Locked).Sum(b => b.Bytes);

    public MemoryTestPhase Phase => _phase;

    public bool IsRunning => _phase is MemoryTestPhase.Preparing or MemoryTestPhase.Running;

    public TimeSpan Elapsed => _watch.Elapsed;

    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>Биты, которые хоть раз прочитались неверно (по всем ошибкам).</summary>
    public ulong FlippedBits => (ulong)Interlocked.Read(ref _flippedBits);

    public string? Failure => _failure;

    /// <summary>Полностью пройденных циклов (по самому медленному потоку).</summary>
    public int CompletedCycles => _blocks.Length == 0 ? 0 : _blocks.Min(b => Volatile.Read(ref b.Cycles));

    /// <summary>Номер алгоритма, который сейчас идёт у первого потока.</summary>
    public int CurrentPattern => _blocks.Length == 0 ? 0 : Volatile.Read(ref _blocks[0].Pattern);

    /// <summary>Доля текущего цикла, пройденная первым потоком (0–1).</summary>
    public double CycleProgress => _blocks.Length == 0 ? 0 : Math.Clamp(Volatile.Read(ref _blocks[0].CycleProgress), 0, 1);

    /// <summary>Сколько всего байт прочитано и записано — для скорости работы теста.</summary>
    public long ProcessedBytes => Interlocked.Read(ref _processedBytes);

    public IReadOnlyList<MemoryTestError> Errors
    {
        get
        {
            lock (_errorsLock)
                return _errors.ToList();
        }
    }

    /// <summary>Свободная физическая память сейчас.</summary>
    public static long AvailablePhysicalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailPhys : 0;
    }

    public static long TotalPhysicalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
        return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhys : 0;
    }

    public void Start()
    {
        if (_phase != MemoryTestPhase.NotStarted)
            throw new InvalidOperationException("Тест уже запущен.");

        _phase = MemoryTestPhase.Preparing;
        var thread = new Thread(Run) { IsBackground = true, Name = "PcToolkit.MemTest" };
        thread.Start();
    }

    public void Stop(string reason = "остановлен")
    {
        if (_cts.IsCancellationRequested)
            return;

        _cts.Cancel();
        SpinWait.SpinUntil(() => _phase == MemoryTestPhase.Finished, TimeSpan.FromSeconds(30));
        AppLog.Info($"Тест памяти {reason}: {Elapsed:hh\\:mm\\:ss}, циклов {CompletedCycles}, ошибок {ErrorCount}.");
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    private void Run()
    {
        try
        {
            Allocate();
            if (_cts.IsCancellationRequested)
                return;

            AppLog.Info($"Тест памяти запущен: {Formatting.Bytes((ulong)AllocatedBytes)} в {ThreadCount} потоках, " +
                        $"закреплено в RAM {Formatting.Bytes((ulong)LockedBytes)}.");
            _watch.Restart();
            _phase = MemoryTestPhase.Running;

            Interlocked.Exchange(ref _running, _blocks.Length);
            for (int i = 0; i < _blocks.Length; i++)
            {
                Block block = _blocks[i];
                var thread = new Thread(() => Work(block))
                {
                    IsBackground = true,
                    Name = $"PcToolkit.MemTest.{i}",
                    Priority = ThreadPriority.BelowNormal,
                };
                _threads.Add(thread);
                thread.Start();
            }

            foreach (Thread thread in _threads)
                thread.Join();
        }
        catch (Exception ex)
        {
            _failure = ex.Message;
            AppLog.Error("Тест памяти прерван ошибкой.", ex);
        }
        finally
        {
            _watch.Stop();
            Release();
            _phase = MemoryTestPhase.Finished;
        }
    }

    /// <summary>Выделяет участки и закрепляет их в RAM. Если закрепить не дали — тест всё равно идёт.</summary>
    private void Allocate()
    {
        IntPtr process = GetCurrentProcess();
        if (GetProcessWorkingSetSizeEx(process, out _oldMinWorkingSet, out _oldMaxWorkingSet, out _))
        {
            nuint min = (nuint)(RequestedBytes + (256L << 20));
            nuint max = (nuint)(RequestedBytes + (2048L << 20));
            _workingSetChanged = SetProcessWorkingSetSizeEx(process, min, max, 0);
            if (!_workingSetChanged)
                AppLog.Warn($"Не удалось увеличить рабочий набор (код {Marshal.GetLastWin32Error()}): память не будет закреплена в RAM.");
        }

        var blocks = new List<Block>();
        for (int i = 0; i < ThreadCount && !_cts.IsCancellationRequested; i++)
        {
            IntPtr memory = VirtualAlloc(IntPtr.Zero, (nuint)BlockBytes, 0x3000 /* COMMIT | RESERVE */, 0x04 /* READWRITE */);
            if (memory == IntPtr.Zero)
            {
                AppLog.Warn($"Не удалось выделить участок {i + 1} из {ThreadCount} (код {Marshal.GetLastWin32Error()}).");
                break;
            }

            var block = new Block { Index = i, Memory = (ulong*)memory, Bytes = BlockBytes };
            // VirtualLock ещё и загружает страницы в RAM — заодно проверяем, что память действительно есть.
            block.Locked = _workingSetChanged && VirtualLock(memory, (nuint)BlockBytes);
            blocks.Add(block);
            _blocks = blocks.ToArray();
        }

        if (blocks.Count == 0)
            throw new InvalidOperationException("Windows не выделила память для теста. Закройте программы и попробуйте меньший объём.");
    }

    private void Release()
    {
        foreach (Block block in _blocks)
        {
            if (block.Memory == null)
                continue;
            if (block.Locked)
                VirtualUnlock((IntPtr)block.Memory, (nuint)block.Bytes);
            VirtualFree((IntPtr)block.Memory, 0, 0x8000 /* RELEASE */);
            block.Memory = null;
        }

        if (_workingSetChanged)
        {
            SetProcessWorkingSetSizeEx(GetCurrentProcess(), _oldMinWorkingSet, _oldMaxWorkingSet, 0);
            _workingSetChanged = false;
        }
    }

    private void Work(Block block)
    {
        CancellationToken token = _cts.Token;
        ulong* p = block.Memory;
        long words = block.Bytes / sizeof(ulong);
        long offset = (long)block.Index * BlockBytes;
        var random = new Random(block.Index * 7919 + 17);
        double sweepsPerCycle = 27; // примерное число проходов по участку за цикл — для полоски прогресса
        double sweeps = 0;

        void Sweep(double count = 1)
        {
            sweeps += count;
            Volatile.Write(ref block.CycleProgress, sweeps / sweepsPerCycle);
        }

        try
        {
            for (int cycle = 0; !token.IsCancellationRequested; cycle++)
            {
                sweeps = 0;

                // 1. Свой адрес: каждое слово хранит собственный адрес (ловит ошибки адресации), затем его инверсию.
                Volatile.Write(ref block.Pattern, 0);
                ulong salt = (ulong)cycle * 0x9E3779B97F4A7C15UL;
                OwnAddress(block, p, words, offset, salt, token, Sweep);
                OwnAddress(block, p, words, offset, ~salt, token, Sweep);

                // 2. Бегущие инверсии нулей и единиц.
                Volatile.Write(ref block.Pattern, 1);
                MovingInversions(block, p, words, offset, 0UL, 1, token, Sweep);

                // 3. Бегущие инверсии: одна единица в каждом байте; за 4 цикла — все 8 положений.
                Volatile.Write(ref block.Pattern, 2);
                for (int k = 0; k < 2; k++)
                {
                    int shift = (cycle * 2 + k) % 8;
                    MovingInversions(block, p, words, offset, 0x0101010101010101UL << shift, 2, token, Sweep);
                }

                // 4. Случайные числа (своя последовательность у каждого потока и цикла).
                Volatile.Write(ref block.Pattern, 3);
                RandomInversions(block, p, words, offset, (ulong)random.NextInt64() | 1, token, Sweep);

                // 5. Перемещение блоков: копирование больших кусков (как в играх при загрузке) и проверка копии.
                Volatile.Write(ref block.Pattern, 4);
                BlockMove(block, p, words, offset, (ulong)cycle, token, Sweep);

                // 6. Модуль 20: одно слово из 20 хранит шаблон, а соседние много раз перезаписываются инверсией.
                Volatile.Write(ref block.Pattern, 5);
                Modulo20(block, p, words, offset, 0x5555AAAA3333CCCCUL ^ salt, cycle % 20, token, Sweep);

                if (!token.IsCancellationRequested)
                    Interlocked.Increment(ref block.Cycles);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    // ---------- Алгоритмы ----------
    // Участок проходится кусками по 8 МБ: между кусками — проверка остановки и учёт прогресса (Checkpoint).

    private void OwnAddress(Block block, ulong* p, long words, long offset, ulong salt, CancellationToken token, Action<double> sweep)
    {
        for (long from = 0; from < words; from += SegmentWords)
        {
            long to = Checkpoint(from, words, token);
            for (long i = from; i < to; i++)
                p[i] = (ulong)(p + i) ^ salt;
        }

        sweep(1);
        for (long from = 0; from < words; from += SegmentWords)
        {
            long to = Checkpoint(from, words, token);
            for (long i = from; i < to; i++)
            {
                ulong expected = (ulong)(p + i) ^ salt;
                ulong actual = p[i];
                if (actual != expected)
                    Report(0, offset + i * 8, expected, actual);
            }
        }

        sweep(1);
    }

    /// <summary>
    /// Бегущие инверсии (MemTest86, тесты 3–5): заполнить шаблоном; вверх по адресам — проверить и записать инверсию;
    /// вниз по адресам — проверить инверсию и записать шаблон. Ловит «залипшие» биты и влияние соседних ячеек.
    /// </summary>
    private void MovingInversions(Block block, ulong* p, long words, long offset, ulong pattern, int index,
        CancellationToken token, Action<double> sweep)
    {
        ulong inverse = ~pattern;
        for (long from = 0; from < words; from += SegmentWords)
        {
            long to = Checkpoint(from, words, token);
            for (long i = from; i < to; i++)
                p[i] = pattern;
        }

        sweep(1);
        for (long from = 0; from < words; from += SegmentWords)
        {
            long to = Checkpoint(from, words, token);
            for (long i = from; i < to; i++)
            {
                ulong actual = p[i];
                if (actual != pattern)
                    Report(index, offset + i * 8, pattern, actual);
                p[i] = inverse;
            }
        }

        sweep(2);
        for (long to = words; to > 0; to -= SegmentWords)
        {
            long from = Math.Max(0, to - SegmentWords);
            Checkpoint(from, to, token);
            for (long i = to - 1; i >= from; i--)
            {
                ulong actual = p[i];
                if (actual != inverse)
                    Report(index, offset + i * 8, inverse, actual);
                p[i] = pattern;
            }
        }

        sweep(2);
    }

    /// <summary>xorshift64: быстрая последовательность, которую легко повторить с начала.</summary>
    private static ulong Next(ref ulong x)
    {
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        return x;
    }

    private void RandomInversions(Block block, ulong* p, long words, long offset, ulong seed, CancellationToken token, Action<double> sweep)
    {
        ulong state = seed;
        for (long from = 0; from < words; from += SegmentWords)
        {
            long to = Checkpoint(from, words, token);
            for (long i = from; i < to; i++)
                p[i] = Next(ref state);
        }

        sweep(1);
        state = seed;
        for (long from = 0; from < words; from += SegmentWords)
        {
            long to = Checkpoint(from, words, token);
            for (long i = from; i < to; i++)
            {
                ulong expected = Next(ref state);
                ulong actual = p[i];
                if (actual != expected)
                    Report(3, offset + i * 8, expected, actual);
                p[i] = ~expected;
            }
        }

        sweep(2);
        state = seed;
        for (long from = 0; from < words; from += SegmentWords)
        {
            long to = Checkpoint(from, words, token);
            for (long i = from; i < to; i++)
            {
                ulong expected = ~Next(ref state);
                ulong actual = p[i];
                if (actual != expected)
                    Report(3, offset + i * 8, expected, actual);
            }
        }

        sweep(1);
    }

    private void BlockMove(Block block, ulong* p, long words, long offset, ulong salt, CancellationToken token, Action<double> sweep)
    {
        long half = words / 2;
        const ulong Multiplier = 0xD6E8FEB86659FD93UL;
        for (long from = 0; from < half; from += SegmentWords)
        {
            long to = Checkpoint(from, half, token);
            for (long i = from; i < to; i++)
                p[i] = ((ulong)i * Multiplier) ^ salt;
        }

        sweep(0.5);
        // Обычное копирование памяти (SIMD) большими кусками — как в программах.
        for (long from = 0; from < half; from += SegmentWords)
        {
            long to = Checkpoint(from, half, token);
            Buffer.MemoryCopy(p + from, p + half + from, (to - from) * 8, (to - from) * 8);
        }

        sweep(1);
        for (long from = 0; from < half; from += SegmentWords)
        {
            long to = Checkpoint(from, half, token);
            for (long i = from; i < to; i++)
            {
                ulong expected = ((ulong)i * Multiplier) ^ salt;
                ulong actual = p[half + i];
                if (actual != expected)
                    Report(4, offset + (half + i) * 8, expected, actual);
            }
        }

        sweep(0.5);
    }

    private void Modulo20(Block block, ulong* p, long words, long offset, ulong pattern, int start, CancellationToken token,
        Action<double> sweep)
    {
        ulong inverse = ~pattern;
        // Первое слово с шаблоном в куске [from, to): индекс ≡ start (mod 20).
        static long First(long from, int start) => from + ((start - from % 20) % 20 + 20) % 20;

        for (long from = 0; from < words; from += SegmentWords)
        {
            long to = Checkpoint(from, words, token);
            long next = First(from, start);
            for (long i = from; i < to; i++)
            {
                if (i == next)
                {
                    p[i] = pattern;
                    next += 20;
                }
                else
                {
                    p[i] = inverse;
                }
            }
        }

        sweep(1);
        // Соседей перезаписываем инверсией ещё дважды — шаблон в каждом 20-м слове должен уцелеть.
        for (int repeat = 0; repeat < 2; repeat++)
        {
            for (long from = 0; from < words; from += SegmentWords)
            {
                long to = Checkpoint(from, words, token);
                long next = First(from, start);
                for (long i = from; i < to; i++)
                {
                    if (i == next)
                        next += 20;
                    else
                        p[i] = inverse;
                }
            }

            sweep(0.5);
        }

        for (long from = 0; from < words; from += SegmentWords)
        {
            long to = Checkpoint(from, words, token);
            for (long i = First(from, start); i < to; i += 20)
            {
                ulong actual = p[i];
                if (actual != pattern)
                    Report(5, offset + i * 8, pattern, actual);
            }
        }

        sweep(0.1);
    }

    // ---------- Служебное ----------

    /// <summary>Проверяет остановку, учитывает прогресс и возвращает конец куска.</summary>
    private long Checkpoint(long from, long limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long to = Math.Min(limit, from + SegmentWords);
        Interlocked.Add(ref _processedBytes, (to - from) * 8);
        return to;
    }

    private void Report(int pattern, long offset, ulong expected, ulong actual)
    {
        Interlocked.Increment(ref _errorCount);
        long bits = (long)(expected ^ actual);
        long current;
        do
        {
            current = Interlocked.Read(ref _flippedBits);
        }
        while ((current | bits) != current && Interlocked.CompareExchange(ref _flippedBits, current | bits, current) != current);

        lock (_errorsLock)
        {
            if (_errors.Count < MaxStoredErrors)
                _errors.Add(new MemoryTestError(_watch.Elapsed, Patterns[pattern], offset, expected, actual));
        }
    }

    private sealed class Block
    {
        public int Index;
        public ulong* Memory;
        public long Bytes;
        public bool Locked;
        public int Cycles;
        public int Pattern;
        public double CycleProgress;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualLock(IntPtr address, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualUnlock(IntPtr address, nuint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessWorkingSetSizeEx(IntPtr process, out nuint min, out nuint max, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, nuint min, nuint max, uint flags);
}
