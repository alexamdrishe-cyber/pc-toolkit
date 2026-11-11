using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Benchmarks.Disk;

/// <summary>Один тест как в CrystalDiskMark: размер блока, глубина очереди, последовательно или вразброс.</summary>
public sealed record DiskTestSpec(string Name, int BlockSize, int QueueDepth, bool Random, string Hint);

/// <summary>Результат одного теста: МБ/с (10⁶ байт), операций в секунду и средняя задержка одной операции.</summary>
public sealed record DiskTestResult(string Name, double ReadMBps, double WriteMBps, double ReadIops, double WriteIops,
    double ReadLatencyUs, double WriteLatencyUs);

public sealed record DiskBenchmarkProgress(string Step, int Index, int Count);

/// <summary>
/// Скорость диска в духе CrystalDiskMark. Работает с временным файлом в папке на выбранном диске
/// (на системном — во временной папке пользователя): файл заполняется случайными данными, затем
/// читается и пишется блоками без кэша Windows (FILE_FLAG_NO_BUFFERING), с заданной глубиной очереди.
/// Файл удаляется сам при закрытии (DeleteOnClose) — даже если программа упадёт.
/// </summary>
public static class DiskBenchmark
{
    /// <summary>Версия замера для общего рейтинга: меняется, если результаты перестают быть сравнимыми со старыми.</summary>
    public const int Version = 1;

    public static readonly IReadOnlyList<DiskTestSpec> Specs = new DiskTestSpec[]
    {
        new("SEQ1M Q8T1", 1 << 20, 8, false, "Последовательно, блоки 1 МБ, очередь 8: копирование больших файлов, установка игр"),
        new("SEQ1M Q1T1", 1 << 20, 1, false, "Последовательно, очередь 1: один большой файл"),
        new("RND4K Q32T1", 4096, 32, true, "Вразброс, блоки 4 КБ, очередь 32: много одновременных мелких запросов"),
        new("RND4K Q1T1", 4096, 1, true, "Вразброс, очередь 1: отзывчивость Windows и программ — самое важное для ощущения скорости"),
    };

    private const int Alignment = 4096;

    /// <summary>Папка для временного файла на диске <paramref name="root"/> (например, «D:\»).</summary>
    public static string TestFolder(string root)
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return string.Equals(Path.GetPathRoot(root), systemRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetTempPath(), "PcToolkit")
            : Path.Combine(root, "PcToolkitDiskTest");
    }

    public static async Task<IReadOnlyList<DiskTestResult>> RunAsync(string root, long fileSize, int runs, TimeSpan duration,
        IProgress<DiskBenchmarkProgress>? progress = null, CancellationToken cancellation = default)
    {
        fileSize = fileSize / (1 << 20) * (1 << 20);
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < fileSize + (512L << 20))
            throw new IOException($"На диске {drive.Name} мало места: нужно хотя бы {Formatting.Bytes((ulong)(fileSize + (512L << 20)))} свободных.");

        string folder = TestFolder(root);
        bool createdFolder = !Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"disktest-{Guid.NewGuid():N}.tmp");
        int steps = 1 + Specs.Count * 2;
        try
        {
            // Асинхронно, без кэша Windows; файл удалится при закрытии.
            const FileOptions NoBuffering = (FileOptions)0x20000000;
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose | NoBuffering, fileSize);

            progress?.Report(new DiskBenchmarkProgress($"подготовка файла {Formatting.Bytes((ulong)fileSize)}", 1, steps));
            await FillAsync(handle, fileSize, cancellation);

            var results = new List<DiskTestResult>();
            int step = 1;
            foreach (DiskTestSpec spec in Specs)
            {
                progress?.Report(new DiskBenchmarkProgress($"{spec.Name}, чтение", ++step, steps));
                Measurement read = await BestOfAsync(handle, spec, write: false, fileSize, runs, duration, cancellation);
                progress?.Report(new DiskBenchmarkProgress($"{spec.Name}, запись", ++step, steps));
                Measurement write = await BestOfAsync(handle, spec, write: true, fileSize, runs, duration, cancellation);
                results.Add(new DiskTestResult(spec.Name,
                    read.Bytes / read.Seconds / 1e6, write.Bytes / write.Seconds / 1e6,
                    read.Operations / read.Seconds, write.Operations / write.Seconds,
                    read.AverageLatencyUs, write.AverageLatencyUs));
            }

            AppLog.Info($"Замер диска {root}: " + string.Join("; ", results.Select(r => $"{r.Name} {r.ReadMBps:0}/{r.WriteMBps:0} МБ/с")));
            return results;
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (createdFolder && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось убрать временный файл теста диска {path}.", ex);
            }
        }
    }

    private sealed record Measurement(long Operations, long Bytes, double Seconds, double AverageLatencyUs);

    /// <summary>Несколько прогонов, лучший — как в CrystalDiskMark (случайные помехи только замедляют).</summary>
    private static async Task<Measurement> BestOfAsync(SafeFileHandle handle, DiskTestSpec spec, bool write, long fileSize,
        int runs, TimeSpan duration, CancellationToken cancellation)
    {
        Measurement? best = null;
        for (int run = 0; run < Math.Max(1, runs); run++)
        {
            Measurement m = await MeasureAsync(handle, spec, write, fileSize, duration, cancellation);
            if (best is null || m.Bytes / m.Seconds > best.Bytes / best.Seconds)
                best = m;
            await Task.Delay(500, cancellation); // пауза, чтобы диск закончил фоновые дела
        }

        return best!;
    }

    private static async Task<Measurement> MeasureAsync(SafeFileHandle handle, DiskTestSpec spec, bool write, long fileSize,
        TimeSpan duration, CancellationToken cancellation)
    {
        long blocks = fileSize / spec.BlockSize;
        long nextBlock = 0;
        long operations = 0;
        long latencyTicks = 0;
        var buffers = new AlignedBuffer[spec.QueueDepth];
        try
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = new AlignedBuffer(spec.BlockSize);
                if (write)
                    Random.Shared.NextBytes(buffers[i].Memory.Span); // несжимаемые данные
            }

            var watch = Stopwatch.StartNew();
            long deadline = (long)(duration.TotalSeconds * Stopwatch.Frequency);

            async Task Worker(AlignedBuffer buffer)
            {
                while (watch.ElapsedTicks < deadline)
                {
                    cancellation.ThrowIfCancellationRequested();
                    long block = spec.Random
                        ? Random.Shared.NextInt64(blocks)
                        : (Interlocked.Increment(ref nextBlock) - 1) % blocks;
                    long offset = block * spec.BlockSize;
                    long start = Stopwatch.GetTimestamp();
                    if (write)
                        await RandomAccess.WriteAsync(handle, buffer.Memory, offset, cancellation);
                    else
                        await RandomAccess.ReadAsync(handle, buffer.Memory, offset, cancellation);
                    Interlocked.Add(ref latencyTicks, Stopwatch.GetTimestamp() - start);
                    Interlocked.Increment(ref operations);
                }
            }

            await Task.WhenAll(buffers.Select(b => Task.Run(() => Worker(b), cancellation)));
            watch.Stop();
            double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.001);
            double latencyUs = operations > 0 ? latencyTicks * 1e6 / Stopwatch.Frequency / operations : 0;
            return new Measurement(operations, operations * spec.BlockSize, seconds, latencyUs);
        }
        finally
        {
            foreach (AlignedBuffer? buffer in buffers)
                ((IDisposable?)buffer)?.Dispose();
        }
    }

    /// <summary>Заполняет файл случайными данными: чтение «пустых» мест на SSD было бы нечестно быстрым.</summary>
    private static async Task FillAsync(SafeFileHandle handle, long fileSize, CancellationToken cancellation)
    {
        const int Block = 1 << 20;
        const int Depth = 4;
        var buffers = new AlignedBuffer[Depth];
        try
        {
            for (int i = 0; i < Depth; i++)
            {
                buffers[i] = new AlignedBuffer(Block);
                Random.Shared.NextBytes(buffers[i].Memory.Span);
            }

            long next = 0;
            async Task Worker(AlignedBuffer buffer)
            {
                while (true)
                {
                    long offset = (Interlocked.Increment(ref next) - 1) * Block;
                    if (offset >= fileSize)
                        return;
                    cancellation.ThrowIfCancellationRequested();
                    await RandomAccess.WriteAsync(handle, buffer.Memory, offset, cancellation);
                }
            }

            await Task.WhenAll(buffers.Select(b => Task.Run(() => Worker(b), cancellation)));
        }
        finally
        {
            foreach (AlignedBuffer? buffer in buffers)
                ((IDisposable?)buffer)?.Dispose();
        }
    }

    /// <summary>Буфер, выровненный по 4 КБ: так требует чтение и запись без кэша Windows.</summary>
    private sealed unsafe class AlignedBuffer : MemoryManager<byte>
    {
        private byte* _pointer;
        private readonly int _length;

        public AlignedBuffer(int length)
        {
            _length = length;
            _pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, Alignment);
            NativeMemory.Clear(_pointer, (nuint)length);
        }

        public override Span<byte> GetSpan() => new(_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (_pointer != null)
            {
                NativeMemory.AlignedFree(_pointer);
                _pointer = null;
            }
        }
    }
}

/// <summary>Сохранённый замер диска.</summary>
public sealed class DiskBenchmarkRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset At { get; set; }

    /// <summary>Буква диска: «C:».</summary>
    public string Drive { get; set; } = "";

    /// <summary>Модель и тип: «Samsung SSD 980 · SSD NVMe».</summary>
    public string Model { get; set; } = "";

    public long FileSize { get; set; }

    public List<DiskTestResult> Results { get; set; } = new();

    [JsonIgnore]
    public DiskTestResult? this[string name] => Results.FirstOrDefault(r => r.Name == name);
}

/// <summary>Типичные скорости классов накопителей (по паспортам) для сравнения: SEQ1M Q8T1 чтение и RND4K Q1T1 чтение.</summary>
public static class DiskClasses
{
    public sealed record DiskClass(string Name, double SequentialRead, double RandomRead);

    public static IReadOnlyList<DiskClass> All { get; } = new DiskClass[]
    {
        new("NVMe PCIe 5.0", 14000, 95),
        new("NVMe PCIe 4.0", 7000, 85),
        new("NVMe PCIe 3.0", 3500, 65),
        new("SSD SATA", 560, 45),
        new("Жёсткий диск (HDD)", 220, 1.5),
        new("USB-флешка", 150, 10),
    };

    /// <summary>Ближайший класс по последовательному чтению (в логарифмической шкале).</summary>
    public static DiskClass Closest(double sequentialRead) =>
        All.MinBy(c => Math.Abs(Math.Log(Math.Max(sequentialRead, 1)) - Math.Log(c.SequentialRead)))!;
}
