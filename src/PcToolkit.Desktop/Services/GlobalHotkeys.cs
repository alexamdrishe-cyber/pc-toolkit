using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using PcToolkit.Core.Logging;

namespace PcToolkit.Desktop.Services;

/// <summary>
/// Глобальные горячие клавиши (RegisterHotKey): работают, даже когда на переднем плане игра.
/// Сообщения приходят в скрытое служебное окно в потоке интерфейса.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5C00;

    public GlobalHotkeys()
    {
        // Окно только для сообщений (HWND_MESSAGE): невидимое и без значка на панели задач.
        var parameters = new HwndSourceParameters("PcToolkit.Hotkeys")
        {
            ParentWindow = new IntPtr(-3),
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
    }

    /// <summary>Регистрирует сочетание. false — его уже заняла другая программа.</summary>
    public bool Register(ModifierKeys modifiers, Key key, Action action)
    {
        int id = _nextId++;
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(_window.Handle, id, (uint)modifiers | ModNoRepeat, virtualKey))
        {
            AppLog.Warn($"Сочетание {modifiers}+{key} занято другой программой (код {Marshal.GetLastPInvokeError()}).");
            return false;
        }

        _actions[id] = action;
        return true;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys)
            UnregisterHotKey(_window.Handle, id);
        _actions.Clear();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out Action? action))
        {
            handled = true;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error("Ошибка обработки горячей клавиши.", ex);
            }
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
