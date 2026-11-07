using System.Collections.ObjectModel;
using System.Windows.Media;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Settings;
using PcToolkit.Desktop.Controls;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>
/// Вид «Все датчики»: таблица на всю ширину (сейчас, мин, среднее, макс, график за 5 минут),
/// фильтры по устройствам и типам, скрытие пустых и служебных датчиков и общий график
/// для закреплённых датчиков (до четырёх).
/// </summary>
public sealed class SensorTableViewModel : ObservableObject
{
    public const int MaxPins = 4;

    private readonly Dictionary<string, SensorRowViewModel> _rows = new();
    private readonly List<(TableDeviceHeader Header, List<SensorRowViewModel> Rows)> _devices = new();
    private readonly List<string> _pinnedIds;
    private IReadOnlyList<object> _items = Array.Empty<object>();
    private SensorHistory? _history;
    private string _searchText = "";
    private bool _hideEmpty = true;
    private bool _onlyPinned;
    private bool _defaultPinsDone;
    private int _hiddenSignature;
    private string _shownText = "";
    private IReadOnlyList<ChartLine> _pinLines = Array.Empty<ChartLine>();
    private ChartRange _pinLeft = new(0, 100);
    private ChartRange? _pinRight;
    private string _pinLeftUnit = "";
    private string _pinRightUnit = "";
    private string _chartHint = "";

    public SensorTableViewModel()
    {
        _pinnedIds = AppSettings.Load().MonitorPins.Take(MaxPins).ToList();
        _defaultPinsDone = _pinnedIds.Count > 0;
        KindFilters = new[]
        {
            new KindFilterViewModel("Температура", new[] { SensorKind.Temperature }, ApplyFilter),
            new KindFilterViewModel("Загрузка", new[] { SensorKind.Load }, ApplyFilter),
            new KindFilterViewModel("Частота", new[] { SensorKind.Clock, SensorKind.Frequency }, ApplyFilter),
            new KindFilterViewModel("Мощность", new[] { SensorKind.Power, SensorKind.Current, SensorKind.Energy }, ApplyFilter),
            new KindFilterViewModel("Напряжение", new[] { SensorKind.Voltage }, ApplyFilter),
            new KindFilterViewModel("Вентиляторы", new[] { SensorKind.Fan, SensorKind.Control }, ApplyFilter),
            new KindFilterViewModel("Данные и прочее", Enum.GetValues<SensorKind>()
                .Except(new[]
                {
                    SensorKind.Temperature, SensorKind.Load, SensorKind.Clock, SensorKind.Frequency, SensorKind.Power,
                    SensorKind.Current, SensorKind.Energy, SensorKind.Voltage, SensorKind.Fan, SensorKind.Control,
                })
                .ToArray(), ApplyFilter),
        };
    }

    public ObservableCollection<DeviceFilterViewModel> DeviceFilters { get; } = new();

    public IReadOnlyList<KindFilterViewModel> KindFilters { get; }

    /// <summary>Строки таблицы вперемешку с заголовками устройств и групп.</summary>
    public IReadOnlyList<object> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    public ObservableCollection<SensorRowViewModel> PinnedRows { get; } = new();

    public bool HasPins => PinnedRows.Count > 0;

    public string ShownText
    {
        get => _shownText;
        private set => SetProperty(ref _shownText, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
                ApplyFilter();
        }
    }

    public bool HideEmpty
    {
        get => _hideEmpty;
        set
        {
            if (SetProperty(ref _hideEmpty, value))
                ApplyFilter();
        }
    }

    public bool OnlyPinned
    {
        get => _onlyPinned;
        set
        {
            if (SetProperty(ref _onlyPinned, value))
                ApplyFilter();
        }
    }

    public IReadOnlyList<ChartLine> PinLines
    {
        get => _pinLines;
        private set => SetProperty(ref _pinLines, value);
    }

    public ChartRange PinLeftRange
    {
        get => _pinLeft;
        private set => SetProperty(ref _pinLeft, value);
    }

    public ChartRange? PinRightRange
    {
        get => _pinRight;
        private set => SetProperty(ref _pinRight, value);
    }

    public string PinLeftUnit
    {
        get => _pinLeftUnit;
        private set => SetProperty(ref _pinLeftUnit, value);
    }

    public string PinRightUnit
    {
        get => _pinRightUnit;
        private set => SetProperty(ref _pinRightUnit, value);
    }

    public string ChartHint
    {
        get => _chartHint;
        private set => SetProperty(ref _chartHint, value);
    }

    public void Rebuild(HardwareSnapshot snapshot)
    {
        var uncheckedDevices = DeviceFilters.Where(f => !f.IsChecked).Select(f => f.Id).ToHashSet();
        _rows.Clear();
        _devices.Clear();
        DeviceFilters.Clear();

        foreach (DeviceSnapshot device in snapshot.Devices
                     .Where(d => !MonitorPlan.IsDuplicateAdapter(d))
                     .OrderBy(d => SensorFormatter.DeviceOrder(d.Kind)))
        {
            DeviceColors colors = DevicePalette.For(device.Kind);
            var rows = device.Sensors.Select(s => new SensorRowViewModel(this, device, s)).ToList();
            foreach (SensorRowViewModel row in rows)
                _rows[row.Id] = row;

            string name = SensorText.DeviceName(device.Kind, device.Name);
            _devices.Add((new TableDeviceHeader(device.Id, name, colors), rows));
            var filter = new DeviceFilterViewModel(device.Id, name, colors, ApplyFilter);
            if (uncheckedDevices.Contains(device.Id))
                filter.IsChecked = false;
            DeviceFilters.Add(filter);
        }

        if (!_defaultPinsDone)
        {
            // По умолчанию на графике — температуры процессора и видеокарты.
            KeyMetrics metrics = KeyMetrics.From(snapshot);
            foreach (string? id in new[] { metrics.CpuTemperature?.SensorId, metrics.GpuTemperature?.SensorId, metrics.GpuHotSpot?.SensorId })
            {
                if (id is not null && _rows.ContainsKey(id) && !_pinnedIds.Contains(id))
                    _pinnedIds.Add(id);
            }

            _defaultPinsDone = _pinnedIds.Count > 0;
        }

        RefreshPins(save: false);
        ApplyFilter();
    }

    public void Update(HardwareSnapshot snapshot, SensorHistory history, bool visible)
    {
        _history = history;
        int signature = 0;
        foreach (DeviceSnapshot device in snapshot.Devices)
        {
            foreach (SensorReading reading in device.Sensors)
            {
                if (!_rows.TryGetValue(reading.Id, out SensorRowViewModel? row))
                    continue;

                row.Update(reading);
                if (row.HiddenByDefault)
                    signature = signature * 31 + reading.Id.GetHashCode();
            }
        }

        if (!visible)
            return;

        foreach (SensorRowViewModel row in _rows.Values)
            row.Tick(history);

        // Датчик появился или пропал (например, вентилятор раскрутился) — пересобираем список.
        if (signature != _hiddenSignature)
        {
            _hiddenSignature = signature;
            if (_hideEmpty)
                ApplyFilter();
        }

        UpdatePinChart();
    }

    public void TogglePin(SensorRowViewModel row)
    {
        if (_pinnedIds.Remove(row.Id))
        {
            RefreshPins(save: true);
        }
        else
        {
            _pinnedIds.Add(row.Id);
            while (_pinnedIds.Count > MaxPins)
                _pinnedIds.RemoveAt(0);
            RefreshPins(save: true);
        }

        if (_onlyPinned)
            ApplyFilter();
    }

    private void RefreshPins(bool save)
    {
        foreach (SensorRowViewModel row in _rows.Values)
            row.SetPin(null);

        PinnedRows.Clear();
        int index = 0;
        foreach (string id in _pinnedIds)
        {
            if (!_rows.TryGetValue(id, out SensorRowViewModel? row))
                continue;

            var brush = new SolidColorBrush(DevicePalette.Pins[index++ % DevicePalette.Pins.Count]);
            brush.Freeze();
            row.SetPin(brush);
            PinnedRows.Add(row);
        }

        OnPropertyChanged(nameof(HasPins));
        if (save)
        {
            List<string> ids = _pinnedIds.ToList();
            AppSettings.Update(s => s.MonitorPins = ids);
        }

        UpdatePinChart();
    }

    private void UpdatePinChart()
    {
        if (PinnedRows.Count == 0)
        {
            PinLines = Array.Empty<ChartLine>();
            ChartHint = "Нажмите на булавку в строке, чтобы вывести датчик на график (до 4)";
            return;
        }

        string leftUnit = SensorText.Unit(PinnedRows[0].Kind, PinnedRows[0].Style);
        string? rightUnit = null;
        var lines = new List<ChartLine>();
        var leftValues = new List<double>();
        var rightValues = new List<double>();
        SensorKind leftKind = PinnedRows[0].Kind;
        SensorKind rightKind = leftKind;
        bool skipped = false;

        foreach (SensorRowViewModel row in PinnedRows)
        {
            string unit = SensorText.Unit(row.Kind, row.Style);
            bool right = false;
            if (unit != leftUnit)
            {
                rightUnit ??= unit;
                if (unit != rightUnit)
                {
                    skipped = true;
                    continue;
                }

                right = true;
                rightKind = row.Kind;
            }

            HistoryWindow window = _history?.GetWindow(row.Id, 300, 1200) ?? HistoryWindow.Empty;
            double[] values = window.Values
                .Select(v => SensorText.Scale(row.Kind, v, row.Style))
                .ToArray();
            (right ? rightValues : leftValues).AddRange(values);
            Color color = row.PinBrush is SolidColorBrush solid ? solid.Color : DevicePalette.Cpu.Accent;
            SensorKind kind = row.Kind;
            ValueStyle style = row.Style;
            lines.Add(new ChartLine
            {
                Values = values,
                SampleSeconds = window.SampleSeconds,
                FirstIndex = window.FirstIndex,
                Color = color,
                RightAxis = right,
                Label = row.Name,
                Format = v => SensorText.Format(kind, Unscale(kind, style, v), style),
            });
        }

        PinLeftUnit = leftUnit;
        PinRightUnit = rightUnit ?? "";
        PinLeftRange = ChartScale.For(leftKind, leftValues);
        PinRightRange = rightUnit is null ? null : ChartScale.For(rightKind, rightValues);
        PinLines = lines;
        ChartHint = skipped
            ? "Здесь помещаются две единицы измерения: датчики с третьей не нарисованы"
            : rightUnit is not null ? $"Шкала слева — {leftUnit}, справа — {rightUnit}" : "Булавка в строке добавляет датчик (до 4)";
    }

    private static double Unscale(SensorKind kind, ValueStyle style, double value) => style switch
    {
        ValueStyle.Gigahertz => value * 1000,
        ValueStyle.GigabytesFromMegabytes => value * 1024,
        ValueStyle.Megabits => value * 1_000_000 / 8,
        _ => kind == SensorKind.Throughput ? value * 1024 * 1024 : value,
    };

    private void ApplyFilter()
    {
        var allowedKinds = KindFilters.Where(k => k.IsChecked).SelectMany(k => k.Kinds).ToHashSet();
        string query = _searchText.Trim();
        var items = new List<object>();
        int shown = 0;
        int total = _rows.Count;

        foreach ((TableDeviceHeader header, List<SensorRowViewModel> rows) in _devices)
        {
            DeviceFilterViewModel? filter = DeviceFilters.FirstOrDefault(f => f.Id == header.Id);
            bool deviceMatches = query.Length > 0 && header.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            List<SensorRowViewModel> visible = rows
                .Where(r => !_hideEmpty || !r.HiddenByDefault || r.IsPinned)
                .Where(r => allowedKinds.Contains(r.Kind))
                .Where(r => !_onlyPinned || r.IsPinned)
                .Where(r => query.Length == 0 || deviceMatches
                            || r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || r.OriginalName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || r.GroupTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

            if (filter is not null)
            {
                filter.Count = visible.Count;
                // Устройства, у которых все датчики пустые или служебные, в списке не показываем.
                filter.HasRows = rows.Any(r => !_hideEmpty || !r.HiddenByDefault || r.IsPinned);
            }
            if (visible.Count == 0 || filter is { IsChecked: false })
                continue;

            items.Add(header);
            foreach (IGrouping<string, SensorRowViewModel> group in visible
                         .GroupBy(r => r.GroupTitle)
                         .OrderBy(g => SensorFormatter.GroupOrder(g.First().Kind)))
            {
                items.Add(new TableGroupHeader(group.Key));
                int index = 0;
                foreach (SensorRowViewModel row in group)
                {
                    row.IsStriped = index++ % 2 == 1;
                    items.Add(row);
                    shown++;
                }
            }
        }

        Items = items;
        ShownText = $"Показано {shown} из {total}";
    }
}
