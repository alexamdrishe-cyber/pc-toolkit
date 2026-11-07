using System.ServiceProcess;
using PcToolkit.Core.Logging;
using PcToolkit.Service;

string logDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcToolkit", "logs");
AppLog.UseFile(logDirectory, "service.log");

if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
{
    // Отладка без установки: запустить из консоли с правами администратора.
    using var host = new ServiceHost();
    host.Start();
    Console.WriteLine("Служба PC Toolkit работает в консоли. Нажмите Enter для остановки.");
    Console.ReadLine();
    return;
}

ServiceBase.Run(new ToolkitService());
