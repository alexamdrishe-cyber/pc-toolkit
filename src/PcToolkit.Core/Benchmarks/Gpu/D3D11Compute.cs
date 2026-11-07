using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PcToolkit.Core.Benchmarks.Gpu;

/// <summary>Видеокарта, которую видит DirectX.</summary>
public sealed record GpuAdapterInfo(uint Index, string Name, uint VendorId, ulong DedicatedMemory, bool IsSoftware)
{
    public string Vendor => VendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        _ => "",
    };
}

/// <summary>Ошибка DirectX с понятным текстом.</summary>
public class GpuException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Драйвер видеокарты сбросился во время работы (TDR, «устройство удалено»).
/// Под нагрузкой это признак нестабильного разгона, андервольта или перегрева.
/// </summary>
public sealed class GpuDeviceLostException(string message, int code) : GpuException(message, code);

/// <summary>Буфер в видеопамяти с доступом из шейдера (RWByteAddressBuffer).</summary>
public sealed class GpuBuffer : IDisposable
{
    internal IntPtr Resource;
    internal IntPtr View;

    internal GpuBuffer(IntPtr resource, IntPtr view, long bytes)
    {
        Resource = resource;
        View = view;
        Bytes = bytes;
    }

    public long Bytes { get; }

    public void Dispose()
    {
        Com.Release(ref View);
        Com.Release(ref Resource);
    }
}

/// <summary>Текстура в видеопамяти: для чтения шейдером и, если нужно, для рисования в неё.</summary>
public sealed class GpuTexture : IDisposable
{
    internal IntPtr Resource;
    internal IntPtr ShaderView;
    internal IntPtr RenderView;

    internal GpuTexture(IntPtr resource, IntPtr shaderView, IntPtr renderView, uint width, uint height)
    {
        Resource = resource;
        ShaderView = shaderView;
        RenderView = renderView;
        Width = width;
        Height = height;
    }

    public uint Width { get; }

    public uint Height { get; }

    public void Dispose()
    {
        Com.Release(ref RenderView);
        Com.Release(ref ShaderView);
        Com.Release(ref Resource);
    }
}

/// <summary>Состояния конвейера для 3D-нагрузки.</summary>
public sealed class GpuRenderState : IDisposable
{
    internal IntPtr Blend;
    internal IntPtr Sampler;
    internal IntPtr Rasterizer;

    internal GpuRenderState(IntPtr blend, IntPtr sampler, IntPtr rasterizer)
    {
        Blend = blend;
        Sampler = sampler;
        Rasterizer = rasterizer;
    }

    public void Dispose()
    {
        Com.Release(ref Rasterizer);
        Com.Release(ref Sampler);
        Com.Release(ref Blend);
    }
}

public sealed class GpuShader : IDisposable
{
    internal IntPtr Handle;

    internal GpuShader(IntPtr handle) => Handle = handle;

    public void Dispose() => Com.Release(ref Handle);
}

/// <summary>
/// Минимальная обёртка над Direct3D 11 для вычислительных шейдеров: без сторонних библиотек,
/// через таблицы методов COM. Direct3D 11 и компилятор шейдеров (d3dcompiler_47) есть в любой Windows 10/11.
/// Объект не потокобезопасен: все вызовы — из одного потока.
/// </summary>
public sealed unsafe class D3D11Compute : IDisposable
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    private const int DxgiErrorDriverInternal = unchecked((int)0x887A0020);

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _constants;
    private IntPtr _staging;
    private readonly IntPtr[] _queries = new IntPtr[2];

    private D3D11Compute(GpuAdapterInfo adapter, IntPtr device, IntPtr context)
    {
        Adapter = adapter;
        _device = device;
        _context = context;
        _constants = CreateRawResource(16, usage: 0, bind: 0x4 /* CONSTANT_BUFFER */, cpu: 0, misc: 0);
        _staging = CreateRawResource(256, usage: 3 /* STAGING */, bind: 0, cpu: 0x20000 /* READ */, misc: 0);
        for (int i = 0; i < _queries.Length; i++)
            _queries[i] = CreateEventQuery();

        // Точный таймер Windows (1 мс вместо 15,6 мс) на время теста: иначе ожидание видеокарты «просыпается» поздно
        // и она простаивает между порциями работы. С Windows 10 2004 действует только на этот процесс.
        _timerPeriodSet = timeBeginPeriod(1) == 0;
    }

    private bool _timerPeriodSet;

    public GpuAdapterInfo Adapter { get; }

    // ---------- Видеокарты ----------

    /// <summary>Все видеокарты, кроме программной (Microsoft Basic Render Driver).</summary>
    public static IReadOnlyList<GpuAdapterInfo> ListAdapters()
    {
        var result = new List<GpuAdapterInfo>();
        Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        Com.Check(CreateDXGIFactory1(in iid, out IntPtr factory), "Не удалось получить список видеокарт (DXGI)");
        try
        {
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.V(factory)[12])(factory, i, &adapter);
                if (hr == DxgiErrorNotFound)
                    break;
                Com.Check(hr, "Не удалось прочитать видеокарту (DXGI)");
                try
                {
                    AdapterDesc1 desc;
                    Com.Check(((delegate* unmanaged<IntPtr, AdapterDesc1*, int>)Com.V(adapter)[10])(adapter, &desc),
                        "Не удалось прочитать описание видеокарты");
                    string name = new string(desc.Description).TrimEnd('\0').Trim();
                    bool software = (desc.Flags & 2) != 0 || desc.VendorId == 0x1414;
                    result.Add(new GpuAdapterInfo(i, name, desc.VendorId, (ulong)desc.DedicatedVideoMemory, software));
                }
                finally
                {
                    Com.Release(ref adapter);
                }
            }
        }
        finally
        {
            Com.Release(ref factory);
        }

        return result.Where(a => !a.IsSoftware).ToList();
    }

    /// <summary>Самая мощная видеокарта: дискретная с наибольшим объёмом видеопамяти.</summary>
    public static GpuAdapterInfo? BestAdapter() =>
        ListAdapters().OrderByDescending(a => a.DedicatedMemory).FirstOrDefault();

    public static D3D11Compute Create(GpuAdapterInfo? adapter = null)
    {
        adapter ??= BestAdapter() ?? throw new GpuException("Видеокарта с поддержкой DirectX 11 не найдена.");

        Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387");
        Com.Check(CreateDXGIFactory1(in iid, out IntPtr factory), "Не удалось открыть DXGI");
        IntPtr dxgiAdapter = IntPtr.Zero;
        try
        {
            Com.Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.V(factory)[12])(factory, adapter.Index, &dxgiAdapter),
                "Видеокарта не найдена");

            int level = 0xB000; // D3D_FEATURE_LEVEL_11_0
            int hr = D3D11CreateDevice(dxgiAdapter, 0 /* UNKNOWN: задан адаптер */, IntPtr.Zero, 0, &level, 1, 7,
                out IntPtr device, out _, out IntPtr context);
            Com.Check(hr, "Видеокарта не поддерживает DirectX 11 (уровень 11_0) или драйвер не установлен");
            return new D3D11Compute(adapter, device, context);
        }
        finally
        {
            Com.Release(ref dxgiAdapter);
            Com.Release(ref factory);
        }
    }

    // ---------- Шейдеры и буферы ----------

    public GpuShader CompileCompute(string hlsl, string name) => Compile(hlsl, name, "main", "cs_5_0", 18);

    /// <summary>Вершинный шейдер (точка входа VS) для 3D-нагрузки.</summary>
    public GpuShader CompileVertex(string hlsl, string name) => Compile(hlsl, name, "VS", "vs_5_0", 12);

    /// <summary>Пиксельный шейдер (точка входа PS) для 3D-нагрузки.</summary>
    public GpuShader CompilePixel(string hlsl, string name) => Compile(hlsl, name, "PS", "ps_5_0", 15);

    /// <param name="createIndex">Метод ID3D11Device: 18 — CreateComputeShader, 12 — CreateVertexShader, 15 — CreatePixelShader.</param>
    private GpuShader Compile(string hlsl, string name, string entry, string target, int createIndex)
    {
        byte[] source = Encoding.ASCII.GetBytes(hlsl);
        IntPtr code, errors;
        int hr;
        fixed (byte* src = source)
        {
            hr = D3DCompile(src, (nuint)source.Length, name, IntPtr.Zero, IntPtr.Zero, entry, target,
                1u << 15 /* OPTIMIZATION_LEVEL3 */, 0, out code, out errors);
        }

        try
        {
            if (hr < 0)
            {
                string message = errors != IntPtr.Zero ? BlobText(errors) : $"0x{hr:X8}";
                throw new GpuException($"Не удалось скомпилировать шейдер {name}: {message}", hr);
            }

            void* bytes = ((delegate* unmanaged<IntPtr, void*>)Com.V(code)[3])(code);
            nuint size = ((delegate* unmanaged<IntPtr, nuint>)Com.V(code)[4])(code);
            IntPtr shader;
            Com.Check(((delegate* unmanaged<IntPtr, void*, nuint, IntPtr, IntPtr*, int>)Com.V(_device)[createIndex])(
                _device, bytes, size, IntPtr.Zero, &shader), "Не удалось создать шейдер");
            return new GpuShader(shader);
        }
        finally
        {
            Com.Release(ref code);
            Com.Release(ref errors);
        }
    }

    // ---------- Текстуры и 3D ----------

    /// <summary>Текстура RGBA16F для рисования (RT) и чтения шейдером (SRV), или только для чтения (копия-эталон).</summary>
    public GpuTexture CreateFloatTexture(uint width, uint height, bool renderTarget)
    {
        var desc = new Texture2DDesc
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1,
            Format = 10, // R16G16B16A16_FLOAT
            SampleCount = 1,
            BindFlags = 0x8 /* SHADER_RESOURCE */ | (renderTarget ? 0x20u /* RENDER_TARGET */ : 0),
        };
        return CreateTexture(desc, null, 0, renderTarget);
    }

    /// <summary>Текстура RGBA8 с данными (по 4 байта на пиксель), только для чтения.</summary>
    public GpuTexture CreateImageTexture(uint size, byte[] pixels)
    {
        if (pixels.Length != size * size * 4)
            throw new ArgumentException("Размер данных не совпадает с размером текстуры.", nameof(pixels));

        var desc = new Texture2DDesc
        {
            Width = size, Height = size, MipLevels = 1, ArraySize = 1,
            Format = 28, // R8G8B8A8_UNORM
            SampleCount = 1,
            BindFlags = 0x8,
        };
        fixed (byte* data = pixels)
            return CreateTexture(desc, data, size * 4, renderTarget: false);
    }

    private GpuTexture CreateTexture(Texture2DDesc desc, byte* data, uint pitch, bool renderTarget)
    {
        IntPtr texture = IntPtr.Zero, srv = IntPtr.Zero, rtv = IntPtr.Zero;
        try
        {
            var init = new SubresourceData { SysMem = data, SysMemPitch = pitch };
            int hr = ((delegate* unmanaged<IntPtr, Texture2DDesc*, SubresourceData*, IntPtr*, int>)Com.V(_device)[5])(
                _device, &desc, data == null ? null : &init, &texture);
            ThrowIfLost(hr);
            Com.Check(hr, "Не удалось создать текстуру");
            Com.Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Com.V(_device)[7])(_device, texture, null, &srv),
                "Не удалось создать представление текстуры");
            if (renderTarget)
            {
                Com.Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Com.V(_device)[9])(_device, texture, null, &rtv),
                    "Не удалось создать цель рисования");
            }

            var result = new GpuTexture(texture, srv, rtv, desc.Width, desc.Height);
            texture = srv = rtv = IntPtr.Zero;
            return result;
        }
        finally
        {
            Com.Release(ref rtv);
            Com.Release(ref srv);
            Com.Release(ref texture);
        }
    }

    /// <summary>Состояние для 3D: сложение цветов слоёв (как полупрозрачные эффекты в играх) и билинейная выборка с повтором.</summary>
    public GpuRenderState CreateAdditiveState()
    {
        IntPtr blend = IntPtr.Zero, sampler = IntPtr.Zero, raster = IntPtr.Zero;
        try
        {
            var blendDesc = new BlendDesc();
            blendDesc.Target0.BlendEnable = 1;
            blendDesc.Target0.SrcBlend = 2; // ONE
            blendDesc.Target0.DestBlend = 2; // ONE
            blendDesc.Target0.BlendOp = 1; // ADD
            blendDesc.Target0.SrcBlendAlpha = 2;
            blendDesc.Target0.DestBlendAlpha = 2;
            blendDesc.Target0.BlendOpAlpha = 1;
            blendDesc.Target0.WriteMask = 0xF;
            Com.Check(((delegate* unmanaged<IntPtr, BlendDesc*, IntPtr*, int>)Com.V(_device)[20])(_device, &blendDesc, &blend),
                "Не удалось создать режим смешивания");

            var samplerDesc = new SamplerDesc
            {
                Filter = 0x15, // MIN_MAG_MIP_LINEAR
                AddressU = 1, AddressV = 1, AddressW = 1, // WRAP
                MaxAnisotropy = 1,
                ComparisonFunc = 1, // NEVER
                MaxLod = float.MaxValue,
            };
            Com.Check(((delegate* unmanaged<IntPtr, SamplerDesc*, IntPtr*, int>)Com.V(_device)[23])(_device, &samplerDesc, &sampler),
                "Не удалось создать сэмплер");

            var rasterDesc = new RasterizerDesc { FillMode = 3 /* SOLID */, CullMode = 1 /* NONE */, DepthClipEnable = 1 };
            Com.Check(((delegate* unmanaged<IntPtr, RasterizerDesc*, IntPtr*, int>)Com.V(_device)[22])(_device, &rasterDesc, &raster),
                "Не удалось создать состояние растеризации");

            var result = new GpuRenderState(blend, sampler, raster);
            blend = sampler = raster = IntPtr.Zero;
            return result;
        }
        finally
        {
            Com.Release(ref raster);
            Com.Release(ref sampler);
            Com.Release(ref blend);
        }
    }

    public void ClearTexture(GpuTexture target)
    {
        float* color = stackalloc float[4];
        ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Com.V(_context)[50])(_context, target.RenderView, color);
    }

    /// <summary>
    /// Рисует <paramref name="layers"/> полноэкранных слоёв в цель: вершинный шейдер строит прямоугольник на весь экран,
    /// номер слоя — номер экземпляра. Константы (b0) и текстура (t0) — для пиксельного шейдера.
    /// </summary>
    public void DrawLayers(GpuShader vertex, GpuShader pixel, GpuRenderState state, GpuTexture target, GpuTexture source,
        ReadOnlySpan<uint> constants, uint layers)
    {
        UpdateConstants(constants);
        IntPtr cb = _constants;
        IntPtr rtv = target.RenderView;
        IntPtr srv = source.ShaderView;
        IntPtr sampler = state.Sampler;
        var viewport = new Viewport { Width = target.Width, Height = target.Height, MaxDepth = 1 };

        // Цель могла остаться привязанной к вычислительному шейдеру как текстура для чтения — отвязываем.
        IntPtr* none = stackalloc IntPtr[2];
        none[0] = none[1] = IntPtr.Zero;
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Com.V(_context)[67])(_context, 0, 2, none);

        ((delegate* unmanaged<IntPtr, IntPtr, void>)Com.V(_context)[17])(_context, IntPtr.Zero); // IASetInputLayout
        ((delegate* unmanaged<IntPtr, uint, void>)Com.V(_context)[24])(_context, 4); // TRIANGLELIST
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Com.V(_context)[11])(_context, vertex.Handle, null, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Com.V(_context)[9])(_context, pixel.Handle, null, 0);
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Com.V(_context)[16])(_context, 0, 1, &cb);
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Com.V(_context)[8])(_context, 0, 1, &srv);
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Com.V(_context)[10])(_context, 0, 1, &sampler);
        ((delegate* unmanaged<IntPtr, IntPtr, void>)Com.V(_context)[43])(_context, state.Rasterizer);
        ((delegate* unmanaged<IntPtr, uint, Viewport*, void>)Com.V(_context)[44])(_context, 1, &viewport);
        ((delegate* unmanaged<IntPtr, IntPtr, float*, uint, void>)Com.V(_context)[35])(_context, state.Blend, null, 0xFFFFFFFF);
        ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Com.V(_context)[33])(_context, 1, &rtv, IntPtr.Zero);
        ((delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)Com.V(_context)[21])(_context, 6, layers, 0, 0); // DrawInstanced: 2 треугольника на слой
    }

    /// <summary>Отвязывает цель рисования, чтобы её можно было читать вычислительным шейдером.</summary>
    public void UnbindRenderTarget() =>
        ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Com.V(_context)[33])(_context, 0, null, IntPtr.Zero);

    public void CopyTexture(GpuTexture destination, GpuTexture source) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Com.V(_context)[47])(_context, destination.Resource, source.Resource);

    /// <summary>Вычислительный шейдер с текстурами для чтения (t0, t1, …) и буферами (u0, …).</summary>
    public void Dispatch(GpuShader shader, ReadOnlySpan<uint> constants, ReadOnlySpan<GpuTexture> textures,
        ReadOnlySpan<GpuBuffer> buffers, uint groupsX, uint groupsY)
    {
        IntPtr* views = stackalloc IntPtr[textures.Length];
        for (int i = 0; i < textures.Length; i++)
            views[i] = textures[i].ShaderView;
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Com.V(_context)[67])(_context, 0, (uint)textures.Length, views);
        Dispatch(shader, constants, buffers, groupsX, groupsY);
    }

    /// <summary>Буфер в видеопамяти. null — памяти не хватило.</summary>
    public GpuBuffer? TryCreateBuffer(long bytes)
    {
        if (bytes <= 0 || bytes % 16 != 0 || bytes > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bytes));

        IntPtr resource;
        var desc = new BufferDesc
        {
            ByteWidth = (uint)bytes,
            Usage = 0, // DEFAULT
            BindFlags = 0x80, // UNORDERED_ACCESS
            MiscFlags = 0x20, // BUFFER_ALLOW_RAW_VIEWS
        };
        int hr = ((delegate* unmanaged<IntPtr, BufferDesc*, void*, IntPtr*, int>)Com.V(_device)[3])(_device, &desc, null, &resource);
        if (hr == unchecked((int)0x8007000E)) // E_OUTOFMEMORY
            return null;
        ThrowIfLost(hr);
        Com.Check(hr, "Не удалось выделить видеопамять");

        var view = new UavDesc
        {
            Format = 39, // DXGI_FORMAT_R32_TYPELESS
            ViewDimension = 1, // BUFFER
            FirstElement = 0,
            NumElements = (uint)(bytes / 4),
            Flags = 1, // RAW
        };
        IntPtr uav;
        hr = ((delegate* unmanaged<IntPtr, IntPtr, UavDesc*, IntPtr*, int>)Com.V(_device)[8])(_device, resource, &view, &uav);
        if (hr < 0)
        {
            Com.Release(ref resource);
            if (hr == unchecked((int)0x8007000E))
                return null;
            Com.Check(hr, "Не удалось создать представление буфера");
        }

        return new GpuBuffer(resource, uav, bytes);
    }

    public GpuBuffer CreateBuffer(long bytes) =>
        TryCreateBuffer(bytes) ?? throw new GpuException("Не хватило видеопамяти.");

    public void ClearBuffer(GpuBuffer buffer)
    {
        uint* zero = stackalloc uint[4];
        ((delegate* unmanaged<IntPtr, IntPtr, uint*, void>)Com.V(_context)[51])(_context, buffer.View, zero);
    }

    // ---------- Запуск ----------

    /// <summary>Запускает шейдер: 4 константы (b0) и буферы u0, u1, …</summary>
    public void Dispatch(GpuShader shader, ReadOnlySpan<uint> constants, ReadOnlySpan<GpuBuffer> buffers, uint groupsX, uint groupsY = 1)
    {
        UpdateConstants(constants);
        IntPtr cb = _constants;
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Com.V(_context)[71])(_context, 0, 1, &cb);

        IntPtr* views = stackalloc IntPtr[buffers.Length];
        for (int i = 0; i < buffers.Length; i++)
            views[i] = buffers[i].View;
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, void>)Com.V(_context)[68])(
            _context, 0, (uint)buffers.Length, views, null);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Com.V(_context)[69])(_context, shader.Handle, null, 0);
        ((delegate* unmanaged<IntPtr, uint, uint, uint, void>)Com.V(_context)[41])(_context, groupsX, groupsY, 1);
    }

    private void UpdateConstants(ReadOnlySpan<uint> constants)
    {
        uint* data = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
            data[i] = i < constants.Length ? constants[i] : 0;
        ((delegate* unmanaged<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)Com.V(_context)[48])(
            _context, _constants, 0, null, data, 0, 0);
    }

    /// <summary>Отмечает конец пачки работы (0 или 1) — потом её можно дождаться в <see cref="Wait"/>.</summary>
    public void Mark(int slot) =>
        ((delegate* unmanaged<IntPtr, IntPtr, void>)Com.V(_context)[28])(_context, _queries[slot]);

    /// <summary>
    /// Ждёт, пока видеокарта выполнит работу до отметки. Обычно процессор при этом почти не нагружается;
    /// <paramref name="precise"/> — ждать без сна, чтобы точно измерить время (для калибровки).
    /// </summary>
    public void Wait(int slot, bool precise = false)
    {
        IntPtr query = _queries[slot];
        var watch = Stopwatch.StartNew();
        int spins = 0;
        while (true)
        {
            int done;
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, void*, uint, uint, int>)Com.V(_context)[29])(_context, query, &done, 4, 0);
            if (hr == 0)
                return;
            if (hr < 0)
            {
                ThrowIfLost(hr);
                ThrowIfLost(RemovedReason());
                Com.Check(hr, "Ошибка ожидания видеокарты");
            }

            // Видеокарта не отвечает дольше Windows-таймаута (2 с) с запасом — значит, драйвер сбросился.
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                ThrowIfLost(RemovedReason());
                throw new GpuDeviceLostException("Видеокарта перестала отвечать.", DxgiErrorDeviceHung);
            }

            if (precise || ++spins < 20)
                Thread.SpinWait(100);
            else
                Thread.Sleep(1);
        }
    }

    /// <summary>Выполнить всё и дождаться.</summary>
    public void Finish(bool precise = false)
    {
        Mark(0);
        Wait(0, precise);
    }

    /// <summary>Читает первые <paramref name="count"/> чисел буфера (до 64). Ждёт окончания работы видеокарты.</summary>
    public uint[] Read(GpuBuffer buffer, int count)
    {
        if (count is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(count));

        // CopySubresourceRegion целиком не нужен: буферы счётчиков маленькие, копируем первые 256 байт.
        Box box = new() { Left = 0, Right = (uint)Math.Min(256, buffer.Bytes), Top = 0, Bottom = 1, Front = 0, Back = 1 };
        ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Com.V(_context)[46])(
            _context, _staging, 0, 0, 0, 0, buffer.Resource, 0, &box);

        Mapped mapped;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, Mapped*, int>)Com.V(_context)[14])(
            _context, _staging, 0, 1 /* READ */, 0, &mapped);
        ThrowIfLost(hr);
        Com.Check(hr, "Не удалось прочитать результат с видеокарты");
        try
        {
            var result = new uint[count];
            new ReadOnlySpan<uint>(mapped.Data, count).CopyTo(result);
            return result;
        }
        finally
        {
            ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Com.V(_context)[15])(_context, _staging, 0);
        }
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, void>)Com.V(_context)[110])(_context); // ClearState
            ((delegate* unmanaged<IntPtr, void>)Com.V(_context)[111])(_context); // Flush
        }

        for (int i = 0; i < _queries.Length; i++)
            Com.Release(ref _queries[i]);
        Com.Release(ref _staging);
        Com.Release(ref _constants);
        Com.Release(ref _context);
        Com.Release(ref _device);

        if (_timerPeriodSet)
        {
            timeEndPeriod(1);
            _timerPeriodSet = false;
        }
    }

    // ---------- Внутреннее ----------

    private int RemovedReason() => ((delegate* unmanaged<IntPtr, int>)Com.V(_device)[39])(_device);

    private static void ThrowIfLost(int hr)
    {
        if (hr is DxgiErrorDeviceRemoved or DxgiErrorDeviceHung or DxgiErrorDeviceReset or DxgiErrorDriverInternal)
        {
            string reason = hr switch
            {
                DxgiErrorDeviceHung => "видеокарта зависла на командах",
                DxgiErrorDeviceReset => "видеокарта была сброшена",
                DxgiErrorDriverInternal => "внутренняя ошибка драйвера",
                _ => "драйвер видеокарты перезапустился",
            };
            throw new GpuDeviceLostException(reason, hr);
        }
    }

    private IntPtr CreateRawResource(uint bytes, uint usage, uint bind, uint cpu, uint misc)
    {
        var desc = new BufferDesc { ByteWidth = bytes, Usage = usage, BindFlags = bind, CpuAccessFlags = cpu, MiscFlags = misc };
        IntPtr resource;
        Com.Check(((delegate* unmanaged<IntPtr, BufferDesc*, void*, IntPtr*, int>)Com.V(_device)[3])(_device, &desc, null, &resource),
            "Не удалось создать служебный буфер");
        return resource;
    }

    private IntPtr CreateEventQuery()
    {
        uint* desc = stackalloc uint[2]; // D3D11_QUERY_EVENT, MiscFlags 0
        desc[0] = 0;
        desc[1] = 0;
        IntPtr query;
        Com.Check(((delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)Com.V(_device)[24])(_device, desc, &query),
            "Не удалось создать запрос DirectX");
        return query;
    }

    private static string BlobText(IntPtr blob)
    {
        void* bytes = ((delegate* unmanaged<IntPtr, void*>)Com.V(blob)[3])(blob);
        nuint size = ((delegate* unmanaged<IntPtr, nuint>)Com.V(blob)[4])(blob);
        return Encoding.ASCII.GetString((byte*)bytes, (int)size).TrimEnd('\0', '\n', '\r');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc
    {
        public uint ByteWidth;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SubresourceData
    {
        public void* SysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenderTargetBlend
    {
        public int BlendEnable;
        public int SrcBlend, DestBlend, BlendOp, SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha;
        public byte WriteMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendDesc
    {
        public int AlphaToCoverageEnable;
        public int IndependentBlendEnable;
        public RenderTargetBlend Target0, Target1, Target2, Target3, Target4, Target5, Target6, Target7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SamplerDesc
    {
        public int Filter, AddressU, AddressV, AddressW;
        public float MipLodBias;
        public uint MaxAnisotropy;
        public int ComparisonFunc;
        public float Border0, Border1, Border2, Border3;
        public float MinLod, MaxLod;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RasterizerDesc
    {
        public int FillMode, CullMode, FrontCounterClockwise, DepthBias;
        public float DepthBiasClamp, SlopeScaledDepthBias;
        public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Viewport
    {
        public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UavDesc
    {
        public uint Format;
        public uint ViewDimension;
        public uint FirstElement;
        public uint NumElements;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped
    {
        public void* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint period);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        int* featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    private static extern int D3DCompile(byte* source, nuint size, [MarshalAs(UnmanagedType.LPStr)] string? name,
        IntPtr defines, IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entry, [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
}

internal static unsafe class Com
{
    public static void** V(IntPtr p) => *(void***)p;

    public static void Release(ref IntPtr p)
    {
        if (p == IntPtr.Zero)
            return;
        ((delegate* unmanaged<IntPtr, uint>)V(p)[2])(p);
        p = IntPtr.Zero;
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new GpuException($"{what} (код 0x{hr:X8}).", hr);
    }
}
