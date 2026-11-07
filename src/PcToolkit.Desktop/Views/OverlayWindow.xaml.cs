using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PcToolkit.Desktop.ViewModels;

namespace PcToolkit.Desktop.Views;

/// <summary>
/// Оверлей — прозрачное окно поверх всех окон, сквозь которое проходят щелчки мыши.
/// В игру ничего не внедряется. Окно держится на мониторе, где сейчас активное окно (игра),
/// в выбранном углу, и раз в секунду заново поднимается наверх: некоторые игры сами встают поверх всех.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly OverlayViewModel _model;
    private readonly DispatcherTimer _timer;
    private IntPtr _handle;

    public OverlayWindow(OverlayViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
        model.PropertyChanged += OnModelChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        long style = GetWindowLongPtrW(_handle, GwlExStyle).ToInt64();
        style |= WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
        SetWindowLongPtrW(_handle, GwlExStyle, new IntPtr(style));
    }

    protected override void OnClosed(EventArgs e)
    {
        _model.PropertyChanged -= OnModelChanged;
        _timer.Stop();
        base.OnClosed(e);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.IsVisible))
        {
            if (_model.IsVisible)
            {
                Show();
                _timer.Start();
                Reposition();
            }
            else
            {
                _timer.Stop();
                Hide();
            }
        }
        else if (e.PropertyName == nameof(OverlayViewModel.Corner))
        {
            Reposition();
        }
    }

    /// <summary>Ставит окно в угол монитора, на котором активное окно, и поверх всех (координаты в пикселях экрана).</summary>
    private void Reposition()
    {
        if (_handle == IntPtr.Zero || !IsVisible)
            return;

        IntPtr foreground = GetForegroundWindow();
        IntPtr monitor = MonitorFromWindow(foreground != IntPtr.Zero ? foreground : _handle, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(_handle, out Rect window))
            return;

        int width = window.Right - window.Left;
        int height = window.Bottom - window.Top;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int margin = (int)Math.Round(14 * scale);
        Rect area = info.Monitor;
        int x = _model.Corner.EndsWith("Right", StringComparison.Ordinal) ? area.Right - width - margin : area.Left + margin;
        int y = _model.Corner.StartsWith("Bottom", StringComparison.Ordinal) ? area.Bottom - height - margin : area.Top + margin;

        const uint SwpNoSize = 0x0001;
        const uint SwpNoActivate = 0x0010;
        const uint SwpNoOwnerZOrder = 0x0200;
        SetWindowPos(_handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
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
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
