using System.Runtime.CompilerServices;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Tests;

internal static class TestSetup
{
    /// <summary>Тесты не пишут в журнал приложения пользователя (%LocalAppData%\PcToolkit\logs).</summary>
#pragma warning disable CA2255 // Инициализатор модуля в тестовой сборке — осознанно.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void DisableAppLog() => AppLog.Enabled = false;
}
