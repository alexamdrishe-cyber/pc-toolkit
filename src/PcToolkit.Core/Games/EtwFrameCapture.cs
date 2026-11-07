using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Games;

/// <summary>Процесс, который сейчас выводит кадры: сколько кадров в секунду и через какой API.</summary>
public sealed record FramePresenter(int ProcessId, double Fps, string Api);

/// <summary>Ошибка запуска счётчика кадров с понятным текстом.</summary>
public sealed class FrameCaptureException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Счётчик кадров через ETW — тот же способ, что у PresentMon (Intel), CapFrameX и оверлея Xbox.
/// Windows сама сообщает о каждом вызове Present в любом процессе: событие Present_Start
/// у DXGI (Direct3D 10/11/12, Vulkan и OpenGL поверх DXGI) и у Direct3D 9. Ничего не внедряется в игры,
/// поэтому античиты на это не реагируют. Отметки времени — счётчик QPC, как у <see cref="Stopwatch"/>.
/// Запускать сеанс ETW может только администратор: в обычном режиме кадры считает служба PC Toolkit.
/// </summary>
public sealed unsafe class EtwFrameCapture : IDisposable
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    // Номера событий Present_Start: DXGI — 42 (и 44 для PresentMultiplaneOverlay), D3D9 — 1.
    private const ushort DxgiPresentStart = 42;
    private const ushort DxgiPresentMpoStart = 44;
    private const ushort D3D9PresentStart = 1;
    private const uint DxgiPresentTest = 0x1; // пробный Present без вывода кадра: PresentMon его тоже пропускает

    private const int ChainCapacity = 8192; // степень двойки: ~16 секунд при 500 FPS
    private const int PropertiesSize = 120; // sizeof(EVENT_TRACE_PROPERTIES) в 64-битном процессе
    private const int LogFileSize = 448;    // sizeof(EVENT_TRACE_LOGFILEW) в 64-битном процессе
    private const int NameBytes = 1024 * sizeof(char);

    private readonly object _lock = new();
    private readonly Dictionary<(int Pid, ulong Chain), Chain> _chains = new();
    private readonly Dictionary<int, Chain> _primary = new();
    private readonly string _sessionName;
    private readonly int _ownProcessId = Environment.ProcessId;
    private ulong _sessionHandle;
    private ulong _traceHandle = ulong.MaxValue;
    private IntPtr _logFile;
    private IntPtr _loggerName;
    private GCHandle _self;
    private Thread? _thread;
    private long _lastCleanup;
    private volatile bool _running;
    private bool _disposed;

    private EtwFrameCapture(string sessionName)
    {
        _sessionName = sessionName;
    }

    /// <summary>Сеанс работает и получает события.</summary>
    public bool IsRunning => _running;

    /// <summary>Сколько событий о кадрах получено с начала сеанса.</summary>
    public long FramesSeen { get; private set; }

    /// <summary>
    /// Запускает сеанс ETW с именем <paramref name="sessionName"/>. Если сеанс с таким именем остался
    /// от прошлого запуска (например, программа упала), он останавливается и запускается заново.
    /// </summary>
    public static EtwFrameCapture Start(string sessionName)
    {
        if (!Environment.Is64BitProcess)
            throw new FrameCaptureException("Счётчик кадров работает только в 64-битном процессе.");

        var capture = new EtwFrameCapture(sessionName);
        try
        {
            capture.StartSession();
            capture.OpenConsumer();
            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Процессы, выводившие кадры за последние 3 секунды, с частотой кадров за секунду до их последнего кадра
    /// (события ETW приходят с задержкой до секунды, поэтому отсчёт не от «сейчас»).
    /// </summary>
    public IReadOnlyList<FramePresenter> GetPresenters()
    {
        long now = Stopwatch.GetTimestamp();
        long second = Stopwatch.Frequency;
        var result = new List<FramePresenter>();
        lock (_lock)
        {
            CleanupLocked(now);
            foreach ((int pid, Chain chain) in _primary)
            {
                if (chain.Count == 0 || now - chain.Last > 3 * second)
                    continue;

                int frames = chain.CountSince(chain.Last - second);
                result.Add(new FramePresenter(pid, frames, chain.Api));
            }
        }

        result.Sort((a, b) => b.Fps.CompareTo(a.Fps));
        return result;
    }

    /// <summary>
    /// Отметки времени кадров процесса (QPC), строго больше <paramref name="since"/>, по порядку, не больше <paramref name="max"/>.
    /// Если кадров больше, возвращаются самые ранние — следующий запрос продолжит с последнего.
    /// </summary>
    public long[] GetFrames(int processId, long since, int max = 4096)
    {
        lock (_lock)
        {
            if (!_primary.TryGetValue(processId, out Chain? chain))
                return Array.Empty<long>();

            return chain.Since(since, max);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _running = false;

        // Остановка сеанса завершает ProcessTrace в потоке чтения; CloseTrace — на случай, если сеанс уже остановлен.
        if (_sessionHandle != 0)
            StopSession(_sessionHandle, _sessionName);
        if (_traceHandle != ulong.MaxValue)
            CloseTrace(_traceHandle);

        bool threadDone = _thread is null || _thread.Join(TimeSpan.FromSeconds(3));
        if (!threadDone)
        {
            // Поток всё ещё может вызвать обработчик: память и ссылку на себя не освобождаем.
            AppLog.Warn("Поток счётчика кадров не завершился за 3 секунды.");
            return;
        }

        if (_self.IsAllocated)
            _self.Free();
        if (_logFile != IntPtr.Zero)
            Marshal.FreeHGlobal(_logFile);
        if (_loggerName != IntPtr.Zero)
            Marshal.FreeHGlobal(_loggerName);
        _logFile = IntPtr.Zero;
        _loggerName = IntPtr.Zero;
    }

    private void StartSession()
    {
        IntPtr properties = AllocateProperties(includeLogFileName: false);
        try
        {
            uint status = StartTraceW(out ulong handle, _sessionName, properties);
            if (status == ErrorAlreadyExists)
            {
                // Сеанс остался от прошлого запуска: сеансы ETW живут дольше процесса, который их создал.
                AppLog.Info($"Сеанс ETW «{_sessionName}» уже существует: останавливаю и запускаю заново.");
                StopSession(0, _sessionName);
                Marshal.FreeHGlobal(properties);
                properties = AllocateProperties(includeLogFileName: false);
                status = StartTraceW(out handle, _sessionName, properties);
            }

            if (status != 0)
            {
                throw status == ErrorAccessDenied
                    ? new FrameCaptureException("Для счётчика кадров нужны права администратора или служба PC Toolkit.")
                    : new FrameCaptureException($"Не удалось запустить сеанс ETW: {new Win32Exception((int)status).Message} (код {status}).");
            }

            _sessionHandle = handle;
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }

        bool dxgi = EnableProvider(DxgiProvider, "DXGI", DxgiPresentStart, DxgiPresentMpoStart);
        bool d3d9 = EnableProvider(D3D9Provider, "Direct3D 9", D3D9PresentStart);
        if (!dxgi && !d3d9)
            throw new FrameCaptureException("Windows не дала подписаться на события Direct3D. Подробности в журнале.");
    }

    /// <summary>
    /// Включает поставщика событий. Сначала с фильтром по номерам событий (меньше лишней работы, Windows 8.1+),
    /// если не вышло — без фильтра: ненужные события всё равно отбрасываются в обработчике.
    /// </summary>
    private bool EnableProvider(Guid provider, string name, params ushort[] eventIds)
    {
        const uint EnableControlCode = 1;   // EVENT_CONTROL_CODE_ENABLE_PROVIDER
        const byte LevelInformation = 4;    // TRACE_LEVEL_INFORMATION
        const uint FilterTypeEventId = 0x80000200; // EVENT_FILTER_TYPE_EVENT_ID

        int filterSize = 4 + eventIds.Length * sizeof(ushort);
        byte* filter = stackalloc byte[filterSize];
        filter[0] = 1; // FilterIn = TRUE: пропускать только эти события
        filter[1] = 0;
        *(ushort*)(filter + 2) = (ushort)eventIds.Length;
        for (int i = 0; i < eventIds.Length; i++)
            *(ushort*)(filter + 4 + i * 2) = eventIds[i];

        // EVENT_FILTER_DESCRIPTOR: Ptr (8), Size (4), Type (4).
        byte* descriptor = stackalloc byte[16];
        *(ulong*)descriptor = (ulong)filter;
        *(uint*)(descriptor + 8) = (uint)filterSize;
        *(uint*)(descriptor + 12) = FilterTypeEventId;

        // ENABLE_TRACE_PARAMETERS (версия 2): Version, EnableProperty, ControlFlags, SourceId (GUID),
        // EnableFilterDesc (указатель, смещение 32), FilterDescCount (смещение 40). Всего 48 байт.
        byte* parameters = stackalloc byte[48];
        new Span<byte>(parameters, 48).Clear();
        *(uint*)parameters = 2;
        *(ulong*)(parameters + 32) = (ulong)descriptor;
        *(uint*)(parameters + 40) = 1;

        Guid id = provider;
        uint status = EnableTraceEx2(_sessionHandle, ref id, EnableControlCode, LevelInformation, ulong.MaxValue, 0, 0, (IntPtr)parameters);
        if (status != 0)
        {
            AppLog.Info($"Подписка на {name} с фильтром не удалась (код {status}), пробую без фильтра.");
            status = EnableTraceEx2(_sessionHandle, ref id, EnableControlCode, LevelInformation, ulong.MaxValue, 0, 0, IntPtr.Zero);
        }

        if (status != 0)
        {
            AppLog.Warn($"Не удалось подписаться на события {name}: код {status}.");
            return false;
        }

        return true;
    }

    private void OpenConsumer()
    {
        const uint ProcessTraceModeRealTime = 0x00000100;
        const uint ProcessTraceModeRawTimestamp = 0x00001000;
        const uint ProcessTraceModeEventRecord = 0x10000000;

        _self = GCHandle.Alloc(this);
        _loggerName = Marshal.StringToHGlobalUni(_sessionName);
        _logFile = Marshal.AllocHGlobal(LogFileSize);
        byte* logFile = (byte*)_logFile;
        new Span<byte>(logFile, LogFileSize).Clear();

        // EVENT_TRACE_LOGFILEW (x64): LoggerName — смещение 8, ProcessTraceMode — 28,
        // EventRecordCallback — 424, Context — 440.
        *(IntPtr*)(logFile + 8) = _loggerName;
        *(uint*)(logFile + 28) = ProcessTraceModeRealTime | ProcessTraceModeRawTimestamp | ProcessTraceModeEventRecord;
        delegate* unmanaged<byte*, void> callback = &OnEventRecord;
        *(IntPtr*)(logFile + 424) = (IntPtr)(void*)callback;
        *(IntPtr*)(logFile + 440) = GCHandle.ToIntPtr(_self);

        ulong handle = OpenTraceW(_logFile);
        if (handle == ulong.MaxValue)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new FrameCaptureException($"Не удалось подключиться к сеансу ETW: {new Win32Exception(error).Message} (код {error}).");
        }

        _traceHandle = handle;
        _running = true;
        _thread = new Thread(ProcessLoop) { IsBackground = true, Name = "PcToolkit.FrameCapture" };
        _thread.Start();
        AppLog.Info($"Счётчик кадров запущен (сеанс ETW «{_sessionName}»).");
    }

    private void ProcessLoop()
    {
        ulong handle = _traceHandle;
        uint status = ProcessTrace(&handle, 1, IntPtr.Zero, IntPtr.Zero);
        _running = false;
        if (!_disposed)
            AppLog.Warn($"Сеанс счётчика кадров завершился сам (код {status}).");
    }

    [UnmanagedCallersOnly]
    private static void OnEventRecord(byte* record)
    {
        try
        {
            // EVENT_RECORD.UserContext (смещение 104) — то, что записано в Context при открытии.
            IntPtr context = *(IntPtr*)(record + 104);
            if (context != IntPtr.Zero && GCHandle.FromIntPtr(context).Target is EtwFrameCapture capture)
                capture.OnEvent(record);
        }
        catch
        {
            // Исключение не должно уйти в Windows: процесс бы упал.
        }
    }

    private void OnEvent(byte* record)
    {
        // EVENT_HEADER: Flags — 4, ProcessId — 12, TimeStamp — 16, ProviderId — 24, Id — 40.
        // EVENT_RECORD: UserDataLength — 86, UserData — 96.
        ushort flags = *(ushort*)(record + 4);
        int pid = *(int*)(record + 12);
        long timestamp = *(long*)(record + 16);
        Guid provider = *(Guid*)(record + 24);
        ushort id = *(ushort*)(record + 40);
        ushort length = *(ushort*)(record + 86);
        byte* data = *(byte**)(record + 96);

        if (pid == _ownProcessId)
            return; // окно самого PC Toolkit (WPF рисует через Direct3D 9) и оверлей

        string api;
        if (provider == DxgiProvider && id is DxgiPresentStart or DxgiPresentMpoStart)
            api = "DXGI";
        else if (provider == D3D9Provider && id == D3D9PresentStart)
            api = "D3D9";
        else
            return;

        // Первое поле — указатель на цепочку кадров (swap chain) или устройство: 4 байта у 32-битных процессов.
        int pointerSize = (flags & 0x0020) != 0 ? 4 : 8; // EVENT_HEADER_FLAG_32_BIT_HEADER
        ulong chainAddress = 0;
        if (data != null && length >= pointerSize)
        {
            chainAddress = pointerSize == 4 ? *(uint*)data : *(ulong*)data;
            if (api == "DXGI" && length >= pointerSize + 4 && (*(uint*)(data + pointerSize) & DxgiPresentTest) != 0)
                return;
        }

        lock (_lock)
        {
            if (!_chains.TryGetValue((pid, chainAddress), out Chain? chain))
            {
                chain = new Chain(pid, api);
                _chains[(pid, chainAddress)] = chain;
            }

            chain.Add(timestamp);
            FramesSeen++;

            // Основная цепочка процесса: та, что выводит кадры. Меняется, только если прежняя замолчала на секунду
            // (у игры бывает вторая цепочка — например, окно лаунчера или встроенного браузера).
            if (!_primary.TryGetValue(pid, out Chain? primary)
                || (primary != chain && timestamp - primary.Last > Stopwatch.Frequency))
            {
                _primary[pid] = chain;
            }
        }
    }

    /// <summary>Забывает процессы, которые не выводили кадры 30 секунд.</summary>
    private void CleanupLocked(long now)
    {
        long limit = 30 * Stopwatch.Frequency;
        if (now - _lastCleanup < 5 * Stopwatch.Frequency)
            return;

        _lastCleanup = now;
        foreach (var key in _chains.Where(p => now - p.Value.Last > limit).Select(p => p.Key).ToList())
            _chains.Remove(key);
        foreach (int pid in _primary.Where(p => now - p.Value.Last > limit).Select(p => p.Key).ToList())
            _primary.Remove(pid);
    }

    private static IntPtr AllocateProperties(bool includeLogFileName)
    {
        int size = PropertiesSize + NameBytes * 2;
        IntPtr pointer = Marshal.AllocHGlobal(size);
        byte* p = (byte*)pointer;
        new Span<byte>(p, size).Clear();

        // WNODE_HEADER: BufferSize — 0, ClientContext — 40 (1 = QPC), Flags — 44 (WNODE_FLAG_TRACED_GUID).
        *(uint*)p = (uint)size;
        *(uint*)(p + 40) = 1;
        *(uint*)(p + 44) = 0x00020000;
        // Маленькие буферы по 4 КБ: во время игры заполняются несколько раз в секунду, и кадры приходят почти без задержки.
        *(uint*)(p + 48) = 4;    // BufferSize, КБ
        *(uint*)(p + 52) = 8;    // MinimumBuffers
        *(uint*)(p + 56) = 128;  // MaximumBuffers
        *(uint*)(p + 64) = 0x00000100; // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
        *(uint*)(p + 68) = 1;    // FlushTimer: не реже раза в секунду
        *(uint*)(p + 112) = includeLogFileName ? (uint)(PropertiesSize + NameBytes) : 0; // LogFileNameOffset
        *(uint*)(p + 116) = PropertiesSize; // LoggerNameOffset
        return pointer;
    }

    private static void StopSession(ulong handle, string name)
    {
        const uint ControlStop = 1; // EVENT_TRACE_CONTROL_STOP
        IntPtr properties = AllocateProperties(includeLogFileName: true);
        try
        {
            uint status = ControlTraceW(handle, handle == 0 ? name : null, properties, ControlStop);
            if (status != 0 && status != ErrorWmiInstanceNotFound)
                AppLog.Warn($"Остановка сеанса ETW «{name}»: код {status}.");
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }
    }

    /// <summary>Отметки времени кадров одной цепочки в кольцевом буфере.</summary>
    private sealed class Chain(int pid, string api)
    {
        private readonly long[] _times = new long[ChainCapacity];

        public int Pid { get; } = pid;

        public string Api { get; } = api;

        /// <summary>Сколько кадров записано всего (в буфере — последние <see cref="ChainCapacity"/>).</summary>
        public long Count { get; private set; }

        public long Last => Count == 0 ? 0 : _times[(Count - 1) & (ChainCapacity - 1)];

        public void Add(long timestamp)
        {
            // События одного процесса приходят по порядку; на всякий случай не даём времени идти назад.
            if (Count > 0 && timestamp < Last)
                timestamp = Last;
            _times[Count & (ChainCapacity - 1)] = timestamp;
            Count++;
        }

        public int CountSince(long since)
        {
            int count = 0;
            long first = Math.Max(0, Count - ChainCapacity);
            for (long i = Count - 1; i >= first && _times[i & (ChainCapacity - 1)] > since; i--)
                count++;
            return count;
        }

        public long[] Since(long since, int max)
        {
            long first = Math.Max(0, Count - ChainCapacity);
            long start = Count;
            while (start > first && _times[(start - 1) & (ChainCapacity - 1)] > since)
                start--;

            int count = (int)Math.Min(Count - start, max);
            var result = new long[count];
            for (int i = 0; i < count; i++)
                result[i] = _times[(start + i) & (ChainCapacity - 1)];
            return result;
        }
    }

    private const uint ErrorAccessDenied = 5;
    private const uint ErrorAlreadyExists = 183;
    private const uint ErrorWmiInstanceNotFound = 4201;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong traceHandle, string instanceName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong traceHandle, string? instanceName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logFile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong* handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);
}
