using System.Windows.Input;
using PcToolkit.Core;
using PcToolkit.Core.Cleanup;
using PcToolkit.Core.Logging;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Показывает окно поверх всего приложения (затемнение + карточка) и закрывает его.</summary>
public interface IOverlayHost
{
    void ShowOverlay(object overlay);

    void CloseOverlay();
}

/// <summary>Строка правила очистки.</summary>
public sealed class CleanupItemViewModel : ObservableObject
{
    private CleanupScanResult? _scan;
    private bool _isScanning;
    private bool _isSelected;

    public CleanupItemViewModel(CleanupRule rule)
    {
        Rule = rule;
    }

    public event EventHandler? SelectionChanged;

    public CleanupRule Rule { get; }

    public string Title => Rule.Title;

    public string Description => Rule.Description;

    public string Note => Rule.Note ?? "";

    /// <summary>Подсказку показываем, только когда есть что чистить: у пустого пункта она лишняя.</summary>
    public bool HasNote => Note.Length > 0 && HasSize;

    public bool ShowAdminBadge => Rule.RequiresAdmin;

    public CleanupScanResult? Scan
    {
        get => _scan;
        private set
        {
            if (!SetProperty(ref _scan, value))
                return;

            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(CanSelect));
            OnPropertyChanged(nameof(HasSize));
            OnPropertyChanged(nameof(HasNote));
            OnPropertyChanged(nameof(WarningText));
            OnPropertyChanged(nameof(HasWarning));
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public long Bytes => Scan?.CanClean == true ? Scan.TotalBytes : 0;

    public bool CanSelect => !IsScanning && Scan?.CanClean == true;

    public bool HasSize => Scan?.CanClean == true;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            bool newValue = value && CanSelect;
            if (SetProperty(ref _isSelected, newValue))
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string SizeText => IsScanning
        ? "…"
        : Scan?.CanClean == true ? Formatting.Bytes((ulong)Scan.TotalBytes) : "";

    public string DetailText
    {
        get
        {
            CleanupScanResult? scan = Scan;
            if (IsScanning)
                return "ищу файлы…";
            if (scan is null)
                return "";

            return scan.State switch
            {
                CleanupScanState.NeedsAdmin => "нужны права администратора",
                CleanupScanState.Failed => "не удалось проверить",
                CleanupScanState.Empty => "чисто",
                _ when scan.Rule.Kind == CleanupKind.RecycleBin =>
                    $"{scan.ItemCount} {Formatting.Plural(scan.ItemCount, "объект", "объекта", "объектов")}",
                _ => $"{scan.ItemCount:N0} {Formatting.Plural(scan.ItemCount, "файл", "файла", "файлов")}",
            };
        }
    }

    public string WarningText => Scan is { RunningProcesses.Count: > 0, CanClean: true }
        ? "Программа сейчас открыта: занятые файлы будут пропущены. Для полной очистки закройте её."
        : "";

    public bool HasWarning => WarningText.Length > 0;

    public void BeginScan()
    {
        IsScanning = true;
        OnPropertyChanged(nameof(CanSelect));
    }

    public void SetScan(CleanupScanResult scan)
    {
        IsScanning = false;
        Scan = scan;
        if (!CanSelect)
            IsSelected = false;
    }
}

public sealed class CleanupCategoryViewModel
{
    public CleanupCategoryViewModel(CleanupCategory category, IReadOnlyList<CleanupItemViewModel> items)
    {
        Title = category.Title;
        Description = category.Description;
        Items = items;
    }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<CleanupItemViewModel> Items { get; }
}

/// <summary>Экран «Очистка»: сканирование по правилам, выбор, предпросмотр.</summary>
public sealed class CleanupViewModel : ObservableObject
{
    private readonly CleanupHistory _history;
    private readonly OperationGate _gate;
    private readonly IOverlayHost _host;
    private readonly bool _isElevated;
    private readonly List<CleanupItemViewModel> _items;
    private readonly Dictionary<string, bool> _userChoice = new(StringComparer.OrdinalIgnoreCase);
    private bool _isScanning;
    private bool _scannedOnce;
    private bool _applyingDefaults;

    public CleanupViewModel(CleanupCatalog catalog, CleanupHistory history, OperationGate gate, IOverlayHost host,
                            bool isElevated, ICommand restartAsAdminCommand)
    {
        _history = history;
        _gate = gate;
        _host = host;
        _isElevated = isElevated;
        RestartAsAdminCommand = restartAsAdminCommand;

        Categories = catalog.Categories
            .Select(c => new CleanupCategoryViewModel(c, catalog.Rules
                .Where(r => string.Equals(r.Category, c.Id, StringComparison.OrdinalIgnoreCase))
                .Select(r => new CleanupItemViewModel(r))
                .ToList()))
            .Where(c => c.Items.Count > 0)
            .ToList();

        _items = Categories.SelectMany(c => c.Items).ToList();
        foreach (CleanupItemViewModel item in _items)
            item.SelectionChanged += OnItemSelectionChanged;

        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !_isScanning);
        PreviewCommand = new RelayCommand(Preview, () => !_isScanning && SelectedBytes > 0);
    }

    public IReadOnlyList<CleanupCategoryViewModel> Categories { get; }

    public ICommand ScanCommand { get; }

    public ICommand PreviewCommand { get; }

    public ICommand RestartAsAdminCommand { get; }

    public bool ShowAdminBanner => !_isElevated;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(TotalText));
                RelayCommand.Refresh();
            }
        }
    }

    public long FoundBytes => _items.Sum(i => i.Bytes);

    public long SelectedBytes => _items.Where(i => i.IsSelected).Sum(i => i.Bytes);

    public string TotalText => IsScanning
        ? "Ищу файлы…"
        : _scannedOnce ? $"Можно освободить до {Formatting.Bytes((ulong)FoundBytes)}" : "";

    public string SelectedText => SelectedBytes == 0
        ? "Отметьте, что нужно очистить."
        : $"Выбрано к очистке: {Formatting.Bytes((ulong)SelectedBytes)}";

    public string HistoryText
    {
        get
        {
            CleanupRunRecord? last = _history.Records.FirstOrDefault();
            return last is null
                ? "Очистка ещё не запускалась."
                : $"Последняя очистка {last.At.LocalDateTime:d MMMM, HH:mm}: освобождено {Formatting.Bytes((ulong)last.FreedBytes)}.";
        }
    }

    /// <summary>Сканирует при первом открытии экрана, а не при запуске приложения.</summary>
    public void EnsureScanned()
    {
        if (!_scannedOnce && !_isScanning)
            _ = ScanAsync();
    }

    public async Task ScanAsync()
    {
        if (_isScanning)
            return;

        IsScanning = true;
        foreach (CleanupItemViewModel item in _items)
            item.BeginScan();

        try
        {
            foreach (CleanupItemViewModel item in _items)
            {
                CleanupScanResult scan = await Task.Run(() => CleanupEngine.Scan(item.Rule, _isElevated));
                item.SetScan(scan);
                RestoreSelection(item);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Очистка: ошибка при сканировании.", ex);
        }
        finally
        {
            _scannedOnce = true;
            IsScanning = false;
            RaiseTotals();
        }
    }

    private void RestoreSelection(CleanupItemViewModel item)
    {
        _applyingDefaults = true;
        try
        {
            item.IsSelected = _userChoice.TryGetValue(item.Rule.Id, out bool chosen) ? chosen : item.Rule.SelectedByDefault;
        }
        finally
        {
            _applyingDefaults = false;
        }

        RaiseTotals();
    }

    private void OnItemSelectionChanged(object? sender, EventArgs e)
    {
        if (!_applyingDefaults && sender is CleanupItemViewModel item && item.CanSelect)
            _userChoice[item.Rule.Id] = item.IsSelected;

        RaiseTotals();
    }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(FoundBytes));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(SelectedText));
        RelayCommand.Refresh();
    }

    private void Preview()
    {
        List<CleanupScanResult> selected = _items
            .Where(i => i.IsSelected && i.Scan is { CanClean: true })
            .Select(i => i.Scan!)
            .ToList();
        if (selected.Count == 0)
            return;

        _host.ShowOverlay(new CleanupPreviewViewModel(selected, _gate, _history, OnPreviewClosed));
    }

    private void OnPreviewClosed(bool cleaned)
    {
        _host.CloseOverlay();
        if (!cleaned)
            return;

        OnPropertyChanged(nameof(HistoryText));
        _ = ScanAsync();
    }
}

public sealed record CleanupPreviewRow(string Title, string Files, string Size);

/// <summary>Окно «что будет удалено» и результат очистки.</summary>
public sealed class CleanupPreviewViewModel : ObservableObject
{
    private readonly IReadOnlyList<CleanupScanResult> _scans;
    private readonly OperationGate _gate;
    private readonly CleanupHistory _history;
    private readonly Action<bool> _onClosed;
    private bool _cleaned;
    private bool _isBusy;
    private bool _isDone;
    private string _resultTitle = "";
    private string _resultText = "";
    private bool _resultIsSuccess;

    public CleanupPreviewViewModel(IReadOnlyList<CleanupScanResult> scans, OperationGate gate, CleanupHistory history, Action<bool> onClosed)
    {
        _scans = scans;
        _gate = gate;
        _history = history;
        _onClosed = onClosed;

        Rows = scans
            .Select(s => new CleanupPreviewRow(
                s.Rule.Title,
                s.Rule.Kind == CleanupKind.RecycleBin
                    ? $"{s.ItemCount} {Formatting.Plural(s.ItemCount, "объект", "объекта", "объектов")}"
                    : $"{s.ItemCount:N0}",
                Formatting.Bytes((ulong)s.TotalBytes)))
            .ToList();

        long total = scans.Sum(s => s.TotalBytes);
        long files = scans.Sum(s => s.ItemCount);
        Summary = $"Будет удалено до {Formatting.Bytes((ulong)total)} — {files:N0} {Formatting.Plural(files, "файл", "файла", "файлов")}. " +
                  "Файлы удаляются сразу, мимо корзины, и вернуть их нельзя.";

        var warnings = new List<string>();
        foreach (CleanupScanResult scan in scans)
        {
            if (scan.RunningProcesses.Count > 0)
                warnings.Add($"{scan.Rule.Title}: программа открыта, занятые ею файлы будут пропущены.");
            if (!string.IsNullOrEmpty(scan.Rule.Note))
                warnings.Add($"{scan.Rule.Title}: {scan.Rule.Note}");
        }

        Warnings = warnings;
        CleanCommand = new RelayCommand(Clean, () => !IsBusy && !IsDone);
        CloseCommand = new RelayCommand(() => _onClosed(_cleaned), () => !IsBusy);
    }

    public string Title => "Очистка";

    public string Summary { get; }

    public IReadOnlyList<CleanupPreviewRow> Rows { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public ICommand CleanCommand { get; }

    public ICommand CloseCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsDone
    {
        get => _isDone;
        private set
        {
            if (SetProperty(ref _isDone, value))
            {
                OnPropertyChanged(nameof(ShowPlan));
                OnPropertyChanged(nameof(CloseButtonText));
            }
        }
    }

    public bool ShowPlan => !IsDone;

    public string CloseButtonText => IsDone ? "Закрыть" : "Отмена";

    public string ResultTitle
    {
        get => _resultTitle;
        private set => SetProperty(ref _resultTitle, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public bool ResultIsSuccess
    {
        get => _resultIsSuccess;
        private set => SetProperty(ref _resultIsSuccess, value);
    }

    private async void Clean()
    {
        IDisposable? slot = _gate.TryEnter("Очистка", out string? busyWith);
        if (slot is null)
        {
            ResultIsSuccess = false;
            ResultTitle = "Подождите";
            ResultText = $"Сейчас выполняется другая операция: «{busyWith}». Очистить можно, когда она закончится.";
            IsDone = true;
            RelayCommand.Refresh();
            return;
        }

        try
        {
            IsBusy = true;
            RelayCommand.Refresh();

            CleanupRunResult result = await Task.Run(() => CleanupEngine.Clean(_scans));
            _cleaned = true;
            _history.Add(new CleanupRunRecord
            {
                At = DateTimeOffset.Now,
                FreedBytes = result.FreedBytes,
                Deleted = result.Deleted,
                Skipped = result.Skipped,
                Rules = _scans.Select(s => s.Rule.Id).ToList(),
            });

            ResultIsSuccess = true;
            ResultTitle = $"Освобождено {Formatting.Bytes((ulong)result.FreedBytes)}";
            string text = $"Удалено {result.Deleted:N0} {Formatting.Plural(result.Deleted, "файл", "файла", "файлов")}.";
            if (result.Skipped > 0)
                text += $" Пропущено {result.Skipped:N0}: их держат открытые программы или они изменились после проверки.";
            ResultText = text;
        }
        catch (Exception ex)
        {
            AppLog.Error("Очистка: ошибка при удалении.", ex);
            ResultIsSuccess = false;
            ResultTitle = "Не получилось";
            ResultText = ex.Message;
        }
        finally
        {
            slot.Dispose();
            IsBusy = false;
            IsDone = true;
            RelayCommand.Refresh();
        }
    }
}
