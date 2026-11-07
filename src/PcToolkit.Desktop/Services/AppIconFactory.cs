using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcToolkit.Desktop.Services;

/// <summary>
/// Значок приложения рисуется кодом (как логотип в боковом меню): синий квадрат со скруглёнными углами
/// и белым знаком. Используется для окна, панели задач и трея — отдельный .ico не нужен.
/// </summary>
public static class AppIconFactory
{
    private const string Glyph = "";

    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
            double radius = size * 0.22;
            dc.DrawRoundedRectangle(accent, null, new Rect(0, 0, size, size), radius, radius);

            var typeface = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var text = new FormattedText(Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size * 0.58, Brushes.White, 1.0);
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Значок Windows (HICON) для трея. Освобождать через DestroyIcon.</summary>
    public static IntPtr CreateHIcon(int size)
    {
        BitmapSource bitmap = Render(size);
        int stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);

        // RenderTargetBitmap даёт цвета, умноженные на прозрачность; значку нужны обычные.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte alpha = pixels[i + 3];
            if (alpha is > 0 and < 255)
            {
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
            }
        }

        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size,
            Height = -size, // строки сверху вниз
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };

        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero)
            return IntPtr.Zero;

        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var maskBits = new byte[(size + 15) / 16 * 2 * size];
        IntPtr mask = CreateBitmap(size, size, 1, 1, maskBits);
        try
        {
            var info = new IconInfo { IsIcon = true, Mask = mask, Color = color };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);
}
