using System.IO;
using System.Windows.Input;
using PcToolkit.Core;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Settings;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>
/// Экран «Мониторинг»: три вида одного экрана — «Обзор» (главное на одном экране), «Все датчики» (таблица)
/// и «Графики» (главные показатели во времени). Плюс запись сессии в CSV.
/// </summary>
public sealed class MonitoringViewModel : ObservableObject, IDisposable
{
    private readonly SessionRecorder _recorder = new();
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private DateTime _lastRecorded = DateTime.MinValue;
    private int _devices;
    private int _sensors;
    private HardwareSnapshot? _latest;
    private int _structureHash;
    private bool _isBuilt;
    private string _tab;
    private string _searchText = "";
    private string _recordingText = "";
    private string _summaryText = "Ожидание данных датчиков…";

    public MonitoringViewModel()
    {
        string saved = AppSettings.Load().MonitoringTab;
        _tab = saved is "overview" or "table" or "graphs" ? saved : "overview";

        SetTabCommand = new RelayCommand(p =>
        {
            if (p is string tab)
                Tab = tab;
        });
        ToggleRecordingCommand = new RelayCommand(ToggleRecording, () => _latest is not null);
        OpenSessionsFolderCommand = new RelayCommand(() => ShellService.OpenFolder(SessionRecorder.DefaultDirectory));
    }

    public MonitorOverviewViewModel Overview { get; } = new();

    public SensorTableViewModel Table { get; } = new();

    public MonitorGraphsViewModel Graphs { get; } = new();

    public ICommand SetTabCommand { get; }

    public ICommand ToggleRecordingCommand { get; }

    public ICommand OpenSessionsFolderCommand { get; }

    /// <summary>Виден ли экран: пока он скрыт, графики и таблица не пересчитываются.</summary>
    public bool IsPageVisible { get; set; }

    /// <summary>Есть ли полный набор датчиков (права администратора или служба) — для подписей «нет данных».</summary>
    public bool HasFullSensors { get; set; } = true;

    public string Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value))
                return;

            AppSettings.Update(s => s.MonitoringTab = value);
            OnPropertyChanged(nameof(IsOverview));
            OnPropertyChanged(nameof(IsTable));
            OnPropertyChanged(nameof(IsGraphs));
            if (_latest is not null)
                Refresh(_latest, _history);
        }
    }

    // Сеттеры для переключателя видов: «снятие» отметки с соседней кнопки игнорируем.
    public bool IsOverview { get => _tab == "overview"; set { if (value) Tab = "overview"; } }

    public bool IsTable { get => _tab == "table"; set { if (value) Tab = "table"; } }

    public bool IsGraphs { get => _tab == "graphs"; set { if (value) Tab = "graphs"; } }

    /// <summary>Поиск датчика: сразу показывает таблицу с отфильтрованными строками.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? ""))
                return;

            Table.SearchText = _searchText;
            if (_searchText.Length > 0 && !IsTable)
                Tab = "table";
        }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public bool IsRecording => _recorder.IsRecording;

    public string RecordButtonText => IsRecording ? "Остановить запись" : "Записать в CSV";

    public string RecordingText
    {
        get => _recordingText;
        private set => SetProperty(ref _recordingText, value);
    }

    private SensorHistory? _history;

    /// <summary>Открыть таблицу с поиском (из поиска по программе).</summary>
    public void ShowSensor(string query)
    {
        Tab = "table";
        SearchText = query;
    }

    public void Update(HardwareSnapshot snapshot, SensorHistory history)
    {
        _latest = snapshot;
        _history = history;

        int hash = ComputeStructureHash(snapshot);
        if (!_isBuilt || hash != _structureHash)
        {
            Overview.Rebuild(snapshot);
            Table.Rebuild(snapshot);
            Graphs.Rebuild(snapshot);
            _structureHash = hash;
            _isBuilt = true;
            _devices = snapshot.Devices.Count;
            _sensors = snapshot.SensorCount;
            UpdateSummary();
        }

        Refresh(snapshot, history);
        AppendRecording(snapshot);
    }

    private void Refresh(HardwareSnapshot snapshot, SensorHistory? history)
    {
        if (history is null)
            return;

        // Таблица хранит последние значения всегда (для поиска и счётчиков), остальное — только когда видно.
        Table.Update(snapshot, history, IsPageVisible && IsTable);
        if (!IsPageVisible)
            return;

        if (IsOverview || IsGraphs)
        {
            var readings = new Dictionary<string, SensorReading>(snapshot.SensorCount);
            foreach (DeviceSnapshot device in snapshot.Devices)
            {
                foreach (SensorReading reading in device.Sensors)
                    readings[reading.Id] = reading;
            }

            if (IsOverview)
                Overview.Update(snapshot, readings, history, HasFullSensors);
            else
                Graphs.Update(readings, history);
        }
    }

    public void Dispose() => _recorder.Dispose();

    private void ToggleRecording()
    {
        try
        {
            if (_recorder.IsRecording)
            {
                _recorder.Stop();
                RecordingText = $"Сохранено: {Path.GetFileName(_recorder.FilePath)}";
            }
            else if (_latest is not null)
            {
                _recorder.Start(_latest);
                UpdateRecordingText();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось начать или остановить запись сессии.", ex);
            _recorder.Stop();
            RecordingText = $"Ошибка записи: {ex.Message}";
        }

        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordButtonText));
    }

    /// <summary>Как часто приходят данные — для подписи под заголовком.</summary>
    public void SetInterval(TimeSpan interval)
    {
        _interval = interval;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (!_isBuilt)
            return;

        int perSecond = (int)Math.Round(1000 / Math.Max(1, _interval.TotalMilliseconds));
        string rate = perSecond <= 1 ? "раз в секунду" : $"{perSecond} {Formatting.Plural(perSecond, "раз", "раза", "раз")} в секунду";
        SummaryText = $"{_devices} {Formatting.Plural(_devices, "устройство", "устройства", "устройств")} · " +
                      $"{_sensors} {Formatting.Plural(_sensors, "датчик", "датчика", "датчиков")} · обновление {rate}";
    }

    private void AppendRecording(HardwareSnapshot snapshot)
    {
        if (!_recorder.IsRecording)
            return;

        // В файл — раз в секунду, как и раньше, даже если на экране данные обновляются чаще.
        if (snapshot.Timestamp - _lastRecorded < TimeSpan.FromMilliseconds(950))
            return;
        _lastRecorded = snapshot.Timestamp;

        try
        {
            _recorder.Append(snapshot);
            UpdateRecordingText();
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка при записи сессии, запись остановлена.", ex);
            _recorder.Stop();
            RecordingText = $"Запись остановлена: {ex.Message}";
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(RecordButtonText));
        }
    }

    private void UpdateRecordingText()
    {
        TimeSpan elapsed = DateTime.Now - _recorder.StartedAt;
        RecordingText = $"Идёт запись · {elapsed:hh\\:mm\\:ss} · строк: {_recorder.RowCount}";
    }

    private static int ComputeStructureHash(HardwareSnapshot snapshot)
    {
        var hash = new HashCode();
        foreach (DeviceSnapshot device in snapshot.Devices)
        {
            hash.Add(device.Id);
            foreach (SensorReading sensor in device.Sensors)
                hash.Add(sensor.Id);
        }

        return hash.ToHashCode();
    }
}
