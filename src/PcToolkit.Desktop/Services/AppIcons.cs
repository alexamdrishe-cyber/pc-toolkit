using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcToolkit.Core.Apps;

namespace PcToolkit.Desktop.Services;

/// <summary>Значки приложений: из exe/ico программы или логотип пакета Store. Готовые картинки заморожены — их можно создавать в фоне.</summary>
public static class AppIcons
{
    public static ImageSource? Load(InstalledApp app)
    {
        try
        {
            if (app.Kind == AppKind.Store)
                return app.LogoPath.Length > 0 && File.Exists(app.LogoPath) ? FromImageFile(app.LogoPath) : null;

            (string path, int index) = ParseIcon(app.DisplayIcon);
            if (path.Length == 0 && app.InstallLocation.Length > 3 && Directory.Exists(app.InstallLocation))
            {
                // Значка в записи нет — берём первый exe в папке программы, похожий по имени.
                path = Directory.GetFiles(app.InstallLocation, "*.exe")
                    .OrderBy(f => Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .FirstOrDefault() ?? "";
                index = 0;
            }

            if (path.Length == 0 || !File.Exists(path))
                return null;
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return FromImageFile(path);
            return FromExecutable(path, index);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Значок exe-файла (первый в файле) или null.</summary>
    public static ImageSource? LoadExecutable(string path)
    {
        try
        {
            return path.Length > 0 && File.Exists(path) ? FromExecutable(path, 0) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>«"C:\App\app.exe",0» → путь и номер значка.</summary>
    internal static (string Path, int Index) ParseIcon(string value)
    {
        string text = Environment.ExpandEnvironmentVariables(value.Trim());
        if (text.Length == 0)
            return ("", 0);

        int index = 0;
        int comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out int parsed))
        {
            index = parsed;
            text = text[..comma];
        }

        return (text.Trim().Trim('"').Trim(), index);
    }

    private static ImageSource? FromExecutable(string path, int index)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        uint count = ExtractIconEx(path, index, large, small, 1);
        try
        {
            IntPtr handle = large[0] != IntPtr.Zero ? large[0] : small[0];
            if (count == 0 || handle == IntPtr.Zero)
                return null;
            BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (large[0] != IntPtr.Zero)
                DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero)
                DestroyIcon(small[0]);
        }
    }

    private static ImageSource FromImageFile(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 32;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
