using System.IO;
using PcToolkit.Core;
using PcToolkit.Core.Diagnostics;
using PcToolkit.Core.Sensors;
using PcToolkit.Desktop.Controls;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Строка «подпись — значение» в плитке устройства.</summary>
public sealed class TileStat : ObservableObject
{
    private string _value = "—";

    public TileStat(string label) => Label = label;

    public string Label { get; }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

/// <summary>
/// Плитка устройства (процессор, видеокарта, память, диски): крупное главное значение, три показателя
/// и график за 5 минут с подписанными шкалами. Одна и та же на главной и в «Обзоре» мониторинга.
/// </summary>
public sealed class DeviceTileViewModel : ObservableObject
{
    private string _title;
    private string _bigValue = "—";
    private string _bigUnit = "";
    private string _caption = "ожидание данных…";
    private HealthState _state = HealthState.Unknown;
    private IReadOnlyList<ChartLine> _lines = Array.Empty<ChartLine>();
    private ChartRange _range = new(0, 100);
    private string _unit = "";

    public DeviceTileViewModel(DeviceColors colors, string title, params string[] stats)
    {
        Colors = colors;
        _title = title;
        Stats = stats.Select(s => new TileStat(s)).ToArray();
    }

    public DeviceColors Colors { get; }

    public string Tag => Colors.Tag;

    public IReadOnlyList<TileStat> Stats { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string BigValue
    {
        get => _bigValue;
        set => SetProperty(ref _bigValue, value);
    }

    public string BigUnit
    {
        get => _bigUnit;
        set => SetProperty(ref _bigUnit, value);
    }

    public string Caption
    {
        get => _caption;
        set => SetProperty(ref _caption, value);
    }

    public HealthState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    public IReadOnlyList<ChartLine> Lines
    {
        get => _lines;
        set => SetProperty(ref _lines, value);
    }

    public ChartRange Range
    {
        get => _range;
        set => SetProperty(ref _range, value);
    }

    public string Unit
    {
        get => _unit;
        set => SetProperty(ref _unit, value);
    }

    /// <summary>Главное значение без единицы («66,1») и единица отдельно («°C») — единица рисуется мельче.</summary>
    public void SetBig(string formatted)
    {
        int space = formatted.LastIndexOf(' ');
        if (space > 0)
        {
            BigValue = formatted[..space];
            BigUnit = formatted[(space + 1)..];
        }
        else
        {
            BigValue = formatted;
            BigUnit = "";
        }
    }

    public void SetChart(HistoryWindow window, SensorKind kind, ValueStyle style = ValueStyle.Default, double? fixedMax = null)
    {
        double[] scaled = style == ValueStyle.Default && kind != SensorKind.Throughput
            ? window.Values
            : window.Values.Select(v => SensorText.Scale(kind, v, style)).ToArray();
        Range = ChartScale.For(kind, scaled, fixedMax: fixedMax);
        Unit = SensorText.Unit(kind, style);
        Lines = new[]
        {
            new ChartLine { Values = scaled, SampleSeconds = window.SampleSeconds, FirstIndex = window.FirstIndex, Color = Colors.Accent, Fill = true, Thickness = 1.6 },
        };
    }
}

/// <summary>Четыре плитки устройств: заполняются из снимка датчиков и истории за 5 минут.</summary>
public sealed class DeviceTiles
{
    /// <summary>Пять минут на графике плитки; точек не больше, чем пикселей в ширину графика.</summary>
    private const int WindowSeconds = 300;
    private const int MaxPoints = 300;

    private static HistoryWindow Recent(SensorHistory history, string id) => history.GetWindow(id, WindowSeconds, MaxPoints);
    private DateTime _lastDriveCheck = DateTime.MinValue;
    private string _systemDriveFree = "—";

    public DeviceTileViewModel Cpu { get; } = new(DevicePalette.Cpu, "Процессор", "Загрузка", "Частота", "Мощность");

    public DeviceTileViewModel Gpu { get; } = new(DevicePalette.Gpu, "Видеокарта", "Загрузка", "Частота", "Мощность");

    public DeviceTileViewModel Memory { get; } = new(DevicePalette.Ram, "Оперативная память", "Загрузка", "Свободно", "Модули");

    public DeviceTileViewModel Storage { get; } = new(DevicePalette.Ssd, "Накопители", "Активность", "Запись", "Свободно на C:");

    public IReadOnlyList<DeviceTileViewModel> All => new[] { Cpu, Gpu, Memory, Storage };

    public void Update(HardwareSnapshot snapshot, SensorHistory history, bool hasFullSensors)
    {
        KeyMetrics m = KeyMetrics.From(snapshot);
        UpdateCpu(snapshot, m, history);
        UpdateGpu(snapshot, m, history);
        UpdateMemory(snapshot, m, history);
        UpdateStorage(snapshot, m, history, hasFullSensors);
    }

    private void UpdateCpu(HardwareSnapshot snapshot, KeyMetrics m, SensorHistory history)
    {
        DeviceTileViewModel tile = Cpu;
        tile.Title = m.CpuName ?? "Процессор не найден";
        if (m.CpuTemperature is { } temperature)
        {
            tile.SetBig(SensorText.Format(SensorKind.Temperature, temperature.Value));
            tile.Caption = "температура";
            tile.State = Thresholds.Classify(temperature.Value, Thresholds.CpuWarning, Thresholds.CpuCritical);
            tile.SetChart(Recent(history, temperature.SensorId), SensorKind.Temperature);
        }
        else if (m.CpuLoad is { } load)
        {
            tile.SetBig(SensorText.Format(SensorKind.Load, load.Value));
            tile.Caption = "загрузка (температура недоступна)";
            tile.State = HealthState.Unknown;
            tile.SetChart(Recent(history, load.SensorId), SensorKind.Load);
        }
        else
        {
            tile.SetBig("—");
            tile.Caption = "нет данных";
            tile.State = HealthState.Unknown;
            tile.Lines = Array.Empty<ChartLine>();
        }

        DeviceSnapshot? cpu = snapshot.Devices.FirstOrDefault(d => d.Kind == DeviceKind.Cpu);
        SensorReading? average = cpu is null ? null : MonitorPlan.Find(cpu, SensorKind.Clock, "Cores (Average)");
        tile.Stats[0].Value = SensorText.Format(SensorKind.Load, m.CpuLoad?.Value);
        tile.Stats[1].Value = average?.Value is float clock && clock > 0
            ? SensorText.Format(SensorKind.Clock, clock, ValueStyle.Gigahertz)
            : SensorText.Format(SensorKind.Clock, m.CpuClock, ValueStyle.Gigahertz);
        tile.Stats[2].Value = SensorText.Format(SensorKind.Power, m.CpuPower?.Value);
    }

    private void UpdateGpu(HardwareSnapshot snapshot, KeyMetrics m, SensorHistory history)
    {
        DeviceTileViewModel tile = Gpu;
        tile.Title = m.GpuName ?? "Видеокарта не найдена";
        if (m.GpuTemperature is { } temperature)
        {
            tile.SetBig(SensorText.Format(SensorKind.Temperature, temperature.Value));
            tile.Caption = m.GpuHotSpot is { } hotSpot
                ? $"ядро · горячая точка {SensorText.Format(SensorKind.Temperature, Math.Round(hotSpot.Value))}"
                : "температура";
            tile.State = Thresholds.Classify(temperature.Value, Thresholds.GpuWarning, Thresholds.GpuCritical);
            tile.SetChart(Recent(history, temperature.SensorId), SensorKind.Temperature);
        }
        else if (m.GpuLoad is { } load)
        {
            tile.SetBig(SensorText.Format(SensorKind.Load, load.Value));
            tile.Caption = "загрузка";
            tile.State = HealthState.Unknown;
            tile.SetChart(Recent(history, load.SensorId), SensorKind.Load);
        }
        else
        {
            tile.SetBig("—");
            tile.Caption = "нет данных";
            tile.State = HealthState.Unknown;
            tile.Lines = Array.Empty<ChartLine>();
        }

        tile.Stats[0].Value = SensorText.Format(SensorKind.Load, m.GpuLoad?.Value);
        tile.Stats[1].Value = SensorText.Format(SensorKind.Clock, m.GpuClock?.Value);
        tile.Stats[2].Value = SensorText.Format(SensorKind.Power, m.GpuPower?.Value);
    }

    private void UpdateMemory(HardwareSnapshot snapshot, KeyMetrics m, SensorHistory history)
    {
        DeviceTileViewModel tile = Memory;
        string modules = MonitorPlan.MemoryModules(snapshot);
        tile.Title = "Оперативная память";
        if (m.MemoryUsed is { } used)
        {
            tile.SetBig(SensorText.Format(SensorKind.Data, used.Value));
            double? total = m.MemoryAvailable is { } available ? used.Value + available.Value : null;
            tile.Caption = total is double t ? $"занято из {SensorText.Format(SensorKind.Data, t)}" : "занято";
            tile.State = m.MemoryLoad is { } load && load.Value >= Thresholds.MemoryLoadWarning ? HealthState.Warning : HealthState.Normal;
            tile.SetChart(Recent(history, used.SensorId), SensorKind.Data, fixedMax: total is double tt ? Math.Ceiling(tt) : null);
        }
        else
        {
            tile.SetBig("—");
            tile.Caption = "нет данных";
            tile.State = HealthState.Unknown;
            tile.Lines = Array.Empty<ChartLine>();
        }

        tile.Stats[0].Value = SensorText.Format(SensorKind.Load, m.MemoryLoad?.Value);
        tile.Stats[1].Value = SensorText.Format(SensorKind.Data, m.MemoryAvailable?.Value);
        tile.Stats[2].Value = modules.Length > 0 ? modules : "—";
    }

    private void UpdateStorage(HardwareSnapshot snapshot, KeyMetrics m, SensorHistory history, bool hasFullSensors)
    {
        DeviceTileViewModel tile = Storage;
        List<DeviceSnapshot> disks = snapshot.Devices.Where(d => d.Kind == DeviceKind.Storage).ToList();
        tile.Title = disks.Count switch
        {
            0 => "Накопители",
            1 => disks[0].Name.Trim(),
            _ => $"{disks.Count} {Formatting.Plural(disks.Count, "накопитель", "накопителя", "накопителей")}",
        };

        if (m.HottestStorage is { } hottest)
        {
            tile.SetBig(SensorText.Format(SensorKind.Temperature, hottest.Value));
            tile.Caption = disks.Count > 1 ? $"самый горячий: {hottest.DeviceName.Trim()}" : "температура";
            tile.State = Thresholds.Classify(hottest.Value, Thresholds.StorageWarning, Thresholds.StorageCritical);
            tile.SetChart(Recent(history, hottest.SensorId), SensorKind.Temperature);
        }
        else
        {
            tile.SetBig("—");
            tile.Caption = disks.Count > 0 ? "температура недоступна" : hasFullSensors ? "накопители не найдены" : "нужны права администратора";
            tile.State = HealthState.Unknown;
            tile.Lines = Array.Empty<ChartLine>();
        }

        double write = disks.Sum(d => (double)(MonitorPlan.Find(d, SensorKind.Throughput, "Write Rate")?.Value ?? 0));
        tile.Stats[0].Value = SensorText.Format(SensorKind.Load, m.BusiestStorage?.Value);
        tile.Stats[1].Value = disks.Count > 0 ? SensorText.Format(SensorKind.Throughput, write) : "—";
        // Свободное место меняется медленно — раз в 30 секунд.
        if (DateTime.Now - _lastDriveCheck >= TimeSpan.FromSeconds(30))
        {
            _lastDriveCheck = DateTime.Now;
            _systemDriveFree = SystemDriveFree();
        }
        tile.Stats[2].Value = _systemDriveFree;
    }

    private static string SystemDriveFree()
    {
        try
        {
            string? root = Path.GetPathRoot(Environment.SystemDirectory);
            return string.IsNullOrEmpty(root) ? "—" : Formatting.Bytes((ulong)new DriveInfo(root).AvailableFreeSpace);
        }
        catch
        {
            return "—";
        }
    }
}
