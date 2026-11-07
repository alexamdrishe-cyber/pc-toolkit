using System.Windows.Input;
using System.Windows.Media;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Settings;
using PcToolkit.Desktop.Controls;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Карточка вида «Графики»: крупное значение, легенда с текущими значениями, график со шкалами и порогом.</summary>
public sealed class GraphCardViewModel : ObservableObject
{
    /// <summary>Точек на линию: карточка шириной в несколько сотен пикселей, а за 30 минут снимков — до 7200.</summary>
    private const int MaxPoints = 600;

    private readonly GraphPlan _plan;
    private readonly Color[] _colors;
    private string _valueText = "—";
    private string _unitText = "";
    private string _rangeText = "";
    private IReadOnlyList<ChartLine> _lines = Array.Empty<ChartLine>();
    private ChartRange _range = new(0, 100);

    public GraphCardViewModel(GraphPlan plan)
    {
        _plan = plan;
        Colors = DevicePalette.For(plan.Tag);
        _colors = new[] { Colors.Accent, Colors.Secondary, DevicePalette.Ram.Accent };
        Legend = plan.Series.Select((s, i) => new LegendItemViewModel(s.Label, MakeBrush(_colors[Math.Min(i, _colors.Length - 1)]))).ToList();
        if (plan.Limit is not null)
            LimitLegend = new LegendItemViewModel(plan.LimitLabel, (Brush)System.Windows.Application.Current.FindResource("CriticalBrush"), dashed: true);
        Unit = plan.Series.Count > 0 ? SensorText.Unit(plan.Series[0].Kind, plan.Series[0].Style) : "";
    }

    public string Title => _plan.Title;

    public DeviceColors Colors { get; }

    public IReadOnlyList<LegendItemViewModel> Legend { get; }

    public LegendItemViewModel? LimitLegend { get; }

    public bool HasLimit => LimitLegend is not null;

    public double Limit => _plan.Limit ?? double.NaN;

    public string Unit { get; }

    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    public string UnitText
    {
        get => _unitText;
        private set => SetProperty(ref _unitText, value);
    }

    /// <summary>«мин 38,5 · макс 78,3» за окно графика.</summary>
    public string RangeText
    {
        get => _rangeText;
        private set => SetProperty(ref _rangeText, value);
    }

    public IReadOnlyList<ChartLine> Lines
    {
        get => _lines;
        private set => SetProperty(ref _lines, value);
    }

    public ChartRange Range
    {
        get => _range;
        private set => SetProperty(ref _range, value);
    }

    public void Update(IReadOnlyDictionary<string, SensorReading> readings, SensorHistory history, int windowSeconds)
    {
        if (_plan.Series.Count == 0)
            return;

        var lines = new List<ChartLine>();
        var all = new List<double>();
        for (int i = 0; i < _plan.Series.Count; i++)
        {
            PlanSeries series = _plan.Series[i];
            HistoryWindow window = history.GetWindow(series.SensorId, windowSeconds, MaxPoints);
            double[] values = window.Values
                .Select(v => SensorText.Scale(series.Kind, v, series.Style))
                .ToArray();
            all.AddRange(values);
            lines.Add(new ChartLine
            {
                Values = values,
                SampleSeconds = window.SampleSeconds,
                FirstIndex = window.FirstIndex,
                Color = _colors[Math.Min(i, _colors.Length - 1)],
                Fill = i == 0,
                Thickness = i == 0 ? 1.8 : 1.4,
                Label = series.Label,
                Format = v => SensorText.Format(series.Kind, OverviewColumnViewModel.Unscale(series, v), series.Style),
            });

            if (readings.TryGetValue(series.SensorId, out SensorReading? reading))
            {
                string text = SensorText.Format(series.Kind, reading.Value, series.Style);
                Legend[i].ValueText = text;
                if (i == 0)
                {
                    int space = text.LastIndexOf(' ');
                    ValueText = space > 0 ? text[..space] : text;
                    UnitText = space > 0 ? text[(space + 1)..] : "";
                }
            }
        }

        PlanSeries first = _plan.Series[0];
        Range = ChartScale.For(first.Kind, all, _plan.Limit, _plan.FixedMax);
        // Минимум и максимум — по всем снимкам, а не по усреднённым точкам линии: короткий пик не теряется.
        double[] raw = history.GetWindow(first.SensorId, windowSeconds).Values;
        RangeText = raw.Length > 0
            ? $"мин {SensorText.Bare(first.Kind, raw.Min(), first.Style)} · " +
              $"макс {SensorText.Bare(first.Kind, raw.Max(), first.Style)}"
            : "";
        Lines = lines;
    }

    private static Brush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Вид «Графики»: главные показатели во времени, окно 1, 5 или 30 минут.</summary>
public sealed class MonitorGraphsViewModel : ObservableObject
{
    private IReadOnlyList<GraphCardViewModel> _cards = Array.Empty<GraphCardViewModel>();
    private int _windowSeconds;

    public MonitorGraphsViewModel()
    {
        int saved = AppSettings.Load().GraphWindowSeconds;
        _windowSeconds = saved is 60 or 300 or 1800 ? saved : 300;
        SetWindowCommand = new RelayCommand(p =>
        {
            if (p is string text && int.TryParse(text, out int seconds))
                WindowSeconds = seconds;
        });
    }

    public ICommand SetWindowCommand { get; }

    public IReadOnlyList<GraphCardViewModel> Cards
    {
        get => _cards;
        private set => SetProperty(ref _cards, value);
    }

    public int WindowSeconds
    {
        get => _windowSeconds;
        set
        {
            if (!SetProperty(ref _windowSeconds, value))
                return;
            AppSettings.Update(s => s.GraphWindowSeconds = value);
            OnPropertyChanged(nameof(Is1Min));
            OnPropertyChanged(nameof(Is5Min));
            OnPropertyChanged(nameof(Is30Min));
        }
    }

    public bool Is1Min { get => _windowSeconds == 60; set { if (value) WindowSeconds = 60; } }

    public bool Is5Min { get => _windowSeconds == 300; set { if (value) WindowSeconds = 300; } }

    public bool Is30Min { get => _windowSeconds == 1800; set { if (value) WindowSeconds = 1800; } }

    public void Rebuild(HardwareSnapshot snapshot) =>
        Cards = MonitorPlan.Graphs(snapshot).Select(g => new GraphCardViewModel(g)).ToList();

    public void Update(IReadOnlyDictionary<string, SensorReading> readings, SensorHistory history)
    {
        foreach (GraphCardViewModel card in _cards)
            card.Update(readings, history, _windowSeconds);
    }
}
