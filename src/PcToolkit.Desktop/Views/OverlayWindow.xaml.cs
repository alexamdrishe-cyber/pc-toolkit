using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PcToolkit.Core.Overlay;
using PcToolkit.Desktop.ViewModels;

namespace PcToolkit.Desktop.Views;

/// <summary>
/// Оверлей — прозрачное окно поверх всех окон, сквозь которое проходят щелчки мыши. В игру ничего не внедряется.
/// Окно стоит на мониторе игры, на месте из настроек, и раз в секунду заново поднимается наверх:
/// некоторые игры сами встают поверх всех. Пока оверлей двигают, окно ловит мышь и его можно перетащить.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly OverlayViewModel _model;
    private readonly DispatcherTimer _timer;
    private IntPtr _handle;
    private IntPtr _gameWindow;
    private IntPtr _moveMonitor;
    private bool _dragging;
    private NativePoint _dragStart;
    private NativeRect _dragOrigin;

    public OverlayWindow(OverlayViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        View.Source = model.Content;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateShown();
        SizeChanged += (_, _) => Reposition();
        model.PropertyChanged += OnModelChanged;
        model.Content.LayoutChanged += OnLayoutChanged;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMoved;
        MouseLeftButtonUp += (_, _) => EndDrag();
        LostMouseCapture += (_, _) => EndDrag();
        MouseDoubleClick += (_, _) =>
        {
            if (_model.IsMoving)
                _model.FinishMoving();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        ApplyMoveMode();
    }

    protected override void OnClosed(EventArgs e)
    {
        _model.PropertyChanged -= OnModelChanged;
        _model.Content.LayoutChanged -= OnLayoutChanged;
        _timer.Stop();
        base.OnClosed(e);
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => Reposition();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.IsVisible):
            case nameof(OverlayViewModel.ShowOverBackgroundGame):
                UpdateShown();
                break;
            case nameof(OverlayViewModel.IsMoving):
                _moveMonitor = IntPtr.Zero;
                ApplyMoveMode();
                UpdateShown();
                break;
        }
    }

    /// <summary>Показать или спрятать окно; раз в секунду — заново (и снова поверх всех).</summary>
    private void UpdateShown()
    {
        if (_model.IsVisible || _model.IsMoving || _model.ShowOverBackgroundGame)
            _timer.Start();
        else
            _timer.Stop();

        if (WantShown())
        {
            if (!IsVisible)
                Show();
            Reposition();
        }
        else if (IsVisible)
        {
            EndDrag();
            Hide();
        }
    }

    /// <summary>
    /// Игра не на переднем плане, но открыты настройки оверлея: оверлей над игрой, только если она на другом мониторе,
    /// чем окно PC Toolkit, — иначе он висел бы поверх самих настроек.
    /// </summary>
    private bool WantShown()
    {
        if (_model.IsVisible || _model.IsMoving)
            return true;
        if (!_model.ShowOverBackgroundGame)
            return false;

        IntPtr game = GameWindow();
        return game != IntPtr.Zero
               && MonitorFromWindow(game, MonitorDefaultToNearest) != MonitorFromWindow(MainWindowHandle(), MonitorDefaultToNearest);
    }

    /// <summary>Пока оверлей двигают, окно ловит мышь (без WS_EX_TRANSPARENT) и показывает рамку; иначе щелчки уходят в игру.</summary>
    private void ApplyMoveMode()
    {
        bool moving = _model.IsMoving;
        View.ShowFrame = moving;
        IsHitTestVisible = moving;
        Cursor = moving ? Cursors.SizeAll : null;
        if (_handle == IntPtr.Zero)
            return;

        long style = GetWindowLongPtrW(_handle, GwlExStyle).ToInt64();
        style |= WsExToolWindow | WsExLayered | WsExNoActivate;
        style = moving ? style & ~WsExTransparent : style | WsExTransparent;
        SetWindowLongPtrW(_handle, GwlExStyle, new IntPtr(style));
    }

    /// <summary>Ставит окно на место из настроек на мониторе игры и поверх всех (координаты — пиксели экрана).</summary>
    private void Reposition()
    {
        if (_handle == IntPtr.Zero || !IsVisible || _dragging)
            return;

        IntPtr monitor;
        if (_model.IsMoving)
        {
            // Пока оверлей двигают — монитор, куда его поставили (сначала — монитор игры или окна PC Toolkit).
            if (_moveMonitor == IntPtr.Zero)
                _moveMonitor = MonitorFromWindow(GameWindow() is var game && game != IntPtr.Zero ? game : MainWindowHandle(), MonitorDefaultToNearest);
            monitor = _moveMonitor;
        }
        else
        {
            IntPtr game = GameWindow();
            monitor = MonitorFromWindow(game != IntPtr.Zero ? game : GetForegroundWindow(), MonitorDefaultToPrimary);
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(_handle, out NativeRect window))
            return;

        PixelRect area = info.Monitor.ToPixelRect();
        (int x, int y) = OverlayPlacement.Place(area, window.Right - window.Left, window.Bottom - window.Top, _model.Content.Layout.Position);
        SetWindowPos(_handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>
    /// Окно игры: запоминается, когда игра на переднем плане. Пока открыты настройки оверлея, игра может быть
    /// не на переднем плане (щёлкнули в PC Toolkit на другом мониторе) — оверлей остаётся над ней.
    /// </summary>
    private IntPtr GameWindow()
    {
        if (_model.TargetProcessId is not int target)
        {
            _gameWindow = IntPtr.Zero;
            return IntPtr.Zero;
        }

        IntPtr foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && ProcessOf(foreground) == target)
            _gameWindow = foreground;

        if (_gameWindow != IntPtr.Zero && (!IsWindow(_gameWindow) || IsIconic(_gameWindow) || ProcessOf(_gameWindow) != target))
            _gameWindow = IntPtr.Zero;
        if (_gameWindow == IntPtr.Zero && _model.ShowOverBackgroundGame)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(target);
                IntPtr main = process.MainWindowHandle;
                if (main != IntPtr.Zero && !IsIconic(main))
                    _gameWindow = main;
            }
            catch (Exception)
            {
                // процесс уже завершился или недоступен
            }
        }

        return _gameWindow;
    }

    private static IntPtr MainWindowHandle() =>
        Application.Current?.MainWindow is { } main ? new WindowInteropHelper(main).Handle : GetForegroundWindow();

    private static int ProcessOf(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint process);
        return (int)process;
    }

    // ----- Перетаскивание мышью -----

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_model.IsMoving || _handle == IntPtr.Zero || e.ClickCount > 1)
            return;
        if (!GetCursorPos(out _dragStart) || !GetWindowRect(_handle, out _dragOrigin))
            return;

        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMoved(object sender, MouseEventArgs e)
    {
        if (!_dragging || !GetCursorPos(out NativePoint point))
            return;

        int x = _dragOrigin.Left + point.X - _dragStart.X;
        int y = _dragOrigin.Top + point.Y - _dragStart.Y;
        SetWindowPos(_handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private void EndDrag()
    {
        if (!_dragging)
            return;

        _dragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();

        IntPtr monitor = MonitorFromWindow(_handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(_handle, out NativeRect window))
            return;

        _moveMonitor = monitor;
        _model.DropPosition(OverlayPlacement.FromWindow(info.Monitor.ToPixelRect(), window.ToPixelRect()));
    }

    // ----- Win32 -----

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
