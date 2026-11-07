using PcToolkit.Core.Benchmarks.Gpu;

namespace PcToolkit.Core.Tests;

public sealed class GpuReferenceListTests
{
    [Fact]
    public void ReferenceGpuIsTenThousand()
    {
        Assert.Equal(10000, GpuReferenceList.All.Single(g => g.Name == "AMD Radeon RX 6700 XT").Estimate);
    }

    [Theory]
    [InlineData("AMD Radeon RX 6700 XT", "AMD Radeon RX 6700 XT")]
    [InlineData("NVIDIA GeForce RTX 4060", "NVIDIA GeForce RTX 4060")]
    [InlineData("NVIDIA GeForce RTX 4060 Ti", "NVIDIA GeForce RTX 4060 Ti")]
    [InlineData("NVIDIA GeForce RTX 4070 SUPER", "NVIDIA GeForce RTX 4070 Super")]
    [InlineData("NVIDIA GeForce RTX 4070 Ti SUPER", "NVIDIA GeForce RTX 4070 Ti Super")]
    [InlineData("AMD Radeon RX 6600 XT", "AMD Radeon RX 6600 XT")]
    [InlineData("AMD Radeon RX 6600", "AMD Radeon RX 6600")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", "Intel Arc B580")]
    public void FindsModel(string systemName, string expected)
    {
        Assert.Equal(expected, GpuReferenceList.Find(systemName)?.Name);
    }

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics")]
    [InlineData("Intel(R) UHD Graphics 770")]
    [InlineData(null)]
    public void UnknownModelIsNotGuessed(string? systemName)
    {
        Assert.Null(GpuReferenceList.Find(systemName));
    }

    [Fact]
    public void ScoreIsGeometricMeanAgainstReference()
    {
        Assert.Equal(10000, GpuScore.Compute(GpuScore.ReferenceFill, GpuScore.ReferenceGflops, GpuScore.ReferenceBandwidth));
        Assert.Equal(20000, GpuScore.Compute(GpuScore.ReferenceFill * 2, GpuScore.ReferenceGflops * 2, GpuScore.ReferenceBandwidth * 2));
        Assert.Equal(0, GpuScore.Compute(1, 0, 100));
    }
}

/// <summary>Короткая проверка на настоящей видеокарте. Без видеокарты с DirectX 11 тест ничего не делает.</summary>
public sealed class GpuDeviceTests
{
    [Fact]
    public void ComputeIsDeterministicAndMemoryCorruptionIsDetected()
    {
        if (D3D11Compute.BestAdapter() is null)
            return;

        using D3D11Compute gpu = D3D11Compute.Create();
        using GpuShader compute = gpu.CompileCompute(GpuKernels.Compute, "compute");
        using GpuShader memory = gpu.CompileCompute(GpuKernels.Memory, "memory");
        using GpuShader copy = gpu.CompileCompute(GpuKernels.Copy, "copy");
        using GpuBuffer counters = gpu.CreateBuffer(256);
        using GpuBuffer expected = gpu.CreateBuffer(GpuKernels.ComputeThreads * 4L);
        gpu.ClearBuffer(counters);

        // Эталон и две проверки: результат совпадает до бита.
        gpu.Dispatch(compute, [64, 0], [expected, counters], GpuKernels.ComputeGroups);
        gpu.Dispatch(compute, [64, 1], [expected, counters], GpuKernels.ComputeGroups);
        gpu.Dispatch(compute, [64, 1], [expected, counters], GpuKernels.ComputeGroups);
        gpu.Finish();
        Assert.Equal(0u, gpu.Read(counters, 1)[0]);

        // Другое число шагов — другой результат: проверка действительно ловит расхождения.
        gpu.Dispatch(compute, [65, 1], [expected, counters], GpuKernels.ComputeGroups);
        Assert.InRange(gpu.Read(counters, 1)[0], GpuKernels.ComputeThreads * 99 / 100, GpuKernels.ComputeThreads);
        gpu.ClearBuffer(counters);

        // Память: записали проход 1 — проверка прохода 1 чистая, проверка прохода 2 видит все слова как ошибки.
        using GpuBuffer data = gpu.CreateBuffer(GpuKernels.MemoryBytesPerGroup * 64);
        uint groups = (uint)(data.Bytes / GpuKernels.MemoryBytesPerGroup);
        gpu.Dispatch(memory, [1, 0], [data, counters], groups);
        gpu.Dispatch(memory, [1, 1], [data, counters], groups);
        Assert.Equal(0u, gpu.Read(counters, 2)[1]);
        gpu.Dispatch(memory, [2, 1], [data, counters], groups);
        uint words = (uint)(data.Bytes / 4);
        Assert.InRange(gpu.Read(counters, 2)[1], words - words / 1000, words);

        // 3D-сцена: повторные кадры совпадают с эталоном до бита.
        gpu.ClearBuffer(counters);
        using (var scene = new GpuScene(gpu))
        {
            scene.Calibrate(TimeSpan.FromMilliseconds(5)); // несколько слоёв: проверяем и смешивание
            scene.CaptureReference();
            var counts = new List<uint>();
            scene.Verify(counters); // без перерисовки
            counts.Add(gpu.Read(counters, 4)[3]);
            for (int i = 0; i < 4; i++)
            {
                scene.Render();
                gpu.Finish();
                scene.Verify(counters);
                counts.Add(gpu.Read(counters, 4)[3]);
            }

            Assert.True(counts.All(c => c == 0), "Расхождения по шагам: " + string.Join(", ", counts));
        }

        // Копирование работает и не падает.
        using GpuBuffer other = gpu.CreateBuffer(data.Bytes);
        gpu.Dispatch(copy, [], [data, other], groups);
        gpu.Finish();
    }
}
