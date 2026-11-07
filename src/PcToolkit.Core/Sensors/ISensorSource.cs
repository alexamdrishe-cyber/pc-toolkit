namespace PcToolkit.Core.Sensors;

public enum SensorSourceState
{
    Stopped,
    Starting,
    Running,
    Failed,
}

/// <summary>
/// Источник данных датчиков.
/// Сейчас данные читаются прямо в приложении (<see cref="LhmSensorSource"/>).
/// На этапе 2 появится реализация, которая получает снимки от службы Windows,
/// а интерфейс останется прежним.
/// </summary>
public interface ISensorSource : IDisposable
{
    /// <summary>Новый снимок. Вызывается в фоновом потоке.</summary>
    event EventHandler<HardwareSnapshot>? SnapshotReady;

    /// <summary>Изменилось состояние источника. Вызывается в фоновом потоке.</summary>
    event EventHandler? StateChanged;

    SensorSourceState State { get; }

    string? LastError { get; }

    /// <summary>Как часто приходят снимки. Можно менять на ходу: новое значение действует со следующего снимка.</summary>
    TimeSpan Interval { get; set; }

    void Start();
}
