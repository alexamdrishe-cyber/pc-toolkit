using System.Diagnostics;

namespace PcToolkit.Core.Fans;

/// <summary>
/// Программы, которые тоже умеют управлять вентиляторами. Если в такой программе включено управление, она и
/// PC Toolkit перебивают друг друга: обороты скачут, а кривые не работают. Ищем по именам запущенных процессов.
/// </summary>
public static class FanConflicts
{
    /// <summary>Имя процесса (без .exe) → название программы для человека.</summary>
    private static readonly (string Process, string Name)[] Known =
    {
        ("FanControl", "FanControl"),
        ("MSIAfterburner", "MSI Afterburner"),
        ("SpeedFan", "SpeedFan"),
        ("ArgusMonitor", "Argus Monitor"),
        ("HWiNFO64", "HWiNFO"),
        ("HWiNFO32", "HWiNFO"),
        ("GPUTweakII", "ASUS GPU Tweak"),
        ("GPUTweakIII", "ASUS GPU Tweak"),
        ("PrecisionX_x64", "EVGA Precision X1"),
        ("TriXX", "Sapphire TriXX"),
        ("NZXT CAM", "NZXT CAM"),
        ("iCUE", "Corsair iCUE"),
    };

    /// <summary>Названия программ из списка, чьи процессы есть среди этих (без повторов, в порядке списка).</summary>
    public static IReadOnlyList<string> Find(IEnumerable<string> processNames)
    {
        var running = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        return Known.Where(k => running.Contains(k.Process)).Select(k => k.Name).Distinct().ToList();
    }

    /// <summary>Запущенные сейчас программы из списка. Ошибка перечисления процессов — пустой список.</summary>
    public static IReadOnlyList<string> Running()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>(processes.Length);
        foreach (Process process in processes)
        {
            try
            {
                names.Add(process.ProcessName);
            }
            catch (InvalidOperationException)
            {
                // процесс уже завершился
            }
            finally
            {
                process.Dispose();
            }
        }

        return Find(names);
    }
}
