using System.Diagnostics;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Benchmarks.Gpu;

public enum GpuTestPhase
{
    NotStarted,
    Preparing,
    Running,
    Finished,
}

/// <summary>
/// Стресс-тест видеокарты в духе OCCT: вычислительная нагрузка с проверкой результата и тест видеопамяти.
/// Эталон вычислений считается до нагрева; любое расхождение потом — ошибка. Сброс драйвера (TDR) тоже ловится.
/// Вся работа с DirectX — в одном отдельном потоке.
/// </summary>
public sealed class GpuStressTest : IDisposable
{
    private static readonly TimeSpan SliceTarget = TimeSpan.FromMilliseconds(25);
    private const long ChunkBytes = 256L * 1024 * 1024;

    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _watch = new();
    private Thread? _thread;
    private long _computeErrors;
    private long _memoryErrors;
    private long _renderErrors;
    private long _memoryBytes;
    private volatile GpuTestPhase _phase;
    private volatile string? _failure;
    private volatile bool _deviceLost;
    private volatile string _adapterName = "";

    public GpuStressTest(bool render, bool compute, bool memory, double memoryFraction = 0.6)
    {
        if (!render && !compute && !memory)
            throw new ArgumentException("Нужно выбрать хотя бы один вид нагрузки.");

        Render = render;
        Compute = compute;
        Memory = memory;
        MemoryFraction = Math.Clamp(memoryFraction, 0.05, 0.9);
    }

    /// <summary>3D-нагрузка как в играх (самое большое потребление).</summary>
    public bool Render { get; }

    public bool Compute { get; }

    public bool Memory { get; }

    /// <summary>Какую долю видеопамяти занять тестом памяти.</summary>
    public double MemoryFraction { get; }

    public GpuTestPhase Phase => _phase;

    public bool IsRunning => _phase is GpuTestPhase.Preparing or GpuTestPhase.Running;

    public TimeSpan Elapsed => _watch.Elapsed;

    public long ComputeErrors => Interlocked.Read(ref _computeErrors);

    public long MemoryErrors => Interlocked.Read(ref _memoryErrors);

    /// <summary>Сколько раз кадр 3D-сцены отличался от эталона (в пикселях).</summary>
    public long RenderErrors => Interlocked.Read(ref _renderErrors);

    public long ErrorCount => ComputeErrors + MemoryErrors + RenderErrors;

    /// <summary>Сколько видеопамяти проверяется.</summary>
    public long MemoryTestedBytes => Interlocked.Read(ref _memoryBytes);

    public string AdapterName => _adapterName;

    /// <summary>Почему тест прервался сам (сброс драйвера, ошибка DirectX). null — не прерывался.</summary>
    public string? Failure => _failure;

    /// <summary>Драйвер видеокарты сбросился (TDR) — главный признак нестабильности.</summary>
    public bool DeviceLost => _deviceLost;

    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("Тест уже запущен.");

        _phase = GpuTestPhase.Preparing;
        _thread = new Thread(Run) { IsBackground = true, Name = "PcToolkit.GpuStress" };
        _thread.Start();
    }

    public void Stop(string reason = "остановлен")
    {
        if (_cts.IsCancellationRequested)
            return;

        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(15));
        _watch.Stop();
        AppLog.Info($"Стресс-тест видеокарты {reason}: {Elapsed:hh\\:mm\\:ss}, ошибок 3D {RenderErrors}, вычислений {ComputeErrors}, памяти {MemoryErrors}" +
                    (Failure is null ? "." : $", сбой: {Failure}."));
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    private void Run()
    {
        var buffers = new List<GpuBuffer>();
        GpuScene? scene = null;
        CancellationToken token = _cts.Token;
        try
        {
            using D3D11Compute gpu = D3D11Compute.Create();
            _adapterName = gpu.Adapter.Name;
            using GpuShader computeShader = gpu.CompileCompute(GpuKernels.Compute, "compute");
            using GpuShader memoryShader = gpu.CompileCompute(GpuKernels.Memory, "memory");
            using GpuBuffer counters = gpu.CreateBuffer(256);
            using GpuBuffer expected = gpu.CreateBuffer(GpuKernels.ComputeThreads * 4L);
            gpu.ClearBuffer(counters);

            if (Render)
            {
                scene = new GpuScene(gpu);
                scene.Calibrate(SliceTarget);
                scene.CaptureReference(); // эталон кадра — на холодной видеокарте
            }

            uint iterations = 0;
            if (Compute)
            {
                iterations = CalibrateCompute(gpu, computeShader, expected, counters, SliceTarget);
                // Эталон — на холодной видеокарте.
                gpu.Dispatch(computeShader, [iterations, 0], [expected, counters], GpuKernels.ComputeGroups);
                gpu.Finish();
            }

            var chunks = new List<(GpuBuffer Buffer, uint Pass)>();
            if (Memory)
            {
                long target = (long)(gpu.Adapter.DedicatedMemory * MemoryFraction);
                long chunk = Math.Min(ChunkBytes, RoundDown(Math.Max((long)gpu.Adapter.DedicatedMemory / 8, GpuKernels.MemoryBytesPerGroup)));
                long allocated = 0;
                while (allocated + chunk <= target && !token.IsCancellationRequested)
                {
                    GpuBuffer? buffer = gpu.TryCreateBuffer(chunk);
                    if (buffer is null)
                        break;
                    buffers.Add(buffer);
                    allocated += chunk;
                    uint pass = (uint)chunks.Count + 1;
                    WriteMemory(gpu, memoryShader, buffer, counters, pass);
                    chunks.Add((buffer, pass));
                    gpu.Finish();
                }

                Interlocked.Exchange(ref _memoryBytes, allocated);
                if (chunks.Count == 0)
                    throw new GpuException("Не удалось выделить видеопамять для теста.");
            }

            AppLog.Info($"Стресс-тест видеокарты запущен: {gpu.Adapter.Name}; 3D {(Render ? $"да ({scene!.Layers} слоёв 4K)" : "нет")}, вычисления {(Compute ? $"да ({iterations} шагов)" : "нет")}, " +
                        $"видеопамять {(Memory ? Formatting.Bytes((ulong)MemoryTestedBytes) : "нет")}.");

            _watch.Restart();
            _phase = GpuTestPhase.Running;
            var pending = new bool[2];
            var lastRead = Stopwatch.StartNew();
            uint nextPass = (uint)chunks.Count + 1;
            int slot = 0;
            long cycle = 0;
            while (!token.IsCancellationRequested)
            {
                if (scene is not null)
                {
                    scene.Render();
                    scene.Verify(counters);
                }

                if (Compute)
                    gpu.Dispatch(computeShader, [iterations, 1], [expected, counters], GpuKernels.ComputeGroups);

                if (Memory)
                {
                    // Сначала проверяем то, что записали раньше (память хранила данные всё это время), потом пишем новое.
                    // Без вычислительной нагрузки за один круг проходим всю память, чтобы видеокарта не простаивала.
                    int count = Compute || Render ? 1 : chunks.Count;
                    for (int k = 0; k < count; k++)
                    {
                        int index = (int)((cycle * count + k) % chunks.Count);
                        (GpuBuffer buffer, uint pass) = chunks[index];
                        gpu.Dispatch(memoryShader, [pass, 1], [buffer, counters], Groups(buffer));
                        WriteMemory(gpu, memoryShader, buffer, counters, nextPass);
                        chunks[index] = (buffer, nextPass++);
                    }
                }

                gpu.Mark(slot);
                if (pending[1 - slot])
                    gpu.Wait(1 - slot);
                pending[slot] = true;
                slot = 1 - slot;
                cycle++;

                if (lastRead.Elapsed >= TimeSpan.FromSeconds(1))
                {
                    ReadCounters(gpu, counters);
                    lastRead.Restart();
                }
            }

            gpu.Finish();
            ReadCounters(gpu, counters);
        }
        catch (GpuDeviceLostException ex)
        {
            _deviceLost = true;
            _failure = $"{ex.Message} (сброс драйвера видеокарты)";
            AppLog.Warn($"Стресс-тест видеокарты: устройство потеряно через {Elapsed:hh\\:mm\\:ss}: {ex.Message} (0x{ex.Code:X8}).");
        }
        catch (Exception ex)
        {
            _failure = ex.Message;
            AppLog.Error("Стресс-тест видеокарты прерван ошибкой.", ex);
        }
        finally
        {
            scene?.Dispose();
            foreach (GpuBuffer buffer in buffers)
                buffer.Dispose();
            _watch.Stop();
            _phase = GpuTestPhase.Finished;
        }
    }

    private void ReadCounters(D3D11Compute gpu, GpuBuffer counters)
    {
        uint[] values = gpu.Read(counters, 4);
        Interlocked.Exchange(ref _computeErrors, values[0]);
        Interlocked.Exchange(ref _memoryErrors, values[1]);
        Interlocked.Exchange(ref _renderErrors, values[3]);
    }

    private static void WriteMemory(D3D11Compute gpu, GpuShader shader, GpuBuffer buffer, GpuBuffer counters, uint pass) =>
        gpu.Dispatch(shader, [pass, 0], [buffer, counters], Groups(buffer));

    internal static uint Groups(GpuBuffer buffer) => (uint)(buffer.Bytes / GpuKernels.MemoryBytesPerGroup);

    internal static long RoundDown(long bytes) => bytes / GpuKernels.MemoryBytesPerGroup * GpuKernels.MemoryBytesPerGroup;

    /// <summary>Подбирает число шагов цикла так, чтобы один запуск длился около <paramref name="target"/>.</summary>
    internal static uint CalibrateCompute(D3D11Compute gpu, GpuShader shader, GpuBuffer expected, GpuBuffer counters, TimeSpan target)
    {
        uint iterations = 16;
        // Первый запуск — прогрев: драйвер докомпилирует шейдер, видеокарта поднимет частоты.
        gpu.Dispatch(shader, [iterations, 2], [expected, counters], GpuKernels.ComputeGroups);
        gpu.Finish(precise: true);

        var watch = new Stopwatch();
        while (true)
        {
            watch.Restart();
            gpu.Dispatch(shader, [iterations, 2], [expected, counters], GpuKernels.ComputeGroups);
            gpu.Finish(precise: true);
            watch.Stop();
            if (watch.Elapsed >= TimeSpan.FromMilliseconds(8) || iterations >= 1u << 22)
                break;
            iterations *= 2;
        }

        // Ещё раз на подобранном размере: частота видеокарты к этому моменту уже поднялась.
        double scale = target.TotalMilliseconds / Math.Max(watch.Elapsed.TotalMilliseconds, 0.01);
        iterations = (uint)Math.Clamp(iterations * scale, 16, 1u << 24);
        watch.Restart();
        gpu.Dispatch(shader, [iterations, 2], [expected, counters], GpuKernels.ComputeGroups);
        gpu.Finish(precise: true);
        watch.Stop();
        scale = target.TotalMilliseconds / Math.Max(watch.Elapsed.TotalMilliseconds, 0.01);
        return (uint)Math.Clamp(iterations * scale, 16, 1u << 24);
    }
}
