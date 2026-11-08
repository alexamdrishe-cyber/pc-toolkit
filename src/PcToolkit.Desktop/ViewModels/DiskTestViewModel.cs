using System.IO;
using System.Windows;
using System.Windows.Input;
using PcToolkit.Core;
using PcToolkit.Core.Benchmarks;
using PcToolkit.Core.Benchmarks.Disk;
using PcToolkit.Core.Inventory;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Storage;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Диск для выбора: буква, метка, свободно, модель.</summary>
public sealed class DriveOptionViewModel : ObservableObject
{
    private bool _isSelected;
    private string _details;

    public DriveOptionViewModel(string root, string title, string details, bool selected)
    {
        Root = root;
        Title = title;
        _details = details;
        _isSelected = selected;
    }

    public string Root { get; }

    public string Title { get; }

    public string Details
    {
        get => _details;
        set => SetProperty(ref _details, value);
    }

    public string Model { get; set; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>Строка результатов как в CrystalDiskMark.</summary>
public sealed record DiskResultRow(string Name, string Hint, string Read, string Write, string ReadExtra, string WriteExtra,
    string ReadDelta, string WriteDelta);

/// <summary>Полоса сравнения с классами накопителей.</summary>
public sealed class DiskClassRow
{
    public DiskClassRow(string title, string subtitle, double value, double max, bool isCurrent)
    {
        Title = title;
        Subtitle = subtitle;
        ValueText = $"{value:N0} МБ/с";
        IsCurrent = isCurrent;
        // Логарифмическая шкала: иначе HDD и флешка не видны рядом с PCIe 5.0.
        double f = Math.Clamp(Math.Log10(Math.Max(value, 1)) / Math.Log10(Math.Max(max, 10)), 0.02, 1);
        BarLength = new GridLength(f, GridUnitType.Star);
        RestLength = new GridLength(1 - f + 1e-6, GridUnitType.Star);
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string ValueText { get; }

    public bool IsCurrent { get; }

    public bool IsEstimate => !IsCurrent;

    public GridLength BarLength { get; }

    public GridLength RestLength { get; }
}

/// <summary>Вкладка «Диск»: скорость в духе CrystalDiskMark.</summary>
public sealed class DiskTestViewModel : ObservableObject
{
    private readonly OperationGate _gate;
    private readonly BenchmarkHistory _history;
    private IDisposable? _slot;
    private CancellationTokenSource? _cancel;
    private bool _isRunning;
    private string _statusText = "";
    private double _progress;
    private string _resultTitle = "";
    private string _resultText = "";
    private string _resultKind = "";
    private IReadOnlyList<DriveOptionViewModel> _drives = Array.Empty<DriveOptionViewModel>();
    private IReadOnlyList<DiskResultRow> _rows = Array.Empty<DiskResultRow>();
    private IReadOnlyList<DiskClassRow> _classRows = Array.Empty<DiskClassRow>();
    private DiskBenchmarkRecord? _shown;
    private string _classSummary = "";

    public DiskTestViewModel(OperationGate gate, BenchmarkHistory history)
    {
        _gate = gate;
        _history = history;

        Sizes = new[]
        {
            new ChoiceViewModel("disk-size", "256 МБ", 256, false),
            new ChoiceViewModel("disk-size", "1 ГБ", 1024, true),
            new ChoiceViewModel("disk-size", "4 ГБ", 4096, false),
        };
        Runs = new[]
        {
            new ChoiceViewModel("disk-runs", "1 раз (быстро)", 1, false),
            new ChoiceViewModel("disk-runs", "3 раза", 3, true),
            new ChoiceViewModel("disk-runs", "5 раз (точнее)", 5, false),
        };

        StartCommand = new RelayCommand(() => _ = RunAsync(), () => !IsRunning && Drives.Any(d => d.IsSelected));
        StopCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsRunning);

        // Список дисков — при открытии вкладки и в фоне: сетевой или «уснувший» USB-диск может отвечать секундами.
        if (_history.Disk.FirstOrDefault() is { } last)
            Show(last);
    }

    public IReadOnlyList<ChoiceViewModel> Sizes { get; }

    public IReadOnlyList<ChoiceViewModel> Runs { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public IReadOnlyList<DriveOptionViewModel> Drives
    {
        get => _drives;
        private set => SetProperty(ref _drives, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanEditOptions));
                RelayCommand.Refresh();
            }
        }
    }

    public bool CanEditOptions => !IsRunning;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>0–100.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string ResultTitle
    {
        get => _resultTitle;
        private set
        {
            if (SetProperty(ref _resultTitle, value))
                OnPropertyChanged(nameof(HasResult));
        }
    }

    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public string ResultKind
    {
        get => _resultKind;
        private set => SetProperty(ref _resultKind, value);
    }

    public bool HasResult => ResultTitle.Length > 0;

    public bool HasRows => Rows.Count > 0;

    public IReadOnlyList<DiskResultRow> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
                OnPropertyChanged(nameof(HasRows));
        }
    }

    public string ResultCaption => _shown is { } r
        ? $"Диск {r.Drive}{(r.Model.Length > 0 ? " · " + r.Model : "")} · файл {Formatting.Bytes((ulong)r.FileSize)} · замер от {r.At.LocalDateTime:d MMMM, HH:mm}"
        : "";

    public IReadOnlyList<DiskClassRow> ClassRows
    {
        get => _classRows;
        private set => SetProperty(ref _classRows, value);
    }

    public string ClassSummary
    {
        get => _classSummary;
        private set => SetProperty(ref _classSummary, value);
    }

    /// <summary>Перечитывает список дисков (при открытии вкладки: могли подключить флешку). Чтение — в фоне.</summary>
    public void RefreshDrives() => _ = RefreshDrivesAsync();

    private int _driveRefresh;

    private async Task RefreshDrivesAsync()
    {
        int refresh = ++_driveRefresh;
        IReadOnlyList<DriveSpaceInfo> drives = await Task.Run(() => DriveSpace.Read(includeExternal: true));
        if (refresh != _driveRefresh)
            return; // пока читали, начали новое чтение

        string? selected = Drives.FirstOrDefault(d => d.IsSelected)?.Root;
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var list = new List<DriveOptionViewModel>();
        foreach (DriveSpaceInfo drive in drives)
        {
            string label = drive.Label.Length > 0 ? drive.Label : drive.Type == DriveType.Removable ? "Съёмный диск" : "Локальный диск";
            string title = $"{drive.Letter} {label}";
            string details = $"свободно {Formatting.Bytes((ulong)drive.AvailableBytes)} из {Formatting.Bytes((ulong)drive.TotalBytes)}";
            bool isSelected = selected is not null
                ? string.Equals(selected, drive.Root, StringComparison.OrdinalIgnoreCase)
                : string.Equals(systemRoot, drive.Root, StringComparison.OrdinalIgnoreCase);
            list.Add(new DriveOptionViewModel(drive.Root, title, details, isSelected));
        }

        if (list.Count > 0 && !list.Any(d => d.IsSelected))
            list[0].IsSelected = true;
        foreach (DriveOptionViewModel drive in list)
            drive.PropertyChanged += (_, _) => RelayCommand.Refresh();
        Drives = list;
        _ = LoadModelsAsync(list);
    }

    private static async Task LoadModelsAsync(IReadOnlyList<DriveOptionViewModel> drives)
    {
        IReadOnlyDictionary<char, DriveDetails> details = await Task.Run(SystemInfoProvider.ReadDriveDetails);
        foreach (DriveOptionViewModel drive in drives)
        {
            if (details.TryGetValue(char.ToUpperInvariant(drive.Root[0]), out DriveDetails? d))
            {
                drive.Model = d.Kind.Length > 0 ? $"{d.Model} · {d.Kind}" : d.Model;
                drive.Details = $"{drive.Model} · {drive.Details}";
            }
        }
    }

    private async Task RunAsync()
    {
        if (IsRunning || Drives.FirstOrDefault(d => d.IsSelected) is not { } drive)
            return;

        _slot = _gate.TryEnter("Замер диска", out string? busy);
        if (_slot is null)
        {
            ShowResult("Сейчас нельзя", $"Выполняется другая операция: «{busy}». Запустите замер, когда она закончится.", "warning");
            return;
        }

        ResultTitle = "";
        IsRunning = true;
        _cancel = new CancellationTokenSource();
        try
        {
            long size = Sizes.First(s => s.IsSelected).Value * (1L << 20);
            int runs = Runs.First(r => r.IsSelected).Value;
            var progress = new Progress<DiskBenchmarkProgress>(p =>
            {
                StatusText = $"Шаг {p.Index} из {p.Count}: {p.Step}…";
                Progress = (p.Index - 1) * 100.0 / p.Count;
            });

            IReadOnlyList<DiskTestResult> results = await DiskBenchmark.RunAsync(drive.Root, size, runs,
                TimeSpan.FromSeconds(4), progress, _cancel.Token);
            Progress = 100;

            var record = new DiskBenchmarkRecord
            {
                At = DateTimeOffset.Now,
                Drive = drive.Root.TrimEnd('\\'),
                Model = drive.Model,
                FileSize = size,
                Results = results.ToList(),
            };
            _history.AddDisk(record);
            Show(record);

            DiskTestResult seq = results[0];
            DiskTestResult rnd = results[^1];
            ShowResult($"Чтение {seq.ReadMBps:N0} МБ/с, запись {seq.WriteMBps:N0} МБ/с",
                $"{ClassSummary} Мелкие файлы (4 КБ, очередь 1): {rnd.ReadMBps:0.0} МБ/с чтение, {rnd.ReadLatencyUs:0} мкс на запрос. " +
                "Временный файл удалён.", "good");
        }
        catch (OperationCanceledException)
        {
            ShowResult("Замер остановлен", "Результаты не сохранены, временный файл удалён.", "info");
        }
        catch (UnauthorizedAccessException)
        {
            ShowResult("Нет доступа к диску", $"Windows не разрешила создать временный файл на {drive.Root}. Выберите другой диск.", "warning");
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка замера диска.", ex);
            ShowResult("Замер не удался", ex.Message, "critical");
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            StatusText = "";
            Progress = 0;
            _slot?.Dispose();
            _slot = null;
            IsRunning = false;
            RefreshDrives(); // свободное место после теста — тоже в фоне
        }
    }

    private void Show(DiskBenchmarkRecord record)
    {
        _shown = record;
        // Сравниваем с прошлым замером того же диска.
        DiskBenchmarkRecord? previous = _history.Disk.FirstOrDefault(r => r.Id != record.Id && r.Drive == record.Drive && r.At < record.At);

        Rows = DiskBenchmark.Specs.Select(spec =>
        {
            DiskTestResult? r = record[spec.Name];
            DiskTestResult? p = previous?[spec.Name];
            if (r is null)
                return new DiskResultRow(spec.Name, spec.Hint, "—", "—", "", "", "", "");
            string readExtra = spec.Random ? $"{r.ReadIops:N0} IOPS · {r.ReadLatencyUs:N0} мкс" : "";
            string writeExtra = spec.Random ? $"{r.WriteIops:N0} IOPS · {r.WriteLatencyUs:N0} мкс" : "";
            return new DiskResultRow(spec.Name, spec.Hint, Mbps(r.ReadMBps), Mbps(r.WriteMBps), readExtra, writeExtra,
                Delta(r.ReadMBps, p?.ReadMBps), Delta(r.WriteMBps, p?.WriteMBps));
        }).ToList();

        double seq = record.Results.FirstOrDefault()?.ReadMBps ?? 0;
        double max = Math.Max(seq, DiskClasses.All.Max(c => c.SequentialRead));
        var rows = DiskClasses.All
            .Select(c => (Title: c.Name, Subtitle: "типичная скорость по паспорту", Value: c.SequentialRead, Current: false))
            .Append((Title: $"Диск {record.Drive}", Subtitle: record.Model.Length > 0 ? $"{record.Model} · ваш замер" : "ваш замер",
                Value: seq, Current: true))
            .OrderByDescending(r => r.Value)
            .ThenByDescending(r => r.Current)
            .Select(r => new DiskClassRow(r.Title, r.Subtitle, r.Value, max, r.Current))
            .ToList();
        ClassRows = rows;

        DiskClasses.DiskClass closest = DiskClasses.Closest(seq);
        ClassSummary = $"По последовательному чтению диск на уровне «{closest.Name}».";
        OnPropertyChanged(nameof(ResultCaption));
    }

    private static string Mbps(double value) => value >= 100 ? value.ToString("N0") : value.ToString("0.00");

    private static string Delta(double now, double? before)
    {
        if (before is not { } b || b <= 0)
            return "";
        double percent = (now - b) / b * 100;
        return Math.Abs(percent) < 1 ? "как в прошлый раз" : $"{percent:+0;-0} % к прошлому";
    }

    private void ShowResult(string title, string text, string kind)
    {
        ResultKind = kind;
        ResultText = text;
        ResultTitle = title;
    }

    public void Shutdown() => _cancel?.Cancel();
}
