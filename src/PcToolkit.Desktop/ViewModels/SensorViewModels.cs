using System.Windows.Input;
using System.Windows.Media;
using PcToolkit.Core.Sensors;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Заголовок устройства в таблице «Все датчики».</summary>
public sealed class TableDeviceHeader
{
    public TableDeviceHeader(string id, string name, DeviceColors colors)
    {
        Id = id;
        Name = name;
        Colors = colors;
    }

    public string Id { get; }

    public string Name { get; }

    public DeviceColors Colors { get; }
}

/// <summary>Заголовок группы в таблице: «Температура», «Частота»…</summary>
public sealed class TableGroupHeader
{
    public TableGroupHeader(string title) => Title = title;

    public string Title { get; }
}

/// <summary>
/// Строка таблицы «Все датчики». Значения обновляются с каждым снимком, а график и среднее
/// считаются, только когда строка на экране (их читает привязка) — иначе сотни строк тратили бы время зря.
/// </summary>
public sealed class SensorRowViewModel : ObservableObject
{
    private readonly SensorTableViewModel _owner;
    private SensorHistory? _history;
    private HistoryWindow? _window;
    private string _valueText = "—";
    private string _minText = "—";
    private string _maxText = "—";
    private bool _isPinned;
    private Brush? _pinBrush;
    private bool _isStriped;
    private bool _hiddenByDefault;

    public SensorRowViewModel(SensorTableViewModel owner, DeviceSnapshot device, SensorReading reading)
    {
        _owner = owner;
        Id = reading.Id;
        Kind = reading.Kind;
        DeviceId = device.Id;
        DeviceName = SensorText.DeviceName(device.Kind, device.Name);
        Colors = DevicePalette.For(device.Kind);
        Name = SensorText.Name(device.Kind, reading.Kind, reading.Name);
        OriginalName = reading.Name.Trim();
        Style = Kind == SensorKind.Throughput && device.Kind == DeviceKind.Network ? ValueStyle.Megabits
            : Kind == SensorKind.Factor && device.Kind == DeviceKind.Cpu && SensorText.IndexOf(reading.Name) is not null ? ValueStyle.Multiplier
            : ValueStyle.Default;
        TogglePinCommand = new RelayCommand(() => _owner.TogglePin(this));
        Update(reading);
    }

    public string Id { get; }

    public SensorKind Kind { get; }

    public ValueStyle Style { get; }

    public string DeviceId { get; }

    public string DeviceName { get; }

    public DeviceColors Colors { get; }

    public string Name { get; }

    /// <summary>Название в LibreHardwareMonitor — во всплывающей подсказке и для поиска по-английски.</summary>
    public string OriginalName { get; }

    public string GroupTitle => SensorFormatter.GroupTitle(Kind);

    public ICommand TogglePinCommand { get; }

    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    public string MinText
    {
        get => _minText;
        private set => SetProperty(ref _minText, value);
    }

    public string MaxText
    {
        get => _maxText;
        private set => SetProperty(ref _maxText, value);
    }

    /// <summary>Среднее за 5 минут (считается при показе строки).</summary>
    public string AvgText
    {
        get
        {
            double[] values = Values;
            return values.Length == 0 ? "—" : SensorText.Bare(Kind, values.Average(), Style);
        }
    }

    /// <summary>Последние 5 минут для графика в строке.</summary>
    public IReadOnlyList<double> Spark => Window.Values;

    /// <summary>Шаг между точками графика в строке, секунды.</summary>
    public double SparkSampleSeconds => Window.SampleSeconds;

    /// <summary>Сквозной номер первой точки графика в строке: к нему привязано усреднение по пикселям.</summary>
    public long SparkFirstIndex => Window.FirstIndex;

    public bool IsPinned
    {
        get => _isPinned;
        private set => SetProperty(ref _isPinned, value);
    }

    /// <summary>Цвет линии на общем графике (у закреплённых).</summary>
    public Brush? PinBrush
    {
        get => _pinBrush;
        private set => SetProperty(ref _pinBrush, value);
    }

    public string PinTooltip => IsPinned ? "Убрать с графика" : "Показать на графике внизу (до 4 датчиков)";

    /// <summary>Каждая вторая строка в группе чуть светлее — глаз не перескакивает на соседнюю.</summary>
    public bool IsStriped
    {
        get => _isStriped;
        set => SetProperty(ref _isStriped, value);
    }

    /// <summary>Пустой или служебный датчик: прячется, пока включено «Скрывать пустые и служебные».</summary>
    public bool HiddenByDefault => _hiddenByDefault;

    internal double[] Values => Window.Values;

    /// <summary>Пять минут истории, не больше 150 точек (график в строке ~150 пикселей шириной).</summary>
    private HistoryWindow Window => _window ??= _history?.GetWindow(Id, 300, 150) ?? HistoryWindow.Empty;

    public void Update(SensorReading reading)
    {
        _hiddenByDefault = MonitorPlan.IsHiddenByDefault(reading);
        ValueText = SensorText.Format(Kind, reading.Value, Style);
        MinText = SensorText.Bare(Kind, reading.Min, Style);
        MaxText = SensorText.Bare(Kind, reading.Max, Style);
    }

    /// <summary>Новая секунда: график и среднее пересчитаются при следующем показе.</summary>
    public void Tick(SensorHistory history)
    {
        _history = history;
        _window = null;
        OnPropertyChanged(nameof(Spark));
        OnPropertyChanged(nameof(SparkSampleSeconds));
        OnPropertyChanged(nameof(SparkFirstIndex));
        OnPropertyChanged(nameof(AvgText));
    }

    public void SetPin(Brush? brush)
    {
        IsPinned = brush is not null;
        PinBrush = brush;
        OnPropertyChanged(nameof(PinTooltip));
    }
}

/// <summary>Фильтр по устройству в левой колонке таблицы.</summary>
public sealed class DeviceFilterViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked = true;
    private int _count;
    private bool _hasRows = true;

    public DeviceFilterViewModel(string id, string name, DeviceColors colors, Action changed)
    {
        Id = id;
        Name = name;
        Colors = colors;
        _changed = changed;
    }

    public string Id { get; }

    public string Name { get; }

    public DeviceColors Colors { get; }

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public bool HasRows
    {
        get => _hasRows;
        set => SetProperty(ref _hasRows, value);
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                _changed();
        }
    }
}

/// <summary>Фильтр по типу датчика: «Температура», «Загрузка»…</summary>
public sealed class KindFilterViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked = true;

    public KindFilterViewModel(string title, IReadOnlyCollection<SensorKind> kinds, Action changed)
    {
        Title = title;
        Kinds = kinds;
        _changed = changed;
    }

    public string Title { get; }

    public IReadOnlyCollection<SensorKind> Kinds { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                _changed();
        }
    }
}
