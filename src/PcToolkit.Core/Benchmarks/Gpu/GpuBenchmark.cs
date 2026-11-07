using System.Diagnostics;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Benchmarks.Gpu;

public sealed record GpuBenchmarkProgress(string Step, int Index, int Count);

/// <summary>Итог замера: 3D-сцена (гигапикселей/с), вычисления (GFLOPS) и скорость видеопамяти (ГБ/с).</summary>
public sealed record GpuBenchmarkResult(string Adapter, double Fill, double Gflops, double Bandwidth);

/// <summary>Замер производительности видеокарты: три шага по ~8 секунд с прогревом.</summary>
public static class GpuBenchmark
{
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(6);

    public static GpuBenchmarkResult Run(IProgress<GpuBenchmarkProgress>? progress = null, CancellationToken cancellation = default)
    {
        using D3D11Compute gpu = D3D11Compute.Create();
        using GpuShader compute = gpu.CompileCompute(GpuKernels.Compute, "compute");
        using GpuShader copy = gpu.CompileCompute(GpuKernels.Copy, "copy");
        using GpuBuffer counters = gpu.CreateBuffer(256);
        using GpuBuffer expected = gpu.CreateBuffer(GpuKernels.ComputeThreads * 4L);

        // 1. 3D-сцена — ближе всего к играм.
        progress?.Report(new GpuBenchmarkProgress("3D-сцена 4K", 1, 3));
        double fill;
        using (var scene = new GpuScene(gpu))
        {
            scene.Calibrate(TimeSpan.FromMilliseconds(20));
            Measure(gpu, scene.Render, Warmup, cancellation);
            (long frames, double frameSeconds) = Measure(gpu, scene.Render, Duration, cancellation);
            fill = frames * scene.PixelsPerFrame / frameSeconds / 1e9;
        }

        // 2. Вычисления.
        progress?.Report(new GpuBenchmarkProgress("вычисления (FP32)", 2, 3));
        uint iterations = GpuStressTest.CalibrateCompute(gpu, compute, expected, counters, TimeSpan.FromMilliseconds(20));
        void ComputeBatch() => gpu.Dispatch(compute, [iterations, 2], [expected, counters], GpuKernels.ComputeGroups);
        Measure(gpu, ComputeBatch, Warmup, cancellation);
        (long batches, double seconds) = Measure(gpu, ComputeBatch, Duration, cancellation);
        double gflops = batches * (double)GpuKernels.ComputeThreads * iterations * GpuKernels.FlopsPerIteration / seconds / 1e9;

        // 3. Видеопамять: копирование между двумя буферами, туда и обратно.
        progress?.Report(new GpuBenchmarkProgress("скорость видеопамяти", 3, 3));
        long size = Math.Min(256L * 1024 * 1024,
            GpuStressTest.RoundDown(Math.Max((long)gpu.Adapter.DedicatedMemory / 8, GpuKernels.MemoryBytesPerGroup * 64)));
        using GpuBuffer a = gpu.CreateBuffer(size);
        using GpuBuffer b = gpu.CreateBuffer(size);
        uint groups = GpuStressTest.Groups(a);

        // Сколько копирований в одной пачке, чтобы она длилась ~20 мс: видеокарта не должна ждать процессор.
        gpu.Dispatch(copy, [], [a, b], groups);
        gpu.Finish(precise: true);
        var probe = Stopwatch.StartNew();
        for (int i = 0; i < 4; i++)
            gpu.Dispatch(copy, [], [a, b], groups);
        gpu.Finish(precise: true);
        double oneCopyMs = probe.Elapsed.TotalMilliseconds / 4;
        int copiesPerBatch = (int)Math.Clamp(Math.Round(20 / Math.Max(oneCopyMs, 0.01) / 2) * 2, 2, 512);
        void CopyBatch()
        {
            for (int i = 0; i < copiesPerBatch / 2; i++)
            {
                gpu.Dispatch(copy, [], [a, b], groups);
                gpu.Dispatch(copy, [], [b, a], groups);
            }
        }

        Measure(gpu, CopyBatch, Warmup, cancellation);
        (batches, seconds) = Measure(gpu, CopyBatch, Duration, cancellation);
        double bandwidth = batches * copiesPerBatch * 2.0 * size / seconds / 1e9;

        AppLog.Info($"Замер видеокарты {gpu.Adapter.Name}: 3D {fill:0.00} Гпикс/с, {gflops:0} GFLOPS, {bandwidth:0} ГБ/с.");
        return new GpuBenchmarkResult(gpu.Adapter.Name, fill, gflops, bandwidth);
    }

    /// <summary>Гоняет пачки работы, пока не выйдет время; видеокарта всё время занята (одна пачка всегда в очереди).</summary>
    private static (long Batches, double Seconds) Measure(D3D11Compute gpu, Action batch, TimeSpan duration, CancellationToken cancellation)
    {
        var pending = new bool[2];
        int slot = 0;
        long count = 0;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            cancellation.ThrowIfCancellationRequested();
            batch();
            gpu.Mark(slot);
            if (pending[1 - slot])
                gpu.Wait(1 - slot);
            pending[slot] = true;
            slot = 1 - slot;
            count++;
        }

        for (int i = 0; i < 2; i++)
        {
            if (pending[i])
                gpu.Wait(i);
        }

        watch.Stop();
        return (count, watch.Elapsed.TotalSeconds);
    }
}

/// <summary>
/// Итоговые баллы видеокарты: среднее геометрическое 3D-сцены, вычислений и скорости памяти относительно эталона × 10 000.
/// Эталон — Radeon RX 6700 XT (10 000 баллов).
/// </summary>
public static class GpuScore
{
    /// <summary>
    /// 2 — исправлена калибровка (в версии 1 видеокарта простаивала между порциями работы).
    /// 3 — добавлена 3D-сцена.
    /// </summary>
    public const int Version = 3;

    public const string ReferenceName = "AMD Radeon RX 6700 XT";

    // Замер эталона этим тестом: RX 6700 XT, драйвер по умолчанию, без разгона (21.10.2026).
    public const double ReferenceGflops = 12077;

    public const double ReferenceBandwidth = 320;

    public const double ReferenceFill = 23.2;

    public static int Compute(double fill, double gflops, double bandwidth)
    {
        if (!Valid(fill) || !Valid(gflops) || !Valid(bandwidth))
            return 0;
        return (int)Math.Round(10000 * Math.Cbrt(fill / ReferenceFill * (gflops / ReferenceGflops) * (bandwidth / ReferenceBandwidth)));
    }

    private static bool Valid(double value) => value > 0 && double.IsFinite(value);
}

public sealed class GpuBenchmarkRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset At { get; set; }

    public string Gpu { get; set; } = "";

    /// <summary>3D-сцена, гигапикселей в секунду (с учётом всех слоёв).</summary>
    public double Fill { get; set; }

    public double Gflops { get; set; }

    /// <summary>ГБ/с (10⁹ байт).</summary>
    public double Bandwidth { get; set; }

    public int ScoreVersion { get; set; } = GpuScore.Version;

    [System.Text.Json.Serialization.JsonIgnore]
    public int? Score => ScoreVersion == GpuScore.Version && GpuScore.Compute(Fill, Gflops, Bandwidth) is > 0 and var s ? s : null;
}
