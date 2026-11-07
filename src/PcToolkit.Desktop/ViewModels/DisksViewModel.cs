using System.Collections.ObjectModel;
using System.Windows.Input;
using PcToolkit.Core;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Logging;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Показатель здоровья на карточке диска: что это, а для тревожных — чем грозит и что делать.</summary>
public sealed class HealthItemViewModel
{
    public HealthItemViewModel(HealthItem item)
    {
        Title = item.Title;
        Value = item.Value;
        Level = item.Level;
        What = item.What;
        RiskText = item.Risk is null ? null : "Чем грозит: " + item.Risk;
        AdviceText = item.Advice is null ? null : "Что делать: " + item.Advice;
    }

    public string Title { get; }

    public string Value { get; }

    public HealthLevel Level { get; }

    public string What { get; }

    public string? RiskText { get; }

    public string? AdviceText { get; }
}

/// <summary>Крупная цифра на карточке диска: ресурс, температура, наработка, объём записи.</summary>
public sealed record DiskStat(string Label, string Value);

/// <summary>Карточка физического диска на вкладке «Здоровье».</summary>
public sealed class DiskCardViewModel : ObservableObject
{
    private bool _showTable;

    public DiskCardViewModel(DiskHealthReport report)
    {
        DiskHealthData disk = report.Disk;
        Title = report.Title;
        var parts = new List<string> { report.Kind };
        if (disk.SizeBytes > 0)
            parts.Add(Formatting.DecimalBytes(disk.SizeBytes));
        if (disk.Volumes.Count > 0)
            parts.Add((disk.Volumes.Count == 1 ? "раздел " : "разделы ") + string.Join(", ", disk.Volumes));
        Subtitle = string.Join(" · ", parts.Where(p => p.Length > 0));

        Verdict = report.Verdict;
        VerdictText = report.VerdictText;
        Summary = report.Summary;

        var stats = new List<DiskStat>();
        if (report.LifePercent is int life)
            stats.Add(new DiskStat("Ресурс", $"{life} %"));
        if (report.TemperatureC is int temperature)
            stats.Add(new DiskStat("Температура", $"{temperature} °C"));
        if (report.PowerOnHours is long hours)
            stats.Add(new DiskStat("Наработка", $"{Formatting.Number(hours)} ч"));
        if (report.WrittenBytes is long written)
            stats.Add(new DiskStat("Записано", Formatting.DecimalBytes(written)));
        Stats = stats;

        Items = report.Items.Select(i => new HealthItemViewModel(i)).ToList();
        Rows = report.Rows;
        RowsHaveThresholds = report.RowsHaveThresholds;

        var identity = new List<string>();
        if (disk.Firmware.Length > 0)
            identity.Add("прошивка " + disk.Firmware);
        if (disk.Serial.Length > 0)
            identity.Add("серийный номер " + disk.Serial);
        identity.Add($"диск {disk.Number} в «Управлении дисками» Windows");
        IdentityText = char.ToUpperInvariant(identity[0][0]) + string.Join(" · ", identity)[1..];

        ToggleTableCommand = new RelayCommand(() => ShowTable = !ShowTable);
    }

    public string Title { get; }

    public string Subtitle { get; }

    public DiskVerdict Verdict { get; }

    public string VerdictText { get; }

    public string Summary { get; }

    public IReadOnlyList<DiskStat> Stats { get; }

    public bool HasStats => Stats.Count > 0;

    public IReadOnlyList<HealthItemViewModel> Items { get; }

    public bool HasItems => Items.Count > 0;

    public IReadOnlyList<SmartRow> Rows { get; }

    public bool HasRows => Rows.Count > 0;

    /// <summary>У SATA-дисков в таблице есть нормированные значения и пороги, у NVMe — только значения.</summary>
    public bool RowsHaveThresholds { get; }

    public string IdentityText { get; }

    public ICommand ToggleTableCommand { get; }

    /// <summary>Развёрнута ли подробная таблица SMART.</summary>
    public bool ShowTable
    {
        get => _showTable;
        set
        {
            if (SetProperty(ref _showTable, value))
                OnPropertyChanged(nameof(TableButtonText));
        }
    }

    public string TableButtonText => _showTable ? "Скрыть таблицу SMART" : "Все атрибуты SMART";
}

/// <summary>
/// Вкладка «Здоровье»: SMART всех дисков с объяснениями. Читает напрямую (администратор) или через службу,
/// запоминает счётчики ошибок и сообщает, если диску стало хуже.
/// </summary>
public sealed class DiskHealthViewModel : ObservableObject
{
    private readonly Func<DiskReadResult> _read;
    private readonly DiskHealthHistory _history;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private bool _isLoading;
    private bool _loaded;
    private string? _note;
    private string _statusText = "";
    private IReadOnlyList<DiskHealthReport> _reports = Array.Empty<DiskHealthReport>();

    public DiskHealthViewModel(Func<DiskReadResult> read, DiskHealthHistory history, ICommand installServiceCommand,
                               ICommand restartAsAdminCommand)
    {
        _read = read;
        _history = history;
        InstallServiceCommand = installServiceCommand;
        RestartAsAdminCommand = restartAsAdminCommand;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsLoading);
    }

    public ObservableCollection<DiskCardViewModel> Disks { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand InstallServiceCommand { get; }

    public ICommand RestartAsAdminCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                RelayCommand.Refresh();
            }
        }
    }

    /// <summary>Почему SMART прочитан не у всех дисков и что сделать.</summary>
    public string? Note
    {
        get => _note;
        private set
        {
            if (SetProperty(ref _note, value))
            {
                OnPropertyChanged(nameof(HasNote));
                OnPropertyChanged(nameof(ShowServiceButtons));
            }
        }
    }

    public bool HasNote => _note is not null;

    /// <summary>Кнопки «Установить службу» и «Запустить от имени администратора» — когда дело в правах.</summary>
    public bool ShowServiceButtons => _note is DiskHealthSource.InstallServiceNote or DiskHealthSource.UpdateServiceNote;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsEmpty => _loaded && !_isLoading && Disks.Count == 0;

    /// <summary>Результаты последней проверки — для главной и проверки системы.</summary>
    public IReadOnlyList<DiskHealthReport> Reports => _reports;

    /// <summary>Диски проверены заново (в том числе фоновой проверкой).</summary>
    public event EventHandler? ReportsChanged;

    /// <summary>Диску стало хуже с прошлой проверки — повод для уведомления.</summary>
    public event Action<IReadOnlyList<DiskHealthNews>>? News;

    /// <summary>При первом открытии вкладки.</summary>
    public void EnsureLoaded()
    {
        if (!_loaded && !_isLoading)
            _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!await _busy.WaitAsync(0))
            return;

        try
        {
            IsLoading = true;
            StatusText = "Читаю SMART дисков…";
            (DiskReadResult result, List<DiskHealthReport> reports, IReadOnlyList<DiskHealthNews> news) = await Task.Run(() =>
            {
                DiskReadResult read = _read();
                List<DiskHealthReport> evaluated = _history.Evaluate(read.Disks)
                    .OrderBy(r => r.Verdict)
                    .ThenBy(r => r.Disk.Number)
                    .ToList();
                return (read, evaluated, _history.Record(evaluated, DateTimeOffset.Now));
            });

            Disks.Clear();
            foreach (DiskHealthReport report in reports)
                Disks.Add(new DiskCardViewModel(report));
            Note = result.Note;
            _reports = reports;
            _loaded = true;
            StatusText = $"Проверено в {DateTime.Now:HH:mm}";
            AppLog.Info("Проверка дисков: " + string.Join("; ", reports.Select(r => $"{r.Title} — {r.VerdictText}")) + ".");

            ReportsChanged?.Invoke(this, EventArgs.Empty);
            if (news.Count > 0)
                News?.Invoke(news);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось проверить диски.", ex);
            StatusText = "Не удалось прочитать диски: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            _busy.Release();
        }
    }
}

/// <summary>Экран «Диски»: здоровье, замер скорости и анализ занятого места — три вкладки.</summary>
public sealed class DisksViewModel : ObservableObject
{
    private string _tab = "health";

    public DisksViewModel(DiskHealthViewModel health, DiskTestViewModel speed, DiskAnalyzerViewModel space)
    {
        Health = health;
        Speed = speed;
        Space = space;
    }

    public DiskHealthViewModel Health { get; }

    public DiskTestViewModel Speed { get; }

    public DiskAnalyzerViewModel Space { get; }

    public bool IsHealthTab { get => _tab == "health"; set => SetTab("health", value); }

    public bool IsSpeedTab { get => _tab == "speed"; set => SetTab("speed", value); }

    public bool IsSpaceTab { get => _tab == "space"; set => SetTab("space", value); }

    /// <summary>Экран открыт: подгружает то, что нужно текущей вкладке.</summary>
    public void OnShown()
    {
        switch (_tab)
        {
            case "health":
                Health.EnsureLoaded();
                break;
            case "speed" when !Speed.IsRunning:
                Speed.RefreshDrives();
                break;
            case "space":
                Space.EnsureLoaded();
                break;
        }
    }

    public void ShowTab(string tab) => SetTab(tab, true);

    public void Shutdown() => Speed.Shutdown();

    private void SetTab(string tab, bool value)
    {
        if (!value || _tab == tab)
            return;

        _tab = tab;
        OnShown();
        OnPropertyChanged(nameof(IsHealthTab));
        OnPropertyChanged(nameof(IsSpeedTab));
        OnPropertyChanged(nameof(IsSpaceTab));
    }
}
