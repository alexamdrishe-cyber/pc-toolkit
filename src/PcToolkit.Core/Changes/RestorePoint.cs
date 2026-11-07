using System.Management;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Changes;

/// <summary>
/// Точка восстановления Windows: дополнительная страховка поверх собственного журнала приложения.
/// Нужны права администратора и включённая защита системы. Windows создаёт не больше одной точки
/// в сутки: если точка уже есть, новая молча не создаётся, и это нормально.
/// </summary>
public static class RestorePoint
{
    private const uint ModifySettings = 12;
    private const uint BeginSystemChange = 100;

    public static (bool Success, string Message) Create(string description)
    {
        try
        {
            using var restore = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using ManagementBaseObject parameters = restore.GetMethodParameters("CreateRestorePoint");
            parameters["Description"] = description;
            parameters["RestorePointType"] = ModifySettings;
            parameters["EventType"] = BeginSystemChange;

            using ManagementBaseObject result = restore.InvokeMethod("CreateRestorePoint", parameters, null);
            uint code = Convert.ToUInt32(result["ReturnValue"]);
            if (code == 0)
            {
                AppLog.Info($"Точка восстановления Windows создана: {description}.");
                return (true, "Точка восстановления Windows создана.");
            }

            AppLog.Warn($"Точка восстановления не создана, код {code}.");
            return (false, code == 1058
                ? "Точка восстановления не создана: защита системы отключена."
                : $"Точка восстановления не создана (код {code}).");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось создать точку восстановления Windows.", ex);
            return (false, $"Точка восстановления не создана: {ex.Message}");
        }
    }
}
