using System.Runtime.InteropServices;
using System.Windows.Interop;
using PcToolkit.Core.Logging;

namespace PcToolkit.Desktop.Services;

/// <summary>Пункт меню значка в трее.</summary>
public sealed record TrayMenuItem(string Text, Action? Action, bool Checked = false, bool Enabled = true)
{
    public static TrayMenuItem Separator { get; } = new("-", null);
}

/// <summary>
/// Значок в области уведомлений без WinForms: Shell_NotifyIcon и обычное меню Windows.
/// Левая кнопка — открыть окно, правая — меню. Если Проводник перезапустится, значок вернётся сам.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;
    private const int NifMessage = 0x1;
    private const int NifIcon = 0x2;
    private const int NifTip = 0x4;
    private const int NifInfo = 0x10;

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly int _taskbarCreated;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private string _tooltip;
    private bool _added;

    public TrayIcon(string tooltip, Func<IReadOnlyList<TrayMenuItem>> menu)
    {
        _tooltip = tooltip;
        _menu = menu;
        _icon = AppIconFactory.CreateHIcon(32);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        // Невидимое окно верхнего уровня: меню трея закрывается по щелчку мимо, только если его окно на переднем плане.
        var parameters = new HwndSourceParameters("PcToolkit.Tray")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            Width = 0,
            Height = 0,
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
        Add();
    }

    /// <summary>Щелчок по значку (открыть окно).</summary>
    public event EventHandler? Activated;

    public void SetTooltip(string text)
    {
        _tooltip = text;
        if (!_added)
            return;

        NotifyIconData data = Data(NifTip);
        Shell_NotifyIconW(NimModify, ref data);
    }

    /// <summary>Уведомление Windows от значка (в Windows 10/11 — всплывающее уведомление).</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added)
            return;

        NotifyIconData data = Data(NifInfo);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = 0x1 | 0x20; // NIIF_INFO | NIIF_LARGE_ICON
        Shell_NotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_added)
        {
            NotifyIconData data = Data(0);
            Shell_NotifyIconW(NimDelete, ref data);
            _added = false;
        }

        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (_icon != IntPtr.Zero)
            DestroyIcon(_icon);
    }

    private void Add()
    {
        NotifyIconData data = Data(NifMessage | NifIcon | NifTip);
        _added = Shell_NotifyIconW(NimAdd, ref data);
        if (!_added)
            AppLog.Warn("Не удалось добавить значок в трей.");
    }

    private NotifyIconData Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // Проводник перезапустился: значки трея надо добавить заново.
            _added = false;
            Add();
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage)
            return IntPtr.Zero;

        int mouse = (int)(lParam.ToInt64() & 0xFFFF);
        try
        {
            if (mouse is WmLButtonUp or WmLButtonDblClk)
                Activated?.Invoke(this, EventArgs.Empty);
            else if (mouse == WmRButtonUp)
                ShowMenu();
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка меню трея.", ex);
        }

        handled = true;
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        const uint MfString = 0x0;
        const uint MfGrayed = 0x1;
        const uint MfChecked = 0x8;
        const uint MfSeparator = 0x800;
        const uint TpmRightButton = 0x2;
        const uint TpmReturnCmd = 0x100;

        IReadOnlyList<TrayMenuItem> items = _menu();
        IntPtr menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                TrayMenuItem item = items[i];
                if (ReferenceEquals(item, TrayMenuItem.Separator))
                {
                    AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
                    continue;
                }

                uint flags = MfString | (item.Checked ? MfChecked : 0) | (item.Enabled ? 0 : MfGrayed);
                AppendMenuW(menu, flags, (UIntPtr)(uint)(i + 1), item.Text);
            }

            GetCursorPos(out Point point);
            SetForegroundWindow(_window.Handle);
            int chosen = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, _window.Handle, IntPtr.Zero);
            PostMessageW(_window.Handle, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL: иначе меню иногда не закрывается
            if (chosen > 0 && chosen <= items.Count)
                items[chosen - 1].Action?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
