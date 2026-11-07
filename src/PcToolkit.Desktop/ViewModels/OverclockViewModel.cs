using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using PcToolkit.Core;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Overclock;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Tweaks;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>
/// Экран «Разгон»: видеокарта AMD через ADLX — режимы, частоты, напряжение, память, предел мощности, вентиляторы.
/// Изменения применяются через движок (предпросмотр, журнал, откат), после применения — проверка стабильности;
/// если она не прошла или компьютер завис, настройки возвращаются. Есть автоподбор по тестам.
/// </summary>
public sealed class OverclockViewModel : ObservableObject
{
    public const string TweakId = "gpu-tuning";

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly ChangeEngine _engine;
    private readonly OperationGate _gate;
    private readonly IChangePresenter _presenter;
    private readonly Action _journalChanged;
    private AdlxGpuTuning? _tuning;
    private GpuTuningInfo? _info;
    private bool _loading;
    private bool _loaded;
    private string _unavailableText = "";
    private bool _isBusy;
    private string _busyText = "";
    private double _busyProgress;
    private string _message = "";
    private bool _messageIsWarning;
    private bool _verifyAfterApply = true;
    private int _verifyMinutes = 1;
    private CancellationTokenSource? _cancel;
    private string _liveClock = "—";
    private string _liveTemperature = "—";
    private string _liveHotSpot = "—";
    private string _livePower = "—";
    private string _liveLoad = "—";

    public OverclockViewModel(ChangeEngine engine, OperationGate gate, IChangePresenter presenter, Action journalChanged)
    {
        _engine = engine;
        _gate = gate;
        _presenter = presenter;
        _journalChanged = journalChanged;

        ApplyCommand = new RelayCommand(Apply, () => IsAvailable && !_isBusy && HasChanges);
        DiscardCommand = new RelayCommand(Discard, () => IsAvailable && !_isBusy && HasChanges);
        DefaultsCommand = new RelayCommand(SetDefaults, () => IsAvailable && !_isBusy);
        ApplyPresetCommand = new RelayCommand(ApplyPreset, () => IsAvailable && !_isBusy && SelectedPreset is { IsCurrent: false });
        CheckCommand = new RelayCommand(() => _ = CheckStabilityAsync(null), () => IsAvailable && !_isBusy);
        UndervoltCommand = new RelayCommand(() => _ = AutoTuneAsync(TuneGoal.Undervolt), () => CanTune(Voltage));
        OverclockGpuCommand = new RelayCommand(() => _ = AutoTuneAsync(TuneGoal.OverclockGpu), () => CanTune(MaxFrequency));
        OverclockVramCommand = new RelayCommand(() => _ = AutoTuneAsync(TuneGoal.OverclockVram), () => CanTune(VramFrequency));
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => _isBusy && _cancel is not null);
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !_isBusy && !_loading);
    }

    public ICommand ApplyCommand { get; }

    public ICommand DiscardCommand { get; }

    public ICommand DefaultsCommand { get; }

    public ICommand ApplyPresetCommand { get; }

    public ICommand CheckCommand { get; }

    public ICommand UndervoltCommand { get; }

    public ICommand OverclockGpuCommand { get; }

    public ICommand OverclockVramCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RefreshCommand { get; }

    // ----- Состояние -----

    public bool IsAvailable => _info is not null;

    public bool IsLoading => _loading;

    public string UnavailableText
    {
        get => _unavailableText;
        private set => SetProperty(ref _unavailableText, value);
    }

    public bool ShowUnavailable => !_loading && _info is null && _loaded;

    public string GpuName => _info?.GpuName ?? "";

    public string GpuDetails => _info is null ? "" : $"{_info.VramMb / 1024.0:0.#} ГБ видеопамяти · ADLX {_info.AdlxVersion}";

    public string FactoryText => _info is null ? "" : _info.IsAtFactory ? "Заводские настройки" : "Настройки изменены";

    public bool IsAtFactory => _info?.IsAtFactory ?? true;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEdit));
                RelayCommand.Refresh();
            }
        }
    }

    public bool CanEdit => !_isBusy;

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public double BusyProgress
    {
        get => _busyProgress;
        private set => SetProperty(ref _busyProgress, value);
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool MessageIsWarning
    {
        get => _messageIsWarning;
        private set => SetProperty(ref _messageIsWarning, value);
    }

    public bool HasMessage => _message.Length > 0;

    public string LiveClock { get => _liveClock; private set => SetProperty(ref _liveClock, value); }

    public string LiveTemperature { get => _liveTemperature; private set => SetProperty(ref _liveTemperature, value); }

    public string LiveHotSpot { get => _liveHotSpot; private set => SetProperty(ref _liveHotSpot, value); }

    public string LivePower { get => _livePower; private set => SetProperty(ref _livePower, value); }

    public string LiveLoad { get => _liveLoad; private set => SetProperty(ref _liveLoad, value); }

    // ----- Параметры -----

    public ObservableCollection<PresetOptionViewModel> Presets { get; } = new();

    public PresetOptionViewModel? SelectedPreset => Presets.FirstOrDefault(p => p.IsSelected);

    public bool HasPresets => Presets.Count > 0;

    public TuningSliderViewModel? MaxFrequency { get; private set; }

    public TuningSliderViewModel? MinFrequency { get; private set; }

    public TuningSliderViewModel? Voltage { get; private set; }

    public TuningSliderViewModel? VramFrequency { get; private set; }

    public TuningSliderViewModel? PowerLimit { get; private set; }

    public TuningSliderViewModel? Tdc { get; private set; }

    public TuningSliderViewModel? MinAcoustic { get; private set; }

    public TuningSliderViewModel? TargetFanSpeed { get; private set; }

    public TuningSliderViewModel? MinFanSpeed { get; private set; }

    public ObservableCollection<TuningSliderViewModel> GfxSliders { get; } = new();

    public ObservableCollection<TuningSliderViewModel> VramSliders { get; } = new();

    public ObservableCollection<TuningSliderViewModel> PowerSliders { get; } = new();

    public ObservableCollection<TuningSliderViewModel> FanSliders { get; } = new();

    public ObservableCollection<FanPointViewModel> FanCurve { get; } = new();

    public ObservableCollection<TimingOptionViewModel> MemoryTimings { get; } = new();

    public bool HasGfx => GfxSliders.Count > 0;

    public string GfxNote => _info?.GfxNote ?? "";

    public bool HasVram => VramSliders.Count > 0 || MemoryTimings.Count > 0;

    public bool HasPower => PowerSliders.Count > 0;

    public bool HasFan => FanSliders.Count > 0 || FanCurve.Count > 0 || HasZeroRpm;

    public bool HasFanCurve => FanCurve.Count > 0;

    public bool HasZeroRpm => _info?.ZeroRpm is not null;

    private bool _zeroRpm;

    public bool ZeroRpm
    {
        get => _zeroRpm;
        set
        {
            if (SetProperty(ref _zeroRpm, value))
                OnEdited();
        }
    }

    public string ZeroRpmHint => _info?.ZeroRpmDefault is bool d ? $"По умолчанию {(d ? "включено" : "выключено")}." : "";

    /// <summary>Точки кривой вентилятора для графика.</summary>
    public IReadOnlyList<FanPoint> FanCurvePoints => FanCurve.Select(p => new FanPoint(p.Temperature.IntValue, p.Speed.IntValue)).ToList();

    public bool HasChanges => Changes().Count > 0;

    public string ChangesText
    {
        get
        {
            int count = Changes().Count;
            return count == 0 ? "Изменений нет" : $"Изменено параметров: {count}";
        }
    }

    public bool VerifyAfterApply
    {
        get => _verifyAfterApply;
        set => SetProperty(ref _verifyAfterApply, value);
    }

    public bool Verify1 { get => _verifyMinutes == 1; set => SetVerify(1, value); }

    public bool Verify5 { get => _verifyMinutes == 5; set => SetVerify(5, value); }

    public bool Verify15 { get => _verifyMinutes == 15; set => SetVerify(15, value); }

    // ===================================================================

    /// <summary>Экран открыт: читаем настройки видеокарты (первый раз или после изменений).</summary>
    public void EnsureLoaded()
    {
        if (!_loaded && !_loading)
            _ = LoadAsync();
    }

    /// <summary>Показания датчиков раз в секунду.</summary>
    public void Update(HardwareSnapshot snapshot)
    {
        KeyMetrics metrics = KeyMetrics.From(snapshot);
        LiveClock = metrics.GpuClock is { } clock ? $"{clock.Value:0} МГц" : "—";
        LiveTemperature = metrics.GpuTemperature is { } t ? $"{t.Value:0} °C" : "—";
        LiveHotSpot = metrics.GpuHotSpot is { } h ? $"{h.Value:0} °C" : "—";
        LivePower = metrics.GpuPower is { } p ? $"{p.Value:0} Вт" : "—";
        LiveLoad = metrics.GpuLoad is { } l ? $"{l.Value:0} %" : "—";
    }

    /// <summary>
    /// При запуске: если прошлая проверка разгона или автоподбор оборвались (зависание, перезагрузка),
    /// возвращает прежние настройки. Возвращает текст для пользователя или null.
    /// </summary>
    public static string? RecoverInterrupted(ChangeEngine engine)
    {
        TuningMarker? marker = TuningMarker.Load();
        if (marker is null)
            return null;

        try
        {
            AppLog.Warn($"Найдена незавершённая проверка разгона ({marker.Kind}, начата {marker.StartedAt:g}).");
            if (marker.Kind == TuningMarker.CheckKind)
            {
                ChangePlan plan = engine.PlanRevertSet(marker.SetId);
                ChangeResult result = plan.CanExecute ? engine.Execute(plan) : ChangeResult.Fail(string.Join(" ", plan.Problems.Concat(plan.Skipped)));
                return "Прошлая проверка разгона не завершилась — похоже, компьютер завис или перезагрузился. " +
                       (result.Success ? "Настройки видеокарты возвращены к прежним." : "Вернуть настройки не удалось: " + result.Message +
                                                                                        " Драйвер AMD после сбоя обычно сам сбрасывает разгон.");
            }

            if (marker.Kind == TuningMarker.TuneKind && AdlxGpuTuning.Shared(out _) is { } tuning)
            {
                tuning.Set(marker.Parameter, marker.Original);
                return "Прошлый автоподбор не завершился — похоже, компьютер завис или перезагрузился. " +
                       $"{GpuParameters.Title(marker.Parameter)}: возвращено {GpuParameters.Describe(marker.Parameter, marker.Original)}.";
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось вернуть настройки видеокарты после прерванной проверки.", ex);
            return "Прошлая проверка разгона не завершилась, и вернуть настройки не удалось: " + ex.Message;
        }
        finally
        {
            TuningMarker.Delete();
        }
    }

    public async Task LoadAsync()
    {
        if (_loading)
            return;

        _loading = true;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowUnavailable));
        try
        {
            (AdlxGpuTuning? tuning, GpuTuningInfo? info, string? error) = await Task.Run<(AdlxGpuTuning?, GpuTuningInfo?, string?)>(() =>
            {
                AdlxGpuTuning? t = AdlxGpuTuning.Shared(out string? e);
                return t is null ? (null, null, e) : (t, t.Read(), (string?)null);
            });

            _tuning = tuning;
            _info = info;
            UnavailableText = info is not null ? "" : "Разгон доступен для видеокарт AMD Radeon с установленным драйвером AMD. " +
                                                       $"Сейчас: {error}.";
            BuildEditors();
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось прочитать настройки видеокарты.", ex);
            _info = null;
            UnavailableText = "Не удалось прочитать настройки видеокарты: " + ex.Message;
        }
        finally
        {
            _loading = false;
            _loaded = true;
            RaiseAll();
        }
    }

    private void BuildEditors()
    {
        GfxSliders.Clear();
        VramSliders.Clear();
        PowerSliders.Clear();
        FanSliders.Clear();
        FanCurve.Clear();
        MemoryTimings.Clear();
        Presets.Clear();
        MaxFrequency = MinFrequency = Voltage = VramFrequency = PowerLimit = Tdc = MinAcoustic = TargetFanSpeed = MinFanSpeed = null;
        if (_info is not { } info)
            return;

        foreach (string preset in info.SupportedPresets)
            Presets.Add(new PresetOptionViewModel(preset, preset == info.CurrentPreset, this));

        MaxFrequency = Add(GfxSliders, AdlxGpuTuning.GfxMaxFrequency, "Максимальная частота", "МГц", info.MaxFrequencyRange, info.MaxFrequency, info.MaxFrequencyDefault, 10,
            "Выше — быстрее в играх, но больше нагрев и риск сбоев. Видеокарта сама решает, сколько из этого взять под нагрузкой.");
        MinFrequency = Add(GfxSliders, AdlxGpuTuning.GfxMinFrequency, "Минимальная частота", "МГц", info.MinFrequencyRange, info.MinFrequency, info.MinFrequencyDefault, 10,
            "Нижняя граница под нагрузкой. Поднимать стоит, только если в лёгких играх частота «проседает».");
        Voltage = Add(GfxSliders, AdlxGpuTuning.GfxVoltage, "Напряжение", "мВ", info.VoltageRange, info.Voltage, info.VoltageDefault, 5,
            "Ниже — холоднее, тише и часто быстрее (андервольт). Слишком низкое даёт сбой драйвера — тогда проверка вернёт всё назад.");
        VramFrequency = Add(VramSliders, AdlxGpuTuning.VramMaxFrequency, "Частота видеопамяти", "МГц", info.VramFrequencyRange, info.VramFrequency, info.VramFrequencyDefault, 5,
            "Помогает в играх, упирающихся в память (высокие разрешения). Ошибки памяти видны в проверке как ошибки в кадрах.");
        PowerLimit = Add(PowerSliders, AdlxGpuTuning.PowerLimit, "Предел мощности", "%", info.PowerLimitRange, info.PowerLimit, info.PowerLimitDefault ?? 0, 1,
            "Сколько ватт видеокарта может взять сверх заводского. Больше — выше частоты под нагрузкой и больше нагрев; меньше — тише и экономнее.");
        Tdc = Add(PowerSliders, AdlxGpuTuning.PowerTdc, "Предел тока (TDC)", "%", info.TdcRange, info.Tdc, info.TdcDefault ?? 0, 1,
            "Ограничение тока питания ядра.");
        MinAcoustic = Add(FanSliders, AdlxGpuTuning.FanMinAcoustic, "Акустический предел", "МГц", info.MinAcousticRange, info.MinAcoustic, info.MinAcousticDefault, 10,
            "Частота, ниже которой видеокарта не опустится ради тишины. Меньше — тише вентиляторы, но ниже частоты при перегреве.");
        TargetFanSpeed = Add(FanSliders, AdlxGpuTuning.FanTargetSpeed, "Целевая скорость вентиляторов", "об/мин", info.TargetFanSpeedRange, info.TargetFanSpeed, info.TargetFanSpeedDefault, 50, "");
        MinFanSpeed = Add(FanSliders, AdlxGpuTuning.FanMinSpeed, "Минимальная скорость вентиляторов", "об/мин", info.MinFanSpeedRange, info.MinFanSpeed, info.MinFanSpeedDefault, 50, "");

        foreach (int timing in info.MemoryTimings)
            MemoryTimings.Add(new TimingOptionViewModel(timing, timing == info.MemoryTiming, this));

        if (info.FanSpeedRange is { } speed && info.FanTemperatureRange is { } temperature)
        {
            for (int i = 0; i < info.FanCurve.Count; i++)
            {
                FanPoint point = info.FanCurve[i];
                FanPoint? fallback = i < info.FanCurveDefault.Count ? info.FanCurveDefault[i] : null;
                FanCurve.Add(new FanPointViewModel(i + 1,
                    new TuningSliderViewModel("", "Температура", "°C", temperature, point.Temperature, fallback?.Temperature, 1, "", OnEdited),
                    new TuningSliderViewModel("", "Скорость", "%", speed, point.Speed, fallback?.Speed, 1, "", OnEdited)));
            }
        }

        _zeroRpm = info.ZeroRpm ?? false;
    }

    private TuningSliderViewModel? Add(ObservableCollection<TuningSliderViewModel> group, string parameter, string title, string unit,
        TuningRange? range, int? current, int? defaultValue, int buttonStep, string hint)
    {
        if (range is not { } r || current is not int value)
            return null;

        var slider = new TuningSliderViewModel(parameter, title, unit, r, value, defaultValue, buttonStep, hint, OnEdited);
        group.Add(slider);
        return slider;
    }

    internal void OnEdited()
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(ChangesText));
        OnPropertyChanged(nameof(FanCurvePoints));
        RelayCommand.Refresh();
    }

    internal void OnPresetSelected() => RelayCommand.Refresh();

    /// <summary>Изменённые параметры в порядке, в котором их безопасно записывать.</summary>
    private List<ChangeDefinition> Changes()
    {
        var list = new List<ChangeDefinition>();
        if (_info is null)
            return list;

        // Максимальную частоту — раньше минимальной: иначе новая минимальная может оказаться выше старой максимальной.
        foreach (TuningSliderViewModel? slider in new[] { MaxFrequency, MinFrequency, Voltage, VramFrequency, PowerLimit, Tdc, MinAcoustic, TargetFanSpeed, MinFanSpeed })
        {
            if (slider is { IsChanged: true })
                list.Add(GpuTuningTarget.Definition(slider.Parameter, slider.IntValue.ToString(CultureInfo.InvariantCulture)));
        }

        if (MemoryTimings.FirstOrDefault(t => t.IsSelected) is { } timing && timing.Value != _info.MemoryTiming)
            list.Add(GpuTuningTarget.Definition(AdlxGpuTuning.VramTiming, timing.Value.ToString(CultureInfo.InvariantCulture)));

        if (_info.ZeroRpm is bool zero && zero != _zeroRpm)
            list.Add(GpuTuningTarget.Definition(AdlxGpuTuning.FanZeroRpm, _zeroRpm ? "1" : "0"));

        if (FanCurve.Count > 0 && FanCurve.Any(p => p.Temperature.IsChanged || p.Speed.IsChanged))
            list.Add(GpuTuningTarget.Definition(AdlxGpuTuning.FanCurve, AdlxGpuTuning.FormatCurve(FanCurvePoints)));

        return list;
    }

    private string? Validate()
    {
        if (MaxFrequency is { } max && MinFrequency is { } min && min.IntValue > max.IntValue - 100)
            return "Минимальная частота должна быть хотя бы на 100 МГц ниже максимальной.";

        for (int i = 1; i < FanCurve.Count; i++)
        {
            if (FanCurve[i].Temperature.IntValue <= FanCurve[i - 1].Temperature.IntValue)
                return $"Температура в точке {i + 1} кривой вентилятора должна быть выше, чем в точке {i}.";
            if (FanCurve[i].Speed.IntValue < FanCurve[i - 1].Speed.IntValue)
                return $"Скорость в точке {i + 1} кривой вентилятора не может быть ниже, чем в точке {i}.";
        }

        return null;
    }

    private void Apply()
    {
        if (Validate() is { } problem)
        {
            ShowMessage(problem, warning: true);
            return;
        }

        List<ChangeDefinition> changes = Changes();
        if (changes.Count == 0)
            return;

        var tweak = new TweakDefinition
        {
            Id = TweakId,
            Title = "Настройка видеокарты",
            Category = "gpu",
            Risk = TweakRisk.Medium,
            Changes = changes,
        };

        ChangePlan plan = _engine.PlanApply(new[] { tweak });
        _presenter.ShowPlan(plan, offerRestorePoint: false, result =>
        {
            if (result is null)
                return;

            _ = AfterApplyAsync(result);
        });
    }

    private async Task AfterApplyAsync(ChangeResult result)
    {
        await LoadAsync();
        if (!result.Success)
        {
            ShowMessage(result.Message, warning: true);
            return;
        }

        if (VerifyAfterApply && result.Set is { } set)
            await CheckStabilityAsync(set.Id);
        else
            ShowMessage("Настройки видеокарты применены. Отменить их можно здесь же или в «Журнале и откате».", warning: false);
    }

    private void ApplyPreset()
    {
        if (SelectedPreset is not { } preset)
            return;

        var tweak = new TweakDefinition
        {
            Id = TweakId,
            Title = "Режим видеокарты: " + GpuParameters.PresetName(preset.Name),
            Category = "gpu",
            Risk = TweakRisk.Low,
            Changes = { GpuTuningTarget.Definition(AdlxGpuTuning.Preset, preset.Name) },
        };

        _presenter.ShowPlan(_engine.PlanApply(new[] { tweak }), offerRestorePoint: false, result =>
        {
            if (result is not null)
                _ = AfterPresetAsync(result);
        });
    }

    private async Task AfterPresetAsync(ChangeResult result)
    {
        await LoadAsync();
        ShowMessage(result.Success ? "Режим видеокарты включён." : result.Message, warning: !result.Success);
    }

    private void Discard()
    {
        BuildEditors();
        RaiseAll();
    }

    private void SetDefaults()
    {
        foreach (TuningSliderViewModel slider in GfxSliders.Concat(VramSliders).Concat(PowerSliders).Concat(FanSliders))
            slider.ResetToDefault();
        foreach (FanPointViewModel point in FanCurve)
        {
            point.Temperature.ResetToDefault();
            point.Speed.ResetToDefault();
        }

        if (MemoryTimings.FirstOrDefault(t => t.Value == 0) is { } standard)
            standard.IsSelected = true;
        if (_info?.ZeroRpmDefault is bool zero)
            ZeroRpm = zero;
        OnEdited();
        ShowMessage("Значения выставлены на заводские. Нажмите «Применить», чтобы записать их в видеокарту.", warning: false);
    }

    /// <summary>
    /// Проверка стабильности. Если <paramref name="setId"/> задан — это проверка только что применённых настроек:
    /// при сбое они откатываются, а на случай зависания пишется отметка, по которой откат сделается при следующем запуске.
    /// </summary>
    private async Task CheckStabilityAsync(Guid? setId)
    {
        using IDisposable? slot = _gate.TryEnter("Проверка стабильности видеокарты", out string? busy);
        if (slot is null)
        {
            ShowMessage($"Сейчас идёт другая операция: {busy}.", warning: true);
            return;
        }

        if (setId is Guid id)
            TuningMarker.Save(new TuningMarker { Kind = TuningMarker.CheckKind, SetId = id });

        _cancel = new CancellationTokenSource();
        IsBusy = true;
        var duration = TimeSpan.FromMinutes(_verifyMinutes);
        BusyText = $"Проверка стабильности: {GpuStability.Format(duration)} нагрузки 3D и вычислениями…";
        BusyProgress = 0;
        Message = "";
        try
        {
            var progress = new Progress<double>(p => BusyProgress = p * 100);
            StabilityResult result = await GpuStability.RunAsync(duration, progress, _cancel.Token);

            if (setId is Guid applied && !result.Passed)
            {
                // Отменили проверку — настройки оставляем (пользователь сам решил), но предупреждаем.
                if (result.Cancelled)
                {
                    ShowMessage("Проверка отменена — настройки оставлены как есть. Если появятся сбои, верните их в «Журнале и откате».", warning: true);
                }
                else
                {
                    ChangePlan plan = _engine.PlanRevertSet(applied);
                    ChangeResult revert = plan.CanExecute ? _engine.Execute(plan) : ChangeResult.Fail(string.Join(" ", plan.Problems));
                    _journalChanged();
                    ShowMessage($"{result.Message} " + (revert.Success
                        ? "Настройки видеокарты возвращены к прежним."
                        : "Вернуть настройки автоматически не удалось: " + revert.Message), warning: true);
                }
            }
            else
            {
                ShowMessage(result.Passed
                    ? (setId is null ? "Видеокарта стабильна: " : "Настройки применены и проверены: ") + result.Message.ToLowerInvariant()
                    : result.Message, warning: !result.Passed);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка проверки стабильности видеокарты.", ex);
            ShowMessage("Проверка не удалась: " + ex.Message, warning: true);
        }
        finally
        {
            if (setId is not null)
                TuningMarker.Delete();
            _cancel?.Dispose();
            _cancel = null;
            IsBusy = false;
            BusyText = "";
            await LoadAsync();
        }
    }

    private bool CanTune(TuningSliderViewModel? slider) => IsAvailable && !_isBusy && slider is not null && !HasChanges;

    private async Task AutoTuneAsync(TuneGoal goal)
    {
        if (_tuning is null)
            return;

        string what = goal switch
        {
            TuneGoal.Undervolt => "андервольт",
            TuneGoal.OverclockGpu => "разгон ядра",
            _ => "разгон видеопамяти",
        };

        MessageBoxResult answer = MessageBox.Show(
            $"Автоподбор ({what}) займёт 8–12 минут: PC Toolkit будет понемногу менять настройку и после каждого шага " +
            "нагружать видеокарту на 45 секунд, следя за ошибками.\n\n" +
            "Экран может мигнуть, а игра или видео — прерваться, если драйвер сбросится: это нормально, так находится предел. " +
            "Закройте игры и не пользуйтесь видеокартой во время подбора.\n\n" +
            "В конце значение вернётся к исходному, а найденное вы примените сами — с предпросмотром и возможностью отката.",
            "PC Toolkit — автоподбор", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
            return;

        using IDisposable? slot = _gate.TryEnter("Автоподбор настроек видеокарты", out string? busy);
        if (slot is null)
        {
            ShowMessage($"Сейчас идёт другая операция: {busy}.", warning: true);
            return;
        }

        _cancel = new CancellationTokenSource();
        IsBusy = true;
        Message = "";
        BusyProgress = 0;
        try
        {
            var tuner = new GpuAutoTuner(_tuning, goal, TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2));
            tuner.Progress += (_, p) =>
            {
                BusyText = $"Автоподбор ({what}). {p.Text}";
                BusyProgress = ((p.Step - 1) + p.StepProgress) / p.StepCount * 100;
            };

            TuneResult result = await tuner.RunAsync(_cancel.Token);
            await LoadAsync();
            if (result.Found is int found)
            {
                TuningSliderViewModel? slider = goal switch
                {
                    TuneGoal.Undervolt => Voltage,
                    TuneGoal.OverclockGpu => MaxFrequency,
                    _ => VramFrequency,
                };
                slider?.SetValue(found);
                OnEdited();
                ShowMessage(result.Message + " Значение подставлено — нажмите «Применить», чтобы сохранить его (с проверкой).", warning: false);
            }
            else
            {
                ShowMessage(result.Message, warning: !result.Cancelled);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка автоподбора.", ex);
            ShowMessage("Автоподбор не удался: " + ex.Message, warning: true);
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
            IsBusy = false;
            BusyText = "";
        }
    }

    private void SetVerify(int minutes, bool value)
    {
        if (!value || _verifyMinutes == minutes)
            return;

        _verifyMinutes = minutes;
        OnPropertyChanged(nameof(Verify1));
        OnPropertyChanged(nameof(Verify5));
        OnPropertyChanged(nameof(Verify15));
    }

    private void ShowMessage(string text, bool warning)
    {
        MessageIsWarning = warning;
        Message = text;
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowUnavailable));
        OnPropertyChanged(nameof(GpuName));
        OnPropertyChanged(nameof(GpuDetails));
        OnPropertyChanged(nameof(FactoryText));
        OnPropertyChanged(nameof(IsAtFactory));
        OnPropertyChanged(nameof(HasPresets));
        OnPropertyChanged(nameof(HasGfx));
        OnPropertyChanged(nameof(GfxNote));
        OnPropertyChanged(nameof(HasVram));
        OnPropertyChanged(nameof(HasPower));
        OnPropertyChanged(nameof(HasFan));
        OnPropertyChanged(nameof(HasFanCurve));
        OnPropertyChanged(nameof(HasZeroRpm));
        OnPropertyChanged(nameof(ZeroRpm));
        OnPropertyChanged(nameof(ZeroRpmHint));
        OnPropertyChanged(nameof(MaxFrequency));
        OnPropertyChanged(nameof(MinFrequency));
        OnPropertyChanged(nameof(Voltage));
        OnPropertyChanged(nameof(VramFrequency));
        OnEdited();
    }
}

/// <summary>Один настраиваемый параметр: ползунок с шагом, кнопки ±, заводское значение.</summary>
public sealed class TuningSliderViewModel : ObservableObject
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly Action _changed;
    private int _value;

    public TuningSliderViewModel(string parameter, string title, string unit, TuningRange range, int current, int? defaultValue,
        int buttonStep, string hint, Action changed)
    {
        Parameter = parameter;
        Title = title;
        Unit = unit;
        Range = range;
        Current = current;
        Default = defaultValue;
        ButtonStep = Math.Max(buttonStep, Math.Max(range.Step, 1));
        Hint = hint;
        _changed = changed;
        _value = current;
        DecreaseCommand = new RelayCommand(() => SetValue(_value - ButtonStep));
        IncreaseCommand = new RelayCommand(() => SetValue(_value + ButtonStep));
        DefaultCommand = new RelayCommand(ResetToDefault, () => Default is not null);
    }

    public string Parameter { get; }

    public string Title { get; }

    public string Unit { get; }

    public TuningRange Range { get; }

    public double Minimum => Range.Min;

    public double Maximum => Range.Max;

    public double Step => Math.Max(Range.Step, 1);

    public int ButtonStep { get; }

    /// <summary>Значение, которое сейчас записано в видеокарте.</summary>
    public int Current { get; }

    public int? Default { get; }

    public string Hint { get; }

    public bool HasHint => Hint.Length > 0;

    public ICommand DecreaseCommand { get; }

    public ICommand IncreaseCommand { get; }

    public ICommand DefaultCommand { get; }

    public int IntValue => _value;

    /// <summary>Для ползунка (он работает с double).</summary>
    public double Value
    {
        get => _value;
        set => SetValue((int)Math.Round(value));
    }

    public string ValueText => Format(_value);

    public bool IsChanged => _value != Current;

    public string RangeText => $"{Format(Range.Min)} … {Format(Range.Max)}";

    public string StatusText
    {
        get
        {
            string text = Default is int d ? $"заводское {Format(d)}" : "";
            if (IsChanged)
                text = (text.Length > 0 ? text + " · " : "") + $"сейчас {Format(Current)}";
            return text;
        }
    }

    public void SetValue(int value)
    {
        int snapped = Range.Snap(value);
        if (snapped == _value)
        {
            OnPropertyChanged(nameof(Value)); // ползунок мог встать между шагами — вернуть его на шаг
            return;
        }

        _value = snapped;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IntValue));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(StatusText));
        _changed();
    }

    public void ResetToDefault()
    {
        if (Default is int value)
            SetValue(value);
    }

    private string Format(int value) => Unit switch
    {
        // Предел мощности и тока — относительно заводского, со знаком; скорость вентилятора — просто проценты.
        "%" when Parameter is AdlxGpuTuning.PowerLimit or AdlxGpuTuning.PowerTdc => value > 0 ? $"+{value} %" : $"{value} %",
        "%" => $"{value} %",
        "°C" => $"{value} °C",
        _ => $"{value.ToString("N0", Russian)} {Unit}",
    };
}

/// <summary>Точка кривой вентилятора: два ползунка.</summary>
public sealed class FanPointViewModel(int number, TuningSliderViewModel temperature, TuningSliderViewModel speed)
{
    public int Number { get; } = number;

    public string Title => $"Точка {Number}";

    public TuningSliderViewModel Temperature { get; } = temperature;

    public TuningSliderViewModel Speed { get; } = speed;
}

/// <summary>Режим видеокарты (Тихий, Сбалансированный, Rage…).</summary>
public sealed class PresetOptionViewModel : ObservableObject
{
    private readonly OverclockViewModel _owner;
    private bool _isSelected;

    public PresetOptionViewModel(string name, bool isCurrent, OverclockViewModel owner)
    {
        Name = name;
        IsCurrent = isCurrent;
        _isSelected = isCurrent;
        _owner = owner;
    }

    public string Name { get; }

    public string Title => GpuParameters.PresetName(Name) + (IsCurrent ? " · сейчас" : "");

    public bool IsCurrent { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                _owner.OnPresetSelected();
        }
    }
}

/// <summary>Вариант таймингов видеопамяти.</summary>
public sealed class TimingOptionViewModel : ObservableObject
{
    private readonly OverclockViewModel _owner;
    private bool _isSelected;

    public TimingOptionViewModel(int value, bool isSelected, OverclockViewModel owner)
    {
        Value = value;
        _isSelected = isSelected;
        _owner = owner;
    }

    public int Value { get; }

    public string Title => char.ToUpper(GpuParameters.TimingName(Value)[0]) + GpuParameters.TimingName(Value)[1..];

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                _owner.OnEdited();
        }
    }
}
