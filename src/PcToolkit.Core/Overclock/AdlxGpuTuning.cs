using System.Globalization;
using System.Runtime.InteropServices;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Overclock;

/// <summary>Допустимый диапазон параметра: минимум, максимум и шаг.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct TuningRange(int Min, int Max, int Step)
{
    public bool Contains(int value) => value >= Min && value <= Max && (Step <= 1 || (value - Min) % Step == 0);

    /// <summary>Ближайшее допустимое значение.</summary>
    public int Snap(int value)
    {
        value = Math.Clamp(value, Min, Max);
        if (Step > 1)
            value = Min + (int)Math.Round((value - Min) / (double)Step) * Step;
        return Math.Clamp(value, Min, Max);
    }
}

/// <summary>Точка кривой вентилятора: температура (°C) → скорость (%).</summary>
public readonly record struct FanPoint(int Temperature, int Speed);

/// <summary>Ошибка ADLX с кодом результата и понятным текстом.</summary>
public sealed class AdlxException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Настройки видеокарты AMD через ADLX — официальную библиотеку AMD, которая ставится вместе с драйвером
/// (amdadlx64.dll). Тот же механизм использует AMD Software: Adrenalin Edition. Права администратора не нужны.
/// Вызовы — по таблицам виртуальных функций из заголовков ADLX SDK (C-интерфейс), без сторонних обёрток.
/// Все обращения идут под одной блокировкой.
/// </summary>
public sealed unsafe class AdlxGpuTuning : IDisposable
{
    // Параметры, которыми управляет приложение (ключи для журнала изменений).
    public const string GfxMinFrequency = "gfx.minFrequency";
    public const string GfxMaxFrequency = "gfx.maxFrequency";
    public const string GfxVoltage = "gfx.voltage";
    public const string VramMaxFrequency = "vram.maxFrequency";
    public const string VramTiming = "vram.timing";
    public const string PowerLimit = "power.limit";
    public const string PowerTdc = "power.tdc";
    public const string FanCurve = "fan.curve";
    public const string FanZeroRpm = "fan.zeroRpm";
    public const string FanMinAcoustic = "fan.minAcoustic";
    public const string FanTargetSpeed = "fan.targetSpeed";
    public const string FanMinSpeed = "fan.minSpeed";
    public const string Preset = "preset";

    public static readonly string[] Presets = { "PowerSaver", "Quiet", "Balanced", "Turbo", "Rage" };

    private const int AdlxOk = 0;
    private const int AdlxAlreadyEnabled = 1;
    private const int AdlxAlreadyInitialized = 2;

    private static readonly object SharedLock = new();
    private static AdlxGpuTuning? _shared;
    private static string? _sharedError;
    private static DateTime _sharedFailedAt;

    private readonly object _lock = new();
    private IntPtr _library;
    private IntPtr _system;
    private IntPtr _services;
    private IntPtr _gpu;
    private delegate* unmanaged[Cdecl]<int> _terminate;
    private bool _disposed;
    private bool _loggedSupport;
    private readonly HashSet<string> _loggedMissing = new();

    private AdlxGpuTuning()
    {
    }

    public string GpuName { get; private set; } = "";

    public int VramMb { get; private set; }

    public string AdlxVersion { get; private set; } = "";

    /// <summary>
    /// Общий экземпляр на всё приложение. null — ADLX недоступен (не AMD, нет драйвера), причина в <paramref name="error"/>.
    /// После неудачи следующая попытка — не раньше чем через 30 секунд.
    /// </summary>
    public static AdlxGpuTuning? Shared(out string? error)
    {
        lock (SharedLock)
        {
            if (_shared is not null)
            {
                error = null;
                return _shared;
            }

            if (_sharedError is not null && DateTime.UtcNow - _sharedFailedAt < TimeSpan.FromSeconds(30))
            {
                error = _sharedError;
                return null;
            }

            try
            {
                _shared = Open();
                _sharedError = null;
                error = null;
                return _shared;
            }
            catch (Exception ex)
            {
                _sharedError = ex is AdlxException or DllNotFoundException or EntryPointNotFoundException
                    ? ex.Message
                    : "ошибка ADLX: " + ex.Message;
                _sharedFailedAt = DateTime.UtcNow;
                AppLog.Warn("Настройки видеокарты AMD недоступны: " + _sharedError);
                error = _sharedError;
                return null;
            }
        }
    }

    /// <summary>Закрывает общий экземпляр (при выходе из приложения).</summary>
    public static void ShutdownShared()
    {
        lock (SharedLock)
        {
            _shared?.Dispose();
            _shared = null;
        }
    }

    private static AdlxGpuTuning Open()
    {
        if (!Environment.Is64BitProcess)
            throw new AdlxException("нужен 64-битный процесс", -1);
        if (!NativeLibrary.TryLoad("amdadlx64.dll", typeof(AdlxGpuTuning).Assembly, DllImportSearchPath.System32, out IntPtr library))
            throw new AdlxException("библиотека AMD ADLX не найдена — видеокарта не AMD или драйвер AMD не установлен", -1);

        var tuning = new AdlxGpuTuning { _library = library };
        try
        {
            tuning.Initialize();
            return tuning;
        }
        catch
        {
            tuning.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        var queryVersion = (delegate* unmanaged[Cdecl]<ulong*, int>)Export("ADLXQueryFullVersion");
        _terminate = (delegate* unmanaged[Cdecl]<int>)Export("ADLXTerminate");

        ulong version;
        Check(queryVersion(&version), "версия ADLX");
        AdlxVersion = $"{version >> 48}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}.{version & 0xFFFF}";

        // Передаём версию самой библиотеки: нужные интерфейсы есть во всех версиях ADLX,
        // а более новые запрашиваются через QueryInterface и просто не используются, если их нет.
        IntPtr system;
        if (NativeLibrary.TryGetExport(_library, "ADLXInitialize", out IntPtr init))
        {
            Check(((delegate* unmanaged[Cdecl]<ulong, IntPtr*, int>)init)(version, &system), "запуск ADLX");
        }
        else
        {
            IntPtr mapping;
            Check(((delegate* unmanaged[Cdecl]<ulong, IntPtr*, IntPtr*, int>)Export("ADLXInitialize2"))(version, &system, &mapping), "запуск ADLX");
        }

        _system = system;

        IntPtr services;
        Check(CallPtr(_system, 8, &services), "сервис настроек видеокарты"); // IADLXSystem::GetGPUTuningServices
        _services = services;

        IntPtr list;
        Check(CallPtr(_system, 1, &list), "список видеокарт"); // IADLXSystem::GetGPUs
        try
        {
            uint count = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(list, 3))(list); // Size
            IntPtr best = IntPtr.Zero;
            for (uint i = 0; i < count; i++)
            {
                IntPtr gpu;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(list, 11))(list, i, &gpu) != AdlxOk) // At_GPUList
                    continue;

                int type;
                bool discrete = CallPtr(gpu, 5, &type) == AdlxOk && type == 2; // IADLXGPU::Type: GPUTYPE_DISCRETE
                if (best == IntPtr.Zero || discrete)
                {
                    if (best != IntPtr.Zero)
                        Release(best);
                    best = gpu;
                    if (discrete)
                        break;
                }
                else
                {
                    Release(gpu);
                }
            }

            if (best == IntPtr.Zero)
                throw new AdlxException("видеокарта AMD не найдена", -1);
            _gpu = best;
        }
        finally
        {
            Release(list);
        }

        sbyte* name;
        if (CallPtr(_gpu, 7, &name) == AdlxOk && name != null) // IADLXGPU::Name
            GpuName = (Marshal.PtrToStringUTF8((IntPtr)name) ?? "").Trim();
        uint vram;
        if (CallPtr(_gpu, 11, &vram) == AdlxOk) // IADLXGPU::TotalVRAM
            VramMb = (int)vram;

        AppLog.Info($"ADLX {AdlxVersion}: {GpuName}, {VramMb} МБ.");
    }

    // ===================== Чтение =====================

    /// <summary>Всё, что можно настроить, с диапазонами, текущими и заводскими значениями.</summary>
    public GpuTuningInfo Read()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            var info = new GpuTuningInfo { GpuName = GpuName, VramMb = VramMb, AdlxVersion = AdlxVersion };
            info.IsAtFactory = ServiceBool(4) ?? true;                       // IsAtFactory
            info.SupportsPreset = ServiceBool(7) ?? false;
            info.SupportsGfx = ServiceBool(8) ?? false;
            info.SupportsVram = ServiceBool(9) ?? false;
            info.SupportsFan = ServiceBool(10) ?? false;
            info.SupportsPower = ServiceBool(11) ?? false;
            info.SupportsAuto = ServiceBool(6) ?? false;

            // Флаги «поддерживается» — только подсказка: на части драйверов они не совпадают с реальностью,
            // поэтому каждый раздел пробуем прочитать, а неподдерживаемый просто не отдаст интерфейс.
            ReadPresets(info);
            ReadGfx(info);
            ReadVram(info);
            ReadPower(info);
            ReadFan(info);

            if (!_loggedSupport)
            {
                _loggedSupport = true;
                AppLog.Info($"ADLX: заводские {info.IsAtFactory}; флаги поддержки — режимы {info.SupportsPreset}, ядро {info.SupportsGfx}, " +
                            $"память {info.SupportsVram}, вентиляторы {info.SupportsFan}, питание {info.SupportsPower}, автонастройка {info.SupportsAuto}; " +
                            $"прочитано — ядро {info.MaxFrequencyRange is not null}, память {info.VramFrequencyRange is not null}, " +
                            $"питание {info.PowerLimitRange is not null}, кривая {info.FanCurve.Count} точек, Zero RPM {info.ZeroRpm is not null}.");
                if (info.FanSpeedRange is { } speed && info.FanTemperatureRange is { } temperature)
                {
                    AppLog.Info($"ADLX: вентиляторы — скорость {speed.Min}–{speed.Max} % (шаг {speed.Step}), температура {temperature.Min}–{temperature.Max} °C " +
                                $"(шаг {temperature.Step}); кривая {FormatCurve(info.FanCurve)}, по умолчанию {FormatCurve(info.FanCurveDefault)}; " +
                                $"остановка в простое {info.ZeroRpm?.ToString() ?? "—"}.");
                }
            }

            return info;
        }
    }

    /// <summary>Текущее значение параметра в виде строки (так оно хранится в журнале изменений).</summary>
    public string Get(string parameter)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            switch (parameter)
            {
                case GfxMinFrequency:
                    return WithInterface(14, "IADLXManualGraphicsTuning2", t => Int(GetInt(t, 4)));
                case GfxMaxFrequency:
                    return WithInterface(14, "IADLXManualGraphicsTuning2", t => Int(GetInt(t, 7)));
                case GfxVoltage:
                    return WithInterface(14, "IADLXManualGraphicsTuning2", t => Int(GetInt(t, 10)));
                case VramMaxFrequency:
                    return WithInterface(15, "IADLXManualVRAMTuning2", t => Int(GetInt(t, 8)));
                case VramTiming:
                    return WithInterface(15, "IADLXManualVRAMTuning2", t => Int(GetInt(t, 5)));
                case PowerLimit:
                    return WithInterface(17, "IADLXManualPowerTuning", t => Int(GetInt(t, 4)));
                case PowerTdc:
                    return WithInterface(17, "IADLXManualPowerTuning", t => Int(GetInt(t, 8)));
                case FanCurve:
                    return WithInterface(16, "IADLXManualFanTuning", t => FormatCurve(ReadFanStates(t, 4)));
                case FanZeroRpm:
                    return WithInterface(16, "IADLXManualFanTuning", t => GetBool(t, 9) ? "1" : "0");
                case FanMinAcoustic:
                    return WithInterface(16, "IADLXManualFanTuning", t => Int(GetInt(t, 13)));
                case FanTargetSpeed:
                    return WithInterface(16, "IADLXManualFanTuning", t => Int(GetInt(t, 21)));
                case FanMinSpeed:
                    return WithInterface(16, "IADLXManualFanTuning", t => Int(GetInt(t, 17)));
                case Preset:
                    return WithInterface(13, "IADLXGPUPresetTuning", CurrentPreset);
                default:
                    throw new ArgumentException($"Неизвестный параметр видеокарты «{parameter}».");
            }
        }
    }

    /// <summary>Записывает параметр. Значение проверяется по диапазону до записи.</summary>
    public void Set(string parameter, string value)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            switch (parameter)
            {
                case GfxMinFrequency:
                    WithInterface(14, "IADLXManualGraphicsTuning2", t => SetChecked(t, 3, 5, ParseInt(value), "минимальная частота"));
                    break;
                case GfxMaxFrequency:
                    WithInterface(14, "IADLXManualGraphicsTuning2", t => SetChecked(t, 6, 8, ParseInt(value), "максимальная частота"));
                    break;
                case GfxVoltage:
                    WithInterface(14, "IADLXManualGraphicsTuning2", t => SetChecked(t, 9, 11, ParseInt(value), "напряжение"));
                    break;
                case VramMaxFrequency:
                    WithInterface(15, "IADLXManualVRAMTuning2", t => SetChecked(t, 7, 9, ParseInt(value), "частота памяти"));
                    break;
                case VramTiming:
                    WithInterface(15, "IADLXManualVRAMTuning2", t => Check(CallInt(t, 6, ParseInt(value)), "тайминги памяти"));
                    break;
                case PowerLimit:
                    WithInterface(17, "IADLXManualPowerTuning", t => SetChecked(t, 3, 5, ParseInt(value), "предел мощности"));
                    break;
                case PowerTdc:
                    WithInterface(17, "IADLXManualPowerTuning", t => SetChecked(t, 7, 9, ParseInt(value), "предел тока"));
                    break;
                case FanCurve:
                    WithInterface(16, "IADLXManualFanTuning", t => WriteFanCurve(t, ParseCurve(value)));
                    break;
                case FanZeroRpm:
                    WithInterface(16, "IADLXManualFanTuning", t => Check(CallByte(t, 10, (byte)(value == "1" ? 1 : 0)), "режим без вращения"));
                    break;
                case FanMinAcoustic:
                    WithInterface(16, "IADLXManualFanTuning", t => SetChecked(t, 12, 14, ParseInt(value), "акустический предел"));
                    break;
                case FanTargetSpeed:
                    WithInterface(16, "IADLXManualFanTuning", t => SetChecked(t, 20, 22, ParseInt(value), "целевая скорость вентилятора"));
                    break;
                case FanMinSpeed:
                    WithInterface(16, "IADLXManualFanTuning", t => SetChecked(t, 16, 18, ParseInt(value), "минимальная скорость вентилятора"));
                    break;
                case Preset:
                    WithInterface(13, "IADLXGPUPresetTuning", t => SetPreset(t, value));
                    break;
                default:
                    throw new ArgumentException($"Неизвестный параметр видеокарты «{parameter}».");
            }

            AppLog.Info($"ADLX: {parameter} = {value}.");
        }
    }

    /// <summary>Поддерживает ли видеокарта параметр.</summary>
    public bool Supports(string parameter)
    {
        GpuTuningInfo info = Read();
        return parameter switch
        {
            GfxMinFrequency => info.MinFrequencyRange is not null,
            GfxMaxFrequency => info.MaxFrequencyRange is not null,
            GfxVoltage => info.VoltageRange is not null,
            VramMaxFrequency => info.VramFrequencyRange is not null,
            VramTiming => info.MemoryTimings.Count > 0,
            PowerLimit => info.PowerLimitRange is not null,
            PowerTdc => info.TdcRange is not null,
            FanCurve => info.FanCurve.Count > 0,
            FanZeroRpm => info.ZeroRpm is not null,
            FanMinAcoustic => info.MinAcousticRange is not null,
            FanTargetSpeed => info.TargetFanSpeedRange is not null,
            FanMinSpeed => info.MinFanSpeedRange is not null,
            Preset => info.SupportedPresets.Count > 0,
            _ => false,
        };
    }

    // ===================== Игровые настройки (IADLX3DSettingsServices) =====================
    // Номера методов — из C-интерфейса ADLX SDK (I3DSettings.h, ISystem.h).
    private const int SystemGet3DSettingsServices = 7;
    private const int Services3DGetAntiLag = 3;
    private const int Services3DGetChill = 4;
    private const int Services3DGetImageSharpening = 6;

    /// <summary>
    /// Radeon Anti-Lag, Radeon Chill и Radeon Image Sharpening: поддержка, состояние и диапазоны.
    /// Действуют на все игры сразу (как «Глобальная графика» в AMD Software).
    /// </summary>
    public Games.RadeonState Read3DSettings()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            bool antiLagSupported = false, chillSupported = false, sharpSupported = false;
            bool? antiLag = null, chill = null, sharpening = null;
            int? chillMin = null, chillMax = null, sharpness = null;
            TuningRange? chillRange = null, sharpRange = null;

            Try3D(Services3DGetAntiLag, "Radeon Anti-Lag", o =>
            {
                antiLagSupported = GetBool(o, 3); // IsSupported
                if (antiLagSupported)
                    antiLag = GetBool(o, 4);      // IsEnabled
            });
            Try3D(Services3DGetChill, "Radeon Chill", o =>
            {
                chillSupported = GetBool(o, 3);
                if (!chillSupported)
                    return;
                chill = GetBool(o, 4);
                chillRange = GetRange(o, 5);      // GetFPSRange
                chillMin = GetInt(o, 6);          // GetMinFPS
                chillMax = GetInt(o, 7);          // GetMaxFPS
            });
            Try3D(Services3DGetImageSharpening, "Radeon Image Sharpening", o =>
            {
                sharpSupported = GetBool(o, 3);
                if (!sharpSupported)
                    return;
                sharpening = GetBool(o, 4);
                sharpRange = GetRange(o, 5);      // GetSharpnessRange
                sharpness = GetInt(o, 6);         // GetSharpness
            });

            var values = new Games.RadeonValues
            {
                AntiLag = antiLag,
                Chill = chill,
                ChillMinFps = chillMin,
                ChillMaxFps = chillMax,
                Sharpening = sharpening,
                Sharpness = sharpness,
            };
            return new Games.RadeonState(values, antiLagSupported, chillSupported, sharpSupported, chillRange, sharpRange);
        }
    }

    /// <summary>
    /// Записывает заданные (не null) значения. Сначала выключения, потом включения: Anti-Lag и Chill взаимно
    /// исключают друг друга, и включение одного драйвер сопровождает выключением другого.
    /// </summary>
    public void Apply3DSettings(Games.RadeonValues values)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (values.AntiLag == false)
                With3D(Services3DGetAntiLag, o => Check(CallByte(o, 5, 0), "Radeon Anti-Lag")); // SetEnabled
            if (values.Chill == false)
                With3D(Services3DGetChill, o => Check(CallByte(o, 8, 0), "Radeon Chill"));
            if (values.Sharpening == false)
                With3D(Services3DGetImageSharpening, o => Check(CallByte(o, 7, 0), "Radeon Image Sharpening"));

            if (values.ChillMinFps is not null || values.ChillMaxFps is not null || values.Chill == true)
            {
                With3D(Services3DGetChill, o =>
                {
                    TuningRange range = GetRange(o, 5);
                    int min = range.Snap(values.ChillMinFps ?? GetInt(o, 6));
                    int max = range.Snap(values.ChillMaxFps ?? GetInt(o, 7));
                    if (min > max)
                        (min, max) = (max, min);
                    // Минимум не может быть больше максимума даже на мгновение: сначала максимум — с запасом, потом минимум, потом нужный максимум.
                    Check(CallInt(o, 10, range.Max), "Radeon Chill: максимум FPS"); // SetMaxFPS
                    Check(CallInt(o, 9, min), "Radeon Chill: минимум FPS");         // SetMinFPS
                    Check(CallInt(o, 10, max), "Radeon Chill: максимум FPS");
                    if (values.Chill == true)
                        Check(CallByte(o, 8, 1), "Radeon Chill");                   // SetEnabled
                });
            }

            if (values.AntiLag == true)
                With3D(Services3DGetAntiLag, o => Check(CallByte(o, 5, 1), "Radeon Anti-Lag"));

            if (values.Sharpness is not null || values.Sharpening == true)
            {
                With3D(Services3DGetImageSharpening, o =>
                {
                    if (values.Sharpness is int sharpness)
                        Check(CallInt(o, 8, GetRange(o, 5).Snap(sharpness)), "Radeon Image Sharpening: резкость"); // SetSharpness
                    if (values.Sharpening == true)
                        Check(CallByte(o, 7, 1), "Radeon Image Sharpening");                                     // SetEnabled
                });
            }

            AppLog.Info("ADLX: игровые настройки — " + Games.RadeonPlan.Describe(values) + ".");
        }
    }

    private void With3D(int method, Action<IntPtr> action)
    {
        IntPtr services;
        Check(CallPtr(_system, SystemGet3DSettingsServices, &services), "игровые настройки драйвера");
        try
        {
            IntPtr setting;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Fn(services, method))(services, _gpu, &setting), "игровая настройка");
            try
            {
                action(setting);
            }
            finally
            {
                Release(setting);
            }
        }
        finally
        {
            Release(services);
        }
    }

    private void Try3D(int method, string name, Action<IntPtr> action)
    {
        try
        {
            With3D(method, action);
        }
        catch (AdlxException ex)
        {
            if (_loggedMissing.Add(name))
                AppLog.Info($"ADLX: {name} недоступен ({ex.Message}).");
        }
    }

    /// <summary>Сбрасывает все настройки видеокарты к заводским (как кнопка «Сброс» в Adrenalin).</summary>
    public void ResetToFactory()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_services, 5))(_services, _gpu), "сброс к заводским");
            AppLog.Info("ADLX: настройки видеокарты сброшены к заводским.");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Release(_gpu);
            Release(_services);
            _gpu = IntPtr.Zero;
            _services = IntPtr.Zero;
            if (_system != IntPtr.Zero && _terminate != null)
                _terminate();
            _system = IntPtr.Zero;
            if (_library != IntPtr.Zero)
                NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }
    }

    // ===================== Разделы =====================

    private void ReadPresets(GpuTuningInfo info)
    {
        TryWithInterface(13, "IADLXGPUPresetTuning", t =>
        {
            for (int i = 0; i < Presets.Length; i++)
            {
                if (GetBool(t, 3 + i))
                    info.SupportedPresets.Add(Presets[i]);
            }

            info.CurrentPreset = CurrentPreset(t);
        });
    }

    private void ReadGfx(GpuTuningInfo info)
    {
        TryWithInterface(14, "IADLXManualGraphicsTuning2", t =>
        {
            info.MinFrequencyRange = GetRange(t, 3);
            info.MinFrequency = GetInt(t, 4);
            info.MaxFrequencyRange = GetRange(t, 6);
            info.MaxFrequency = GetInt(t, 7);
            info.VoltageRange = GetRange(t, 9);
            info.Voltage = GetInt(t, 10);
            TryQuery(t, "IADLXManualGraphicsTuning2_1", t1 =>
            {
                info.MinFrequencyDefault = TryGetInt(t1, 12);
                info.MaxFrequencyDefault = TryGetInt(t1, 13);
                info.VoltageDefault = TryGetInt(t1, 14);
            });
        });

        if (info.MaxFrequencyRange is null)
            info.GfxNote = "Эта видеокарта настраивается по точкам частоты (старое поколение) — пока не поддерживается приложением.";
    }

    private void ReadVram(GpuTuningInfo info)
    {
        TryWithInterface(15, "IADLXManualVRAMTuning2", t =>
        {
            info.VramFrequencyRange = GetRange(t, 7);
            info.VramFrequency = GetInt(t, 8);
            if (GetBool(t, 3))
            {
                info.MemoryTiming = TryGetInt(t, 5);
                IntPtr list;
                if (CallPtr(t, 4, &list) == AdlxOk) // GetSupportedMemoryTimingDescriptionList
                {
                    try
                    {
                        uint count = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(list, 3))(list);
                        for (uint i = 0; i < count; i++)
                        {
                            IntPtr item;
                            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(list, 11))(list, i, &item) != AdlxOk)
                                continue;
                            int description;
                            if (CallPtr(item, 3, &description) == AdlxOk)
                                info.MemoryTimings.Add(description);
                            Release(item);
                        }
                    }
                    finally
                    {
                        Release(list);
                    }
                }
            }

            TryQuery(t, "IADLXManualVRAMTuning2_1", t1 => info.VramFrequencyDefault = TryGetInt(t1, 10));
        });
    }

    private void ReadPower(GpuTuningInfo info)
    {
        TryWithInterface(17, "IADLXManualPowerTuning", t =>
        {
            info.PowerLimitRange = GetRange(t, 3);
            info.PowerLimit = GetInt(t, 4);
            if (GetBool(t, 6))
            {
                info.TdcRange = GetRange(t, 7);
                info.Tdc = GetInt(t, 8);
            }

            TryQuery(t, "IADLXManualPowerTuning1", t1 =>
            {
                info.PowerLimitDefault = TryGetInt(t1, 10);
                if (info.TdcRange is not null)
                    info.TdcDefault = TryGetInt(t1, 11);
            });
        });
    }

    private void ReadFan(GpuTuningInfo info)
    {
        TryWithInterface(16, "IADLXManualFanTuning", t =>
        {
            TuningRange speed, temperature;
            if (((delegate* unmanaged[Stdcall]<IntPtr, TuningRange*, TuningRange*, int>)Fn(t, 3))(t, &speed, &temperature) == AdlxOk)
            {
                info.FanSpeedRange = speed;
                info.FanTemperatureRange = temperature;
                info.FanCurve = ReadFanStates(t, 4);
            }

            if (GetBool(t, 8))
                info.ZeroRpm = GetBool(t, 9);
            if (GetBool(t, 11))
            {
                info.MinAcousticRange = GetRange(t, 12);
                info.MinAcoustic = GetInt(t, 13);
            }

            if (GetBool(t, 15))
            {
                info.MinFanSpeedRange = GetRange(t, 16);
                info.MinFanSpeed = GetInt(t, 17);
            }

            if (GetBool(t, 19))
            {
                info.TargetFanSpeedRange = GetRange(t, 20);
                info.TargetFanSpeed = GetInt(t, 21);
            }

            TryQuery(t, "IADLXManualFanTuning1", t1 =>
            {
                info.FanCurveDefault = ReadFanStates(t1, 23);
                if (info.MinAcousticRange is not null)
                    info.MinAcousticDefault = TryGetInt(t1, 24);
                if (info.MinFanSpeedRange is not null)
                    info.MinFanSpeedDefault = TryGetInt(t1, 25);
                if (info.TargetFanSpeedRange is not null)
                    info.TargetFanSpeedDefault = TryGetInt(t1, 26);
                if (info.ZeroRpm is not null)
                {
                    byte value;
                    if (CallPtr(t1, 27, &value) == AdlxOk)
                        info.ZeroRpmDefault = value != 0;
                }
            });
        });
    }

    private static string CurrentPreset(IntPtr preset)
    {
        for (int i = 0; i < Presets.Length; i++)
        {
            byte current;
            if (CallPtr(preset, 8 + i, &current) == AdlxOk && current != 0)
                return Presets[i];
        }

        return "Custom";
    }

    private static void SetPreset(IntPtr preset, string name)
    {
        int index = Array.IndexOf(Presets, name);
        if (index < 0)
            throw new ArgumentException($"Неизвестный режим видеокарты «{name}».");
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(preset, 13 + index))(preset), "режим видеокарты");
    }

    private static List<FanPoint> ReadFanStates(IntPtr fan, int method)
    {
        var points = new List<FanPoint>();
        IntPtr list;
        if (CallPtr(fan, method, &list) != AdlxOk || list == IntPtr.Zero)
            return points;

        try
        {
            uint count = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(list, 3))(list);
            for (uint i = 0; i < count; i++)
            {
                IntPtr state;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(list, 11))(list, i, &state) != AdlxOk)
                    continue;
                int speed, temperature;
                CallPtr(state, 3, &speed);
                CallPtr(state, 5, &temperature);
                points.Add(new FanPoint(temperature, speed));
                Release(state);
            }
        }
        finally
        {
            Release(list);
        }

        return points;
    }

    private static void WriteFanCurve(IntPtr fan, IReadOnlyList<FanPoint> points)
    {
        IntPtr list;
        Check(CallPtr(fan, 5, &list), "кривая вентилятора"); // GetEmptyFanTuningStates
        try
        {
            uint count = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(list, 3))(list);
            if (count != points.Count)
                throw new AdlxException($"у этой видеокарты {count} точек кривой вентилятора, а задано {points.Count}", 4);

            for (uint i = 0; i < count; i++)
            {
                IntPtr state;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(list, 11))(list, i, &state), "точка кривой");
                try
                {
                    Check(CallInt(state, 4, points[(int)i].Speed), "скорость вентилятора");
                    Check(CallInt(state, 6, points[(int)i].Temperature), "температура точки");
                }
                finally
                {
                    Release(state);
                }
            }

            int errorIndex = -1;
            int valid = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)Fn(fan, 6))(fan, list, &errorIndex);
            if (valid != AdlxOk)
                throw new AdlxException($"видеокарта не приняла точку кривой №{errorIndex + 1}", valid);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(fan, 7))(fan, list), "кривая вентилятора");
        }
        finally
        {
            Release(list);
        }
    }

    // ===================== Вспомогательное =====================

    /// <summary>Получает интерфейс настройки (GetManual…Tuning), приводит к нужному и освобождает после использования.</summary>
    private T WithInterface<T>(int serviceMethod, string iid, Func<IntPtr, T> action)
    {
        IntPtr raw;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Fn(_services, serviceMethod))(_services, _gpu, &raw), iid);
        try
        {
            IntPtr typed = QueryInterface(raw, iid);
            try
            {
                return action(typed);
            }
            finally
            {
                Release(typed);
            }
        }
        finally
        {
            Release(raw);
        }
    }

    private void WithInterface(int serviceMethod, string iid, Action<IntPtr> action) =>
        WithInterface<bool>(serviceMethod, iid, t =>
        {
            action(t);
            return true;
        });

    private void TryWithInterface(int serviceMethod, string iid, Action<IntPtr> action)
    {
        try
        {
            WithInterface(serviceMethod, iid, action);
        }
        catch (AdlxException ex)
        {
            if (_loggedMissing.Add(iid))
                AppLog.Info($"ADLX: {iid} недоступен ({ex.Message}).");
        }
    }

    private static void TryQuery(IntPtr obj, string iid, Action<IntPtr> action)
    {
        IntPtr typed;
        fixed (char* name = iid)
        {
            if (((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)Fn(obj, 2))(obj, name, &typed) != AdlxOk || typed == IntPtr.Zero)
                return;
        }

        try
        {
            action(typed);
        }
        finally
        {
            Release(typed);
        }
    }

    private static IntPtr QueryInterface(IntPtr obj, string iid)
    {
        IntPtr typed;
        fixed (char* name = iid)
            Check(((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)Fn(obj, 2))(obj, name, &typed), iid);
        return typed;
    }

    private bool? ServiceBool(int method)
    {
        byte value;
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)Fn(_services, method))(_services, _gpu, &value) == AdlxOk
            ? value != 0
            : null;
    }

    private static void SetChecked(IntPtr obj, int rangeMethod, int setMethod, int value, string what)
    {
        TuningRange range = GetRange(obj, rangeMethod);
        if (value < range.Min || value > range.Max)
            throw new AdlxException($"{what}: {value} вне допустимого диапазона {range.Min}–{range.Max}", 4);
        Check(CallInt(obj, setMethod, value), what);
    }

    private static TuningRange GetRange(IntPtr obj, int method)
    {
        TuningRange range;
        Check(CallPtr(obj, method, &range), "диапазон");
        return range;
    }

    private static int GetInt(IntPtr obj, int method)
    {
        int value;
        Check(CallPtr(obj, method, &value), "значение");
        return value;
    }

    private static int? TryGetInt(IntPtr obj, int method)
    {
        int value;
        return CallPtr(obj, method, &value) == AdlxOk ? value : null;
    }

    private static bool GetBool(IntPtr obj, int method)
    {
        byte value;
        return CallPtr(obj, method, &value) == AdlxOk && value != 0;
    }

    // Все методы ADLX возвращают ADLX_RESULT. Указатели передаются как void* — для вызова это одно и то же.
    private static int CallPtr(IntPtr obj, int method, void* arg) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void*, int>)Fn(obj, method))(obj, arg);

    private static int CallInt(IntPtr obj, int method, int arg) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Fn(obj, method))(obj, arg);

    private static int CallByte(IntPtr obj, int method, byte arg) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Fn(obj, method))(obj, arg);

    private static IntPtr Fn(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    private static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(obj, 1))(obj);
    }

    private IntPtr Export(string name) =>
        NativeLibrary.TryGetExport(_library, name, out IntPtr address)
            ? address
            : throw new AdlxException($"в библиотеке ADLX нет функции {name} — обновите драйвер AMD", -1);

    private static void Check(int result, string what)
    {
        if (result is AdlxOk or AdlxAlreadyEnabled or AdlxAlreadyInitialized)
            return;
        throw new AdlxException($"{what}: {Describe(result)}", result);
    }

    public static string Describe(int result) => result switch
    {
        3 => "ошибка драйвера",
        4 => "недопустимое значение",
        5 => "несовместимая версия ADLX — обновите драйвер AMD",
        6 => "функция не поддерживается этой версией драйвера",
        7 => "ADLX уже закрыт",
        8 => "не удалось подключиться к драйверу AMD",
        9 => "не найдено",
        10 => "объект недействителен",
        12 => "не поддерживается этой видеокартой",
        13 => "драйвер занят другой операцией",
        14 => "видеокарта неактивна",
        15 => "видеокарта занята",
        16 => "драйвер не ответил вовремя",
        17 => "функция выключена",
        18 => "нужен сброс настроек видеокарты к заводским",
        _ => $"код {result}",
    };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new AdlxException("ADLX уже закрыт", 7);
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : throw new ArgumentException($"Неверное число «{value}».");

    /// <summary>«35:20;50:30;65:45;80:65;90:85» — температура:скорость по точкам.</summary>
    public static string FormatCurve(IEnumerable<FanPoint> points) =>
        string.Join(";", points.Select(p => $"{p.Temperature.ToString(CultureInfo.InvariantCulture)}:{p.Speed.ToString(CultureInfo.InvariantCulture)}"));

    public static List<FanPoint> ParseCurve(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                string[] pair = part.Split(':');
                if (pair.Length != 2)
                    throw new ArgumentException($"Неверная точка кривой «{part}».");
                return new FanPoint(ParseInt(pair[0]), ParseInt(pair[1]));
            })
            .ToList();
}

/// <summary>Возможности и текущие значения настроек видеокарты. null — параметр не поддерживается.</summary>
public sealed class GpuTuningInfo
{
    public string GpuName { get; set; } = "";
    public int VramMb { get; set; }
    public string AdlxVersion { get; set; } = "";
    public bool IsAtFactory { get; set; }

    public bool SupportsPreset { get; set; }
    public bool SupportsGfx { get; set; }
    public bool SupportsVram { get; set; }
    public bool SupportsFan { get; set; }
    public bool SupportsPower { get; set; }
    public bool SupportsAuto { get; set; }

    public List<string> SupportedPresets { get; } = new();
    public string? CurrentPreset { get; set; }

    public TuningRange? MinFrequencyRange { get; set; }
    public int? MinFrequency { get; set; }
    public int? MinFrequencyDefault { get; set; }
    public TuningRange? MaxFrequencyRange { get; set; }
    public int? MaxFrequency { get; set; }
    public int? MaxFrequencyDefault { get; set; }
    public TuningRange? VoltageRange { get; set; }
    public int? Voltage { get; set; }
    public int? VoltageDefault { get; set; }
    public string? GfxNote { get; set; }

    public TuningRange? VramFrequencyRange { get; set; }
    public int? VramFrequency { get; set; }
    public int? VramFrequencyDefault { get; set; }
    public int? MemoryTiming { get; set; }
    public List<int> MemoryTimings { get; } = new();

    public TuningRange? PowerLimitRange { get; set; }
    public int? PowerLimit { get; set; }
    public int? PowerLimitDefault { get; set; }
    public TuningRange? TdcRange { get; set; }
    public int? Tdc { get; set; }
    public int? TdcDefault { get; set; }

    public TuningRange? FanSpeedRange { get; set; }
    public TuningRange? FanTemperatureRange { get; set; }
    public List<FanPoint> FanCurve { get; set; } = new();
    public List<FanPoint> FanCurveDefault { get; set; } = new();
    public bool? ZeroRpm { get; set; }
    public bool? ZeroRpmDefault { get; set; }
    public TuningRange? MinAcousticRange { get; set; }
    public int? MinAcoustic { get; set; }
    public int? MinAcousticDefault { get; set; }
    public TuningRange? MinFanSpeedRange { get; set; }
    public int? MinFanSpeed { get; set; }
    public int? MinFanSpeedDefault { get; set; }
    public TuningRange? TargetFanSpeedRange { get; set; }
    public int? TargetFanSpeed { get; set; }
    public int? TargetFanSpeedDefault { get; set; }
}
