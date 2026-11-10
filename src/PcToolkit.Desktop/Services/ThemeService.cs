using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Settings;

namespace PcToolkit.Desktop.Services;

/// <summary>Тема оформления: тёмная, светлая или как в Windows.</summary>
public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>
/// Переключает палитру на лету: стили ссылаются на цвета через DynamicResource, поэтому достаточно заменить
/// словарь Colors.*.xaml (он первый в ресурсах приложения). Графики, которые рисуются кодом, перерисовываются,
/// заголовки окон перекрашиваются. «Как в Windows» следит за настройкой «Режим приложений».
/// </summary>
public static class ThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private static bool _applied;
    private static bool _watching;

    public static AppTheme Choice { get; private set; } = AppTheme.Dark;

    public static bool IsLight { get; private set; }

    /// <summary>После смены палитры (в потоке интерфейса).</summary>
    public static event Action? Changed;

    public static void Initialize(string? saved)
    {
        Choice = Parse(saved);
        Apply();
        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (Choice == AppTheme.System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                    Application.Current?.Dispatcher.BeginInvoke(Apply);
            };
        }
    }

    public static void Set(AppTheme theme)
    {
        if (theme == Choice)
            return;

        Choice = theme;
        AppSettings.Update(s => s.Theme = Name(theme));
        AppLog.Info($"Тема оформления: {Describe(theme)}.");
        Apply();
    }

    public static AppTheme Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "light" => AppTheme.Light,
        "system" => AppTheme.System,
        _ => AppTheme.Dark,
    };

    public static string Name(AppTheme theme) => theme switch
    {
        AppTheme.Light => "light",
        AppTheme.System => "system",
        _ => "dark",
    };

    private static string Describe(AppTheme theme) => theme switch
    {
        AppTheme.Light => "светлая",
        AppTheme.System => "как в Windows",
        _ => "тёмная",
    };

    /// <summary>Тёмный или светлый заголовок окна — под тему (Windows 10 2004+ и Windows 11).</summary>
    public static void StyleTitleBar(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;
            int dark = IsLight ? 0 : 1;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // старая Windows: обычный заголовок
        }
    }

    /// <summary>Цвет из текущей палитры (для того, что рисуется кодом).</summary>
    public static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    public static Color Color(string key, Color fallback) => (Brush(key, fallback) as SolidColorBrush)?.Color ?? fallback;

    private static void Apply()
    {
        Application? app = Application.Current;
        if (app is null)
            return;

        bool light = Choice == AppTheme.Light || (Choice == AppTheme.System && SystemUsesLightTheme());
        if (_applied && light == IsLight)
            return;

        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/Colors.{(light ? "Light" : "Dark")}.xaml", UriKind.Absolute),
        };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0 && merged[0].Source?.OriginalString.Contains("/Themes/Colors.", StringComparison.Ordinal) == true)
            merged[0] = palette;
        else
            merged.Insert(0, palette);

        IsLight = light;
        _applied = true;
        foreach (Window window in app.Windows)
        {
            StyleTitleBar(window);
            Redraw(window);
        }

        Changed?.Invoke();
    }

    /// <summary>Графики и кривые рисуются кодом и берут цвета при отрисовке — перерисовать их.</summary>
    private static void Redraw(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement element && child.GetType().Namespace == "PcToolkit.Desktop.Controls")
                element.InvalidateVisual();
            Redraw(child);
        }
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
