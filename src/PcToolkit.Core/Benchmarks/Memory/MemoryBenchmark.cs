using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json.Serialization;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Benchmarks.Memory;

public sealed record MemoryBenchmarkProgress(string Step, int Index, int Count);

/// <summary>Скорость памяти в духе AIDA64: чтение, запись, копирование (ГБ/с) и задержка (нс).</summary>
public sealed record MemoryBenchmarkResult(double Read, double Write, double Copy, double Latency);

/// <summary>
/// Замер скорости оперативной памяти. Буферы намного больше кэша процессора (по 128 МБ на поток),
/// работают все потоки сразу. Запись и копирование — с «потоковой» записью мимо кэша, как в AIDA64.
/// Задержка — переходы по случайной цепочке в 256 МБ (обычные страницы Windows, поэтому чуть больше, чем в AIDA64).
/// </summary>
public static unsafe class MemoryBenchmark
{
    /// <summary>Версия замера для общего рейтинга: меняется, если результаты перестают быть сравнимыми со старыми.</summary>
    public const int Version = 1;

    private const long BytesPerThread = 128L << 20;
    private static readonly TimeSpan StepDuration = TimeSpan.FromSeconds(2.5);

    public static MemoryBenchmarkResult Run(IProgress<MemoryBenchmarkProgress>? progress = null, CancellationToken cancellation = default)
    {
        int threads = Environment.ProcessorCount;
        progress?.Report(new MemoryBenchmarkProgress("чтение", 1, 4));
        double read = Bandwidth(threads, Kind.Read, cancellation);
        progress?.Report(new MemoryBenchmarkProgress("запись", 2, 4));
        double write = Bandwidth(threads, Kind.Write, cancellation);
        progress?.Report(new MemoryBenchmarkProgress("копирование", 3, 4));
        double copy = Bandwidth(threads, Kind.Copy, cancellation);
        progress?.Report(new MemoryBenchmarkProgress("задержка", 4, 4));
        double latency = Latency(cancellation);

        AppLog.Info($"Замер памяти: чтение {read:0.0} ГБ/с, запись {write:0.0} ГБ/с, копирование {copy:0.0} ГБ/с, задержка {latency:0.0} нс.");
        return new MemoryBenchmarkResult(read, write, copy, latency);
    }

    private enum Kind
    {
        Read,
        Write,
        Copy,
    }

    /// <summary>ГБ/с (10⁹ байт), все потоки одновременно.</summary>
    private static double Bandwidth(int threads, Kind kind, CancellationToken cancellation)
    {
        long bytes = kind == Kind.Copy ? BytesPerThread / 2 : BytesPerThread;
        var buffers = new byte*[threads];
        var destinations = new byte*[threads];
        try
        {
            for (int t = 0; t < threads; t++)
            {
                buffers[t] = (byte*)NativeMemory.AlignedAlloc((nuint)bytes, 4096);
                NativeMemory.Fill(buffers[t], (nuint)bytes, (byte)(t + 1)); // страницы реально выделены
                if (kind == Kind.Copy)
                {
                    destinations[t] = (byte*)NativeMemory.AlignedAlloc((nuint)bytes, 4096);
                    NativeMemory.Clear(destinations[t], (nuint)bytes);
                }
            }

            long total = 0;
            var start = new Barrier(threads + 1);
            var stop = 0;
            var workers = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                int index = t;
                workers[t] = new Thread(() =>
                {
                    byte* source = buffers[index];
                    byte* destination = destinations[index];
                    long done = 0;
                    ulong sink = 0;
                    start.SignalAndWait();
                    while (Volatile.Read(ref stop) == 0)
                    {
                        switch (kind)
                        {
                            case Kind.Read:
                                sink ^= ReadPass(source, bytes);
                                break;
                            case Kind.Write:
                                WritePass(source, bytes);
                                break;
                            default:
                                CopyPass(source, destination, bytes);
                                break;
                        }

                        // Копирование считаем как в AIDA64: прочитанные плюс записанные байты.
                        done += kind == Kind.Copy ? bytes * 2 : bytes;
                    }

                    if (sink == 0x1234567)
                        Console.Write(""); // результат чтения «используется» — компилятор не выбросит цикл
                    Interlocked.Add(ref total, done);
                })
                { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = $"PcToolkit.MemBench.{t}" };
                workers[t].Start();
            }

            // Считаем только целые проходы; время — до момента, когда все потоки закончили свой последний проход.
            start.SignalAndWait();
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < StepDuration)
            {
                if (cancellation.IsCancellationRequested)
                    break;
                Thread.Sleep(20);
            }

            Volatile.Write(ref stop, 1);
            foreach (Thread worker in workers)
                worker.Join();
            watch.Stop();
            cancellation.ThrowIfCancellationRequested();
            return total / watch.Elapsed.TotalSeconds / 1e9;
        }
        finally
        {
            for (int t = 0; t < threads; t++)
            {
                if (buffers[t] != null)
                    NativeMemory.AlignedFree(buffers[t]);
                if (destinations[t] != null)
                    NativeMemory.AlignedFree(destinations[t]);
            }
        }
    }

    private static ulong ReadPass(byte* p, long bytes)
    {
        if (Avx2.IsSupported)
        {
            Vector256<ulong> a = Vector256<ulong>.Zero, b = a, c = a, d = a;
            for (long i = 0; i < bytes; i += 128)
            {
                a = Avx2.Xor(a, Avx.LoadAlignedVector256((ulong*)(p + i)));
                b = Avx2.Xor(b, Avx.LoadAlignedVector256((ulong*)(p + i + 32)));
                c = Avx2.Xor(c, Avx.LoadAlignedVector256((ulong*)(p + i + 64)));
                d = Avx2.Xor(d, Avx.LoadAlignedVector256((ulong*)(p + i + 96)));
            }

            Vector256<ulong> x = Avx2.Xor(Avx2.Xor(a, b), Avx2.Xor(c, d));
            return x.GetElement(0) ^ x.GetElement(1) ^ x.GetElement(2) ^ x.GetElement(3);
        }

        ulong s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        ulong* q = (ulong*)p;
        for (long i = 0; i < bytes / 8; i += 4)
        {
            s0 ^= q[i];
            s1 ^= q[i + 1];
            s2 ^= q[i + 2];
            s3 ^= q[i + 3];
        }

        return s0 ^ s1 ^ s2 ^ s3;
    }

    private static void WritePass(byte* p, long bytes)
    {
        if (Avx.IsSupported)
        {
            Vector256<byte> value = Vector256.Create((byte)0x5A);
            for (long i = 0; i < bytes; i += 128)
            {
                Avx.StoreAlignedNonTemporal(p + i, value);
                Avx.StoreAlignedNonTemporal(p + i + 32, value);
                Avx.StoreAlignedNonTemporal(p + i + 64, value);
                Avx.StoreAlignedNonTemporal(p + i + 96, value);
            }

            Sse.StoreFence();
            return;
        }

        NativeMemory.Fill(p, (nuint)bytes, 0x5A);
    }

    private static void CopyPass(byte* source, byte* destination, long bytes)
    {
        if (Avx.IsSupported)
        {
            for (long i = 0; i < bytes; i += 128)
            {
                Avx.StoreAlignedNonTemporal(destination + i, Avx.LoadAlignedVector256(source + i));
                Avx.StoreAlignedNonTemporal(destination + i + 32, Avx.LoadAlignedVector256(source + i + 32));
                Avx.StoreAlignedNonTemporal(destination + i + 64, Avx.LoadAlignedVector256(source + i + 64));
                Avx.StoreAlignedNonTemporal(destination + i + 96, Avx.LoadAlignedVector256(source + i + 96));
            }

            Sse.StoreFence();
            return;
        }

        Buffer.MemoryCopy(source, destination, bytes, bytes);
    }

    /// <summary>Задержка, нс: каждый следующий адрес зависит от прочитанного, поэтому процессор не может читать наперёд.</summary>
    private static double Latency(CancellationToken cancellation)
    {
        const long Bytes = 256L << 20;
        const int Line = 64;
        long nodes = Bytes / Line;
        byte* memory = (byte*)NativeMemory.AlignedAlloc((nuint)Bytes, 4096);
        try
        {
            // Случайный цикл через все строки кэша (алгоритм Саттоло): каждая строка хранит адрес следующей.
            var order = new int[nodes];
            for (int i = 0; i < nodes; i++)
                order[i] = i;
            var random = new Random(42);
            for (long i = nodes - 1; i > 0; i--)
            {
                long j = random.NextInt64(i);
                (order[i], order[j]) = (order[j], order[i]);
            }

            for (long i = 0; i < nodes; i++)
            {
                long from = order[i];
                long to = order[(i + 1) % nodes];
                *(byte**)(memory + from * Line) = memory + to * Line;
            }

            cancellation.ThrowIfCancellationRequested();
            byte* p = memory;
            for (int i = 0; i < 1_000_000; i++) // прогрев
                p = *(byte**)p;

            long steps = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                for (int i = 0; i < 100_000; i++)
                    p = *(byte**)p;
                steps += 100_000;
                cancellation.ThrowIfCancellationRequested();
            }

            watch.Stop();
            if (p == null)
                Console.Write("");
            return watch.Elapsed.TotalMilliseconds * 1e6 / steps;
        }
        finally
        {
            NativeMemory.AlignedFree(memory);
        }
    }
}

public sealed class MemoryBenchmarkRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset At { get; set; }

    /// <summary>Описание памяти на момент замера: «DDR4-3200, 2 модуля, два канала».</summary>
    public string Memory { get; set; } = "";

    public double Read { get; set; }

    public double Write { get; set; }

    public double Copy { get; set; }

    /// <summary>нс.</summary>
    public double Latency { get; set; }

    /// <summary>Теоретический максимум для этой памяти, ГБ/с (0 — неизвестно).</summary>
    public double Theoretical { get; set; }

    [JsonIgnore]
    public double? Efficiency => Theoretical > 0 ? Read / Theoretical : null;
}
