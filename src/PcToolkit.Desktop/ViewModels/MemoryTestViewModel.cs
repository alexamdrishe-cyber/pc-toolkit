using System.Windows.Input;
using System.Windows.Threading;
using PcToolkit.Core;
using PcToolkit.Core.Benchmarks;
using PcToolkit.Core.Benchmarks.Memory;
using PcToolkit.Core.Inventory;
using PcToolkit.Core.Logging;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Строка списка алгоритмов: пройден в этом цикле, идёт сейчас или впереди.</summary>
public sealed record MemoryPatternRow(int Number, string Title, string State)
{
    public bool IsDone => State == "done";

    public bool IsCurrent => State == "current";
}

/// <summary>Строка таблицы ошибок.</summary>
public sealed record MemoryErrorRow(string Time, string Pattern, string Address, string Expected, string Actual, string Bits);

/// <summary>Вкладка «Память»: тест стабильности в духе MemTest86 и замер скорости в духе AIDA64.</summary>
public sealed class MemoryTestViewModel : ObservableObject
{
    private readonly OperationGate _gate;
    private readonly BenchmarkHistory _history;
    private readonly DispatcherTimer _timer;
    private MemoryTest? _test;
    private IDisposable? _slot;
    private CancellationTokenSource? _benchmarkCancel;
    private MemoryConfig? _config;
    private bool _isStressMode = true;
    private bool _isRunning;
    private bool _finishing;
    private string _statusText = "";
    private string _elapsedText = "00:00:00";
    private string _resultTitle = "";
    private string _resultText = "";
    private string _resultKind = "";
    private long _lastProcessed;
    private DateTime _lastTick;
    private double _speed;
    private IReadOnlyList<MemoryPatternRow> _patternRows;
    private IReadOnlyList<MemoryErrorRow> _errorRows = Array.Empty<MemoryErrorRow>();
    private MemoryBenchmarkRecord? _shownRecord;
    private MemoryBenchmarkRecord? _shownPrevious;

    public MemoryTestViewModel(OperationGate gate, BenchmarkHistory history)
    {
        _gate = gate;
        _history = history;

        Sizes = new[]
        {
            new ChoiceViewModel("mem-size", "50 % свободной", 50, false),
            new ChoiceViewModel("mem-size", "75 % свободной", 75, true),
            new ChoiceViewModel("mem-size", "90 % свободной", 90, false),
        };
        foreach (ChoiceViewModel size in Sizes)
            size.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SizeText));

        Durations = new[]
        {
            new ChoiceViewModel("mem-duration", "10 мин", 10, false),
            new ChoiceViewModel("mem-duration", "30 мин", 30, true),
            new ChoiceViewModel("mem-duration", "1 час", 60, false),
            new ChoiceViewModel("mem-duration", "2 часа", 120, false),
            new ChoiceViewModel("mem-duration", "Без ограничения", 0, false),
        };

        _patternRows = MemoryTest.Patterns.Select((p, i) => new MemoryPatternRow(i + 1, p, "pending")).ToList();

        StartCommand = new RelayCommand(() => _ = StartAsync(), () => !IsRunning);
        StopCommand = new RelayCommand(StopByUser, () => IsRunning);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();

        if (_history.Memory.FirstOrDefault() is { } last)
            ShowBenchmark(last, _history.Memory.Skip(1).FirstOrDefault());

        _ = LoadConfigAsync();
    }

    public IReadOnlyList<ChoiceViewModel> Sizes { get; }

    public IReadOnlyList<ChoiceViewModel> Durations { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public string ConfigText
    {
        get
        {
            long total = MemoryTest.TotalPhysicalMemory();
            string config = _config is { SpeedMts: > 0 } c ? $"{c.Description} · " : "";
            return $"{config}всего {Formatting.Bytes((ulong)total)} · потоков: {Environment.ProcessorCount}";
        }
    }

    public string SizeText
    {
        get
        {
            long free = MemoryTest.AvailablePhysicalMemory();
            long bytes = PlannedBytes(free);
            return $"Будет проверено около {Formatting.Bytes((ulong)bytes)} из {Formatting.Bytes((ulong)free)} свободной памяти. " +
                   "Остальное остаётся Windows и программам. Закройте браузер и игры — проверится больше.";
        }
    }

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

    public string StartButtonText => IsStressMode ? "Запустить тест" : "Запустить замер";

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

    /// <summary>Номер идущего цикла; после завершения — число пройденных.</summary>
    public string CycleText => _test is null ? "—"
        : _test.IsRunning ? $"{_test.CompletedCycles + 1}"
        : $"{_test.CompletedCycles} {CyclesWord(_test.CompletedCycles)}";

    public double CycleProgress => _test is null ? 0 : _test.CycleProgress * 100;

    public string PatternText => _test is { Phase: MemoryTestPhase.Running } t ? MemoryTest.Patterns[t.CurrentPattern] : "—";

    public string TestedText => _test is { AllocatedBytes: > 0 } t
        ? Formatting.Bytes((ulong)t.AllocatedBytes) + (t.LockedBytes >= t.AllocatedBytes ? "" : t.LockedBytes > 0 ? " (часть не закреплена)" : " (не закреплена)")
        : "—";

    public string SpeedText => _speed > 0 ? $"{_speed:0.0} ГБ/с" : "—";

    public IReadOnlyList<MemoryPatternRow> PatternRows
    {
        get => _patternRows;
        private set => SetProperty(ref _patternRows, value);
    }

    public IReadOnlyList<MemoryErrorRow> ErrorRows
    {
        get => _errorRows;
        private set
        {
            if (SetProperty(ref _errorRows, value))
                OnPropertyChanged(nameof(HasErrorRows));
        }
    }

    public bool HasErrorRows => ErrorRows.Count > 0;

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

    // ---------- Замер ----------

    public bool HasBenchmark => _shownRecord is not null;

    public string BenchmarkCaption => _shownRecord is { } r
        ? $"{(r.Memory.Length > 0 ? r.Memory + " · " : "")}замер от {r.At.LocalDateTime:d MMMM, HH:mm}" +
          (_shownPrevious is { } p ? $"; сравнение с замером от {p.At.LocalDateTime:d MMMM, HH:mm}" : "")
        : "";

    public string ReadText => _shownRecord is { } r ? $"{r.Read:0.0} ГБ/с" : "—";

    public string WriteText => _shownRecord is { } r ? $"{r.Write:0.0} ГБ/с" : "—";

    public string CopyText => _shownRecord is { } r ? $"{r.Copy:0.0} ГБ/с" : "—";

    public string LatencyText => _shownRecord is { } r ? $"{r.Latency:0.0} нс" : "—";

    public string ReadDelta => Delta(_shownRecord?.Read, _shownPrevious?.Read, higherIsBetter: true);

    public string WriteDelta => Delta(_shownRecord?.Write, _shownPrevious?.Write, higherIsBetter: true);

    public string CopyDelta => Delta(_shownRecord?.Copy, _shownPrevious?.Copy, higherIsBetter: true);

    public string LatencyDelta => Delta(_shownRecord?.Latency, _shownPrevious?.Latency, higherIsBetter: false);

    /// <summary>Сравнение с теоретическим максимумом модулей и подсказки.</summary>
    public string TheoreticalText
    {
        get
        {
            if (_shownRecord is not { } r || r.Theoretical <= 0)
                return "Теоретический максимум неизвестен: Windows не сообщила частоту памяти.";

            string text = $"Теоретический максимум для {r.Memory}: {r.Theoretical:0.0} ГБ/с. " +
                          $"Чтение — {r.Efficiency * 100:0} % от него (обычно 70–90 %).";
            if (r.Efficiency < 0.55)
                text += " Это мало: возможно, память работает в одноканальном режиме (модули не в тех слотах) или ниже паспортной частоты.";
            return text;
        }
    }

    /// <summary>Подсказка про XMP/EXPO, если память работает на базовой частоте.</summary>
    public string XmpHint => _config is { } c && c.SpeedMts > 0 &&
                             ((c.Type == "DDR4" && c.SpeedMts <= 2666) || (c.Type == "DDR5" && c.SpeedMts <= 4800))
        ? $"Память работает на базовой частоте {c.SpeedMts} МТ/с. Если на модулях указана большая (например, " +
          (c.Type == "DDR4" ? "3200 или 3600" : "6000") + "), включите профиль XMP/EXPO в BIOS — скорость заметно вырастет."
        : "";

    public bool HasXmpHint => XmpHint.Length > 0;

    private async Task LoadConfigAsync()
    {
        _config = await Task.Run(SystemInfoProvider.ReadMemoryConfig);
        OnPropertyChanged(nameof(ConfigText));
        OnPropertyChanged(nameof(XmpHint));
        OnPropertyChanged(nameof(HasXmpHint));
    }

    private long PlannedBytes(long free)
    {
        int percent = Sizes.First(s => s.IsSelected).Value;
        // Оставляем Windows не меньше 1,5 ГБ свободной памяти при любом выборе.
        long bytes = Math.Min(free * percent / 100, free - (1536L << 20));
        return Math.Max(256L << 20, bytes);
    }

    private async Task StartAsync()
    {
        if (IsRunning)
            return;

        string operation = IsStressMode ? "Тест памяти" : "Замер памяти";
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
            ErrorRows = Array.Empty<MemoryErrorRow>();
            _speed = 0;
            _lastProcessed = 0;
            _lastTick = DateTime.UtcNow;
            _test = new MemoryTest(PlannedBytes(MemoryTest.AvailablePhysicalMemory()));
            _test.Start();
            StatusText = "Выделяю и закрепляю память в RAM… Это может занять до минуты, Windows освобождает место.";
            _timer.Start();
            Tick();
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось запустить тест памяти.", ex);
            Release();
            ShowResult("Не удалось запустить тест", ex.Message, "critical");
        }
    }

    private void Tick()
    {
        if (_test is null)
            return;

        ElapsedText = _test.Elapsed.ToString(@"hh\:mm\:ss");
        DateTime now = DateTime.UtcNow;
        long processed = _test.ProcessedBytes;
        double seconds = (now - _lastTick).TotalSeconds;
        if (seconds > 0.2 && _test.Phase == MemoryTestPhase.Running)
            _speed = (processed - _lastProcessed) / seconds / 1e9;
        _lastProcessed = processed;
        _lastTick = now;

        int current = _test.Phase == MemoryTestPhase.Running ? _test.CurrentPattern : -1;
        PatternRows = MemoryTest.Patterns
            .Select((p, i) => new MemoryPatternRow(i + 1, p, current < 0 ? "pending" : i < current ? "done" : i == current ? "current" : "pending"))
            .ToList();

        if (_test.ErrorCount > ErrorRows.Count && ErrorRows.Count < 100)
            ErrorRows = _test.Errors.Select(ToRow).ToList();

        OnPropertyChanged(nameof(ErrorsText));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(CycleText));
        OnPropertyChanged(nameof(CycleProgress));
        OnPropertyChanged(nameof(PatternText));
        OnPropertyChanged(nameof(TestedText));
        OnPropertyChanged(nameof(SpeedText));

        if (_finishing || !IsRunning)
            return;

        if (_test.Phase == MemoryTestPhase.Running && StatusText.StartsWith("Выделяю", StringComparison.Ordinal))
        {
            StatusText = _test.LockedBytes >= _test.AllocatedBytes
                ? "Идёт тест. Компьютер может подтормаживать — это нормально."
                : "Идёт тест. Windows не дала закрепить всю память в RAM: часть может уходить в файл подкачки, тест будет медленнее.";
        }

        if (_test.Phase == MemoryTestPhase.Finished)
        {
            _ = FinishAsync(_test.Failure ?? "завершён");
            return;
        }

        int minutes = Durations.First(d => d.IsSelected).Value;
        if (minutes > 0 && _test.Phase == MemoryTestPhase.Running && _test.Elapsed >= TimeSpan.FromMinutes(minutes))
            _ = FinishAsync(null);
    }

    private void StopByUser()
    {
        if (_benchmarkCancel is not null)
        {
            _benchmarkCancel.Cancel();
            return;
        }

        _ = FinishAsync("остановлен вручную");
    }

    private async Task FinishAsync(string? reason)
    {
        if (_test is null || !IsRunning || _finishing)
            return;

        _finishing = true;
        _timer.Stop();
        StatusText = "Останавливаю тест и возвращаю память Windows…";
        MemoryTest test = _test;
        // Освобождение десятков гигабайт занимает пару секунд — не в потоке интерфейса.
        await Task.Run(() => test.Stop(reason ?? "завершён"));
        Tick();

        string duration = FormatDuration(test.Elapsed);
        int cycles = test.CompletedCycles;
        string tested = Formatting.Bytes((ulong)test.AllocatedBytes);
        if (test.ErrorCount > 0)
        {
            ErrorRows = test.Errors.Select(ToRow).ToList();
            ShowResult($"Найдены ошибки памяти: {test.ErrorCount:N0}",
                $"За {duration} (проверено {tested}, циклов {cycles}) память {test.ErrorCount:N0} раз вернула не то, что в неё записали. " +
                "Это нестабильность: если включён XMP/EXPO или разгон — снизьте частоту, ослабьте тайминги или немного поднимите напряжение DRAM; " +
                "без разгона — проверьте модули по одному и переставьте их в другие слоты. " +
                $"Сбойные биты: {BitsText(test.FlippedBits)}.", "critical");
        }
        else if (test.Failure is not null && reason == test.Failure)
        {
            ShowResult("Тест прерван", test.Failure, "warning");
        }
        else if (reason is not null)
        {
            ShowResult("Тест остановлен", $"Прошло {duration}, проверено {tested}, полных циклов {cycles}, ошибок нет." +
                (test.Elapsed < TimeSpan.FromMinutes(30) ? " Для уверенного результата нужно хотя бы 30 минут." : ""), "info");
        }
        else
        {
            ShowResult($"Стабильно: {duration} без ошибок",
                $"Проверено {tested}, пройдено {cycles} {CyclesWord(cycles)} (все шесть алгоритмов по всей памяти). " +
                (test.Elapsed < TimeSpan.FromMinutes(60) ? "После изменения разгона памяти лучше погонять тест 1–2 часа." : ""), "good");
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
            var progress = new Progress<MemoryBenchmarkProgress>(p => StatusText = $"Шаг {p.Index} из {p.Count}: {p.Step}…");
            CancellationToken token = _benchmarkCancel.Token;
            _config ??= await Task.Run(SystemInfoProvider.ReadMemoryConfig);
            MemoryBenchmarkResult result = await Task.Run(() => MemoryBenchmark.Run(progress, token), token);

            MemoryBenchmarkRecord? previous = _history.Memory.FirstOrDefault();
            var record = new MemoryBenchmarkRecord
            {
                At = DateTimeOffset.Now,
                Memory = _config is { SpeedMts: > 0 } c ? c.Description : "",
                Read = result.Read,
                Write = result.Write,
                Copy = result.Copy,
                Latency = result.Latency,
                Theoretical = _config?.TheoreticalBandwidth ?? 0,
            };
            _history.AddMemory(record);
            ShowBenchmark(record, previous);
            ShowResult($"Чтение {record.Read:0.0} ГБ/с, задержка {record.Latency:0.0} нс",
                (record.Efficiency is { } e ? $"Это {e * 100:0} % от теоретического максимума ваших модулей. " : "") +
                "Замер сохранён: после включения XMP/EXPO или изменения таймингов повторите его и сравните.", "good");
        }
        catch (OperationCanceledException)
        {
            ShowResult("Замер остановлен", "Результаты не сохранены.", "info");
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка замера памяти.", ex);
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

    private void ShowBenchmark(MemoryBenchmarkRecord record, MemoryBenchmarkRecord? previous)
    {
        _shownRecord = record;
        _shownPrevious = previous;
        foreach (string name in new[]
                 {
                     nameof(HasBenchmark), nameof(BenchmarkCaption), nameof(ReadText), nameof(WriteText), nameof(CopyText),
                     nameof(LatencyText), nameof(ReadDelta), nameof(WriteDelta), nameof(CopyDelta), nameof(LatencyDelta),
                     nameof(TheoreticalText),
                 })
            OnPropertyChanged(name);
    }

    private static MemoryErrorRow ToRow(MemoryTestError e) => new(
        e.At.ToString(@"hh\:mm\:ss"),
        e.Pattern,
        $"{e.Offset / (double)(1L << 30):0.000} ГБ",
        $"{e.Expected:X16}",
        $"{e.Actual:X16}",
        BitsText(e.FlippedBits));

    /// <summary>Номера сбойных битов: «3, 17» — если всегда одни и те же, виноват скорее конкретный чип.</summary>
    private static string BitsText(ulong bits)
    {
        var numbers = Enumerable.Range(0, 64).Where(i => (bits >> i & 1) != 0).ToList();
        if (numbers.Count == 0)
            return "—";
        return numbers.Count <= 8 ? string.Join(", ", numbers) : $"{numbers.Count} разных битов";
    }

    private static string CyclesWord(int n) =>
        n % 10 == 1 && n % 100 != 11 ? "цикл" : n % 10 is >= 2 and <= 4 && (n % 100 < 12 || n % 100 > 14) ? "цикла" : "циклов";

    private static string Delta(double? now, double? before, bool higherIsBetter)
    {
        if (now is not { } n || before is not { } b || b <= 0)
            return "";
        double percent = (n - b) / b * 100;
        string word = Math.Abs(percent) < 1 ? "" : (percent > 0) == higherIsBetter ? " (лучше)" : " (хуже)";
        return $"{percent:+0;-0;0} % к прошлому{word}";
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

    public void Refresh()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ConfigText));
    }

    /// <summary>При закрытии приложения тест обязательно останавливается и память возвращается.</summary>
    public void Shutdown()
    {
        _timer.Stop();
        _benchmarkCancel?.Cancel();
        _test?.Dispose();
    }
}
