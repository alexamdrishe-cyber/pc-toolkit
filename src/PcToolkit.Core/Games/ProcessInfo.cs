using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PcToolkit.Core.Games;

/// <summary>Запущенный процесс: номер, имя, полный путь к exe (пустой, если Windows его не отдала) и занятая память.</summary>
public sealed record RunningProcess(int Id, string Name, string Path, long WorkingSet = 0);

/// <summary>Сведения о процессах без прав администратора: только чтение, без внедрения и без доступа к памяти.</summary>
public static class ProcessInfo
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<int, (string Name, string Path)> PathCache = new();
    private static readonly string[] TransientForeground = { "TextInputHost" };
    private static int _lastCheckedPid;
    private static bool _lastCheckedTransient;
    private static int _lastRealForeground;

    /// <summary>Процесс окна, которое сейчас на переднем плане.</summary>
    public static int? ForegroundProcessId()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return null;

        GetWindowThreadProcessId(window, out uint raw);
        if (raw == 0)
            return null;

        int pid = (int)raw;
        if (pid != _lastCheckedPid)
        {
            _lastCheckedPid = pid;
            _lastCheckedTransient = Name(pid) is { } name && TransientForeground.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        // Служебное окно Windows (панель ввода, эмодзи) ненадолго встаёт поверх игры: игра при этом остаётся на экране.
        if (_lastCheckedTransient)
            return _lastRealForeground == 0 ? null : _lastRealForeground;

        _lastRealForeground = pid;
        return pid;
    }

    /// <summary>Окно на переднем плане занимает весь монитор: полноэкранная игра (или видео на весь экран).</summary>
    public static bool IsForegroundFullscreen()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || window == GetShellWindow() || window == GetDesktopWindow())
            return false;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowRect(window, out Rect rect) || !GetMonitorInfoW(MonitorFromWindow(window, 2), ref info))
            return false;

        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top
               && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    /// <summary>Полный путь к exe процесса или null. Открывает процесс только на чтение сведений (как Диспетчер задач).</summary>
    public static string? ImagePath(int processId)
    {
        const uint QueryLimitedInformation = 0x1000;
        IntPtr handle = OpenProcess(QueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string? Name(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Все процессы с путями. Пути кэшируются, поэтому вызывать раз в пару секунд недорого.</summary>
    public static IReadOnlyList<RunningProcess> List()
    {
        Process[] processes = Process.GetProcesses();
        var result = new List<RunningProcess>(processes.Length);
        var alive = new HashSet<int>();
        try
        {
            foreach (Process process in processes)
            {
                int id = process.Id;
                if (id <= 4)
                    continue; // System Idle и System

                string name;
                long workingSet;
                try
                {
                    name = process.ProcessName;
                    workingSet = process.WorkingSet64;
                }
                catch (InvalidOperationException)
                {
                    continue; // процесс уже завершился
                }

                alive.Add(id);
                string path;
                lock (CacheLock)
                {
                    if (!PathCache.TryGetValue(id, out var cached) || cached.Name != name)
                    {
                        cached = (name, ImagePath(id) ?? "");
                        PathCache[id] = cached;
                    }

                    path = cached.Path;
                }

                result.Add(new RunningProcess(id, name, path, workingSet));
            }
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }

        lock (CacheLock)
        {
            foreach (int id in PathCache.Keys.Where(id => !alive.Contains(id)).ToList())
                PathCache.Remove(id);
        }

        return result;
    }

    /// <summary>Лежит ли файл <paramref name="path"/> внутри папки <paramref name="folder"/> (с подпапками).</summary>
    public static bool IsInside(string path, string folder)
    {
        if (path.Length == 0 || folder.Length < 4)
            return false;

        ReadOnlySpan<char> dir = folder.AsSpan().TrimEnd("\\/");
        return path.Length > dir.Length + 1
               && path[dir.Length] is '\\' or '/'
               && path.AsSpan(0, dir.Length).Equals(dir, StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
