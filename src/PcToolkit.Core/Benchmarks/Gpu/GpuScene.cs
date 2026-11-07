using System.Diagnostics;

namespace PcToolkit.Core.Benchmarks.Gpu;

/// <summary>
/// 3D-сцена для нагрузки и замера: кадр 3840×2160 (RGBA16F) из нескольких слоёв с текстурой 2048×2048.
/// Кадр детерминирован, поэтому его можно сверять с эталоном, снятым на холодной видеокарте.
/// </summary>
internal sealed class GpuScene : IDisposable
{
    public const uint Width = 3840;
    public const uint Height = 2160;
    private const uint ImageSize = 2048;
    private const uint Iterations = 8;

    private readonly D3D11Compute _gpu;
    private readonly GpuShader _vertex;
    private readonly GpuShader _pixel;
    private readonly GpuShader _compare;
    private readonly GpuRenderState _state;
    private readonly GpuTexture _image;
    private readonly GpuTexture _target;
    private readonly GpuTexture _reference;

    public GpuScene(D3D11Compute gpu)
    {
        _gpu = gpu;
        _vertex = gpu.CompileVertex(GpuKernels.Scene, "scene");
        _pixel = gpu.CompilePixel(GpuKernels.Scene, "scene");
        _compare = gpu.CompileCompute(GpuKernels.CompareFrames, "compare");
        _state = gpu.CreateAdditiveState();
        _image = gpu.CreateImageTexture(ImageSize, Noise(ImageSize));
        _target = gpu.CreateFloatTexture(Width, Height, renderTarget: true);
        _reference = gpu.CreateFloatTexture(Width, Height, renderTarget: false);
    }

    /// <summary>Слоёв в кадре.</summary>
    public uint Layers { get; private set; } = 1;

    /// <summary>Пикселей, нарисованных за кадр (с учётом слоёв).</summary>
    public double PixelsPerFrame => (double)Width * Height * Layers;

    public void Render()
    {
        _gpu.ClearTexture(_target);
        _gpu.DrawLayers(_vertex, _pixel, _state, _target, _image, [Iterations], Layers);
    }

    /// <summary>Подбирает число слоёв так, чтобы кадр рисовался около <paramref name="target"/>.</summary>
    public void Calibrate(TimeSpan target)
    {
        Layers = 1;
        Render();
        _gpu.Finish(precise: true);

        var watch = new Stopwatch();
        while (true)
        {
            watch.Restart();
            Render();
            _gpu.Finish(precise: true);
            watch.Stop();
            if (watch.Elapsed >= TimeSpan.FromMilliseconds(8) || Layers >= 256)
                break;
            Layers *= 2;
        }

        double scale = target.TotalMilliseconds / Math.Max(watch.Elapsed.TotalMilliseconds, 0.01);
        Layers = (uint)Math.Clamp(Math.Round(Layers * scale), 1, 512);
    }

    /// <summary>Рисует кадр и запоминает его как эталон.</summary>
    public void CaptureReference()
    {
        // Первый кадр после создания ресурсов не берём: драйвер может готовить его иначе.
        Render();
        Render();
        _gpu.CopyTexture(_reference, _target);
        _gpu.Finish();
    }

    /// <summary>Сверяет последний нарисованный кадр с эталоном; разные пиксели прибавляются к Counters[3].</summary>
    public void Verify(GpuBuffer counters)
    {
        _gpu.UnbindRenderTarget();
        _gpu.Dispatch(_compare, [], [_target, _reference], [counters], Width / 16, (Height + 15) / 16);
    }

    public void Dispose()
    {
        _reference.Dispose();
        _target.Dispose();
        _image.Dispose();
        _state.Dispose();
        _compare.Dispose();
        _pixel.Dispose();
        _vertex.Dispose();
    }

    private static byte[] Noise(uint size)
    {
        var pixels = new byte[size * size * 4];
        new Random(20261021).NextBytes(pixels);
        return pixels;
    }
}
