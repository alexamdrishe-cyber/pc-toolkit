using System.Windows.Media;
using PcToolkit.Core.Games;
using PcToolkit.Core.Overlay;
using PcToolkit.Core.Sensors;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Какой график у ячейки.</summary>
public enum OverlayGraphKind
{
    None,

    /// <summary>Значения показателя за последние секунды (каждое новое — справа).</summary>
    Series,

    /// <summary>Время отдельных кадров: ровная линия — плавно, пики — рывки.</summary>
    Frames,
}

/// <summary>Одна ячейка оверлея, готовая к отрисовке: подпись, значение, единица, цвет и график.</summary>
public sealed record OverlayCell(
    string Label,
    string Value,
    string Unit,
    Color ValueColor,
    Color GraphColor,
    bool Big,
    bool Muted,
    bool SameLine,
    OverlayGraphKind Graph,
    double[] GraphValues,
    int GraphSlots,
    double? GraphMin,
    double? GraphMax);

/// <summary>
/// Что сейчас показывает оверлей: его настройки (<see cref="OverlayLayout"/>) и последние данные — кадры, датчики,
/// история значений для графиков. Один и тот же класс рисует оверлей в игре и предпросмотр в настройках.
/// </summary>
public sealed class OverlayContent
{
    private const int HistoryLength = 90;

    private readonly OverlaySources _sources;
    private readonly OverlayHistory _history = new(HistoryLength);
    private OverlayLayout _layout;
    private double[] _frameTimes = Array.Empty<double>();

    /// <param name="sample">Предпросмотр: пока игра не выводит кадры, вместо FPS показывается пример.</param>
    public OverlayContent(OverlayLayout layout, bool sample)
    {
        _layout = layout;
        _sources = new OverlaySources { Sample = sample };
        if (sample)
            _frameTimes = OverlayMetrics.SampleFrameTimes(DateTime.Now);
    }

    public OverlayLayout Layout => _layout;

    /// <summary>Пока игра не выводит кадры, показывать пример FPS (предпросмотр; оверлей, который двигают мышью).</summary>
    public bool Sample
    {
        get => _sources.Sample;
        set
        {
            if (_sources.Sample == value)
                return;
            _sources.Sample = value;
            _sources.Now = DateTime.Now;
            _frameTimes = _sources.UseSample ? OverlayMetrics.SampleFrameTimes(_sources.Now) : _sources.Frames?.RecentFrameTimes ?? Array.Empty<double>();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Номер версии настроек: меняется при каждом <see cref="SetLayout"/>.</summary>
    public int LayoutVersion { get; private set; }

    public bool IsRecording { get; private set; }

    public string RecordingText { get; private set; } = "";

    /// <summary>Последний снимок датчиков (для списка «Добавить показатель»).</summary>
    public HardwareSnapshot? Snapshot => _sources.Snapshot;

    /// <summary>Новые значения — перерисовать.</summary>
    public event EventHandler? Changed;

    /// <summary>Настройки другие или изменились (оформление, место, показатели).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Показать другие настройки или те же после изменения — оверлей перерисуется сразу.</summary>
    public void SetLayout(OverlayLayout layout)
    {
        _layout = layout;
        LayoutVersion++;
        _history.Keep(layout.Items.Select(item => item.Metric).ToHashSet());
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateFrames(FrameMonitorState state)
    {
        _sources.Frames = state;
        _sources.Now = DateTime.Now;
        if (_sources.UseSample)
            return; // пример обновляет Tick

        _frameTimes = state.RecentFrameTimes;
        PushHistory(frames: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateSensors(HardwareSnapshot snapshot, KeyMetrics metrics)
    {
        _sources.Snapshot = snapshot;
        _sources.Metrics = metrics;
        _sources.Now = DateTime.Now;
        PushHistory(frames: false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetRecording(bool recording, string text)
    {
        if (IsRecording == recording && RecordingText == text)
            return;
        IsRecording = recording;
        RecordingText = text;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Предпросмотр без игры: пример FPS и графика кадров «живёт» (вызывается 4 раза в секунду).</summary>
    public void Tick()
    {
        if (!_sources.UseSample)
            return;

        _sources.Now = DateTime.Now;
        _frameTimes = OverlayMetrics.SampleFrameTimes(_sources.Now);
        PushHistory(frames: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ячейки по порядку: только видимые показатели.</summary>
    public IReadOnlyList<OverlayCell> Cells()
    {
        OverlayStyle style = _layout.Style;
        Color valueColor = ToColor(style.ValueColor);
        Color labelColor = ToColor(style.LabelColor);
        var cells = new List<OverlayCell>(_layout.Items.Count);
        string? previousLabel = null;

        foreach (OverlayItem item in _layout.Items)
        {
            if (!item.Visible)
                continue;

            OverlayMetric metric = OverlayMetrics.Find(item.Metric, _sources.Snapshot) ?? OverlayMetrics.Missing(item);
            OverlayReading reading = OverlayMetrics.Read(metric, _sources);
            bool sameLine = item.SameLine && cells.Count > 0;
            string label = (item.Label ?? metric.Label).Trim();
            // Одинаковая подпись подряд в одной строке не повторяется: «CPU 45 % 62 °C».
            string shown = sameLine && label == previousLabel ? "" : label;
            previousLabel = label;

            OverlayLevel level = reading.Value is double value
                ? OverlayMetrics.Level(value, item.Warn, item.Critical, metric.LowerIsWorse)
                : OverlayLevel.Normal;
            Color own = item.Color is { } color ? ToColor(color) : metric.Muted ? labelColor : valueColor;
            Color levelColor = level switch
            {
                OverlayLevel.Critical => ToColor(style.CriticalColor),
                OverlayLevel.Warning => ToColor(style.WarnColor),
                _ => own,
            };
            Color graphColor = level != OverlayLevel.Normal ? levelColor
                : item.Color is { } graph ? ToColor(graph) : ToColor(style.GraphColor);

            OverlayGraphKind kind = !item.Graph || !metric.CanGraph ? OverlayGraphKind.None
                : metric.FrameGraph ? OverlayGraphKind.Frames : OverlayGraphKind.Series;
            double[] values = kind switch
            {
                OverlayGraphKind.Frames => _frameTimes,
                OverlayGraphKind.Series => _history.Values(item.Metric),
                _ => Array.Empty<double>(),
            };
            (double? min, double? max) = kind == OverlayGraphKind.Series ? OverlayMetrics.GraphRange(metric, _sources) : (null, null);

            cells.Add(new OverlayCell(shown, reading.Text, reading.Unit, levelColor, graphColor, item.Big, metric.Muted, sameLine,
                kind, values, kind == OverlayGraphKind.Series ? HistoryLength : values.Length, min, max));
        }

        return cells;
    }

    public static Color ToColor(string hex)
    {
        (byte r, byte g, byte b) = OverlayStyle.Rgb(hex);
        return Color.FromRgb(r, g, b);
    }

    private void PushHistory(bool frames)
    {
        foreach (string key in _layout.Items.Where(item => item.Visible).Select(item => item.Metric).Distinct())
        {
            OverlayMetric? metric = OverlayMetrics.Find(key, _sources.Snapshot);
            if (metric is null || !metric.CanGraph || metric.FrameGraph || metric.FromFrames != frames)
                continue;
            _history.Push(key, OverlayMetrics.Value(metric, _sources));
        }
    }
}
