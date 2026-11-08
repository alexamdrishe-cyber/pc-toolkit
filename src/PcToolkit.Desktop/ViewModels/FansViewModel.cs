using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PcToolkit.Core.Fans;
using PcToolkit.Core.Logging;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Карточка режима: «Не управлять», «Тихо», «Холодно», «Вручную».</summary>
public sealed class FanModeOptionViewModel : ObservableObject
{
    private readonly Action<FanModeOptionViewModel> _selected;
    private bool _isSelected;

    public FanModeOptionViewModel(FanMode? mode, string title, string description, Action<FanModeOptionViewModel> selected)
    {
        Mode = mode;
        Title = title;
        Description = description;
        _selected = selected;
    }

    /// <summary>null — «Не управлять».</summary>
    public FanMode? Mode { get; }

    public string Title { get; }

    public string Description { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value)
                _selected(this);
        }
    }

    internal void SetSilently(bool value)
    {
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }
}

/// <summary>Вентилятор материнской платы, контроллера или видеокарты NVIDIA.</summary>
public sealed class FanCardViewModel : ObservableObject
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly FansViewModel _owner;
    private string _rpmText = "—";
    private string _dutyText = "";
    private string _status = "";
    private string _actionText = "";
    private bool _isBusy;

    public FanCardViewModel(FanChannel channel, FansViewModel owner)
    {
        Id = channel.Id;
        Kind = channel.Kind;
        Name = FanChannels.DisplayName(channel);
        Device = channel.Device;
        _owner = owner;
        IdentifyCommand = new RelayCommand(() => owner.Identify(this), () => !IsBusy && owner.CanTouchFans);
        CalibrateCommand = new RelayCommand(() => _ = owner.CalibrateAsync(this), () => !IsBusy && owner.CanTouchFans && CanCalibrate);
    }

    public string Id { get; }

    public FanChannelKind Kind { get; }

    public string Name { get; }

    public string Device { get; }

    /// <summary>Роль можно выбирать только у вентиляторов платы и контроллеров (у видеокарты она одна).</summary>
    public bool CanChangeRole => Kind != FanChannelKind.GpuNvidia;

    public bool CanCalibrate => Kind != FanChannelKind.GpuNvidia && Role != FanRole.Pump;

    public ICommand IdentifyCommand { get; }

    public ICommand CalibrateCommand { get; }

    public FanRole Role => _owner.RoleOf(this);

    public bool IsCpu
    {
        get => Role == FanRole.Cpu;
        set
        {
            if (value)
                _owner.SetRole(this, FanRole.Cpu);
        }
    }

    public bool IsCase
    {
        get => Role == FanRole.Case;
        set
        {
            if (value)
                _owner.SetRole(this, FanRole.Case);
        }
    }

    public bool IsPump
    {
        get => Role == FanRole.Pump;
        set
        {
            if (value)
                _owner.SetRole(this, FanRole.Pump);
        }
    }

    public bool IsIgnored
    {
        get => Role == FanRole.Ignore;
        set
        {
            if (value)
                _owner.SetRole(this, FanRole.Ignore);
        }
    }

    public string RpmText
    {
        get => _rpmText;
        private set => SetProperty(ref _rpmText, value);
    }

    public string DutyText
    {
        get => _dutyText;
        private set => SetProperty(ref _dutyText, value);
    }

    /// <summary>Минимум оборотов, калибровка, «не управляется».</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Что сейчас происходит: «крутится на 100 %», «калибровка: 40 %…».</summary>
    public string ActionText
    {
        get => _actionText;
        set
        {
            if (SetProperty(ref _actionText, value))
                OnPropertyChanged(nameof(HasAction));
        }
    }

    public bool HasAction => _actionText.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                RelayCommand.Refresh();
        }
    }

    internal void Update(FanChannel? channel, double? duty, FanChannelConfig? config)
    {
        RpmText = channel?.Rpm is double rpm ? rpm.ToString("N0", Russian) + " об/мин" : "обороты не видны";
        DutyText = duty is double d ? $"{d:0} %" : Kind == FanChannelKind.GpuNvidia ? "драйвер" : "BIOS";
        Status = config switch
        {
            { Unresponsive: true } => "Обороты не меняются от управления: вентилятор, похоже, на трёх проводах или подключён через хаб",
            { Calibrated: true } c => $"Не ниже {c.MinDuty:0} % (по калибровке)",
            { } c => $"Не ниже {c.MinDuty:0} % — точнее покажет калибровка",
            _ => "",
        };
        OnPropertyChanged(nameof(Role));
        OnPropertyChanged(nameof(IsCpu));
        OnPropertyChanged(nameof(IsCase));
        OnPropertyChanged(nameof(IsPump));
        OnPropertyChanged(nameof(IsIgnored));
        OnPropertyChanged(nameof(CanCalibrate));
    }
}

/// <summary>Кривая одной группы: видеокарта, процессор или корпус.</summary>
public sealed class FanCurveCardViewModel : ObservableObject
{
    private readonly Action<FanCurve> _edited;
    private FanCurve _curve;
    private bool _isEditable;
    private double _currentTemperature = double.NaN;
    private string _subtitle;
    private bool _isVisible = true;

    public FanCurveCardViewModel(string title, string subtitle, Color accent, FanCurve curve, Action<FanCurve> edited)
    {
        Title = title;
        _subtitle = subtitle;
        Accent = accent;
        _curve = curve;
        _edited = edited;
    }

    public string Title { get; }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public Color Accent { get; }

    public SolidColorBrush AccentBrush => new(Accent);

    public FanCurve Curve
    {
        get => _curve;
        set
        {
            if (_curve.SameAs(value) || !SetProperty(ref _curve, value))
                return;
            if (_isEditable)
                _edited(value);
        }
    }

    public bool IsEditable
    {
        get => _isEditable;
        set => SetProperty(ref _isEditable, value);
    }

    public double CurrentTemperature
    {
        get => _currentTemperature;
        set => SetProperty(ref _currentTemperature, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    internal void Show(FanCurve curve, bool editable)
    {
        IsEditable = editable;
        if (!_curve.SameAs(curve))
        {
            _curve = curve;
            OnPropertyChanged(nameof(Curve));
        }
    }
}

/// <summary>Шаблон ручного режима в списке.</summary>
public sealed class FanTemplateItemViewModel : ObservableObject
{
    private readonly Action<FanTemplateItemViewModel> _selected;
    private bool _isSelected;

    public FanTemplateItemViewModel(string name, bool isSelected, Action<FanTemplateItemViewModel> selected)
    {
        Name = name;
        _isSelected = isSelected;
        _selected = selected;
    }

    public string Name { get; }

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

/// <summary>
/// Экран «Вентиляторы»: режим (не управлять, тихо, холодно, вручную), все найденные вентиляторы с ролями,
/// кривые с текущей температурой и шаблоны ручного режима. Управление действует, пока программа запущена.
/// </summary>
public sealed class FansViewModel : ObservableObject
{
    private static readonly Color GpuColor = Color.FromRgb(0xF5, 0x9E, 0x4A);
    private static readonly Color CpuColor = Color.FromRgb(0x4C, 0x8D, 0xFF);
    private static readonly Color CaseColor = Color.FromRgb(0x34, 0xD3, 0x99);

    private readonly FanService _fans;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _saveTimer;
    private bool _calibrating;
    private bool _autostart;
    private string _templateName = "";
    private string _temperaturesText = "";
    private string _gpuFanText = "";
    private bool _refreshQueued;

    public FansViewModel(FanService fans, Dispatcher dispatcher)
    {
        _fans = fans;
        _dispatcher = dispatcher;
        _autostart = Autostart.IsEnabled;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), dispatcher);
        _saveTimer.Stop();

        Modes = new[]
        {
            new FanModeOptionViewModel(null, "Не управлять", "Как настроено в BIOS и драйвере видеокарты", OnModeSelected),
            new FanModeOptionViewModel(FanMode.Quiet, "Тихо", "В простое почти не слышно, в игре обороты растут плавно", OnModeSelected),
            new FanModeOptionViewModel(FanMode.Cool, "Холодно", "Обороты выше и раньше — железо холоднее", OnModeSelected),
            new FanModeOptionViewModel(FanMode.Manual, "Вручную", "Свои кривые и шаблоны", OnModeSelected),
        };

        FanTemplate active = Settings.Active;
        GpuCurve = new FanCurveCardViewModel("Видеокарта", "по горячей точке видеокарты — по ней драйвер крутит вентиляторы", GpuColor, active.Gpu,
                                             c => EditTemplate(t => t.Gpu = c));
        CpuCurve = new FanCurveCardViewModel("Процессор", "по температуре процессора", CpuColor, active.Cpu, c => EditTemplate(t => t.Cpu = c));
        CaseCurve = new FanCurveCardViewModel("Корпус", "по температуре видеокарты", CaseColor, active.Case, c => EditTemplate(t => t.Case = c));

        EnableAutostartCommand = new RelayCommand(() => IsAutostartEnabled = true);
        NewTemplateCommand = new RelayCommand(NewTemplate);
        DeleteTemplateCommand = new RelayCommand(DeleteTemplate, () => Settings.Templates.Count > 1);
        EditPresetCommand = new RelayCommand(EditPreset, () => Settings.Enabled && Settings.Mode != FanMode.Manual);

        SyncModes();
        RebuildTemplates();
        _fans.Updated += () =>
        {
            if (_refreshQueued)
                return;
            _refreshQueued = true;
            _dispatcher.InvokeAsync(() =>
            {
                _refreshQueued = false;
                Refresh();
            }, DispatcherPriority.Background);
        };
    }

    private FanSettings Settings => _fans.Settings;

    public IReadOnlyList<FanModeOptionViewModel> Modes { get; }

    public ObservableCollection<FanCardViewModel> Fans { get; } = new();

    public ObservableCollection<FanTemplateItemViewModel> Templates { get; } = new();

    public FanCurveCardViewModel GpuCurve { get; }

    public FanCurveCardViewModel CpuCurve { get; }

    public FanCurveCardViewModel CaseCurve { get; }

    public ICommand EnableAutostartCommand { get; }

    public ICommand NewTemplateCommand { get; }

    public ICommand DeleteTemplateCommand { get; }

    public ICommand EditPresetCommand { get; }

    public bool IsEnabled => Settings.Enabled;

    public bool IsManual => Settings.Enabled && Settings.Mode == FanMode.Manual;

    public bool IsPreset => Settings.Enabled && Settings.Mode != FanMode.Manual;

    /// <summary>Вентиляторами можно крутить вручную (определить, откалибровать): есть управление платой.</summary>
    public bool CanTouchFans => _fans.BoardProblem is null && !_calibrating;

    public bool HasFans => Fans.Count > 0;

    public string? BoardProblem => _fans.BoardProblem;

    public bool HasBoardProblem => _fans.BoardProblem is not null;

    /// <summary>Другая программа управления вентиляторами запущена, а наше управление включено.</summary>
    public bool HasConflict => Settings.Enabled && _fans.Conflicts.Count > 0;

    public string ConflictText => _fans.Conflicts.Count switch
    {
        0 => "",
        1 => $"Запущена {_fans.Conflicts[0]} — она тоже умеет управлять вентиляторами. Если в ней включено управление, " +
             "выключите его или закройте программу: вдвоём они перебивают друг друга, и обороты скачут.",
        _ => $"Запущены {string.Join(", ", _fans.Conflicts)} — они тоже умеют управлять вентиляторами. Если в них включено " +
             "управление, выключите его или закройте их: несколько программ перебивают друг друга, и обороты скачут.",
    };

    public bool HasAmdGpu => _fans.AmdGpu is not null;

    public string AmdGpuName => _fans.AmdGpu is null ? "" : "Видеокарта AMD";

    public string GpuFanText
    {
        get => _gpuFanText;
        private set => SetProperty(ref _gpuFanText, value);
    }

    public string AmdStatus
    {
        get
        {
            if (_fans.AmdProblem is { } problem)
                return "Не получилось: " + problem;
            if (!Settings.Enabled)
                return "Вентиляторами управляет драйвер видеокарты — как настроено в AMD Software.";

            FanTemplate template = Settings.Active;
            if (template.GpuByDriver)
            {
                return Settings.Mode == FanMode.Quiet
                    ? "«Тихо»: вентиляторами управляет автоматика драйвера — она тише любой кривой."
                    : $"Шаблон «{template.Name}»: вентиляторами управляет драйвер — как настроено в AMD Software.";
            }

            return $"Драйвер крутит вентиляторы по кривой «{template.Name}» — по горячей точке видеокарты" +
                   (_fans.GpuHotSpot is double hot ? $", сейчас {hot:0} °C." : ".");
        }
    }

    /// <summary>Видеокарта на автоматике драйвера (в «Тихо» и в шаблонах с «Как в драйвере»).</summary>
    public bool GpuByDriver => Settings.Active.GpuByDriver;

    /// <summary>Своя кривая видеокарты — показать график.</summary>
    public bool GpuOwnCurve => !Settings.Active.GpuByDriver;

    public bool GpuUseDriver
    {
        get => Settings.Active.GpuByDriver;
        set
        {
            if (value)
                EditTemplate(t => t.GpuByDriver = true);
        }
    }

    public bool GpuUseCurve
    {
        get => !Settings.Active.GpuByDriver;
        set
        {
            if (value)
                EditTemplate(t => t.GpuByDriver = false);
        }
    }

    /// <summary>Остановка в простое — настройка драйвера AMD, только для своей кривой.</summary>
    public bool ShowZeroRpm => IsManual && _fans.AmdGpu is not null && !Settings.Active.GpuByDriver;

    public string GpuDriverNote => _fans.AmdGpu is not null
        ? "Вентиляторами видеокарты управляет её драйвер: в простое он держит обороты ниже, чем получается по ручной кривой, " +
          "и может совсем останавливать вентиляторы, а под нагрузкой раскручивает их сам."
        : "Вентиляторами видеокарты управляет её драйвер: в простое многие видеокарты совсем останавливают вентиляторы, " +
          "а под нагрузкой драйвер раскручивает их сам.";

    public string TemperaturesText
    {
        get => _temperaturesText;
        private set => SetProperty(ref _temperaturesText, value);
    }

    public bool IsOverheat => _fans.Overheat;

    public bool IsAutostartEnabled
    {
        get => _autostart;
        set
        {
            if (_autostart == value)
                return;
            if (Autostart.Set(value))
                _autostart = value;
            OnPropertyChanged();
        }
    }

    public string TemplateName
    {
        get => _templateName;
        set
        {
            string name = (value ?? "").Trim();
            if (name.Length == 0 || name == _templateName || Settings.Templates.Any(t => t.Name == name))
            {
                OnPropertyChanged();
                return;
            }

            lock (_fans.SettingsLock)
            {
                FanTemplate? template = Settings.Templates.FirstOrDefault(t => t.Name == Settings.SelectedTemplate);
                if (template is null)
                    return;

                template.Name = name;
                Settings.SelectedTemplate = name;
            }

            _templateName = name;
            _fans.SettingsChanged();
            RebuildTemplates();
            OnPropertyChanged();
        }
    }

    public bool CaseFromGpu
    {
        get => Settings.Active.CaseSource == CaseSource.Gpu;
        set
        {
            if (value)
                EditTemplate(t => t.CaseSource = CaseSource.Gpu);
        }
    }

    public bool CaseFromCpu
    {
        get => Settings.Active.CaseSource == CaseSource.Cpu;
        set
        {
            if (value)
                EditTemplate(t => t.CaseSource = CaseSource.Cpu);
        }
    }

    public bool CaseFromHottest
    {
        get => Settings.Active.CaseSource == CaseSource.Hottest;
        set
        {
            if (value)
                EditTemplate(t => t.CaseSource = CaseSource.Hottest);
        }
    }

    public bool GpuZeroRpm
    {
        get => Settings.Active.GpuZeroRpm;
        set => EditTemplate(t => t.GpuZeroRpm = value);
    }

    public void OnShown()
    {
        _autostart = Autostart.IsEnabled; // могли поменять в «Настройках» или «Играх»
        OnPropertyChanged(nameof(IsAutostartEnabled));
        Refresh();
    }

    internal FanRole RoleOf(FanCardViewModel card)
    {
        if (card.Kind == FanChannelKind.GpuNvidia)
            return FanRole.Gpu;
        FanChannel? channel = _fans.Channels.FirstOrDefault(c => c.Id == card.Id);
        return Settings.Channels.FirstOrDefault(c => c.Id == card.Id)?.Role
               ?? (channel is null ? FanRole.Ignore : FanController.DefaultRole(channel));
    }

    internal void SetRole(FanCardViewModel card, FanRole role)
    {
        lock (_fans.SettingsLock)
        {
            FanChannelConfig config = ConfigFor(card.Id);
            if (config.Role == role)
                return;
            config.Role = role;
        }

        AppLog.Info($"Вентилятор «{card.Name}»: роль — {RoleName(role)}.");
        _fans.SettingsChanged();
        Refresh();
    }

    internal void Identify(FanCardViewModel card)
    {
        _fans.Override(card.Id, 100, TimeSpan.FromSeconds(6));
        card.ActionText = "Крутится на 100 % — прислушайтесь или загляните в корпус";
        _ = ClearActionLater(card, TimeSpan.FromSeconds(6));
    }

    /// <summary>
    /// Калибровка: обороты снижаются ступенями, пока вентилятор не остановится; минимум — на ступень выше.
    /// Если обороты почти не меняются, разъём не управляет вентилятором (трёхпроводный или через хаб).
    /// </summary>
    internal async Task CalibrateAsync(FanCardViewModel card)
    {
        if (_calibrating)
            return;

        _calibrating = true;
        card.IsBusy = true;
        RelayCommand.Refresh();
        OnPropertyChanged(nameof(CanTouchFans));
        double? full = null;
        double lowest = 100;
        double? lowestRpm = null;
        bool stopped = false;
        try
        {
            foreach (double duty in new double[] { 100, 80, 60, 50, 40, 30, 25, 20, 15, 10 })
            {
                card.ActionText = $"Калибровка: {duty:0} %…";
                _fans.Override(card.Id, duty, TimeSpan.FromSeconds(30));
                await Task.Delay(duty == 100 ? 5000 : 3500);
                double rpm = _fans.Channels.FirstOrDefault(c => c.Id == card.Id)?.Rpm ?? 0;
                if (duty == 100)
                {
                    full = rpm;
                    if (rpm < 150)
                        break;
                }

                if (rpm < 150)
                {
                    stopped = true;
                    break;
                }

                lowest = duty;
                lowestRpm = rpm;
            }
        }
        finally
        {
            _fans.ClearOverride(card.Id);
            _calibrating = false;
            card.IsBusy = false;
            OnPropertyChanged(nameof(CanTouchFans));
            RelayCommand.Refresh();
        }

        if (full is not > 150)
        {
            card.ActionText = "Обороты не видны: к разъёму ничего не подключено или вентилятор не сообщает обороты. Настройки не изменились.";
            _ = ClearActionLater(card, TimeSpan.FromSeconds(12));
            return;
        }

        bool unresponsive = !stopped && lowestRpm is double low && low > full.Value * 0.8;
        FanChannelConfig config;
        lock (_fans.SettingsLock)
        {
            config = ConfigFor(card.Id);
            config.Unresponsive = unresponsive;
            config.MinDuty = unresponsive ? FanSettings.DefaultMinDuty : Math.Max(FanSettings.AbsoluteMinDuty, stopped ? lowest + 5 : lowest);
            config.Calibrated = true;
        }

        _fans.SettingsChanged();
        card.ActionText = unresponsive
            ? $"Обороты почти не меняются ({full:N0} → {lowestRpm:N0} об/мин): разъём не управляет этим вентилятором, программа его не трогает."
            : stopped
                ? $"Готово: на {lowest:0} % ещё крутится ({lowestRpm:N0} об/мин), ниже останавливается — минимум {config.MinDuty:0} %."
                : $"Готово: крутится даже на {lowest:0} % ({lowestRpm:N0} об/мин), на 100 % — {full:N0} об/мин.";
        AppLog.Info($"Калибровка «{card.Name}»: {card.ActionText}");
        _ = ClearActionLater(card, TimeSpan.FromSeconds(15));
        Refresh();
    }

    private async Task ClearActionLater(FanCardViewModel card, TimeSpan delay)
    {
        string text = card.ActionText;
        await Task.Delay(delay);
        if (card.ActionText == text)
            card.ActionText = "";
    }

    private FanChannelConfig ConfigFor(string id)
    {
        FanChannel? channel = _fans.Channels.FirstOrDefault(c => c.Id == id);
        return Settings.Channel(id, () => new FanChannelConfig { Role = channel is null ? FanRole.Ignore : FanController.DefaultRole(channel) });
    }

    private void OnModeSelected(FanModeOptionViewModel option)
    {
        bool enabled = option.Mode is not null;
        FanMode mode = option.Mode ?? Settings.Mode;
        if (Settings.Enabled == enabled && Settings.Mode == mode)
            return;

        lock (_fans.SettingsLock)
        {
            Settings.Enabled = enabled;
            Settings.Mode = mode;
            // Роли фиксируются при первом включении: дальше их не меняет догадка по оборотам.
            if (enabled)
            {
                foreach (FanChannel channel in _fans.Channels)
                    ConfigFor(channel.Id);
            }
        }

        AppLog.Info(enabled ? $"Вентиляторы: режим «{Settings.Active.Name}»." : "Вентиляторы: управление выключено — BIOS и драйвер видеокарты.");
        _fans.SettingsChanged();
        SyncModes();
        Refresh();
    }

    private void SyncModes()
    {
        foreach (FanModeOptionViewModel option in Modes)
            option.SetSilently(Settings.Enabled ? option.Mode == Settings.Mode : option.Mode is null);
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(IsPreset));
        RelayCommand.Refresh();
    }

    private void EditTemplate(Action<FanTemplate> edit)
    {
        if (!IsManual)
            return;

        lock (_fans.SettingsLock)
        {
            FanTemplate? template = Settings.Templates.FirstOrDefault(t => t.Name == Settings.SelectedTemplate);
            if (template is null)
                return;
            edit(template);
        }

        _saveTimer.Stop();
        _saveTimer.Start();
        RefreshCurves();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        _fans.SettingsChanged();
    }

    private void NewTemplate()
    {
        lock (_fans.SettingsLock)
        {
            FanTemplate source = Settings.Active;
            string name = UniqueName("Шаблон");
            Settings.Templates.Add(source.Copy(name));
            Settings.SelectedTemplate = name;
            Settings.Enabled = true;
            Settings.Mode = FanMode.Manual;
        }

        _fans.SettingsChanged();
        SyncModes();
        RebuildTemplates();
        Refresh();
    }

    /// <summary>Свой шаблон на основе «Тихо» или «Холодно» — и сразу в ручной режим.</summary>
    private void EditPreset()
    {
        lock (_fans.SettingsLock)
        {
            FanTemplate source = Settings.Active;
            string name = UniqueName(source.Name + " — мой");
            Settings.Templates.Add(source.Copy(name));
            Settings.SelectedTemplate = name;
            Settings.Mode = FanMode.Manual;
        }

        _fans.SettingsChanged();
        SyncModes();
        RebuildTemplates();
        Refresh();
    }

    private void DeleteTemplate()
    {
        if (Settings.Templates.Count <= 1)
            return;

        lock (_fans.SettingsLock)
        {
            Settings.Templates.RemoveAll(t => t.Name == Settings.SelectedTemplate);
            Settings.SelectedTemplate = Settings.Templates[0].Name;
        }

        _fans.SettingsChanged();
        RebuildTemplates();
        Refresh();
    }

    private string UniqueName(string baseName)
    {
        if (Settings.Templates.All(t => t.Name != baseName))
            return baseName;
        for (int i = 2; ; i++)
        {
            string name = $"{baseName} {i}";
            if (Settings.Templates.All(t => t.Name != name))
                return name;
        }
    }

    private void RebuildTemplates()
    {
        Templates.Clear();
        foreach (FanTemplate template in Settings.Templates)
            Templates.Add(new FanTemplateItemViewModel(template.Name, template.Name == Settings.SelectedTemplate, OnTemplateSelected));
        _templateName = Settings.SelectedTemplate;
        OnPropertyChanged(nameof(TemplateName));
        RelayCommand.Refresh();
    }

    private void OnTemplateSelected(FanTemplateItemViewModel item)
    {
        if (Settings.SelectedTemplate == item.Name)
            return;

        lock (_fans.SettingsLock)
            Settings.SelectedTemplate = item.Name;
        _fans.SettingsChanged();
        foreach (FanTemplateItemViewModel other in Templates.Where(t => t != item))
            other.IsSelected = false;
        _templateName = item.Name;
        OnPropertyChanged(nameof(TemplateName));
        Refresh();
    }

    private void Refresh()
    {
        // Карточки вентиляторов: добавить новые, убрать пропавшие, обновить обороты.
        IReadOnlyList<FanChannel> channels = _fans.Channels;
        foreach (FanCardViewModel gone in Fans.Where(f => channels.All(c => c.Id != f.Id)).ToList())
            Fans.Remove(gone);
        foreach (FanChannel channel in channels)
        {
            FanCardViewModel? card = Fans.FirstOrDefault(f => f.Id == channel.Id);
            if (card is null)
            {
                card = new FanCardViewModel(channel, this);
                Fans.Add(card);
            }

            card.Update(channel, _fans.Duties.GetValueOrDefault(channel.Id), Settings.Channels.FirstOrDefault(c => c.Id == channel.Id));
        }

        double? cpu = _fans.CpuTemperature;
        double? gpu = _fans.GpuTemperature;
        TemperaturesText = $"Процессор {Temperature(cpu)} · видеокарта {Temperature(gpu)}" +
                           (_fans.GpuHotSpot is double hot ? $", горячая точка {hot:0} °C" : "");
        GpuFanText = _fans.AmdGpu is null ? "" : AmdFanRpm();
        RefreshCurves();
        OnPropertyChanged(nameof(HasFans));
        OnPropertyChanged(nameof(BoardProblem));
        OnPropertyChanged(nameof(HasBoardProblem));
        OnPropertyChanged(nameof(HasConflict));
        OnPropertyChanged(nameof(ConflictText));
        OnPropertyChanged(nameof(HasAmdGpu));
        OnPropertyChanged(nameof(AmdGpuName));
        OnPropertyChanged(nameof(AmdStatus));
        OnPropertyChanged(nameof(IsOverheat));
        OnPropertyChanged(nameof(CanTouchFans));
        OnPropertyChanged(nameof(CaseFromGpu));
        OnPropertyChanged(nameof(CaseFromCpu));
        OnPropertyChanged(nameof(CaseFromHottest));
        OnPropertyChanged(nameof(GpuZeroRpm));
        OnPropertyChanged(nameof(ShowZeroRpm));
    }

    private string AmdFanRpm()
    {
        double? rpm = _fans.GpuFanRpm;
        return rpm is double value ? value.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")) + " об/мин" : "";
    }

    private void RefreshCurves()
    {
        FanTemplate template = Settings.Active;
        bool editable = IsManual;
        GpuCurve.Show(template.Gpu, editable);
        CpuCurve.Show(template.Cpu, editable);
        CaseCurve.Show(template.Case, editable);

        GpuCurve.CurrentTemperature = _fans.GpuHotSpot ?? _fans.GpuTemperature ?? double.NaN;
        GpuCurve.Subtitle = _fans.GpuHotSpot is null && _fans.AmdGpu is null
            ? "по температуре видеокарты"
            : "по горячей точке видеокарты — по ней драйвер крутит вентиляторы";
        CpuCurve.CurrentTemperature = _fans.CpuTemperature ?? double.NaN;
        (string subtitle, double? caseTemperature) = template.CaseSource switch
        {
            CaseSource.Cpu => ("по температуре процессора", _fans.CpuTemperature),
            CaseSource.Hottest => ("по большей из температур процессора и видеокарты", Max(_fans.CpuTemperature, _fans.GpuTemperature)),
            _ => ("по температуре видеокарты — она сильнее и чаще греется в играх", _fans.GpuTemperature ?? _fans.CpuTemperature),
        };
        CaseCurve.Subtitle = subtitle;
        CaseCurve.CurrentTemperature = caseTemperature ?? double.NaN;

        bool any = Settings.Enabled;
        GpuCurve.IsVisible = any && (_fans.AmdGpu is not null || _fans.Channels.Any(c => c.Kind == FanChannelKind.GpuNvidia));
        CpuCurve.IsVisible = any && Fans.Any(f => f.Role == FanRole.Cpu);
        CaseCurve.IsVisible = any && Fans.Any(f => f.Role == FanRole.Case);
        OnPropertyChanged(nameof(HasCurves));
        OnPropertyChanged(nameof(GpuByDriver));
        OnPropertyChanged(nameof(GpuOwnCurve));
        OnPropertyChanged(nameof(GpuUseDriver));
        OnPropertyChanged(nameof(GpuUseCurve));
        OnPropertyChanged(nameof(ShowZeroRpm));
        OnPropertyChanged(nameof(GpuDriverNote));
        OnPropertyChanged(nameof(AmdStatus));
    }

    public bool HasCurves => GpuCurve.IsVisible || CpuCurve.IsVisible || CaseCurve.IsVisible;

    private static string Temperature(double? value) => value is double v ? $"{v:0} °C" : "—";

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static string RoleName(FanRole role) => role switch
    {
        FanRole.Cpu => "процессор",
        FanRole.Case => "корпус",
        FanRole.Pump => "помпа (не трогать)",
        FanRole.Ignore => "не управлять",
        _ => "видеокарта",
    };
}
