using System.IO;
using System.Windows.Input;
using PcToolkit.Core;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Storage;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Диск в списке выбора.</summary>
public sealed class DriveChoiceViewModel : ObservableObject
{
    private bool _isSelected;

    public DriveChoiceViewModel(DriveInfo drive, Action<DriveChoiceViewModel> selected)
    {
        RootPath = drive.RootDirectory.FullName;
        string letter = RootPath.TrimEnd('\\');
        string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Локальный диск" : drive.VolumeLabel;
        Title = $"{label} ({letter})";
        TotalBytes = drive.TotalSize;
        UsedBytes = drive.TotalSize - drive.TotalFreeSpace;
        UsedFraction = TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;
        Details = $"Занято {Formatting.Bytes((ulong)UsedBytes)} из {Formatting.Bytes((ulong)TotalBytes)}, " +
                  $"свободно {Formatting.Bytes((ulong)drive.TotalFreeSpace)}";
        _selected = selected;
    }

    private readonly Action<DriveChoiceViewModel> _selected;

    public string RootPath { get; }

    public string Title { get; }

    public string Details { get; }

    public long TotalBytes { get; }

    public long UsedBytes { get; }

    public double UsedFraction { get; }

    /// <summary>Ширина полоски заполнения в карточке диска (полная ширина — 200).</summary>
    public double UsedBarWidth => Math.Round(UsedFraction * 200);

    public bool IsAlmostFull => UsedFraction >= 0.9;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value)
                _selected(this);
        }
    }
}

/// <summary>Строка папки или файла в списке анализатора.</summary>
public sealed class SizeRowViewModel
{
    private const double BarFullWidth = 220;

    public SizeRowViewModel(string name, string path, long size, long parentSize, string details, bool isFolder,
                            FolderNode? folder, Action<FolderNode>? open)
    {
        Name = name;
        Path = path;
        Size = size;
        SizeText = Formatting.Bytes((ulong)Math.Max(size, 0));
        double fraction = parentSize > 0 ? (double)size / parentSize : 0;
        PercentText = parentSize > 0 ? $"{fraction * 100:0.#} %" : "";
        BarWidth = Math.Max(Math.Round(fraction * BarFullWidth), size > 0 ? 2 : 0);
        Details = details;
        IsFolder = isFolder;
        CanOpen = folder is { Children.Count: > 0 } && open is not null;

        OpenCommand = new RelayCommand(() =>
        {
            if (folder is not null && open is not null)
                open(folder);
        }, () => CanOpen);
        ShowInExplorerCommand = new RelayCommand(() =>
        {
            if (isFolder)
                ShellService.OpenFolder(path);
            else
                ShellService.ShowFileInExplorer(path);
        });
    }

    public string Name { get; }

    public string Path { get; }

    public long Size { get; }

    public string SizeText { get; }

    public string PercentText { get; }

    public double BarWidth { get; }

    public string Details { get; }

    public bool IsFolder { get; }

    public bool CanOpen { get; }

    public ICommand OpenCommand { get; }

    public ICommand ShowInExplorerCommand { get; }
}

/// <summary>Вкладка «Место» экрана «Диски»: что занимает место, по папкам и крупным файлам. Только чтение.</summary>
public sealed class DiskAnalyzerViewModel : ObservableObject
{
    private readonly bool _isElevated;
    private CancellationTokenSource? _cancel;
    private DriveChoiceViewModel? _selectedDrive;
    private DiskScanResult? _result;
    private FolderNode? _current;
    private bool _isScanning;
    private bool _isFoldersMode = true;
    private string _progressText = "";
    private IReadOnlyList<SizeRowViewModel> _rows = Array.Empty<SizeRowViewModel>();
    private IReadOnlyList<SizeRowViewModel> _largestFiles = Array.Empty<SizeRowViewModel>();
    private IReadOnlyList<DriveChoiceViewModel> _drives = Array.Empty<DriveChoiceViewModel>();

    public DiskAnalyzerViewModel(bool isElevated, ICommand restartAsAdminCommand)
    {
        _isElevated = isElevated;
        RestartAsAdminCommand = restartAsAdminCommand;
        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !IsScanning && SelectedDrive is not null);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsScanning);
        UpCommand = new RelayCommand(() => Navigate(_current?.Parent), () => _current?.Parent is not null);
        RefreshDrivesCommand = new RelayCommand(LoadDrives, () => !IsScanning);
    }

    public IReadOnlyList<DriveChoiceViewModel> Drives
    {
        get => _drives;
        private set => SetProperty(ref _drives, value);
    }

    public ICommand ScanCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand UpCommand { get; }

    public ICommand RefreshDrivesCommand { get; }

    public ICommand RestartAsAdminCommand { get; }

    public bool ShowAdminHint => !_isElevated;

    public DriveChoiceViewModel? SelectedDrive
    {
        get => _selectedDrive;
        private set
        {
            if (SetProperty(ref _selectedDrive, value))
                RelayCommand.Refresh();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
                RelayCommand.Refresh();
        }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public bool HasResult => _result is not null;

    public bool IsFoldersMode
    {
        get => _isFoldersMode;
        set
        {
            if (SetProperty(ref _isFoldersMode, value))
                OnPropertyChanged(nameof(IsFilesMode));
        }
    }

    public bool IsFilesMode
    {
        get => !_isFoldersMode;
        set => IsFoldersMode = !value;
    }

    public string SummaryText
    {
        get
        {
            if (_result is null || SelectedDrive is null)
                return "";

            string text = $"Просканировано {_result.Files:N0} {Formatting.Plural(_result.Files, "файл", "файла", "файлов")} " +
                          $"за {_result.Duration.TotalSeconds:0} с. Учтено {Formatting.Bytes((ulong)_result.Root.Size)} " +
                          $"из {Formatting.Bytes((ulong)SelectedDrive.UsedBytes)} занятых на диске.";
            if (_result.InaccessibleFolders > 0)
            {
                text += $" Не удалось открыть {_result.InaccessibleFolders:N0} " +
                        $"{Formatting.Plural(_result.InaccessibleFolders, "папку", "папки", "папок")}";
                text += _isElevated ? "." : " — с правами администратора результат будет точнее.";
            }

            return text;
        }
    }

    public string CurrentPath => _current?.FullPath ?? "";

    public string CurrentSizeText => _current is null ? "" : Formatting.Bytes((ulong)_current.Size);

    public IReadOnlyList<SizeRowViewModel> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    public IReadOnlyList<SizeRowViewModel> LargestFiles
    {
        get => _largestFiles;
        private set => SetProperty(ref _largestFiles, value);
    }

    /// <summary>Список дисков читается при первом открытии экрана.</summary>
    public void EnsureLoaded()
    {
        if (Drives.Count == 0)
            LoadDrives();
    }

    private void LoadDrives()
    {
        string? previous = SelectedDrive?.RootPath;
        var drives = new List<DriveChoiceViewModel>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                    drives.Add(new DriveChoiceViewModel(drive, d => SelectedDrive = d));
            }
            catch (IOException ex)
            {
                AppLog.Warn($"Диск {drive.Name} недоступен.", ex);
            }
        }

        Drives = drives;
        DriveChoiceViewModel? select = drives.FirstOrDefault(d => d.RootPath == previous)
                                       ?? drives.FirstOrDefault(d => d.RootPath.StartsWith(
                                           Path.GetPathRoot(Environment.SystemDirectory) ?? "C:", StringComparison.OrdinalIgnoreCase))
                                       ?? drives.FirstOrDefault();
        if (select is not null)
            select.IsSelected = true;
    }

    private async Task ScanAsync()
    {
        DriveChoiceViewModel? drive = SelectedDrive;
        if (drive is null || IsScanning)
            return;

        _cancel = new CancellationTokenSource();
        IsScanning = true;
        ProgressText = "Начинаю…";
        var progress = new Progress<DiskScanProgress>(p =>
            ProgressText = $"{p.Files:N0} {Formatting.Plural(p.Files, "файл", "файла", "файлов")}, " +
                           $"{Formatting.Bytes((ulong)p.Bytes)} · {p.CurrentFolder}");
        try
        {
            CancellationToken token = _cancel.Token;
            DiskScanResult result = await Task.Run(() => DiskScanner.Scan(drive.RootPath, progress, token), token);
            _result = result;
            LargestFiles = result.LargestFiles
                .Select(f => new SizeRowViewModel(
                    Path.GetFileName(f.Path), f.Path, f.Size, result.LargestFiles[0].Size,
                    KnownLocations.DescribeFile(f.Path), isFolder: false, folder: null, open: null))
                .ToList();
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(SummaryText));
            Navigate(result.Root);
            ProgressText = "";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Анализ остановлен.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Анализ диска завершился ошибкой.", ex);
            ProgressText = "Не удалось проанализировать диск: " + ex.Message;
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            IsScanning = false;
        }
    }

    private void Navigate(FolderNode? folder)
    {
        if (folder is null)
            return;

        _current = folder;
        var rows = folder.Children
            .Select(child => new SizeRowViewModel(
                child.Name,
                child.FullPath,
                child.Size,
                folder.Size,
                child.Inaccessible
                    ? "нет доступа"
                    : KnownLocations.DescribeFolder(child.FullPath,
                        $"{child.FileCount:N0} {Formatting.Plural(child.FileCount, "файл", "файла", "файлов")}"),
                isFolder: true,
                folder: child,
                open: Navigate))
            .ToList();

        if (folder.OwnFileCount > 0)
        {
            rows.Add(new SizeRowViewModel(
                "Файлы в этой папке",
                folder.FullPath,
                folder.OwnFilesSize,
                folder.Size,
                $"{folder.OwnFileCount:N0} {Formatting.Plural(folder.OwnFileCount, "файл", "файла", "файлов")}",
                isFolder: true,
                folder: null,
                open: null));
        }

        // Папок бывают десятки тысяч (например, WinSxS): показываем крупнейшие, остальное одной строкой.
        const int maxRows = 200;
        List<SizeRowViewModel> sorted = rows.OrderByDescending(r => r.Size).ToList();
        if (sorted.Count > maxRows)
        {
            List<SizeRowViewModel> rest = sorted.Skip(maxRows).ToList();
            sorted = sorted.Take(maxRows).ToList();
            sorted.Add(new SizeRowViewModel(
                $"Ещё {rest.Count:N0} {Formatting.Plural(rest.Count, "элемент", "элемента", "элементов")}",
                folder.FullPath,
                rest.Sum(r => r.Size),
                folder.Size,
                "мелкие папки и файлы",
                isFolder: true,
                folder: null,
                open: null));
        }

        Rows = sorted;
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(CurrentSizeText));
        RelayCommand.Refresh();
    }
}

/// <summary>Понятные подписи для системных файлов и папок, которые обычно занимают много места.</summary>
internal static class KnownLocations
{
    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hiberfil.sys"] = "Файл гибернации Windows. Его размер задаёт система",
        ["pagefile.sys"] = "Файл подкачки Windows. Его размер задаёт система",
        ["swapfile.sys"] = "Файл подкачки для приложений из Microsoft Store",
        ["memory.dmp"] = "Полный дамп памяти после синего экрана",
    };

    private static readonly Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$Recycle.Bin"] = "Корзина — очищается на экране «Очистка»",
        ["System Volume Information"] = "Точки восстановления и служебные данные Windows",
        ["WinSxS"] = "Хранилище компонентов Windows. Часть файлов учтена дважды из-за жёстких ссылок",
        ["Windows.old"] = "Копия прошлой версии Windows после обновления",
        ["SoftwareDistribution"] = "Загрузки Центра обновления Windows",
    };

    public static string DescribeFile(string path)
    {
        string folder = Path.GetDirectoryName(path) ?? "";
        if (Files.TryGetValue(Path.GetFileName(path), out string? known))
            return known;

        return path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase)
            ? "В корзине — очищается на экране «Очистка»"
            : folder;
    }

    public static string DescribeFolder(string path, string fallback) =>
        Folders.TryGetValue(Path.GetFileName(path), out string? known) ? $"{fallback} · {known}" : fallback;
}
