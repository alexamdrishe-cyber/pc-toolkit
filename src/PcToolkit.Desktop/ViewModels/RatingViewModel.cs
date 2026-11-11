using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using PcToolkit.Core.Benchmarks;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Rating;
using PcToolkit.Core.Settings;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Строка общего рейтинга: модель, медиана по разным ПК, обычный диапазон и что выжимают лучшие экземпляры.</summary>
public sealed class RatingRowViewModel
{
    /// <summary>Место среди проверенных моделей; у непроверенных — «·».</summary>
    public string RankText { get; init; } = "";

    public string Title { get; init; } = "";

    public string Subtitle { get; init; } = "";

    public string MedianText { get; init; } = "";

    public string RangeText { get; init; } = "";

    public string SecondText { get; init; } = "";

    public bool IsVerified { get; init; }

    public bool IsMine { get; init; }

    public GridLength BarLength { get; init; }

    public GridLength RestLength { get; init; }
}

/// <summary>Своё устройство на фоне таких же: результат и вывод.</summary>
public sealed class RatingMineViewModel
{
    public string Title { get; init; } = "";

    public string ScoreText { get; init; } = "";

    public string VerdictTitle { get; init; } = "";

    public string VerdictDetails { get; init; } = "";

    /// <summary>Good, Notice, Bad, Info — для цвета.</summary>
    public string Level { get; init; } = "Info";
}

/// <summary>
/// Вкладка «Общий рейтинг»: проверенные модели (ориентир — медиана по разным компьютерам) и своё устройство
/// на фоне таких же. Свои результаты уходят в рейтинг только с согласия: модель устройства и баллы, без личных данных.
/// </summary>
public sealed class RatingViewModel : ObservableObject
{
    private readonly BenchmarkHistory _history;
    private readonly RatingClient _client = RatingClient.Shared;
    private readonly bool _available = RatingClient.IsAvailable;
    private string _kind = RatingKinds.Cpu;
    private bool _loading;
    private bool _loaded;
    private bool _share;
    private bool _asked;
    private bool _showUnverified;
    private string _status = "";
    private string _shareStatus = "";
    private int _loadVersion;
    private RatingTable? _table;

    public RatingViewModel(BenchmarkHistory history)
    {
        _history = history;
        AppSettings settings = AppSettings.Load();
        _share = settings.RatingShare;
        _asked = settings.RatingAsked;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(refresh: true), () => !_loading && IsAvailable);
        AcceptCommand = new RelayCommand(() => Share = true);
        DeclineCommand = new RelayCommand(() =>
        {
            _asked = true;
            AppSettings.Update(s => s.RatingAsked = true);
            OnPropertyChanged(nameof(ShowSharePrompt));
        });

        BenchmarkHistory.RecordAdded += record => Application.Current?.Dispatcher.BeginInvoke(() => _ = OnRecordAddedAsync(record));
        if (_share && IsAvailable)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                await _client.FlushAsync(CancellationToken.None);
            });
        }
    }

    public ObservableCollection<RatingRowViewModel> Rows { get; } = new();

    public ObservableCollection<RatingMineViewModel> Mine { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand AcceptCommand { get; }

    public ICommand DeclineCommand { get; }

    /// <summary>Рейтинг подключён: в сборке задан репозиторий проекта (или свой сервер в настройках).</summary>
    public bool IsAvailable => _available;

    public bool IsNotAvailable => !IsAvailable;

    public bool IsCpu { get => _kind == RatingKinds.Cpu; set => SetKind(RatingKinds.Cpu, value); }

    public bool IsGpu { get => _kind == RatingKinds.Gpu; set => SetKind(RatingKinds.Gpu, value); }

    public bool IsMemory { get => _kind == RatingKinds.Memory; set => SetKind(RatingKinds.Memory, value); }

    public bool IsDisk { get => _kind == RatingKinds.Disk; set => SetKind(RatingKinds.Disk, value); }

    public string ScoreHeader => _kind switch
    {
        RatingKinds.Cpu => "баллы, все потоки",
        RatingKinds.Gpu => "баллы",
        RatingKinds.Memory => "чтение, ГБ/с",
        _ => "чтение, МБ/с",
    };

    public string KindHint => _kind switch
    {
        RatingKinds.Cpu => "Баллы теста процессора во всех потоках (у Ryzen 5 5600 в одном потоке — 1 000). Замер: Тесты → Процессор → «Замер производительности».",
        RatingKinds.Gpu => "Баллы теста видеокарты: 3D-сцена, вычисления и память (у Radeon RX 6700 XT — 10 000). Замер: Тесты → Видеокарта.",
        RatingKinds.Memory => "Скорость чтения памяти; группы — по типу, частоте и числу каналов. Замер: Тесты → Память.",
        _ => "Последовательное чтение (SEQ1M Q8T1) и чтение вразброс 4 КБ — отзывчивость системы. Замер: Диски → «Скорость диска».",
    };

    public bool IsLoading
    {
        get => _loading;
        private set
        {
            if (SetProperty(ref _loading, value))
                RelayCommand.Refresh();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Что с отправкой: ждут в очереди, выключено и т. п.</summary>
    public string ShareStatus
    {
        get => _shareStatus;
        private set => SetProperty(ref _shareStatus, value);
    }

    public bool HasRows => Rows.Count > 0;

    public bool HasMine => Mine.Count > 0;

    /// <summary>Однажды спросить, делиться ли результатами.</summary>
    public bool ShowSharePrompt => IsAvailable && !_asked && !_share;

    public bool ShowUnverified
    {
        get => _showUnverified;
        set
        {
            if (SetProperty(ref _showUnverified, value))
                Rebuild();
        }
    }

    /// <summary>Добавлять свои результаты в общий рейтинг. Включение сразу отправляет последние свежие замеры.</summary>
    public bool Share
    {
        get => _share;
        set
        {
            if (!SetProperty(ref _share, value))
                return;
            _asked = true;
            AppSettings.Update(s =>
            {
                s.RatingShare = value;
                s.RatingAsked = true;
            });
            AppLog.Info(value ? "Рейтинг: пользователь согласился делиться результатами." : "Рейтинг: отправка результатов выключена.");
            OnPropertyChanged(nameof(ShowSharePrompt));
            if (value)
                _ = SubmitExistingAsync();
            else
                ShareStatus = "";
        }
    }

    /// <summary>При открытии вкладки.</summary>
    public void OnShown()
    {
        if (!_loaded)
            _ = LoadAsync(refresh: false);
    }

    private void SetKind(string kind, bool value)
    {
        if (!value || _kind == kind)
            return;
        _kind = kind;
        OnPropertyChanged(nameof(IsCpu));
        OnPropertyChanged(nameof(IsGpu));
        OnPropertyChanged(nameof(IsMemory));
        OnPropertyChanged(nameof(IsDisk));
        OnPropertyChanged(nameof(ScoreHeader));
        OnPropertyChanged(nameof(KindHint));
        _ = LoadAsync(refresh: false);
    }

    private async Task LoadAsync(bool refresh)
    {
        int version = ++_loadVersion;
        string kind = _kind;
        _loaded = true;
        _table = null;
        Rebuild();
        if (!IsAvailable)
        {
            Status = "Общий рейтинг ещё не подключён в этой сборке. Сравнение со своими прошлыми замерами и примерный рейтинг — на вкладках устройств.";
            return;
        }

        IsLoading = true;
        Status = "Загружаю рейтинг…";
        try
        {
            RatingTableResult? result = await _client.TableAsync(kind, RatingSubmissions.Version(kind), refresh, CancellationToken.None);
            if (version != _loadVersion)
                return;
            _table = result?.Table;
            if (result is null)
                Status = "Сервер рейтинга сейчас недоступен. Попробуйте позже.";
            else if (!result.Fresh)
                Status = $"Сервер рейтинга недоступен — показан список от {result.Table.Updated.ToLocalTime():dd.MM HH:mm}.";
            else if (result.Table.Models.Count == 0)
                Status = "Здесь пока нет результатов." + (_share ? " Сделайте замер — ваш станет первым." : " Включите «Делиться результатами» и сделайте замер — ваш станет первым.");
            else
                Status = $"Проверенных моделей: {result.Table.Models.Count(m => m.Verified)}. Проверенная — не меньше {RatingStats.MinResults} замеров " +
                         $"с {RatingStats.MinMachines} разных компьютеров; каждый компьютер считается один раз, случайные и поддельные результаты отсеиваются.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Рейтинг не загрузился.", ex);
            Status = "Не удалось загрузить рейтинг. Попробуйте позже.";
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                Rebuild();
            }
        }
    }

    /// <summary>Пересобрать строки и блок «ваше устройство» из таблицы и своей истории замеров.</summary>
    private void Rebuild()
    {
        IReadOnlyList<RatingSubmission> mine = RatingSubmissions.Mine(_history, _kind, DateTimeOffset.Now);
        var mineKeys = new HashSet<string>(mine.Select(m => RatingStats.ModelKey(m.Model)), StringComparer.OrdinalIgnoreCase);
        List<RatingModel> models = (_table?.Models ?? new List<RatingModel>())
            .Where(m => m.Verified || _showUnverified || mineKeys.Contains(m.Model))
            .OrderByDescending(m => m.Verified)
            .ThenByDescending(m => m.Median)
            .ToList();
        double max = models.Count == 0 ? 1 : Math.Max(models.Max(m => m.Median), 1);

        Rows.Clear();
        int rank = 0;
        foreach (RatingModel model in models)
        {
            double share = Math.Clamp(model.Median / max, 0.01, 1);
            Rows.Add(new RatingRowViewModel
            {
                RankText = model.Verified ? (++rank).ToString(CultureInfo.CurrentCulture) : "·",
                Title = model.Model,
                Subtitle = model.Verified
                    ? $"{Plural(model.Machines, "компьютер", "компьютера", "компьютеров")} · {Plural(model.Results, "замер", "замера", "замеров")}"
                    : $"мало данных: {Plural(model.Machines, "компьютер", "компьютера", "компьютеров")}",
                MedianText = Format(model.Median),
                RangeText = model.Machines >= 2 ? $"обычно {Format(model.Low)}–{Format(model.High)} · лучшие {Format(model.Top)}" : "",
                SecondText = Second(model.Median2),
                IsVerified = model.Verified,
                IsMine = mineKeys.Contains(model.Model),
                BarLength = new GridLength(share, GridUnitType.Star),
                RestLength = new GridLength(1 - share + 0.0001, GridUnitType.Star),
            });
        }

        Mine.Clear();
        if (IsAvailable)
        {
            foreach (RatingSubmission own in mine)
                Mine.Add(Judge(own));
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasMine));
    }

    private RatingMineViewModel Judge(RatingSubmission own)
    {
        string key = RatingStats.ModelKey(own.Model);
        RatingModel? model = _table?.Models.FirstOrDefault(m => string.Equals(m.Model, key, StringComparison.OrdinalIgnoreCase));
        (RatingVerdict verdict, double deviation) = RatingStats.Compare(own.Score, model);
        string score = Format(own.Score) + " " + Unit + (own.Score2 is double s2 ? " · " + Second(s2) : "");
        string percent = Math.Abs(deviation * 100).ToString("0", CultureInfo.CurrentCulture);

        if (_table is null)
        {
            return new RatingMineViewModel
            {
                Title = key, ScoreText = score, Level = "Info",
                VerdictTitle = "Сравнение появится, когда загрузится рейтинг",
            };
        }

        return verdict switch
        {
            RatingVerdict.Normal => new RatingMineViewModel
            {
                Title = key, ScoreText = score, Level = "Good",
                VerdictTitle = "Как у большинства таких же",
                VerdictDetails = $"Обычный диапазон для этой модели — {Format(model!.Low)}–{Format(model.High)} (медиана {Format(model.Median)}). " +
                                 $"Хорошо охлаждённые экземпляры выдают до {Format(model.Top)}.",
            },
            RatingVerdict.Above => new RatingMineViewModel
            {
                Title = key, ScoreText = score, Level = "Good",
                VerdictTitle = $"Выше обычного на {percent} %",
                VerdictDetails = $"Обычно у такой модели {Format(model!.Low)}–{Format(model.High)}. Хорошее охлаждение, удачный экземпляр или разгон.",
            },
            RatingVerdict.Below => new RatingMineViewModel
            {
                Title = key, ScoreText = score, Level = "Bad",
                VerdictTitle = $"Ниже обычного на {percent} %",
                VerdictDetails = $"Обычно у такой модели {Format(model!.Low)}–{Format(model.High)}. {BelowHint}",
            },
            _ => new RatingMineViewModel
            {
                Title = key, ScoreText = score, Level = "Info",
                VerdictTitle = model is null ? "Таких результатов в рейтинге ещё нет" : "Пока мало данных для сравнения",
                VerdictDetails = model is null
                    ? (_share ? "Ваш замер уйдёт в рейтинг первым — сравнение появится, когда к нему добавятся другие компьютеры."
                              : "Включите «Делиться результатами» — ваш замер станет первым для этой модели.")
                    : $"Есть {Plural(model.Machines, "компьютер", "компьютера", "компьютеров")}, нужно хотя бы {RatingStats.MinMachines} " +
                      $"и {RatingStats.MinResults} замеров. Медиана сейчас — {Format(model.Median)}.",
            },
        };
    }

    private string BelowHint => _kind switch
    {
        RatingKinds.Cpu => "Частые причины: перегрев и сброс частот, схема питания «Экономия энергии», программы в фоне во время замера, медленная память.",
        RatingKinds.Gpu => "Частые причины: перегрев, старый драйвер, ограничение мощности, слот PCIe с меньшим числом линий, программы в фоне.",
        RatingKinds.Memory => "Частые причины: высокие тайминги, контроллер памяти процессора (другая платформа), программы в фоне.",
        _ => "Частые причины: диск почти заполнен, перегрев NVMe, слот с меньшей скоростью PCIe, замер во время обновления Windows или антивирусной проверки.",
    };

    private string Unit => _kind switch
    {
        RatingKinds.Cpu or RatingKinds.Gpu => "баллов",
        RatingKinds.Memory => "ГБ/с",
        _ => "МБ/с",
    };

    private string Format(double value) => _kind switch
    {
        RatingKinds.Memory => value.ToString(value >= 100 ? "N0" : "0.0", CultureInfo.CurrentCulture),
        _ => value.ToString("N0", CultureInfo.CurrentCulture),
    };

    private string Second(double? value) => value is not double v || v <= 0
        ? ""
        : _kind switch
        {
            RatingKinds.Cpu => $"1 поток: {v.ToString("N0", CultureInfo.CurrentCulture)}",
            RatingKinds.Memory => $"задержка {v.ToString("0", CultureInfo.CurrentCulture)} нс",
            RatingKinds.Disk => $"4 КБ: {v.ToString(v >= 100 ? "N0" : "0.0", CultureInfo.CurrentCulture)} МБ/с",
            _ => "",
        };

    private async Task OnRecordAddedAsync(object record)
    {
        try
        {
            await SubmitNewAsync(record);
        }
        catch (Exception ex)
        {
            AppLog.Error("Рейтинг: новый замер не отправлен.", ex);
        }
    }

    private async Task SubmitNewAsync(object record)
    {
        if (RatingSubmissions.From(record) is not { } submission)
            return;
        if (_share && IsAvailable)
        {
            bool sent = await _client.SubmitAsync(submission, CancellationToken.None);
            ShareStatus = sent ? "Последний замер добавлен в рейтинг." : QueueText();
            if (sent && submission.Kind == _kind)
            {
                await LoadAsync(refresh: true);
                return;
            }
        }

        if (submission.Kind == _kind)
            Rebuild();
    }

    /// <summary>Сразу после согласия — последние свежие замеры каждого вида.</summary>
    private async Task SubmitExistingAsync()
    {
        try
        {
            await SubmitLatestAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Рейтинг: замеры не отправлены.", ex);
            ShareStatus = "Не удалось отправить замеры — попробую при следующем запуске.";
        }
    }

    private async Task SubmitLatestAsync()
    {
        if (!IsAvailable)
            return;
        ShareStatus = "Отправляю последние замеры…";
        int sent = 0, total = 0;
        foreach (string kind in RatingKinds.All)
        {
            foreach (RatingSubmission submission in RatingSubmissions.Mine(_history, kind, DateTimeOffset.Now))
            {
                total++;
                if (await _client.SubmitAsync(submission, CancellationToken.None))
                    sent++;
            }
        }

        ShareStatus = total == 0
            ? "Свежих замеров пока нет: результаты будут добавляться после каждого теста."
            : sent == total ? $"Добавлено в рейтинг: {Plural(sent, "замер", "замера", "замеров")}." : QueueText();
        await LoadAsync(refresh: true);
    }

    private string QueueText()
    {
        int queued = _client.QueuedCount;
        return queued > 0 ? $"Сервер рейтинга сейчас недоступен — {Plural(queued, "замер ждёт", "замера ждут", "замеров ждут")} отправки и уйдут позже." : "";
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int last = n % 10, lastTwo = n % 100;
        string word = last == 1 && lastTwo != 11 ? one : last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? few : many;
        return $"{n.ToString("N0", CultureInfo.CurrentCulture)} {word}";
    }
}
