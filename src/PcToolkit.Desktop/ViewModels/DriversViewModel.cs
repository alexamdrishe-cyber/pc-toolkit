using System.Collections.ObjectModel;
using System.Windows.Input;
using PcToolkit.Core.Diagnostics;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Logging;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Строка экрана «Драйверы».</summary>
public sealed class DriverItemViewModel
{
    public DriverItemViewModel(DriverItem item, ICommand? reinstallPawnIoCommand = null)
    {
        if (item.Action == DriverActions.ReinstallPawnIo && reinstallPawnIoCommand is not null)
        {
            ActionTitle = "Переустановить PawnIO";
            ActionCommand = reinstallPawnIoCommand;
        }

        Category = item.Category;
        Title = item.Title;
        Details = item.Details;
        Level = item.Level;
        AdviceText = item.Advice is null ? null : (item.Level is HealthLevel.Good or HealthLevel.Fact ? item.Advice : "Что делать: " + item.Advice);
        LinkTitle = item.LinkTitle;
        string? url = item.LinkUrl;
        OpenLinkCommand = new RelayCommand(() =>
        {
            if (url is not null)
                ShellService.OpenUrl(url);
        });
    }

    public string Category { get; }

    public string Title { get; }

    public string Details { get; }

    public HealthLevel Level { get; }

    public string? AdviceText { get; }

    public string? LinkTitle { get; }

    public ICommand OpenLinkCommand { get; }

    /// <summary>Что приложение может сделать само («Переустановить PawnIO»); null — только совет.</summary>
    public string? ActionTitle { get; }

    public ICommand? ActionCommand { get; }
}

/// <summary>
/// Экран «Драйверы»: устройства без драйвера или с ошибкой и возраст важных драйверов.
/// Ничего не устанавливает — только объясняет и даёт ссылку на официальный сайт.
/// </summary>
public sealed class DriversViewModel : ObservableObject
{
    private readonly SemaphoreSlim _busy = new(1, 1);
    private bool _isLoading;
    private bool _loaded;
    private string _summary = "Драйверы ещё не проверялись.";
    private string _statusText = "";
    private string? _error;
    private HealthLevel _level = HealthLevel.Good;
    private DriverReport? _report;
    private readonly ICommand? _reinstallPawnIoCommand;

    public DriversViewModel(ICommand? reinstallPawnIoCommand = null)
    {
        _reinstallPawnIoCommand = reinstallPawnIoCommand;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsLoading);
        OpenDeviceManagerCommand = new RelayCommand(() => ShellService.OpenSystemTool("devmgmt.msc"));
    }

    public ObservableCollection<DriverItemViewModel> Problems { get; } = new();

    public ObservableCollection<DriverItemViewModel> Drivers { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand OpenDeviceManagerCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                RelayCommand.Refresh();
        }
    }

    public bool HasProblems => Problems.Count > 0;

    public bool HasDrivers => Drivers.Count > 0;

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public HealthLevel Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Последняя проверка — для проверки системы на главной.</summary>
    public DriverReport? Report => _report;

    public event EventHandler? ReportChanged;

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
            StatusText = "Проверяю устройства и драйверы…";
            DriverReport report = await Task.Run(Check);

            Problems.Clear();
            foreach (DriverItem item in report.Problems)
                Problems.Add(new DriverItemViewModel(item, _reinstallPawnIoCommand));
            Drivers.Clear();
            foreach (DriverItem item in report.Drivers)
                Drivers.Add(new DriverItemViewModel(item));
            OnPropertyChanged(nameof(HasProblems));
            OnPropertyChanged(nameof(HasDrivers));
            Summary = report.Summary;
            Level = report.Level;
            Error = report.Error;
            _report = report;
            _loaded = true;
            StatusText = $"Проверено в {DateTime.Now:HH:mm}";
            ReportChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось проверить драйверы.", ex);
            StatusText = "Не удалось проверить драйверы: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            _busy.Release();
        }
    }

    /// <summary>Читает устройства и драйверы и оценивает их (в фоновом потоке).</summary>
    public static DriverReport Check()
    {
        (List<PnpDeviceRaw> problems, List<PnpDeviceRaw> displays, List<SignedDriverRaw> drivers, string? error) = DriverReader.Read();
        DriverReport report = DriverAnalyzer.Analyze(problems, displays, drivers, DateTime.Now, error);
        AppLog.Info($"Проверка драйверов: проблемных устройств {report.Problems.Count}, драйверов в списке {report.Drivers.Count}. {report.Summary}");
        return report;
    }
}
