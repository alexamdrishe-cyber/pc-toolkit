using System.Diagnostics;
using System.Runtime.InteropServices;
using PcToolkit.Core.Logging;

namespace PcToolkit.Desktop.Services;

/// <summary>
/// Не даёт запустить вторую копию приложения: две копии одновременно опрашивали бы датчики
/// и мешали друг другу. Вместо второй копии на передний план выводится уже открытое окно.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PcToolkit.SingleInstance";
    private const string ActivateEventName = @"Local\PcToolkit.Activate";
    /// <summary>Просьба закрыться: её подаёт установщик перед заменой файлов (имя — в installer\PcToolkit.iss).</summary>
    private const string ExitEventName = @"Local\PcToolkit.Exit";
    private const int SwRestore = 9;

    private readonly Mutex? _mutex;
    private bool _owned;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activateWait;
    private EventWaitHandle? _exit;
    private RegisteredWaitHandle? _exitWait;

    private SingleInstance(Mutex? mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    /// <summary>true, если эта копия приложения единственная.</summary>
    public bool IsFirstInstance => _owned;

    /// <param name="wait">
    /// Сколько ждать, пока закроется прежняя копия. Нужно при перезапуске от имени администратора:
    /// новая копия стартует, пока старая ещё завершает работу.
    /// </param>
    public static SingleInstance Acquire(TimeSpan wait)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
        }
        catch (UnauthorizedAccessException)
        {
            // Мьютекс создала копия, запущенная от имени администратора: открыть его нельзя, значит она уже работает.
            return new SingleInstance(null, owned: false);
        }

        bool owned;
        try
        {
            owned = mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            // Прежняя копия завершилась аварийно; мьютекс теперь принадлежит нам.
            owned = true;
        }

        return new SingleInstance(mutex, owned);
    }

    /// <summary>
    /// Первая копия ждёт сигнала от следующих: окно может быть скрыто в трее, и найти его по процессу нельзя.
    /// <paramref name="onActivate"/> вызывается в фоновом потоке.
    /// </summary>
    public void ListenForActivation(Action onActivate)
    {
        try
        {
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось создать сигнал для показа окна.", ex);
        }
    }

    /// <summary>
    /// Ждёт просьбы закрыться от установщика: приложение часто свёрнуто в трей, и закрыть его иначе,
    /// как «Выходом» в меню значка, нельзя. <paramref name="onExit"/> вызывается в фоновом потоке.
    /// </summary>
    public void ListenForExit(Action onExit)
    {
        try
        {
            _exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exit, (_, _) => onExit(), null, Timeout.Infinite, executeOnlyOnce: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось создать сигнал для закрытия установщиком.", ex);
        }
    }

    /// <summary>Просит запущенную копию закрыться (тот же сигнал, что у установщика). true — сигнал подан.</summary>
    public static bool RequestExit()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ExitEventName, out EventWaitHandle? signal))
                return false;
            using (signal)
                signal.Set();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>Показывает окно уже запущенной копии.</summary>
    public static void ActivateExisting()
    {
        // Вторую копию запустил пользователь, она на переднем плане: разрешаем первой вывести окно вперёд,
        // иначе Windows только мигнёт кнопкой на панели задач (особенно поверх полноэкранной игры).
        AllowSetForegroundWindow(AsfwAny);

        try
        {
            // Первая копия (даже свёрнутая в трей) покажет окно сама.
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out EventWaitHandle? signal))
            {
                using (signal)
                    signal.Set();
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось подать сигнал запущенной копии.", ex);
        }

        try
        {
            using Process current = Process.GetCurrentProcess();
            foreach (Process process in Process.GetProcessesByName(current.ProcessName))
            {
                using (process)
                {
                    if (process.Id == current.Id)
                        continue;

                    IntPtr handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero)
                        continue;

                    if (IsIconic(handle))
                        ShowWindow(handle, SwRestore);

                    SetForegroundWindow(handle);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось показать окно уже запущенной копии.", ex);
        }
    }

    public void Dispose()
    {
        _activateWait?.Unregister(null);
        _activate?.Dispose();
        _exitWait?.Unregister(null);
        _exit?.Dispose();

        if (_owned && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Мьютекс уже освобождён.
            }

            _owned = false;
        }

        _mutex?.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    private const uint AsfwAny = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);
}
