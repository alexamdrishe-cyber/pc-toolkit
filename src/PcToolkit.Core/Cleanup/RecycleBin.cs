using System.Runtime.InteropServices;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Cleanup;

/// <summary>Корзина Windows через системные функции оболочки.</summary>
public static class RecycleBin
{
    private const uint NoConfirmation = 0x1;
    private const uint NoProgressUi = 0x2;
    private const uint NoSound = 0x4;
    private const int EUnexpected = unchecked((int)0x8000FFFF);

    /// <summary>Размер и число элементов корзины на всех дисках.</summary>
    public static (long Size, long Count) Query()
    {
        var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
        int hr = SHQueryRecycleBinW(null, ref info);
        if (hr != 0)
            throw new InvalidOperationException($"Не удалось прочитать состояние корзины (код 0x{hr:X8}).");

        return (info.i64Size, info.i64NumItems);
    }

    /// <summary>Очищает корзину без окна подтверждения. Пустая корзина — не ошибка.</summary>
    public static bool Empty()
    {
        int hr = SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        if (hr == 0 || hr == EUnexpected)
            return true;

        AppLog.Warn($"Очистка: корзину очистить не удалось (код 0x{hr:X8}).");
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);
}
