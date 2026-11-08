using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using PcToolkit.Core;
using PcToolkit.Core.Diagnostics;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Logging;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Строка «когда — что» в развёрнутой карточке сбоя.</summary>
public sealed record CrashOccurrenceRow(string When, string Text);

/// <summary>Карточка однотипных сбоев: что это, почему бывает, что делать и список случаев.</summary>
public sealed class CrashGroupViewModel : ObservableObject
{
    private const int MaxRows = 15;
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly int _count;
    private bool _isExpanded;

    public CrashGroupViewModel(CrashGroup group)
    {
        _count = group.Count;
        Title = group.Title;
        Level = group.Level;
        What = group.What;
        CausesText = group.Causes is null ? null : "Частые причины: " + group.Causes;
        AdviceText = group.Advice is null ? null : "Что делать: " + group.Advice;
        LastText = group.Count == 0 ? "" : "Последний раз: " + When(group.Last);
        Rows = group.Occurrences.Take(MaxRows).Select(o => new CrashOccurrenceRow(When(o.Time), o.Text)).ToList();
        MoreText = group.Count > MaxRows ? $"и ещё {group.Count - MaxRows} — полный список в «Просмотре событий» Windows" : null;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string Title { get; }

    public HealthLevel Level { get; }

    public string What { get; }

    public string? CausesText { get; }

    public string? AdviceText { get; }

    public string LastText { get; }

    public IReadOnlyList<CrashOccurrenceRow> Rows { get; }

    public string? MoreText { get; }

    public ICommand ToggleCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                OnPropertyChanged(nameof(ToggleText));
        }
    }

    public string ToggleText => _isExpanded ? "Скрыть случаи" : $"Когда это было ({_count})";

    private static string When(DateTime time) => time.ToString("d MMMM, HH:mm", Russian);
}

/// <summary>
/// Экран «Сбои»: синие экраны, внезапные выключения, аппаратные ошибки, сбросы видеодрайвера, ошибки дисков
/// и вылеты программ из журнала Windows с объяснениями. Только чтение.
/// </summary>
public sealed class CrashesViewModel : ObservableObject
{
    private readonly SemaphoreSlim _busy = new(1, 1);
    private bool _isLoading;
    private bool _loaded;
    private bool _refreshAgain;
    private string _summary = "Журнал ещё не прочитан.";
    private string _statusText = "";
    private string? _readError;
    private HealthLevel _level = HealthLevel.Good;
    private IReadOnlyList<string> _hints = Array.Empty<string>();
    private CrashReport? _report;

    private readonly Func<IReadOnlyDictionary<int, string>>? _diskNames;

    /// <param name="diskNames">Номер диска → модель (из проверки дисков): «диск 1 · MSI M480 PRO 1TB».</param>
    public CrashesViewModel(Func<IReadOnlyDictionary<int, string>>? diskNames = null)
    {
        _diskNames = diskNames;
        Periods = new[]
        {
            new ChoiceViewModel("crash-period", "7 дней", 7, false),
            new ChoiceViewModel("crash-period", "30 дней", 30, true),
            new ChoiceViewModel("crash-period", "90 дней", 90, false),
        };
        foreach (ChoiceViewModel period in Periods)
        {
            period.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(ChoiceViewModel.IsSelected) && sender is ChoiceViewModel { IsSelected: true } && _loaded)
                    _ = RefreshAsync();
            };
        }

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsLoading);
        OpenEventViewerCommand = new RelayCommand(() => ShellService.OpenSystemTool("eventvwr.msc"));
        OpenDumpsCommand = new RelayCommand(() => ShellService.OpenFolder(DumpsFolder), () => Directory.Exists(DumpsFolder));
    }

    public static string DumpsFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");

    public IReadOnlyList<ChoiceViewModel> Periods { get; }

    public ObservableCollection<CrashGroupViewModel> Groups { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand OpenEventViewerCommand { get; }

    public ICommand OpenDumpsCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                RelayCommand.Refresh();
        }
    }

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

    public IReadOnlyList<string> Hints
    {
        get => _hints;
        private set
        {
            if (SetProperty(ref _hints, value))
                OnPropertyChanged(nameof(HasHints));
        }
    }

    public bool HasHints => _hints.Count > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? ReadError
    {
        get => _readError;
        private set => SetProperty(ref _readError, value);
    }

    /// <summary>Последний разбор — для проверки системы на главной.</summary>
    public CrashReport? Report => _report;

    public event EventHandler? ReportChanged;

    private int Days => Periods.FirstOrDefault(p => p.IsSelected)?.Value ?? 30;

    public void EnsureLoaded()
    {
        if (!_loaded && !_isLoading)
            _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!await _busy.WaitAsync(0))
        {
            // Период сменили во время чтения — перечитаем, когда закончится текущее.
            _refreshAgain = true;
            return;
        }

        try
        {
            _refreshAgain = false;
            IsLoading = true;
            StatusText = "Читаю журнал Windows…";
            int days = Days;
            DateTime now = DateTime.Now;
            IReadOnlyDictionary<int, string>? names = _diskNames?.Invoke();
            CrashReport report = await Task.Run(() => Read(days, now, names));

            Groups.Clear();
            foreach (CrashGroup group in report.Groups)
                Groups.Add(new CrashGroupViewModel(group));
            Summary = report.Summary;
            Level = report.Level;
            Hints = report.Hints;
            ReadError = report.ReadError;
            _report = report;
            _loaded = true;
            StatusText = $"Проверено в {now:HH:mm}";
            ReportChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось разобрать журнал Windows.", ex);
            StatusText = "Не удалось прочитать журнал: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            _busy.Release();
        }

        if (_refreshAgain)
            await RefreshAsync();
    }

    /// <summary>Читает и разбирает журнал за <paramref name="days"/> дней (в фоновом потоке).</summary>
    public static CrashReport Read(int days, DateTime now, IReadOnlyDictionary<int, string>? diskNames = null)
    {
        DateTime from = now.AddDays(-days);
        List<RawEvent> events = CrashEventReader.Read(from, out string? error);
        CrashReport report = CrashAnalyzer.Analyze(events, from, now, error, diskNames);
        AppLog.Info($"Журнал сбоев за {days} дн.: событий {events.Count}. {report.Summary}");
        return report;
    }
}
