namespace PcToolkit.Core.Benchmarks.Gpu;

/// <summary>
/// Шейдеры теста видеокарты (HLSL, Shader Model 5.0). Компилируются при запуске теста компилятором Windows.
/// </summary>
internal static class GpuKernels
{
    /// <summary>Потоков в одном запуске вычислительной нагрузки: хватает, чтобы занять любую видеокарту.</summary>
    public const uint ComputeGroups = 4096;

    public const uint ComputeThreads = ComputeGroups * 256;

    /// <summary>Операций с плавающей точкой на один шаг цикла в одном потоке: 8 векторов × 4 числа × 2 FMA × 2 операции.</summary>
    public const double FlopsPerIteration = 8 * 4 * 2 * 2;

    /// <summary>Сколько байт обрабатывает одна группа шейдеров памяти: 256 потоков × 4 × uint4.</summary>
    public const long MemoryBytesPerGroup = 256 * 4 * 16;

    /// <summary>
    /// Вычислительная нагрузка: длинные цепочки FMA в восьми независимых векторах (как «3D»-нагрузка OCCT, но без картинки).
    /// Mode 0 — записать эталон, 1 — сравнить с эталоном и считать ошибки (Counters[0]), 2 — только считать (замер).
    /// Результат на одной и той же видеокарте и драйвере всегда одинаковый до бита, поэтому любое расхождение — ошибка.
    /// </summary>
    public const string Compute = """
        cbuffer Params : register(b0) { uint Iterations; uint Mode; uint Unused0; uint Unused1; };
        RWByteAddressBuffer Expected : register(u0);
        RWByteAddressBuffer Counters : register(u1);

        [numthreads(256, 1, 1)]
        void main(uint3 tid : SV_DispatchThreadID)
        {
            uint id = tid.x;
            float s = 0.5f + (float)(id & 1023) * 0.000123f;
            float4 a0 = s + float4(0.00f, 0.01f, 0.02f, 0.03f);
            float4 a1 = a0 + 0.04f;
            float4 a2 = a1 + 0.04f;
            float4 a3 = a2 + 0.04f;
            float4 a4 = a3 + 0.04f;
            float4 a5 = a4 + 0.04f;
            float4 a6 = a5 + 0.04f;
            float4 a7 = a6 + 0.04f;
            const float4 m = float4(0.999991f, 0.999992f, 0.999993f, 0.999994f);
            const float4 n = float4(0.999981f, 0.999982f, 0.999983f, 0.999984f);
            const float4 c = float4(1e-5f, 2e-5f, 3e-5f, 4e-5f);

            [loop]
            for (uint i = 0; i < Iterations; i++)
            {
                a0 = mad(a0, m, c); a1 = mad(a1, m, c); a2 = mad(a2, m, c); a3 = mad(a3, m, c);
                a4 = mad(a4, m, c); a5 = mad(a5, m, c); a6 = mad(a6, m, c); a7 = mad(a7, m, c);
                a0 = mad(a0, n, c); a1 = mad(a1, n, c); a2 = mad(a2, n, c); a3 = mad(a3, n, c);
                a4 = mad(a4, n, c); a5 = mad(a5, n, c); a6 = mad(a6, n, c); a7 = mad(a7, n, c);
            }

            float4 t = ((a0 + a1) + (a2 + a3)) + ((a4 + a5) + (a6 + a7));
            uint r = asuint(t.x) ^ (asuint(t.y) * 3u) ^ (asuint(t.z) * 5u) ^ (asuint(t.w) * 7u);
            if (Mode == 0)
            {
                Expected.Store(id * 4, r);
            }
            else if (Mode == 1)
            {
                if (Expected.Load(id * 4) != r)
                    Counters.InterlockedAdd(0, 1);
            }
            else if (r == 0x7FC00001u)
            {
                // Почти невозможное значение: нужно лишь, чтобы компилятор не выбросил вычисления.
                Counters.InterlockedAdd(8, 1);
            }
        }
        """;

    /// <summary>
    /// Тест видеопамяти: Mode 0 — записать шаблон прохода PassId, 1 — прочитать и сверить (ошибки в Counters[1]).
    /// Шаблон — хэш адреса и номера прохода: каждое слово каждый проход меняется непредсказуемо.
    /// </summary>
    public const string Memory = """
        cbuffer Params : register(b0) { uint PassId; uint Mode; uint Unused0; uint Unused1; };
        RWByteAddressBuffer Data : register(u0);
        RWByteAddressBuffer Counters : register(u1);

        uint Pattern(uint i, uint p)
        {
            uint x = (i * 0x9E3779B1u) ^ (p * 0x85EBCA77u);
            x ^= x >> 16; x *= 0x7FEB352Du;
            x ^= x >> 15; x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }

        [numthreads(256, 1, 1)]
        void main(uint3 gid : SV_GroupID, uint gi : SV_GroupIndex)
        {
            uint base = gid.x * 4096;
            uint bad = 0;
            [unroll]
            for (uint k = 0; k < 4; k++)
            {
                uint w = base + k * 1024 + gi * 4;
                uint4 v = uint4(Pattern(w, PassId), Pattern(w + 1, PassId), Pattern(w + 2, PassId), Pattern(w + 3, PassId));
                if (Mode == 0)
                {
                    Data.Store4(w * 4, v);
                }
                else
                {
                    uint4 d = (uint4)(Data.Load4(w * 4) != v);
                    bad += d.x + d.y + d.z + d.w;
                }
            }

            if (bad > 0)
                Counters.InterlockedAdd(4, bad);
        }
        """;

    /// <summary>Замер пропускной способности видеопамяти: чтение из одного буфера и запись в другой.</summary>
    public const string Copy = """
        RWByteAddressBuffer Src : register(u0);
        RWByteAddressBuffer Dst : register(u1);

        [numthreads(256, 1, 1)]
        void main(uint3 gid : SV_GroupID, uint gi : SV_GroupIndex)
        {
            uint base = gid.x * 4096;
            [unroll]
            for (uint k = 0; k < 4; k++)
            {
                uint w = base + k * 1024 + gi * 4;
                Dst.Store4(w * 4, Src.Load4(w * 4) + 1u);
            }
        }
        """;

    /// <summary>
    /// 3D-нагрузка как в играх и FurMark: полноэкранные слои в 4K с текстурами, вычислениями в пикселях
    /// и сложением цветов (блоки текстур, вывода пикселей, видеопамять и вычисления работают одновременно).
    /// Порядок смешивания в DirectX фиксирован, поэтому кадр всегда одинаковый до бита — его можно проверять.
    /// </summary>
    public const string Scene = """
        cbuffer Params : register(b0) { uint Iterations; uint Unused0; uint Unused1; uint Unused2; };
        Texture2D<float4> Tex : register(t0);
        SamplerState Smp : register(s0);

        struct V
        {
            float4 pos : SV_Position;
            nointerpolation uint layer : LAYER;
        };

        V VS(uint id : SV_VertexID, uint layer : SV_InstanceID)
        {
            // Прямоугольник ровно по экрану из двух треугольников: 0-1-2 и 2-1-3.
            static const float2 corners[6] = { float2(-1, 1), float2(1, 1), float2(-1, -1), float2(-1, -1), float2(1, 1), float2(1, -1) };
            V o;
            o.pos = float4(corners[id], 0, 1);
            o.layer = layer;
            return o;
        }

        float4 PS(V i) : SV_Target
        {
            float2 uv = i.pos.xy * (1.0f / 1536.0f) + float2(i.layer * 0.1373f, i.layer * 0.0731f);
            float4 acc = 0;
            [unroll]
            for (int k = 0; k < 8; k++)
                acc += Tex.SampleLevel(Smp, uv + float2(k * 0.0313f, k * 0.0171f), 0);

            float4 v = acc * 0.125f;
            [loop]
            for (uint n = 0; n < Iterations; n++)
            {
                v = mad(v, 0.99931f, 0.00041f);
                v = mad(v, 0.99917f, 0.00033f);
            }

            return v * 0.004f;
        }
        """;

    /// <summary>Сверка кадра с эталоном: число разных пикселей — в Counters[3].</summary>
    public const string CompareFrames = """
        Texture2D<float4> A : register(t0);
        Texture2D<float4> B : register(t1);
        RWByteAddressBuffer Counters : register(u0);

        [numthreads(16, 16, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            if (any(asuint(A[id.xy]) != asuint(B[id.xy])))
                Counters.InterlockedAdd(12, 1);
        }
        """;
}
