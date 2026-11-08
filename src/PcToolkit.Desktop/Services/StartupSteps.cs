using System.Diagnostics;
using PcToolkit.Core.Logging;

namespace PcToolkit.Desktop.Services;

/// <summary>
/// Замер этапов запуска. В журнал попадают только медленные этапы (от 250 мс): если окно открывается долго,
/// по журналу видно, что именно тормозило.
/// </summary>
internal sealed class StartupSteps
{
    private const long SlowMs = 250;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private long _last;

    public void Done(string step)
    {
        long now = _watch.ElapsedMilliseconds;
        if (now - _last >= SlowMs)
            AppLog.Info($"Запуск: «{step}» — {now - _last} мс.");
        _last = now;
    }
}
