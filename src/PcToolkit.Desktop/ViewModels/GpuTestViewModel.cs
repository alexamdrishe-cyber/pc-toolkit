using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PcToolkit.Core;
using PcToolkit.Core.Benchmarks;
using PcToolkit.Core.Benchmarks.Gpu;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Sensors;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Галочка без привязки к виду нагрузки процессора.</summary>
public sealed class ToggleOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public ToggleOptionViewModel(string title, string description, bool selected)
    {
        Title = title;
        Description = description;
        _isSelected = selected;
    }

    public string Title { get; }

    public string Description { get; set; }

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

/// <summary>Строка рейтинга видеокарт.</summary>
public sealed class GpuRatingRow
{
    public GpuRatingRow(string title, string subtitle, int score, ComparisonKind kind, double fraction)
    {
        Title = title;
        Subtitle = subtitle;
        ScoreText = score.ToString("N0");
        Kind = kind;
        double f = Math.Clamp(fraction, 0.02, 1);
        BarLength = new GridLength(f, GridUnitType.Star);
        RestLength = new GridLength(1 - f + 1e-6, GridUnitType.Star);
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string ScoreText { get; }

    public ComparisonKind Kind { get; }

    public bool IsCurrent => Kind == ComparisonKind.Current;

    public bool IsEstimate => Kind == ComparisonKind.Estimate;

    public GridLength BarLength { get; }

    public GridLength RestLength { get; }
}

/// <summary>Вкладка «Видеокарта»: стресс-тест в духе OCCT и замер с рейтингом.</summary>
public sealed class GpuTestViewModel : ObservableObject
{
    private const int HistoryCapacity = 600;
    private readonly OperationGate _gate;
    private readonly BenchmarkHistory _history;
    private readonly DispatcherTimer _timer;
    private readonly List<double> _temperatures = new();
    private readonly List<double> _clocks = new();
    private GpuStressTest? _test;
    private IDisposable? _slot;
    private CancellationTokenSource? _benchmarkCancel;
    private bool _isStressMode = true;
    private bool _isRunning;
    private bool _finishing;
    private string _statusText = "";
    private string _elapsedText = "00:00:00";
    private string _resultTitle = "";
    private string _resultText = "";
    private string _resultKind = "";
    private string? _gpuName;
    private float? _temperature;
    private float? _maxTemperature;
    private float? _hotSpot;
    private float? _maxHotSpot;
    private float? _clock;
    private float? _power;
    private float? _maxPower;
    private float? _load;
    private float? _memoryUsed;
    private float? _memoryTotal;
    private double _clockSum;
    private int _clockSamples;
    private bool _temperatureKnown;
    private IReadOnlyList<double> _temperatureHistory = Array.Empty<double>();
    private IReadOnlyList<double> _clockHistory = Array.Empty<double>();
    private IReadOnlyList<GpuRatingRow> _ratingRows = Array.Empty<GpuRatingRow>();
    private GpuBenchmarkRecord? _shownRecord;
    private GpuBenchmarkRecord? _shownPrevious;
    private bool _showPopular = true;
    private string _ratingSummary = "";

    public GpuTestViewModel(OperationGate gate, BenchmarkHistory history)
    {
        _gate = gate;
        _history = history;

        RenderOption = new ToggleOptionViewModel("3D-нагрузка (как в играх)",
            "Рендер сцены 4K с текстурами и смешиванием слоёв: работает вся видеокарта, максимум ватт.", true);
        ComputeOption = new ToggleOptionViewModel("Вычисления (шейдеры)",
            "Только вычислительные блоки FP32: меньше ватт, зато точнее ловит ошибки ядра.", false);
        MemoryOption = new ToggleOptionViewModel("Видеопамять",
            "Запись и проверка шаблонов в 60 % видеопамяти.", true);

        Durations = new[]
        {
            new ChoiceViewModel("gpu-duration", "1 мин", 1, false),
            new ChoiceViewModel("gpu-duration", "5 мин", 5, false),
            new ChoiceViewModel("gpu-duration", "15 мин", 15, true),
            new ChoiceViewModel("gpu-duration", "30 мин", 30, false),
            new ChoiceViewModel("gpu-duration", "1 час", 60, false),
            new ChoiceViewModel("gpu-duration", "Без ограничения", 0, false),
        };

        TemperatureLimits = new[]
        {
            new ChoiceViewModel("gpu-limit", "85 °C", 85, false),
            new ChoiceViewModel("gpu-limit", "90 °C", 90, true),
            new ChoiceViewModel("gpu-limit", "95 °C", 95, false),
        };

        StartCommand = new RelayCommand(() => _ = StartAsync(),
            () => !IsRunning && (IsBenchmarkMode || RenderOption.IsSelected || ComputeOption.IsSelected || MemoryOption.IsSelected));
        StopCommand = new RelayCommand(StopByUser, () => IsRunning);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();

        // Сравниваем только замеры текущей версии теста.
        var valid = _history.Gpu.Where(r => r.Score is not null).ToList();
        if (valid.FirstOrDefault() is { } last)
            ShowBenchmark(last, valid.Skip(1).FirstOrDefault());
        RefreshRating();
    }

    public ToggleOptionViewModel RenderOption { get; }

    public ToggleOptionViewModel ComputeOption { get; }

    public ToggleOptionViewModel MemoryOption { get; }

    public IReadOnlyList<ChoiceViewModel> Durations { get; }

    public IReadOnlyList<ChoiceViewModel> TemperatureLimits { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public string AdapterText => $"{_gpuName ?? _test?.AdapterName ?? "Видеокарта"} · DirectX 11 compute";

    public bool IsStressMode
    {
        get => _isStressMode;
        set
        {
            if (SetProperty(ref _isStressMode, value))
            {
                OnPropertyChanged(nameof(IsBenchmarkMode));
                OnPropertyChanged(nameof(StartButtonText));
                RelayCommand.Refresh();
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

    public bool HasErrors => _test is { } t && (t.ErrorCount > 0 || t.DeviceLost);

    public string TemperatureText => _temperature is { } t ? $"{t:0.0} °C" : "нет данных";

    public string MaxTemperatureText => _maxTemperature is { } t ? $"макс. {t:0.0} °C" : "";

    public string HotSpotText => _hotSpot is { } t ? $"{t:0.0} °C" : "нет данных";

    public string MaxHotSpotText => _maxHotSpot is { } t ? $"макс. {t:0.0} °C" : "";

    public string ClockText => _clock is { } c ? $"{c:0} МГц" : "нет данных";

    public string AverageClockText => _clockSamples > 0 ? $"средн. {_clockSum / _clockSamples:0} МГц" : "";

    public string PowerText => _power is { } p ? $"{p:0.0} Вт" : "нет данных";

    public string MaxPowerText => _maxPower is { } p ? $"макс. {p:0} Вт" : "";

    public string LoadText => _load is { } l ? $"{l:0} %" : "нет данных";

    public string MemoryText => _memoryUsed is { } used
        ? _memoryTotal is { } total ? $"{used / 1024:0.0} / {total / 1024:0.0} ГБ" : $"{used / 1024:0.0} ГБ"
        : "нет данных";

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

    // ---------- Замер ----------

    public bool HasBenchmark => _shownRecord?.Score is not null;

    public string BenchmarkCaption => _shownRecord is { } r
        ? $"{r.Gpu} · замер от {r.At.LocalDateTime:d MMMM, HH:mm}" +
          (_shownPrevious is { } p ? $"; сравнение с замером от {p.At.LocalDateTime:d MMMM, HH:mm}" : "")
        : "";

    public string ScoreText => _shownRecord?.Score?.ToString("N0") ?? "—";

    public string ScoreDelta => _shownRecord?.Score is { } now && _shownPrevious?.Score is { } before && before > 0
        ? $"{(now - before) / (double)before * 100:+0;-0;0} % к прошлому замеру"
        : "";

    public string FillText => _shownRecord is { } r ? $"{r.Fill:0.0} Гпикс/с" : "—";

    public string FillDelta => Delta(_shownRecord?.Fill, _shownPrevious?.Fill);

    public string GflopsText => _shownRecord is { } r ? $"{r.Gflops:N0} GFLOPS" : "—";

    public string GflopsDelta => Delta(_shownRecord?.Gflops, _shownPrevious?.Gflops);

    public string BandwidthText => _shownRecord is { } r ? $"{r.Bandwidth:N0} ГБ/с" : "—";

    public string BandwidthDelta => Delta(_shownRecord?.Bandwidth, _shownPrevious?.Bandwidth);

    public string ScoreReferenceText => $"10 000 баллов — {GpuScore.ReferenceName}. Больше — быстрее.";

    // ---------- Рейтинг ----------

    public IReadOnlyList<GpuRatingRow> RatingRows
    {
        get => _ratingRows;
        private set
        {
            if (SetProperty(ref _ratingRows, value))
                OnPropertyChanged(nameof(HasRatingRows));
        }
    }

    public bool HasRatingRows => RatingRows.Count > 0;

    public bool ShowPopular
    {
        get => _showPopular;
        set
        {
            if (SetProperty(ref _showPopular, value))
                RefreshRating();
        }
    }

    public string RatingSummary
    {
        get => _ratingSummary;
        private set
        {
            if (SetProperty(ref _ratingSummary, value))
                OnPropertyChanged(nameof(HasRatingSummary));
        }
    }

    public bool HasRatingSummary => RatingSummary.Length > 0;

    /// <summary>Свежие показания датчиков (раз в секунду из главного окна).</summary>
    public void Update(HardwareSnapshot snapshot)
    {
        KeyMetrics metrics = KeyMetrics.From(snapshot);
        if (metrics.GpuName is { } name)
            _gpuName = name;
        _temperature = metrics.GpuTemperature?.Value;
        _hotSpot = metrics.GpuHotSpot?.Value;
        _clock = metrics.GpuClock?.Value;
        _power = metrics.GpuPower?.Value;
        _load = metrics.GpuLoad?.Value;
        _memoryUsed = metrics.GpuMemoryUsed?.Value;
        _memoryTotal = metrics.GpuMemoryTotal?.Value;
        if (_temperature is not null)
            _temperatureKnown = true;

        if (IsRunning && IsStressMode && _test?.Phase == GpuTestPhase.Running)
        {
            if (_temperature is { } t)
            {
                _maxTemperature = Math.Max(_maxTemperature ?? t, t);
                Push(_temperatures, t);
                TemperatureHistory = _temperatures.ToArray();
            }

            if (_hotSpot is { } h)
                _maxHotSpot = Math.Max(_maxHotSpot ?? h, h);
            if (_power is { } p)
                _maxPower = Math.Max(_maxPower ?? p, p);

            if (_clock is { } c)
            {
                _clockSum += c;
                _clockSamples++;
                Push(_clocks, c);
                ClockHistory = _clocks.ToArray();
            }

            int limit = TemperatureLimits.First(l => l.IsSelected).Value;
            if (_temperature >= limit)
                Finish($"температура видеокарты достигла {_temperature:0} °C (лимит {limit} °C)", overheated: true);
        }

        OnPropertyChanged(nameof(AdapterText));
        OnPropertyChanged(nameof(TemperatureText));
        OnPropertyChanged(nameof(MaxTemperatureText));
        OnPropertyChanged(nameof(HotSpotText));
        OnPropertyChanged(nameof(MaxHotSpotText));
        OnPropertyChanged(nameof(ClockText));
        OnPropertyChanged(nameof(AverageClockText));
        OnPropertyChanged(nameof(PowerText));
        OnPropertyChanged(nameof(MaxPowerText));
        OnPropertyChanged(nameof(LoadText));
        OnPropertyChanged(nameof(MemoryText));
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

        string operation = IsStressMode ? "Стресс-тест видеокарты" : "Замер видеокарты";
        _slot = _gate.TryEnter(operation, out string? busy);
        if (_slot is null)
        {
            ShowResult("Сейчас нельзя", $"Выполняется другая операция: «{busy}». Запустите тест, когда она закончится.", "warning");
            return;
        }

        ResultTitle = "";
        IsRunning = true;
        if (IsStressMode)
            StartStress();
        else
            await RunBenchmarkAsync();
    }

    private void StartStress()
    {
        try
        {
            _temperatures.Clear();
            _clocks.Clear();
            TemperatureHistory = Array.Empty<double>();
            ClockHistory = Array.Empty<double>();
            _maxTemperature = null;
            _maxHotSpot = null;
            _maxPower = null;
            _clockSum = 0;
            _clockSamples = 0;

            _test = new GpuStressTest(RenderOption.IsSelected, ComputeOption.IsSelected, MemoryOption.IsSelected);
            _test.Start();
            StatusText = "Подготовка: компилирую шейдеры, считаю эталон и выделяю видеопамять…";
            _timer.Start();
            Tick();
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось запустить стресс-тест видеокарты.", ex);
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
        OnPropertyChanged(nameof(AdapterText));

        if (_finishing || !IsRunning)
            return;

        if (_test.Phase == GpuTestPhase.Running && StatusText.StartsWith("Подготовка", StringComparison.Ordinal))
        {
            string memory = _test.MemoryTestedBytes > 0 ? $" Проверяется {Formatting.Bytes((ulong)_test.MemoryTestedBytes)} видеопамяти." : "";
            StatusText = "Идёт стресс-тест. Экран может подтормаживать, вентиляторы — шуметь: это нормально." + memory;
        }

        if (_test.Phase == GpuTestPhase.Finished)
        {
            // Тест прервался сам: сброс драйвера или ошибка DirectX.
            Finish(_test.Failure ?? "завершён", overheated: false);
            return;
        }

        int minutes = Durations.First(d => d.IsSelected).Value;
        if (_test.Phase == GpuTestPhase.Running && minutes > 0 && _test.Elapsed >= TimeSpan.FromMinutes(minutes))
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
        if (_test is null || !IsRunning || _finishing)
            return;

        _finishing = true;
        _timer.Stop();
        _test.Stop(reason ?? "завершён");
        Tick();

        string duration = FormatDuration(_test.Elapsed);
        var details = new List<string>();
        if (_maxTemperature is { } t)
            details.Add($"максимум {t:0} °C" + (_maxHotSpot is { } h ? $" (горячая точка {h:0} °C)" : ""));
        if (_clockSamples > 0)
            details.Add($"средняя частота {_clockSum / _clockSamples:0} МГц");
        if (_maxPower is { } p)
            details.Add($"до {p:0} Вт");
        string tail = details.Count > 0 ? " " + char.ToUpper(details[0][0]) + string.Join(", ", details)[1..] + "." : "";

        if (_test.DeviceLost)
        {
            ShowResult("Сбой драйвера видеокарты",
                $"Через {duration}: {_test.Failure}. Под нагрузкой это главный признак нестабильности: " +
                "уберите разгон или андервольт видеокарты, проверьте температуры и блок питания." + tail, "critical");
        }
        else if (_test.ErrorCount > 0)
        {
            var parts = new List<string>();
            if (_test.RenderErrors > 0)
                parts.Add($"неверных пикселей в 3D-кадрах {_test.RenderErrors:N0}");
            if (_test.ComputeErrors > 0)
                parts.Add($"ошибок вычислений {_test.ComputeErrors:N0}");
            if (_test.MemoryErrors > 0)
                parts.Add($"ошибок видеопамяти {_test.MemoryErrors:N0}");
            ShowResult($"Найдены ошибки: {_test.ErrorCount:N0}",
                $"За {duration}: {string.Join(", ", parts)}. Видеокарта считает неправильно — это нестабильность. " +
                (_test.MemoryErrors > 0 ? "Ошибки памяти чаще всего из-за разгона видеопамяти. " : "Чаще всего виноват разгон или андервольт ядра. ") +
                "Верните настройки по умолчанию и повторите тест." + tail, "critical");
        }
        else if (_test.Failure is not null && reason == _test.Failure)
        {
            ShowResult("Тест прерван", $"Через {duration}: {_test.Failure}.", "warning");
        }
        else if (overheated)
        {
            ShowResult("Тест остановлен из-за перегрева",
                $"Через {duration}: {reason}. Ошибок не было. Проверьте охлаждение видеокарты: пыль, термопасту, обороты вентиляторов, продуваемость корпуса." + tail,
                "warning");
        }
        else if (reason is not null)
        {
            ShowResult("Тест остановлен", $"Прошло {duration}, ошибок нет." + tail, "info");
        }
        else
        {
            ShowResult($"Стабильно: {duration} без ошибок", "Видеокарта выдержала полную нагрузку." + tail, "good");
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
            var progress = new Progress<GpuBenchmarkProgress>(p =>
                StatusText = $"Шаг {p.Index} из {p.Count}: {p.Step}…");
            StatusText = "Подготовка…";
            CancellationToken token = _benchmarkCancel.Token;
            GpuBenchmarkResult result = await Task.Run(() => GpuBenchmark.Run(progress, token), token);

            GpuBenchmarkRecord? previous = _history.Gpu.FirstOrDefault(r => r.Score is not null);
            var record = new GpuBenchmarkRecord
            {
                At = DateTimeOffset.Now,
                Gpu = result.Adapter,
                Fill = result.Fill,
                Gflops = result.Gflops,
                Bandwidth = result.Bandwidth,
            };
            _history.AddGpu(record);
            ShowBenchmark(record, previous);
            RefreshRating();
            ShowResult($"Результат: {record.Score:N0} баллов",
                (RatingSummary.Length > 0 ? RatingSummary + " " : "") +
                "Замер сохранён: после разгона, обновления драйвера или чистки повторите его и сравните.", "good");
        }
        catch (OperationCanceledException)
        {
            ShowResult("Замер остановлен", "Результаты не сохранены.", "info");
        }
        catch (GpuDeviceLostException ex)
        {
            AppLog.Warn($"Замер видеокарты: сбой драйвера: {ex.Message}");
            ShowResult("Сбой драйвера видеокарты",
                $"Во время замера {ex.Message}. Это признак нестабильного разгона или проблем с драйвером.", "critical");
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка замера видеокарты.", ex);
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

    private void ShowBenchmark(GpuBenchmarkRecord record, GpuBenchmarkRecord? previous)
    {
        _shownRecord = record;
        _shownPrevious = previous;
        OnPropertyChanged(nameof(HasBenchmark));
        OnPropertyChanged(nameof(BenchmarkCaption));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(ScoreDelta));
        OnPropertyChanged(nameof(FillText));
        OnPropertyChanged(nameof(FillDelta));
        OnPropertyChanged(nameof(GflopsText));
        OnPropertyChanged(nameof(GflopsDelta));
        OnPropertyChanged(nameof(BandwidthText));
        OnPropertyChanged(nameof(BandwidthDelta));
    }

    private void RefreshRating()
    {
        var rows = new List<(string Title, string Subtitle, int Score, ComparisonKind Kind)>();
        GpuBenchmarkRecord? own = _history.Gpu.FirstOrDefault(r => r.Score is not null);
        if (own is not null)
            rows.Add((own.Gpu.Length > 0 ? own.Gpu : "Видеокарта", $"Этот ПК · замер от {own.At.LocalDateTime:d MMMM yyyy}",
                own.Score!.Value, ComparisonKind.Current));

        GpuReference? ownModel = GpuReferenceList.Find(own?.Gpu ?? _gpuName);
        if (ShowPopular)
        {
            foreach (GpuReference reference in GpuReferenceList.All)
            {
                string subtitle = reference == ownModel
                    ? $"Ваша модель · {GpuReferenceList.SourceText}"
                    : GpuReferenceList.SourceText;
                rows.Add((reference.Name, subtitle, reference.Estimate, ComparisonKind.Estimate));
            }
        }

        double max = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(r => r.Score));
        RatingRows = rows
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Kind)
            .Select(r => new GpuRatingRow(r.Title, r.Subtitle, r.Score, r.Kind, r.Score / max))
            .ToList();

        RatingSummary = own?.Score is { } mine ? Summary(mine, ownModel) : "";
    }

    private static string Summary(int mine, GpuReference? ownModel)
    {
        int total = GpuReferenceList.All.Count;
        int slower = GpuReferenceList.All.Count(r => r.Estimate < mine);
        string text = $"Ваша видеокарта быстрее {slower} из {total} популярных моделей.";
        if (ownModel is not null)
        {
            double percent = (mine - ownModel.Estimate) / (double)ownModel.Estimate * 100;
            text += Math.Abs(percent) < 7
                ? $" Это обычный результат для {ownModel.Name}."
                : percent > 0
                    ? $" Это на {percent:0} % выше типичного для {ownModel.Name}."
                    : $" Это на {-percent:0} % ниже типичного для {ownModel.Name}: проверьте драйвер, температуры и не запущены ли игры или браузер с видео.";
        }

        return text;
    }

    private static string Delta(double? now, double? before)
    {
        if (now is not { } n || before is not { } b || b <= 0)
            return "";
        return $"{(n - b) / b * 100:+0;-0;0} % к прошлому";
    }

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
