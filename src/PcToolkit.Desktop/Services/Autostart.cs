using Microsoft.Win32;
using PcToolkit.Core.Logging;

namespace PcToolkit.Desktop.Services;

/// <summary>
/// Запуск вместе с Windows: запись в HKCU\...\Run (только для текущего пользователя, без прав администратора).
/// Приложение стартует свёрнутым в трей — чтобы профили игр и оверлей работали без открытия окна.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PC Toolkit";

    private static string Command => $"\"{Environment.ProcessPath}\" {LaunchArguments.Tray}";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Запись указывает на этот exe (после переноса папки приложения её нужно обновить).</summary>
    public static bool PointsHere
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
                key.SetValue(ValueName, Command, RegistryValueKind.String);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            AppLog.Info(enabled ? $"Автозапуск включён: {Command}." : "Автозапуск выключен.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось изменить автозапуск.", ex);
            return false;
        }
    }
}
