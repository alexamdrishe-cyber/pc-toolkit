using System.Windows.Media;
using PcToolkit.Core.Sensors;
using PcToolkit.Desktop.Controls;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Подпись линии графика: цвет, название и текущее значение.</summary>
public sealed class LegendItemViewModel : ObservableObject
{
    private string _valueText = "";

    public LegendItemViewModel(string label, Brush brush, bool dashed = false)
    {
        Label = label;
        Brush = brush;
        IsDashed = dashed;
    }

    public string Label { get; }

    public Brush Brush { get; }

    /// <summary>Пунктир — порог, а не датчик.</summary>
    public bool IsDashed { get; }

    public bool IsLine => !IsDashed;

    public string ValueText
    {
        get => _valueText;
        set => SetProperty(ref _valueText, value);
    }
}

/// <summary>Элемент колонки «Обзора»: заголовок группы, строка датчика, столбики по ядрам или пояснение.</summary>
public sealed class OverviewItemViewModel : ObservableObject
{
    private readonly PlanItem _plan;
    private string _valueText = "—";
    private string _rangeText = "";
    private double _barPercent;
    private IReadOnlyList<double> _bars = Array.Empty<double>();

    public OverviewItemViewModel(PlanItem plan, DeviceColors colors)
    {
        _plan = plan;
        Accent = colors.AccentBrush;
    }

    public string Text => _plan.Text;

    public bool IsLabel => _plan.Kind == PlanItemKind.Label;

    public bool IsRow => _plan.Kind == PlanItemKind.Row;

    public bool IsBars => _plan.Kind == PlanItemKind.Bars;

    public bool IsNote => _plan.Kind == PlanItemKind.Note;

    public bool HasBar => _plan.BarSensorId is not null;

    public Brush Accent { get; }

    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    public string RangeText
    {
        get => _rangeText;
        private set => SetProperty(ref _rangeText, value);
    }

    /// <summary>Полоска 0…100.</summary>
    public double BarPercent
    {
        get => _barPercent;
        private set => SetProperty(ref _barPercent, value);
    }

    /// <summary>Столбики: доли 0…1.</summary>
    public IReadOnlyList<double> Bars
    {
        get => _bars;
        private set => SetProperty(ref _bars, value);
    }

    public void Update(IReadOnlyDictionary<string, SensorReading> readings)
    {
        if (IsRow && readings.TryGetValue(_plan.SensorId, out SensorReading? reading))
        {
            ValueText = SensorText.Format(_plan.SensorKind, reading.Value, _plan.Style);
            RangeText = reading.Min is null || reading.Max is null
                ? ""
                : $"{SensorText.Bare(_plan.SensorKind, reading.Min, _plan.Style)}–{SensorText.Bare(_plan.SensorKind, reading.Max, _plan.Style)}";

            if (_plan.BarTotalId is { } totalId && readings.TryGetValue(totalId, out SensorReading? total) && total.Value > 0)
                BarPercent = Math.Clamp((reading.Value ?? 0) / total.Value.Value * 100, 0, 100);
            else if (_plan.BarSensorId is { } barId && readings.TryGetValue(barId, out SensorReading? bar))
                BarPercent = Math.Clamp(bar.Value ?? 0, 0, 100);
        }
        else if (IsBars)
        {
            var values = new List<double>(_plan.SensorIds.Count);
            double lo = double.MaxValue, hi = double.MinValue;
            double span = Math.Max(1e-9, _plan.BarsMax - _plan.BarsMin);
            foreach (string id in _plan.SensorIds)
            {
                double v = readings.TryGetValue(id, out SensorReading? r) && r.Value is float f ? f : double.NaN;
                values.Add(double.IsFinite(v) ? (v - _plan.BarsMin) / span : double.NaN);
                if (double.IsFinite(v))
                {
                    lo = Math.Min(lo, v);
                    hi = Math.Max(hi, v);
                }
            }

            Bars = values;
            RangeText = lo <= hi
                ? $"{SensorText.Bare(_plan.SensorKind, lo, _plan.Style)}–{SensorText.Format(_plan.SensorKind, hi, _plan.Style)}"
                : "—";
        }
    }
}

public sealed class OverviewDeviceViewModel
{
    public OverviewDeviceViewModel(PlanDevice plan)
    {
        Colors = DevicePalette.For(plan.Tag);
        Tag = plan.Tag;
        Name = plan.Name;
        Sub = plan.Sub;
        Items = plan.Items.Select(i => new OverviewItemViewModel(i, Colors)).ToList();
    }

    public DeviceColors Colors { get; }

    public string Tag { get; }

    public string Name { get; }

    public string Sub { get; }

    public IReadOnlyList<OverviewItemViewModel> Items { get; }
}

/// <summary>Колонка «Обзора»: устройства и график за 5 минут с подписанными шкалами.</summary>
public sealed class OverviewColumnViewModel : ObservableObject
{
    private const int WindowSeconds = 300;
    private const int MaxPoints = 600;
    private readonly IReadOnlyList<PlanSeries> _chart;
    private readonly IReadOnlyList<Color> _colors;
    private IReadOnlyList<ChartLine> _lines = Array.Empty<ChartLine>();
    private ChartRange _leftRange = new(0, 100);
    private ChartRange? _rightRange;

    public OverviewColumnViewModel(PlanColumn plan)
    {
        Devices = plan.Devices.Select(d => new OverviewDeviceViewModel(d)).ToList();
        _chart = plan.Chart;
        DeviceColors colors = Devices.Count > 0 ? Devices[0].Colors : DevicePalette.Board;
        _colors = new[] { colors.Accent, colors.Secondary };
        Legend = _chart.Select((s, i) => new LegendItemViewModel(
                s.Label + ", " + SensorText.Unit(s.Kind, s.Style) + (s.RightAxis ? " (шкала справа)" : ""),
                i == 0 ? colors.AccentBrush : colors.SecondaryBrush))
            .ToList();
        LeftUnit = _chart.Count > 0 ? SensorText.Unit(_chart[0].Kind, _chart[0].Style) : "";
        PlanSeries? right = _chart.FirstOrDefault(s => s.RightAxis);
        RightUnit = right is null ? "" : SensorText.Unit(right.Kind, right.Style);
        LeftLabelBrush = _chart.Any(s => s.RightAxis) ? colors.TagForeground : null;
        RightLabelBrush = right is null ? null : colors.SecondaryBrush;
    }

    public IReadOnlyList<OverviewDeviceViewModel> Devices { get; }

    public bool HasChart => _chart.Count > 0;

    public IReadOnlyList<LegendItemViewModel> Legend { get; }

    public string LeftUnit { get; }

    public string RightUnit { get; }

    public Brush? LeftLabelBrush { get; }

    public Brush? RightLabelBrush { get; }

    public IReadOnlyList<ChartLine> Lines
    {
        get => _lines;
        private set => SetProperty(ref _lines, value);
    }

    public ChartRange LeftRange
    {
        get => _leftRange;
        private set => SetProperty(ref _leftRange, value);
    }

    public ChartRange? RightRange
    {
        get => _rightRange;
        private set => SetProperty(ref _rightRange, value);
    }

    public void Update(IReadOnlyDictionary<string, SensorReading> readings, SensorHistory history)
    {
        foreach (OverviewDeviceViewModel device in Devices)
        {
            foreach (OverviewItemViewModel item in device.Items)
                item.Update(readings);
        }

        if (_chart.Count == 0)
            return;

        var lines = new List<ChartLine>();
        var leftValues = new List<double>();
        var rightValues = new List<double>();
        for (int i = 0; i < _chart.Count; i++)
        {
            PlanSeries series = _chart[i];
            HistoryWindow window = history.GetWindow(series.SensorId, WindowSeconds, MaxPoints);
            double[] values = window.Values.Select(v => SensorText.Scale(series.Kind, v, series.Style)).ToArray();
            (series.RightAxis ? rightValues : leftValues).AddRange(values);
            lines.Add(new ChartLine
            {
                Values = values,
                SampleSeconds = window.SampleSeconds,
                FirstIndex = window.FirstIndex,
                Color = _colors[Math.Min(i, _colors.Count - 1)],
                Fill = i == 0,
                RightAxis = series.RightAxis,
                Label = series.Label,
                Format = v => SensorText.Format(series.Kind, Unscale(series, v), series.Style),
            });

            if (readings.TryGetValue(series.SensorId, out SensorReading? reading))
                Legend[i].ValueText = SensorText.Format(series.Kind, reading.Value, series.Style);
        }

        PlanSeries first = _chart[0];
        LeftRange = ChartScale.For(first.Kind, leftValues);
        PlanSeries? right = _chart.FirstOrDefault(s => s.RightAxis);
        RightRange = right is null ? null : ChartScale.For(right.Kind, rightValues);
        Lines = lines;
    }

    /// <summary>Обратно из единиц шкалы в единицы датчика — для подсказки на графике.</summary>
    internal static double Unscale(PlanSeries series, double value) => series.Style switch
    {
        ValueStyle.Gigahertz => value * 1000,
        ValueStyle.GigabytesFromMegabytes => value * 1024,
        ValueStyle.Megabits => value * 1_000_000 / 8,
        _ => series.Kind == SensorKind.Throughput ? value * 1024 * 1024 : value,
    };
}

/// <summary>Вид «Обзор»: плитки устройств сверху и три колонки главных датчиков.</summary>
public sealed class MonitorOverviewViewModel : ObservableObject
{
    private IReadOnlyList<OverviewColumnViewModel> _columns = Array.Empty<OverviewColumnViewModel>();

    public DeviceTiles Tiles { get; } = new();

    public IReadOnlyList<OverviewColumnViewModel> Columns
    {
        get => _columns;
        private set => SetProperty(ref _columns, value);
    }

    public void Rebuild(HardwareSnapshot snapshot) =>
        Columns = MonitorPlan.Overview(snapshot).Select(c => new OverviewColumnViewModel(c)).ToList();

    public void Update(HardwareSnapshot snapshot, IReadOnlyDictionary<string, SensorReading> readings, SensorHistory history, bool hasFullSensors)
    {
        Tiles.Update(snapshot, history, hasFullSensors);
        foreach (OverviewColumnViewModel column in _columns)
            column.Update(readings, history);
    }
}
