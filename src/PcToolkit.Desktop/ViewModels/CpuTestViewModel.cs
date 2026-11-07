using System.Windows.Input;
using System.Windows.Threading;
using PcToolkit.Core;
using PcToolkit.Core.Benchmarks;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Sensors;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Галочка вида нагрузки.</summary>
public sealed class WorkloadOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public WorkloadOptionViewModel(CpuWorkload workload, string title, string description, bool selected)
    {
        Workload = workload;
        Title = title;
        Description = description;
        _isSelected = selected;
    }

    public CpuWorkload Workload { get; }

    public string Title { get; }

    public string Description { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                RelayCommand.Refresh();
        }
    }
}

/// <summary>Вариант выбора (длительность, лимит температуры).</summary>
public sealed class ChoiceViewModel : ObservableObject
{
    private bool _isSelected;

    public ChoiceViewModel(string group, string title, int value, bool selected)
    {
        GroupName = group;
        Title = title;
        Value = value;
        _isSelected = selected;
    }

    public string GroupName { get; }

    public string Title { get; }

    public int Value { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed record BenchmarkRow(string Title, string Single, string Multi, string Previous);

public enum ComparisonKind
{
    Current,
    Imported,
    Estimate,
}

/// <summary>Строка рейтинга: результат этого ПК, добавленный из файла или оценка популярного процессора.</summary>
public sealed class ComparisonRowViewModel
{
    public ComparisonRowViewModel(string title, string subtitle, CpuScoreResult score, ComparisonKind kind,
        double fraction, bool bySingle, CpuBenchmarkRecord? record = null, Action<ComparisonRowViewModel>? remove = null)
    {
        Title = title;
        Subtitle = subtitle;
        Kind = kind;
        Record = record;
        SingleText = score.SingleThread.ToString("N0");
        MultiText = score.MultiThread.ToString("N0");
        IsSingleEmphasized = bySingle;
        double f = Math.Clamp(fraction, 0.02, 1);
        BarLength = new System.Windows.GridLength(f, System.Windows.GridUnitType.Star);
        RestLength = new System.Windows.GridLength(1 - f + 1e-6, System.Windows.GridUnitType.Star);
        CanRemove = remove is not null;
        RemoveCommand = new RelayCommand(() => remove?.Invoke(this), () => CanRemove);
    }

    public CpuBenchmarkRecord? Record { get; }

    public ComparisonKind Kind { get; }

    public bool IsCurrent => Kind == ComparisonKind.Current;

    public bool IsEstimate => Kind == ComparisonKind.Estimate;

    public string Title { get; }

    public string Subtitle { get; }

    public string SingleText { get; }

    public string MultiText { get; }

    public bool IsSingleEmphasized { get; }

    public bool IsMultiEmphasized => !IsSingleEmphasized;

    public System.Windows.GridLength BarLength { get; }

    public System.Windows.GridLength RestLength { get; }

    public bool CanRemove { get; }

    public ICommand RemoveCommand { get; }
}

/// <summary>Вкладка «Процессор»: стресс-тест как в AIDA64 и замер производительности.</summary>
public sealed class CpuTestViewModel : ObservableObject
{
    private const int HistoryCapacity = 600;
    private readonly OperationGate _gate;
    private readonly BenchmarkHistory _history;
    private readonly DispatcherTimer _timer;
    private readonly List<double> _temperatures = new();
    private readonly List<double> _clocks = new();
    private CpuStressTest? _test;
    private IDisposable? _slot;
    private CancellationTokenSource? _benchmarkCancel;
    private bool _isStressMode = true;
    private bool _finishing;
    private bool _isRunning;
    private string _statusText = "";
    private string _elapsedText = "00:00:00";
    private string _resultTitle = "";
    private string _resultText = "";
    private string _resultKind = "";
    private float? _temperature;
    private float? _maxTemperature;
    private float? _clock;
    private float? _power;
    private float? _load;
    private double _clockSum;
    private int _clockSamples;
    private string? _cpuName;
    private bool _temperatureKnown;
    private IReadOnlyList<double> _temperatureHistory = Array.Empty<double>();
    private IReadOnlyList<double> _clockHistory = Array.Empty<double>();
    private IReadOnlyList<BenchmarkRow> _benchmarkRows = Array.Empty<BenchmarkRow>();
    private IReadOnlyList<ComparisonRowViewModel> _comparisonRows = Array.Empty<ComparisonRowViewModel>();
    private CpuBenchmarkRecord? _shownRecord;
    private CpuBenchmarkRecord? _shownPrevious;
    private bool _compareBySingle;
    private string _comparisonMessage = "";
    private string _comparisonSummary = "";
    private bool _showPopular = true;

    public CpuTestViewModel(OperationGate gate, BenchmarkHistory history)
    {
        _gate = gate;
        _history = history;

        Workloads = new[]
        {
            new WorkloadOptionViewModel(CpuWorkload.Integer, "Целочисленные (CPU)",
                "Арифметика и логика ядер: умножения, сдвиги, перестановки битов.", true),
            new WorkloadOptionViewModel(CpuWorkload.Float, "С плавающей точкой (FPU)",
                $"Векторные вычисления {CpuKernels.FloatInstructionSet}. Самая сильная нагрузка и нагрев.", true),
            new WorkloadOptionViewModel(CpuWorkload.Cache, "Кэш процессора",
                "Случайные чтения и записи в блоке размером с кэш L2.", true),
            new WorkloadOptionViewModel(CpuWorkload.Memory, "Оперативная память",
                $"Запись и проверка шаблона в {Formatting.Bytes((ulong)CpuStressTest.DefaultMemoryBytes())} памяти.", true),
        };

        Durations = new[]
        {
            new ChoiceViewModel("cpu-duration", "1 мин", 1, false),
            new ChoiceViewModel("cpu-duration", "5 мин", 5, false),
            new ChoiceViewModel("cpu-duration", "15 мин", 15, true),
            new ChoiceViewModel("cpu-duration", "30 мин", 30, false),
            new ChoiceViewModel("cpu-duration", "1 час", 60, false),
            new ChoiceViewModel("cpu-duration", "Без ограничения", 0, false),
        };

        TemperatureLimits = new[]
        {
            new ChoiceViewModel("cpu-limit", "85 °C", 85, false),
            new ChoiceViewModel("cpu-limit", "90 °C", 90, true),
            new ChoiceViewModel("cpu-limit", "95 °C", 95, false),
        };

        StartCommand = new RelayCommand(() => _ = StartAsync(), () => !IsRunning && Workloads.Any(w => w.IsSelected));
        StopCommand = new RelayCommand(() => StopByUser(), () => IsRunning);
        ExportCommand = new RelayCommand(Export, () => _shownRecord?.Score is not null && !IsRunning);
        ImportCommand = new RelayCommand(Import, () => !IsRunning);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        ShowLastBenchmark();
        RefreshComparison();
    }

    public IReadOnlyList<WorkloadOptionViewModel> Workloads { get; }

    public IReadOnlyList<ChoiceViewModel> Durations { get; }

    public IReadOnlyList<ChoiceViewModel> TemperatureLimits { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand ImportCommand { get; }

    public string ScoreReferenceText => $"1000 баллов — {CpuScore.ReferenceName}. Больше — быстрее.";

    public bool HasScore => _shownRecord?.Score is not null;

    public bool ShowScoreHint => _shownRecord is not null && _shownRecord.Score is null;

    /// <summary>Почему у замера нет итоговых баллов.</summary>
    public string ScoreHintText => _shownRecord is { } record && record.ScoreVersion != CpuScore.Version
        ? "Этот замер сделан прежней версией теста: его баллы с новыми несопоставимы. Повторите замер."
        : "В этом замере выбраны не все нагрузки, поэтому итоговых баллов нет. Ниже — результаты по каждой нагрузке.";

    public string ScoreSingleText => _shownRecord?.Score?.SingleThread.ToString("N0") ?? "—";

    public string ScoreMultiText => _shownRecord?.Score?.MultiThread.ToString("N0") ?? "—";

    public string ScoreSingleDelta => ScoreDelta(s => s.SingleThread);

    public string ScoreMultiDelta => ScoreDelta(s => s.MultiThread);

    public IReadOnlyList<ComparisonRowViewModel> ComparisonRows
    {
        get => _comparisonRows;
        private set
        {
            if (SetProperty(ref _comparisonRows, value))
                OnPropertyChanged(nameof(HasComparisonRows));
        }
    }

    public bool HasComparisonRows => ComparisonRows.Count > 0;

    /// <summary>Показывать в рейтинге популярные процессоры (оценка по Cinebench R23).</summary>
    public bool ShowPopular
    {
        get => _showPopular;
        set
        {
            if (SetProperty(ref _showPopular, value))
                RefreshComparison();
        }
    }

    /// <summary>Место этого ПК в рейтинге словами.</summary>
    public string ComparisonSummary
    {
        get => _comparisonSummary;
        private set
        {
            if (SetProperty(ref _comparisonSummary, value))
                OnPropertyChanged(nameof(HasComparisonSummary));
        }
    }

    public bool HasComparisonSummary => ComparisonSummary.Length > 0;

    public bool CompareBySingle
    {
        get => _compareBySingle;
        set
        {
            if (SetProperty(ref _compareBySingle, value))
            {
                OnPropertyChanged(nameof(CompareByMulti));
                RefreshComparison();
            }
        }
    }

    public bool CompareByMulti
    {
        get => !_compareBySingle;
        set => CompareBySingle = !value;
    }

    /// <summary>Итог последнего добавления из файла.</summary>
    public string ComparisonMessage
    {
        get => _comparisonMessage;
        private set
        {
            if (SetProperty(ref _comparisonMessage, value))
                OnPropertyChanged(nameof(HasComparisonMessage));
        }
    }

    public bool HasComparisonMessage => ComparisonMessage.Length > 0;

    public string ProcessorText =>
        $"{_cpuName ?? "Процессор"} · потоков: {Environment.ProcessorCount} · {CpuKernels.FloatInstructionSet}";

    public bool IsStressMode
    {
        get => _isStressMode;
        set
        {
            if (SetProperty(ref _isStressMode, value))
            {
                OnPropertyChanged(nameof(IsBenchmarkMode));
                OnPropertyChanged(nameof(StartButtonText));
            }
        }
    }

    public bool IsBenchmarkMode
    {
        get => !_isStressMode;
        set => IsStressMode = !value;
    }

    public string StartButtonText => IsStressMode ? "Запустить стресс-тест" : "Запустить замер";

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

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public string ErrorsText => _test is null ? "—" : _test.ErrorCount.ToString("N0");

    public bool HasErrors => _test?.ErrorCount > 0;

    public string TemperatureText => _temperature is { } t ? $"{t:0.0} °C" : "нет данных";

    public string MaxTemperatureText => _maxTemperature is { } t ? $"макс. {t:0.0} °C" : "";

    public string ClockText => _clock is { } c ? $"{c / 1000:0.00} ГГц" : "нет данных";

    public string AverageClockText => _clockSamples > 0 ? $"средн. {_clockSum / _clockSamples / 1000:0.00} ГГц" : "";

    public string PowerText => _power is { } p ? $"{p:0.0} Вт" : "нет данных";

    public string LoadText => _load is { } l ? $"{l:0} %" : "нет данных";

    public bool ShowTemperatureWarning => !_temperatureKnown;

    public IReadOnlyList<double> TemperatureHistory
    {
        get => _temperatureHistory;
        private set => SetProperty(ref _temperatureHistory, value);
    }

    public IReadOnlyList<double> ClockHistory
    {
        get => _clockHistory;
        private set => SetProperty(ref _clockHistory, value);
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

    /// <summary>good | warning | critical | info</summary>
    public string ResultKind
    {
        get => _resultKind;
        private set => SetProperty(ref _resultKind, value);
    }

    public bool HasResult => ResultTitle.Length > 0;

    public IReadOnlyList<BenchmarkRow> BenchmarkRows
    {
        get => _benchmarkRows;
        private set
        {
            if (SetProperty(ref _benchmarkRows, value))
                OnPropertyChanged(nameof(HasBenchmarkRows));
        }
    }

    public bool HasBenchmarkRows => BenchmarkRows.Count > 0;

    public string BenchmarkCaption { get; private set; } = "";

    /// <summary>Свежие показания датчиков (раз в секунду из главного окна).</summary>
    public void Update(HardwareSnapshot snapshot)
    {
        KeyMetrics metrics = KeyMetrics.From(snapshot);
        _cpuName ??= metrics.CpuName;
        _temperature = metrics.CpuTemperature?.Value;
        _clock = metrics.CpuClock;
        _power = metrics.CpuPower?.Value;
        _load = metrics.CpuLoad?.Value;
        if (_temperature is not null)
            _temperatureKnown = true;

        if (IsRunning && IsStressMode)
        {
            if (_temperature is { } t)
            {
                _maxTemperature = Math.Max(_maxTemperature ?? t, t);
                Push(_temperatures, t);
                TemperatureHistory = _temperatures.ToArray();
            }

            if (_clock is { } c)
            {
                _clockSum += c;
                _clockSamples++;
                Push(_clocks, c);
                ClockHistory = _clocks.ToArray();
            }

            int limit = TemperatureLimits.First(l => l.IsSelected).Value;
            if (_temperature >= limit)
                Finish($"температура процессора достигла {_temperature:0} °C (лимит {limit} °C)", overheated: true);
        }

        OnPropertyChanged(nameof(ProcessorText));
        OnPropertyChanged(nameof(TemperatureText));
        OnPropertyChanged(nameof(MaxTemperatureText));
        OnPropertyChanged(nameof(ClockText));
        OnPropertyChanged(nameof(AverageClockText));
        OnPropertyChanged(nameof(PowerText));
        OnPropertyChanged(nameof(LoadText));
        OnPropertyChanged(nameof(ShowTemperatureWarning));
    }

    private static void Push(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > HistoryCapacity)
            list.RemoveAt(0);
    }

    private async Task StartAsync()
    {
        if (IsRunning)
            return;

        string operation = IsStressMode ? "Стресс-тест процессора" : "Замер процессора";
        _slot = _gate.TryEnter(operation, out string? busy);
        if (_slot is null)
        {
            ShowResult("Сейчас нельзя", $"Выполняется другая операция: «{busy}». Запустите тест, когда она закончится.", "warning");
            return;
        }

        ResultTitle = "";
        IsRunning = true;
        if (IsStressMode)
            await StartStressAsync();
        else
            await RunBenchmarkAsync();
    }

    private async Task StartStressAsync()
    {
        try
        {
            _temperatures.Clear();
            _clocks.Clear();
            TemperatureHistory = Array.Empty<double>();
            ClockHistory = Array.Empty<double>();
            _maxTemperature = null;
            _clockSum = 0;
            _clockSamples = 0;

            var selected = Workloads.Where(w => w.IsSelected).Select(w => w.Workload).ToList();
            _test = new CpuStressTest(selected);
            StatusText = "Считаю эталонные результаты на холодном процессоре…";
            CpuStressTest test = _test;
            await Task.Run(test.Prepare);

            test.Start();
            StatusText = "Идёт стресс-тест. Вентиляторы будут шуметь — это нормально.";
            _timer.Start();
            Tick();
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось запустить стресс-тест процессора.", ex);
            Release();
            ShowResult("Не удалось запустить тест", ex.Message, "critical");
        }
    }

    private void Tick()
    {
        if (_test is null)
            return;

        ElapsedText = _test.Elapsed.ToString(@"hh\:mm\:ss");
        OnPropertyChanged(nameof(ErrorsText));
        OnPropertyChanged(nameof(HasErrors));

        int minutes = Durations.First(d => d.IsSelected).Value;
        if (!_finishing && _test.IsRunning && minutes > 0 && _test.Elapsed >= TimeSpan.FromMinutes(minutes))
            Finish(null, overheated: false);
    }

    private void StopByUser()
    {
        if (_benchmarkCancel is not null)
        {
            _benchmarkCancel.Cancel();
            return;
        }

        Finish("остановлен вручную", overheated: false);
    }

    private void Finish(string? reason, bool overheated)
    {
        // Защита от повторного входа: Tick ниже снова проверяет время и не должен вызвать Finish ещё раз.
        if (_test is null || !IsRunning || _finishing)
            return;

        _finishing = true;
        _timer.Stop();
        _test.Stop(reason ?? "завершён");
        Tick();

        string duration = FormatDuration(_test.Elapsed);
        string temperatures = _maxTemperature is { } max ? $" Максимум {max:0.0} °C" : "";
        string clocks = _clockSamples > 0 ? $", средняя частота {_clockSum / _clockSamples / 1000:0.00} ГГц." : ".";

        if (_test.ErrorCount > 0)
        {
            ShowResult($"Найдены ошибки вычислений: {_test.ErrorCount:N0}",
                $"За {duration} процессор {_test.ErrorCount:N0} раз посчитал неправильно. Это признак нестабильности: " +
                "проверьте разгон, андервольт (Curve Optimizer), XMP/EXPO памяти и температуры." + temperatures + clocks,
                "critical");
        }
        else if (overheated)
        {
            ShowResult("Тест остановлен из-за перегрева",
                $"Через {duration}: {reason}. Ошибок вычислений не было. Проверьте охлаждение: кулер, термопасту, пыль, обороты вентиляторов." + clocks,
                "warning");
        }
        else if (reason is not null)
        {
            ShowResult("Тест остановлен", $"Прошло {duration}, ошибок вычислений нет.{temperatures}{clocks}", "info");
        }
        else
        {
            ShowResult($"Стабильно: {duration} без ошибок", $"Процессор выдержал полную нагрузку.{temperatures}{clocks}", "good");
        }

        StatusText = "";
        Release();
        _finishing = false;
    }

    private async Task RunBenchmarkAsync()
    {
        _benchmarkCancel = new CancellationTokenSource();
        try
        {
            var workloads = Workloads.Where(w => w.IsSelected).Select(w => w.Workload).ToList();
            var progress = new Progress<CpuBenchmarkProgress>(p =>
                StatusText = $"Шаг {p.Step} из {p.Steps}: {Title(p.Workload)}, {(p.MultiThread ? "все потоки" : "один поток")}…");
            CancellationToken token = _benchmarkCancel.Token;
            IReadOnlyList<CpuBenchmarkScore> scores =
                await Task.Run(() => CpuBenchmark.Run(workloads, TimeSpan.FromSeconds(3), progress, token), token);

            CpuBenchmarkRecord? previous = _history.Cpu.FirstOrDefault();
            var record = new CpuBenchmarkRecord
            {
                At = DateTimeOffset.Now,
                Cpu = _cpuName ?? "",
                Threads = Environment.ProcessorCount,
                Scores = scores.ToList(),
            };
            _history.AddCpu(record);
            ShowBenchmark(record, previous);
            RefreshComparison();
            if (record.Score is { } score)
                ShowResult($"Результат: {score.SingleThread:N0} в 1 потоке, {score.MultiThread:N0} во всех потоках",
                    (ComparisonSummary.Length > 0 ? ComparisonSummary + " " : "") +
                    "Рейтинг ниже. Замер сохранён: после разгона, чистки или замены охлаждения повторите его и сравните.", "good");
            else
                ShowResult("Замер завершён",
                    "Итоговые баллы не посчитаны: для них нужны нагрузки «Целочисленные», «Плавающая точка» и «Кэш». Результаты по выбранным нагрузкам сохранены.", "info");
        }
        catch (OperationCanceledException)
        {
            ShowResult("Замер остановлен", "Результаты не сохранены.", "info");
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка замера процессора.", ex);
            ShowResult("Замер не удался", ex.Message, "critical");
        }
        finally
        {
            _benchmarkCancel.Dispose();
            _benchmarkCancel = null;
            StatusText = "";
            Release();
        }
    }

    private void ShowLastBenchmark()
    {
        if (_history.Cpu.FirstOrDefault() is { } last)
            ShowBenchmark(last, _history.Cpu.Skip(1).FirstOrDefault());
    }

    private void ShowBenchmark(CpuBenchmarkRecord record, CpuBenchmarkRecord? previous)
    {
        // Замер прежней версией теста с новым не сравниваем: ядра теста и формула другие.
        if (previous is not null && previous.ScoreVersion != record.ScoreVersion)
            previous = null;

        _shownRecord = record;
        _shownPrevious = previous;
        DateTimeOffset at = record.At;
        BenchmarkRows = record.Scores.Select(s =>
        {
            CpuBenchmarkScore? before = previous?.Scores.FirstOrDefault(p => p.Workload == s.Workload);
            string compare = before is null
                ? "—"
                : $"{Delta(s.SingleThread, before.SingleThread)} / {Delta(s.MultiThread, before.MultiThread)}";
            return new BenchmarkRow(
                $"{Title(s.Workload)}, {s.Unit}",
                Format(s.SingleThread),
                Format(s.MultiThread),
                compare);
        }).ToList();
        BenchmarkCaption = $"Замер от {at.LocalDateTime:d MMMM, HH:mm}" +
                           (previous is null ? "" : $"; сравнение с замером от {previous.At.LocalDateTime:d MMMM, HH:mm}");
        OnPropertyChanged(nameof(BenchmarkCaption));
        OnPropertyChanged(nameof(HasScore));
        OnPropertyChanged(nameof(ShowScoreHint));
        OnPropertyChanged(nameof(ScoreHintText));
        OnPropertyChanged(nameof(ScoreSingleText));
        OnPropertyChanged(nameof(ScoreMultiText));
        OnPropertyChanged(nameof(ScoreSingleDelta));
        OnPropertyChanged(nameof(ScoreMultiDelta));
        RelayCommand.Refresh();
    }

    private string ScoreDelta(Func<CpuScoreResult, int> pick)
    {
        if (_shownRecord?.Score is not { } now || _shownPrevious?.Score is not { } before)
            return "";

        string delta = Delta(pick(now), pick(before));
        return delta == "—" ? "" : $"{delta} к прошлому замеру";
    }

    /// <summary>Рейтинг: последний результат этого ПК, результаты из файлов и популярные процессоры, лучшие сверху.</summary>
    private void RefreshComparison()
    {
        var rows = new List<(string Title, string Subtitle, CpuScoreResult Score, ComparisonKind Kind, CpuBenchmarkRecord? Record)>();
        CpuBenchmarkRecord? own = _history.Cpu.FirstOrDefault(r => r.Score is not null);
        if (own is not null)
            rows.Add((Name(own), $"Этот ПК · {Threads(own.Threads)}замер от {own.At.LocalDateTime:d MMMM yyyy}",
                own.Score!, ComparisonKind.Current, own));

        foreach (CpuBenchmarkRecord imported in _history.ImportedCpu)
        {
            if (imported.Score is { } score)
                rows.Add((Name(imported), $"Из файла · {Threads(imported.Threads)}замер от {imported.At.LocalDateTime:d MMMM yyyy}",
                    score, ComparisonKind.Imported, imported));
        }

        CpuReference? ownModel = CpuReferenceList.Find(own?.Cpu ?? _cpuName);
        if (ShowPopular)
        {
            foreach (CpuReference reference in CpuReferenceList.All)
            {
                string subtitle = reference == ownModel
                    ? $"Ваша модель · {Threads(reference.Threads)}{CpuReferenceList.SourceText}"
                    : $"{Threads(reference.Threads)}{CpuReferenceList.SourceText}";
                rows.Add((reference.Name, subtitle, reference.Estimate, ComparisonKind.Estimate, null));
            }
        }

        bool bySingle = CompareBySingle;
        int Pick(CpuScoreResult s) => bySingle ? s.SingleThread : s.MultiThread;
        double max = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(e => Pick(e.Score)));

        ComparisonRows = rows
            .OrderByDescending(e => Pick(e.Score))
            .ThenBy(e => e.Kind)
            .Select(e => new ComparisonRowViewModel(e.Title, e.Subtitle, e.Score, e.Kind, Pick(e.Score) / max, bySingle,
                e.Record, e.Kind == ComparisonKind.Imported ? RemoveImported : null))
            .ToList();

        ComparisonSummary = own?.Score is { } mine ? Summary(mine, ownModel, bySingle) : "";
    }

    /// <summary>«Быстрее 24 из 45 популярных процессоров» и сравнение со своей моделью.</summary>
    private static string Summary(CpuScoreResult mine, CpuReference? ownModel, bool bySingle)
    {
        int Pick(CpuScoreResult s) => bySingle ? s.SingleThread : s.MultiThread;
        int value = Pick(mine);
        int total = CpuReferenceList.All.Count;
        int slower = CpuReferenceList.All.Count(r => Pick(r.Estimate) < value);
        string mode = bySingle ? "в одном потоке" : "во всех потоках";
        string text = $"{mode[0].ToString().ToUpperInvariant()}{mode[1..]} ваш процессор быстрее {slower} из {total} популярных моделей.";
        if (ownModel is not null)
        {
            int typical = Pick(ownModel.Estimate);
            double percent = (value - typical) / (double)typical * 100;
            text += Math.Abs(percent) < 5
                ? $" Это обычный результат для {ownModel.Name}."
                : percent > 0
                    ? $" Это на {percent:0} % выше типичного для {ownModel.Name}."
                    : $" Это на {-percent:0} % ниже типичного для {ownModel.Name}: проверьте температуры, режим питания Windows и фоновые программы.";
        }

        return text;
    }

    private static string Name(CpuBenchmarkRecord record) => record.Cpu.Length > 0 ? record.Cpu : "Процессор";

    private static string Threads(int threads) => threads > 0 ? $"{threads} потоков · " : "";

    private void RemoveImported(ComparisonRowViewModel row)
    {
        if (row.Record is null)
            return;

        _history.RemoveImportedCpu(row.Record.Id);
        ComparisonMessage = "";
        RefreshComparison();
    }

    private void Export()
    {
        if (_shownRecord?.Score is null)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить результат",
            Filter = BenchmarkExchange.FileFilter,
            DefaultExt = BenchmarkExchange.Extension,
            FileName = BenchmarkExchange.SuggestedFileName(_shownRecord),
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            BenchmarkExchange.ExportCpu(_shownRecord, dialog.FileName);
            ComparisonMessage = $"Сохранено: {System.IO.Path.GetFileName(dialog.FileName)}. " +
                                "Откройте этот файл на другом ПК кнопкой «Добавить из файла» — и наоборот.";
            AppLog.Info($"Результат замера сохранён в файл: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить результат замера в файл.", ex);
            ComparisonMessage = $"Не удалось сохранить: {ex.Message}";
        }
    }

    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Добавить результаты для сравнения",
            Filter = BenchmarkExchange.FileFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        int added = 0, duplicates = 0;
        var problems = new List<string>();
        foreach (string path in dialog.FileNames)
        {
            try
            {
                if (_history.AddImportedCpu(BenchmarkExchange.ImportCpu(path)))
                    added++;
                else
                    duplicates++;
            }
            catch (BenchmarkImportException ex)
            {
                problems.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось прочитать результат из {path}.", ex);
                problems.Add($"{System.IO.Path.GetFileName(path)}: не удалось прочитать файл.");
            }
        }

        var parts = new List<string>();
        if (added > 0)
            parts.Add($"Добавлено результатов: {added}.");
        if (duplicates > 0)
            parts.Add($"Уже были в списке: {duplicates}.");
        parts.AddRange(problems);
        ComparisonMessage = string.Join(" ", parts);
        RefreshComparison();
    }

    private static string Delta(double now, double before)
    {
        if (before <= 0)
            return "—";
        double percent = (now - before) / before * 100;
        return $"{percent:+0;-0;0} %";
    }

    private static string Format(double value) => value >= 100 ? $"{value:N0}" : $"{value:0.##}";

    private static string Title(CpuWorkload workload) => workload switch
    {
        CpuWorkload.Integer => "Целочисленные",
        CpuWorkload.Float => "Плавающая точка",
        CpuWorkload.Cache => "Кэш",
        CpuWorkload.Memory => "Память",
        _ => workload.ToString(),
    };

    private static string FormatDuration(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} ч {time.Minutes} мин"
        : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} мин {time.Seconds} с"
        : $"{time.Seconds} с";

    private void ShowResult(string title, string text, string kind)
    {
        ResultKind = kind;
        ResultText = text;
        ResultTitle = title;
    }

    private void Release()
    {
        _slot?.Dispose();
        _slot = null;
        IsRunning = false;
    }

    /// <summary>При закрытии приложения тест обязательно останавливается.</summary>
    public void Shutdown()
    {
        _timer.Stop();
        _benchmarkCancel?.Cancel();
        _test?.Dispose();
    }
}

/// <summary>Страница «Тесты»: вкладки по устройствам.</summary>
public sealed class BenchmarksViewModel : ObservableObject
{
    private string _tab = "cpu";

    public BenchmarksViewModel(OperationGate gate, BenchmarkHistory history)
    {
        Cpu = new CpuTestViewModel(gate, history);
        Gpu = new GpuTestViewModel(gate, history);
        Memory = new MemoryTestViewModel(gate, history);
        Disk = new DiskTestViewModel(gate, history);
    }

    public CpuTestViewModel Cpu { get; }

    public GpuTestViewModel Gpu { get; }

    public MemoryTestViewModel Memory { get; }

    public DiskTestViewModel Disk { get; }

    public bool IsCpuTab { get => _tab == "cpu"; set => SetTab("cpu", value); }

    public bool IsGpuTab { get => _tab == "gpu"; set => SetTab("gpu", value); }

    public bool IsMemoryTab { get => _tab == "memory"; set => SetTab("memory", value); }

    public bool IsDiskTab { get => _tab == "disk"; set => SetTab("disk", value); }

    public bool IsPlaceholderTab => false;

    public string PlaceholderText => _tab switch
    {
        _ => "",
    };

    public void Update(HardwareSnapshot snapshot)
    {
        Cpu.Update(snapshot);
        Gpu.Update(snapshot);
    }

    public void Shutdown()
    {
        Cpu.Shutdown();
        Gpu.Shutdown();
        Memory.Shutdown();
        Disk.Shutdown();
    }

    private void SetTab(string tab, bool value)
    {
        if (!value || _tab == tab)
            return;

        _tab = tab;
        if (tab == "memory")
            Memory.Refresh();
        if (tab == "disk" && !Disk.IsRunning)
            Disk.RefreshDrives();
        OnPropertyChanged(nameof(IsCpuTab));
        OnPropertyChanged(nameof(IsGpuTab));
        OnPropertyChanged(nameof(IsMemoryTab));
        OnPropertyChanged(nameof(IsDiskTab));
        OnPropertyChanged(nameof(IsPlaceholderTab));
        OnPropertyChanged(nameof(PlaceholderText));
    }
}
