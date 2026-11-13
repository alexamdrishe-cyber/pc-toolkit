using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PcToolkit.Core.Overlay;
using PcToolkit.Core.Sensors;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Что настраивается: общий оверлей или оверлей отдельной игры.</summary>
public sealed class OverlayTargetViewModel : ObservableObject
{
    private bool _hasOwn;

    public OverlayTargetViewModel(string? gameId, string name, bool hasOwn)
    {
        GameId = gameId;
        Name = name;
        _hasOwn = hasOwn;
    }

    /// <summary>Id игры; null — все игры.</summary>
    public string? GameId { get; }

    public string Name { get; }

    public bool IsAll => GameId is null;

    public bool HasOwn
    {
        get => _hasOwn;
        set
        {
            if (SetProperty(ref _hasOwn, value))
                OnPropertyChanged(nameof(Note));
        }
    }

    /// <summary>Пометка в списке: «свой оверлей».</summary>
    public string Note => IsAll ? "общий" : HasOwn ? "свой оверлей" : "";
}

/// <summary>Строка списка «Добавить показатель»: заголовок раздела или показатель.</summary>
public sealed class OverlayMetricChoice
{
    public OverlayMetricChoice(string title, OverlayMetric? metric = null, string note = "")
    {
        Title = title;
        Metric = metric;
        Note = note;
    }

    public string Title { get; }

    /// <summary>null — это заголовок раздела.</summary>
    public OverlayMetric? Metric { get; }

    /// <summary>Значение сейчас или «уже есть».</summary>
    public string Note { get; }

    public bool IsHeader => Metric is null;
}

/// <summary>
/// Настройки оверлея с предпросмотром. Каждое изменение сразу видно и в предпросмотре, и в игре;
/// на диск настройки пишутся через полсекунды после последнего изменения.
/// </summary>
public sealed class OverlayEditorViewModel : ObservableObject
{
    private static readonly string[] RecommendedFonts =
    {
        "Segoe UI", "Segoe UI Variable Display", "Bahnschrift", "Consolas", "Cascadia Mono", "Cascadia Code", "Arial",
        "Verdana", "Tahoma", "Trebuchet MS", "Calibri", "Franklin Gothic Medium", "Impact", "Lucida Console", "Roboto",
        "Montserrat", "Rubik", "JetBrains Mono",
    };

    private readonly OverlaySettings _settings;
    private readonly GamesViewModel _host;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _tickTimer;
    private OverlayTargetViewModel _selectedTarget;
    private OverlayLayout _layout;
    private IReadOnlyList<string>? _fonts;
    private bool _isMoving;
    private bool _isAddOpen;
    private string _addSearch = "";
    private bool _active;

    public OverlayEditorViewModel(OverlaySettings settings, GamesViewModel host)
    {
        _settings = settings;
        _host = host;
        _layout = settings.Default;
        Preview = new OverlayContent(_layout, sample: true);
        _selectedTarget = new OverlayTargetViewModel(null, "Все игры", hasOwn: false);
        Targets.Add(_selectedTarget);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => Flush();
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(settings.RefreshMs) };
        _tickTimer.Tick += (_, _) =>
        {
            Preview.Tick();
            if (_isMoving)
                _host.Overlay.Content.Tick();
        };

        PlaceCommand = new RelayCommand(anchor => Place(anchor as string ?? "TopLeft"));
        ToggleMovingCommand = new RelayCommand(() => IsMoving = !IsMoving);
        OpenAddCommand = new RelayCommand(() => IsAddOpen = true);
        AddMetricCommand = new RelayCommand(choice => Add(choice as OverlayMetricChoice));
        MakeOwnCommand = new RelayCommand(() => UseOwnLayout = true);

        LoadItems();
    }

    /// <summary>Предпросмотр: настоящие датчики и FPS игры, а пока игры нет — пример.</summary>
    public OverlayContent Preview { get; }

    public ObservableCollection<OverlayTargetViewModel> Targets { get; } = new();

    public ObservableCollection<OverlayItemViewModel> Items { get; } = new();

    public ObservableCollection<OverlayMetricChoice> AddChoices { get; } = new();

    public ICommand PlaceCommand { get; }

    public ICommand ToggleMovingCommand { get; }

    public ICommand OpenAddCommand { get; }

    public ICommand AddMetricCommand { get; }

    public ICommand MakeOwnCommand { get; }

    internal OverlayLayout Layout => _layout;

    internal OverlayStyle Style => _layout.Style;

    // ----- Для чего настраиваем -----

    public OverlayTargetViewModel SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (value is null || !SetProperty(ref _selectedTarget, value))
                return;
            ShowLayout(_settings.For(value.GameId));
            OnPropertyChanged(nameof(IsGameTarget));
            OnPropertyChanged(nameof(UseOwnLayout));
            OnPropertyChanged(nameof(UseCommonLayout));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(TargetHint));
        }
    }

    public bool IsGameTarget => !_selectedTarget.IsAll;

    /// <summary>У выбранной игры свой оверлей. Свой создаётся копией общего; выключенный хранится.</summary>
    public bool UseOwnLayout
    {
        get => _selectedTarget.GameId is { } id && _settings.HasOwn(id);
        set
        {
            if (_selectedTarget.GameId is not { } id || value == UseOwnLayout)
                return;

            ShowLayout(_settings.SetOwn(id, _selectedTarget.Name, value));
            _selectedTarget.HasOwn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UseCommonLayout));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(TargetHint));
            _host.OnOverlayLayoutsChanged();
            ScheduleSave();
        }
    }

    public bool UseCommonLayout
    {
        get => !UseOwnLayout;
        set => UseOwnLayout = !value;
    }

    /// <summary>Настройки ниже можно менять: общий оверлей или свой оверлей игры.</summary>
    public bool CanEdit => !IsGameTarget || UseOwnLayout;

    public string TargetHint => !IsGameTarget
        ? "Общий оверлей — для всех игр, у которых нет своего."
        : UseOwnLayout
            ? $"Свой оверлей «{_selectedTarget.Name}»: показатели, оформление и место — только для этой игры."
            : $"«{_selectedTarget.Name}» показывает общий оверлей. Чтобы настроить его отдельно, выберите «Свой для этой игры».";

    /// <summary>Обновить список игр (после загрузки списка игр или при открытии вкладки).</summary>
    public void RefreshTargets(IEnumerable<(string Id, string Name)> games)
    {
        var all = new Dictionary<string, string>();
        foreach ((string id, string name) in games)
            all.TryAdd(id, name);
        foreach ((string id, GameOverlay overlay) in _settings.Games)
            all.TryAdd(id, overlay.Name.Length > 0 ? overlay.Name : id);

        List<OverlayTargetViewModel> wanted = all
            .Select(pair => new OverlayTargetViewModel(pair.Key, pair.Value, _settings.HasOwn(pair.Key)))
            .OrderByDescending(t => t.HasOwn)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        string? selected = _selectedTarget.GameId;
        bool same = Targets.Count == wanted.Count + 1
                    && Targets.Skip(1).Zip(wanted).All(p => p.First.GameId == p.Second.GameId && p.First.Name == p.Second.Name);
        if (same)
        {
            foreach ((OverlayTargetViewModel existing, OverlayTargetViewModel fresh) in Targets.Skip(1).Zip(wanted))
                existing.HasOwn = fresh.HasOwn;
            return;
        }

        while (Targets.Count > 1)
            Targets.RemoveAt(Targets.Count - 1);
        foreach (OverlayTargetViewModel target in wanted)
            Targets.Add(target);

        OverlayTargetViewModel again = Targets.FirstOrDefault(t => t.GameId == selected) ?? Targets[0];
        _selectedTarget = again;
        OnPropertyChanged(nameof(SelectedTarget));
    }

    /// <summary>Открыть настройки оверлея конкретной игры (из её профиля).</summary>
    public void Select(string gameId)
    {
        if (Targets.FirstOrDefault(t => t.GameId == gameId) is { } target)
            SelectedTarget = target;
    }

    // ----- Работает ли экран -----

    /// <summary>Вкладка открыта: пример FPS в предпросмотре «живёт». Закрыли — оверлей больше не двигают.</summary>
    public void SetActive(bool active)
    {
        if (_active == active)
            return;

        _active = active;
        if (active)
        {
            _tickTimer.Start();
        }
        else
        {
            _tickTimer.Stop();
            IsMoving = false;
            IsAddOpen = false;
            Flush();
        }
    }

    // ----- Частота обновления (для всех игр) -----

    public bool IsRefresh100 { get => _settings.RefreshMs == 100; set { if (value) SetRefresh(100); } }

    public bool IsRefresh250 { get => _settings.RefreshMs == 250; set { if (value) SetRefresh(250); } }

    public bool IsRefresh500 { get => _settings.RefreshMs == 500; set { if (value) SetRefresh(500); } }

    public bool IsRefresh1000 { get => _settings.RefreshMs == 1000; set { if (value) SetRefresh(1000); } }

    private void SetRefresh(int milliseconds)
    {
        if (_settings.RefreshMs == milliseconds)
            return;

        _host.SetOverlayRefresh(milliseconds);
        _tickTimer.Interval = TimeSpan.FromMilliseconds(_settings.RefreshMs);
        OnPropertyChanged(nameof(IsRefresh100));
        OnPropertyChanged(nameof(IsRefresh250));
        OnPropertyChanged(nameof(IsRefresh500));
        OnPropertyChanged(nameof(IsRefresh1000));
        ScheduleSave();
    }

    // ----- Показатели -----

    public bool HasItems => Items.Count > 0;

    /// <summary>Столбиком: можно ставить показатель в строку с предыдущим.</summary>
    public bool IsVertical
    {
        get => !_layout.IsHorizontal;
        set
        {
            if (!value || !_layout.IsHorizontal)
                return;
            _layout.Orientation = OverlayLayout.Vertical;
            OrientationChanged();
        }
    }

    public bool IsHorizontal
    {
        get => _layout.IsHorizontal;
        set
        {
            if (!value || _layout.IsHorizontal)
                return;
            _layout.Orientation = OverlayLayout.Horizontal;
            OrientationChanged();
        }
    }

    private void OrientationChanged()
    {
        OnPropertyChanged(nameof(IsVertical));
        OnPropertyChanged(nameof(IsHorizontal));
        foreach (OverlayItemViewModel item in Items)
            item.RefreshOrientation();
        Commit();
    }

    internal void MoveItem(OverlayItemViewModel item, int delta)
    {
        int from = Items.IndexOf(item);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= Items.Count)
            return;

        Items.Move(from, to);
        _layout.Items.Remove(item.Item);
        _layout.Items.Insert(to, item.Item);
        RefreshItemPositions();
        Commit();
    }

    internal void RemoveItem(OverlayItemViewModel item)
    {
        Items.Remove(item);
        _layout.Items.Remove(item.Item);
        RefreshItemPositions();
        Commit();
    }

    internal void ItemChanged() => Commit();

    private void LoadItems()
    {
        Items.Clear();
        HardwareSnapshot? snapshot = Preview.Snapshot ?? _host.LastSnapshot;
        foreach (OverlayItem item in _layout.Items)
            Items.Add(new OverlayItemViewModel(this, item, OverlayMetrics.Find(item.Metric, snapshot) ?? OverlayMetrics.Missing(item)));
        RefreshItemPositions();
    }

    private void RefreshItemPositions()
    {
        for (int i = 0; i < Items.Count; i++)
            Items[i].SetPosition(i == 0, i == Items.Count - 1);
        OnPropertyChanged(nameof(HasItems));
    }

    // ----- Добавить показатель -----

    public bool IsAddOpen
    {
        get => _isAddOpen;
        set
        {
            if (!SetProperty(ref _isAddOpen, value) || !value)
                return;
            _addSearch = "";
            OnPropertyChanged(nameof(AddSearch));
            RefreshChoices();
        }
    }

    public string AddSearch
    {
        get => _addSearch;
        set
        {
            if (SetProperty(ref _addSearch, value ?? ""))
                RefreshChoices();
        }
    }

    public bool HasNoChoices => AddChoices.Count == 0;

    private void RefreshChoices()
    {
        try
        {
            FillChoices();
        }
        finally
        {
            OnPropertyChanged(nameof(HasNoChoices));
        }
    }

    private void FillChoices()
    {
        AddChoices.Clear();
        string search = _addSearch.Trim();
        var present = _layout.Items.Select(i => i.Metric).ToHashSet();
        HardwareSnapshot? snapshot = Preview.Snapshot ?? _host.LastSnapshot;
        var sources = new OverlaySources { Snapshot = snapshot, Metrics = snapshot is null ? null : KeyMetrics.From(snapshot) };

        bool Matches(string text) => search.Length == 0 || text.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        string Note(OverlayMetric metric)
        {
            if (present.Contains(metric.Key))
                return "уже есть";
            if (metric.FromFrames || metric.IsText)
                return "";
            OverlayReading reading = OverlayMetrics.Read(metric, sources);
            return reading.Value is null ? "" : $"{reading.Text} {reading.Unit}".Trim();
        }

        foreach (IGrouping<OverlayGroup, OverlayMetric> group in OverlayMetrics.BuiltIn.GroupBy(m => m.Group))
        {
            List<OverlayMetric> metrics = group.Where(m => Matches(m.Title) || Matches(OverlayMetrics.GroupTitle(group.Key))).ToList();
            if (metrics.Count == 0)
                continue;
            AddChoices.Add(new OverlayMetricChoice(OverlayMetrics.GroupTitle(group.Key)));
            foreach (OverlayMetric metric in metrics)
                AddChoices.Add(new OverlayMetricChoice(metric.Title, metric, Note(metric)));
        }

        if (snapshot is null)
            return;

        foreach (IGrouping<string, OverlayMetric> device in OverlayMetrics.Sensors(snapshot).GroupBy(m => m.Title.Split(" · ")[0]))
        {
            List<OverlayMetric> metrics = device.Where(m => Matches(m.Title)).ToList();
            if (metrics.Count == 0)
                continue;
            AddChoices.Add(new OverlayMetricChoice("Датчики: " + device.Key));
            foreach (OverlayMetric metric in metrics)
                AddChoices.Add(new OverlayMetricChoice(metric.Title[(device.Key.Length + 3)..], metric, Note(metric)));
        }
    }

    private void Add(OverlayMetricChoice? choice)
    {
        if (choice?.Metric is not { } metric || !CanEdit)
            return;

        OverlayItem item = OverlayItem.For(metric);
        _layout.Items.Add(item);
        var row = new OverlayItemViewModel(this, item, metric);
        Items.Add(row);
        RefreshItemPositions();
        IsAddOpen = false;
        Commit();
    }

    // ----- Оформление -----

    public IReadOnlyList<string> Fonts => _fonts ??= LoadFonts();

    public string FontFamily
    {
        get => Style.FontFamily;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Style.FontFamily)
                return;
            Style.FontFamily = value;
            StyleChanged();
        }
    }

    /// <summary>Размер текста: 8–40.</summary>
    public double FontSize
    {
        get => Style.FontSize;
        set
        {
            double size = Math.Clamp(Math.Round(value * 2) / 2, 8, 40);
            if (size == Style.FontSize)
                return;
            Style.FontSize = size;
            StyleChanged();
        }
    }

    public bool IsNormalWeight { get => Style.FontWeight == OverlayStyle.Normal; set => SetWeight(OverlayStyle.Normal, value); }

    public bool IsSemiBoldWeight { get => Style.FontWeight == OverlayStyle.SemiBold; set => SetWeight(OverlayStyle.SemiBold, value); }

    public bool IsBoldWeight { get => Style.FontWeight == OverlayStyle.Bold; set => SetWeight(OverlayStyle.Bold, value); }

    public string ValueColor { get => Style.ValueColor; set => SetColor(value, c => Style.ValueColor = c); }

    public string LabelColor { get => Style.LabelColor; set => SetColor(value, c => Style.LabelColor = c); }

    public string WarnColor { get => Style.WarnColor; set => SetColor(value, c => Style.WarnColor = c); }

    public string CriticalColor { get => Style.CriticalColor; set => SetColor(value, c => Style.CriticalColor = c); }

    public string GraphColor { get => Style.GraphColor; set => SetColor(value, c => Style.GraphColor = c); }

    public string OutlineColor { get => Style.OutlineColor; set => SetColor(value, c => Style.OutlineColor = c); }

    public string BackgroundColor { get => Style.BackgroundColor; set => SetColor(value, c => Style.BackgroundColor = c); }

    /// <summary>Обводка букв: 0–4 пикселя.</summary>
    public double Outline
    {
        get => Style.Outline;
        set
        {
            double outline = Math.Clamp(Math.Round(value * 2) / 2, 0, 4);
            if (outline == Style.Outline)
                return;
            Style.Outline = outline;
            StyleChanged();
        }
    }

    public string OutlineText => Style.Outline <= 0 ? "нет" : $"{OverlayMetrics.Number(Style.Outline, Style.Outline % 1 == 0 ? 0 : 1)} пкс";

    /// <summary>Плотность фона, %.</summary>
    public double BackgroundPercent
    {
        get => Math.Round(Style.BackgroundOpacity * 100);
        set
        {
            double opacity = Math.Clamp(Math.Round(value) / 100, 0, 1);
            if (opacity == Style.BackgroundOpacity)
                return;
            Style.BackgroundOpacity = opacity;
            StyleChanged();
        }
    }

    public double CornerRadius
    {
        get => Style.CornerRadius;
        set
        {
            double radius = Math.Clamp(Math.Round(value), 0, 24);
            if (radius == Style.CornerRadius)
                return;
            Style.CornerRadius = radius;
            StyleChanged();
        }
    }

    private void SetWeight(string weight, bool value)
    {
        if (!value || Style.FontWeight == weight)
            return;
        Style.FontWeight = weight;
        StyleChanged();
    }

    private void SetColor(string? value, Action<string> apply)
    {
        if (OverlayStyle.ValidColor(value) is not { } color)
            return;
        apply(color);
        StyleChanged();
    }

    private void StyleChanged()
    {
        RaiseStyle();
        foreach (OverlayItemViewModel item in Items)
            item.RefreshStyle();
        Commit();
    }

    private void RaiseStyle()
    {
        foreach (string name in new[]
                 {
                     nameof(FontFamily), nameof(FontSize), nameof(IsNormalWeight), nameof(IsSemiBoldWeight), nameof(IsBoldWeight),
                     nameof(ValueColor), nameof(LabelColor), nameof(WarnColor), nameof(CriticalColor), nameof(GraphColor),
                     nameof(OutlineColor), nameof(BackgroundColor), nameof(Outline), nameof(OutlineText), nameof(BackgroundPercent),
                     nameof(CornerRadius),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private static IReadOnlyList<string> LoadFonts()
    {
        var installed = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            foreach (System.Windows.Media.FontFamily family in System.Windows.Media.Fonts.SystemFontFamilies)
            {
                string name = family.Source;
                if (name.Length > 0 && !name.StartsWith("Global ", StringComparison.Ordinal) && !name.Contains('#'))
                    installed.Add(name);
            }
        }
        catch (Exception)
        {
            // Список шрифтов недоступен — остаются рекомендованные.
        }

        var fonts = RecommendedFonts.Where(f => installed.Contains(f) || f == "Segoe UI").ToList();
        fonts.AddRange(installed.Where(f => !fonts.Contains(f, StringComparer.CurrentCultureIgnoreCase)));
        return fonts;
    }

    // ----- Место -----

    public bool IsTopLeft { get => Anchor == "TopLeft"; set { if (value) Place("TopLeft"); } }

    public bool IsTopCenter { get => Anchor == "TopCenter"; set { if (value) Place("TopCenter"); } }

    public bool IsTopRight { get => Anchor == "TopRight"; set { if (value) Place("TopRight"); } }

    public bool IsBottomLeft { get => Anchor == "BottomLeft"; set { if (value) Place("BottomLeft"); } }

    public bool IsBottomCenter { get => Anchor == "BottomCenter"; set { if (value) Place("BottomCenter"); } }

    public bool IsBottomRight { get => Anchor == "BottomRight"; set { if (value) Place("BottomRight"); } }

    /// <summary>Где оверлей в предпросмотре: так же, как на экране.</summary>
    public HorizontalAlignment PreviewHorizontal =>
        _layout.Position.IsRight ? HorizontalAlignment.Right : _layout.Position.IsCenter ? HorizontalAlignment.Center : HorizontalAlignment.Left;

    public VerticalAlignment PreviewVertical => _layout.Position.IsBottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;

    /// <summary>Оверлей сейчас двигают мышью на экране.</summary>
    public bool IsMoving
    {
        get => _isMoving;
        set
        {
            if (value && !CanEdit)
                return;
            if (!SetProperty(ref _isMoving, value))
                return;
            OnPropertyChanged(nameof(MoveButtonText));
            _host.SetOverlayMoving(value, _layout);
        }
    }

    public string MoveButtonText => _isMoving ? "Готово" : "Передвинуть мышью";

    private string Anchor => _layout.Position.Anchor;

    private void Place(string anchor)
    {
        if (!CanEdit)
            return;
        _layout.Position = OverlayPosition.Corner(anchor);
        PositionChanged();
    }

    /// <summary>Оверлей перетащили мышью и отпустили.</summary>
    public void OnPositionDropped(OverlayPosition position)
    {
        if (!_isMoving)
            return;
        _layout.Position = position;
        PositionChanged();
    }

    private void PositionChanged()
    {
        foreach (string name in new[]
                 {
                     nameof(IsTopLeft), nameof(IsTopCenter), nameof(IsTopRight), nameof(IsBottomLeft), nameof(IsBottomCenter),
                     nameof(IsBottomRight), nameof(PreviewHorizontal), nameof(PreviewVertical),
                 })
        {
            OnPropertyChanged(name);
        }

        Commit();
    }

    // ----- Применить и сохранить -----

    /// <summary>Показать другие настройки (выбрали игру или включили свой оверлей).</summary>
    private void ShowLayout(OverlayLayout layout)
    {
        _layout = layout;
        LoadItems();
        RaiseStyle();
        OnPropertyChanged(nameof(IsVertical));
        OnPropertyChanged(nameof(IsHorizontal));
        PositionChangedQuiet();
        Preview.SetLayout(layout);
        if (_isMoving)
            _host.SetOverlayMoving(true, layout);
    }

    private void PositionChangedQuiet()
    {
        OnPropertyChanged(nameof(IsTopLeft));
        OnPropertyChanged(nameof(IsTopCenter));
        OnPropertyChanged(nameof(IsTopRight));
        OnPropertyChanged(nameof(IsBottomLeft));
        OnPropertyChanged(nameof(IsBottomCenter));
        OnPropertyChanged(nameof(IsBottomRight));
        OnPropertyChanged(nameof(PreviewHorizontal));
        OnPropertyChanged(nameof(PreviewVertical));
    }

    /// <summary>Изменение видно сразу — в предпросмотре и в игре; на диск — чуть позже.</summary>
    private void Commit()
    {
        Preview.SetLayout(_layout);
        _host.OnOverlayLayoutEdited(_layout);
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Записать несохранённые изменения сейчас (закрытие вкладки или программы).</summary>
    public void Flush()
    {
        if (!_saveTimer.IsEnabled)
            return;
        _saveTimer.Stop();
        _settings.Save();
    }

    internal static Brush BrushOf(string color)
    {
        var brush = new SolidColorBrush(OverlayContent.ToColor(color));
        brush.Freeze();
        return brush;
    }

    internal static string Format(double? value) =>
        value is double v ? v.ToString(v % 1 == 0 ? "0" : "0.##", CultureInfo.GetCultureInfo("ru-RU")) : "";
}

/// <summary>Строка показателя в настройках оверлея.</summary>
public sealed class OverlayItemViewModel : ObservableObject
{
    private readonly OverlayEditorViewModel _owner;
    private bool _isFirst;
    private bool _isLast;
    private bool _isExpanded;

    public OverlayItemViewModel(OverlayEditorViewModel owner, OverlayItem item, OverlayMetric metric)
    {
        _owner = owner;
        Item = item;
        Metric = metric;
        MoveUpCommand = new RelayCommand(() => _owner.MoveItem(this, -1), () => !_isFirst);
        MoveDownCommand = new RelayCommand(() => _owner.MoveItem(this, 1), () => !_isLast);
        RemoveCommand = new RelayCommand(() => _owner.RemoveItem(this));
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public OverlayItem Item { get; }

    public OverlayMetric Metric { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand ToggleExpandCommand { get; }

    public string Title => Metric.IsSensor ? Item.Title ?? Metric.Title : Metric.Title;

    /// <summary>Подпись в оверлее; пустая — без подписи.</summary>
    public string Label
    {
        get => Item.Label ?? Metric.Label;
        set
        {
            // Без Trim и без OnPropertyChanged(Label): поле обновляется на лету, пока печатают, — иначе съедались бы пробелы.
            string label = value ?? "";
            Item.Label = label == Metric.Label ? null : label;
            OnPropertyChanged(nameof(Summary));
            _owner.ItemChanged();
        }
    }

    public string DefaultLabel => Metric.Label.Length > 0 ? $"обычно «{Metric.Label}»" : "обычно без подписи";

    /// <summary>Вторая строка: подпись и отметки — «CPU · график · крупно».</summary>
    public string Summary
    {
        get
        {
            string label = Label.Trim();
            var parts = new List<string> { label.Length > 0 ? $"«{label}»" : "без подписи" };
            if (Item.SameLine && !_isFirst)
                parts.Add(_owner.IsHorizontal ? "вплотную к предыдущему" : "в строке с предыдущим");
            if (Item.Warn is not null || Item.Critical is not null)
                parts.Add("пороги");
            return string.Join(" · ", parts);
        }
    }

    public bool Visible
    {
        get => Item.Visible;
        set
        {
            if (Item.Visible == value)
                return;
            Item.Visible = value;
            OnPropertyChanged();
            _owner.ItemChanged();
        }
    }

    public bool CanGraph => Metric.CanGraph;

    public bool Graph
    {
        get => Item.Graph;
        set
        {
            if (Item.Graph == value)
                return;
            Item.Graph = value;
            OnPropertyChanged();
            _owner.ItemChanged();
        }
    }

    public bool Big
    {
        get => Item.Big;
        set
        {
            if (Item.Big == value)
                return;
            Item.Big = value;
            OnPropertyChanged();
            _owner.ItemChanged();
        }
    }

    public bool CanSameLine => !_isFirst;

    public string SameLineText => _owner.IsHorizontal ? "Вплотную к предыдущему" : "В одной строке с предыдущим";

    public bool SameLine
    {
        get => Item.SameLine;
        set
        {
            if (Item.SameLine == value)
                return;
            Item.SameLine = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
            _owner.ItemChanged();
        }
    }

    /// <summary>Свой цвет значения; null — общий.</summary>
    public string? Color
    {
        get => Item.Color;
        set
        {
            string? color = value is null ? null : OverlayStyle.ValidColor(value);
            if (color == Item.Color)
                return;
            Item.Color = color;
            OnPropertyChanged();
            _owner.ItemChanged();
        }
    }

    /// <summary>Каким цветом значение будет без своего цвета.</summary>
    public string DefaultColor => Metric.Muted ? _owner.Style.LabelColor : _owner.Style.ValueColor;

    public bool HasThresholds => !Metric.IsText;

    public string WarnText
    {
        get => OverlayEditorViewModel.Format(Item.Warn);
        set => SetThreshold(value, v => Item.Warn = v);
    }

    public string CriticalText
    {
        get => OverlayEditorViewModel.Format(Item.Critical);
        set => SetThreshold(value, v => Item.Critical = v);
    }

    /// <summary>«Жёлтый — ниже 60 FPS, красный — ниже 30 FPS».</summary>
    public string ThresholdHint
    {
        get
        {
            if (Item.Warn is null && Item.Critical is null)
                return "Без порогов: цвет не меняется.";

            bool lower = OverlayMetrics.IsLowerWorse(Item.Warn, Item.Critical, Metric.LowerIsWorse);
            string Edge(double value)
            {
                (string number, string unit) = OverlayMetrics.Format(Metric, value);
                return $"{(lower ? "ниже" : "от")} {number}{(unit.Length > 0 ? " " + unit : "")}";
            }

            var parts = new List<string>();
            if (Item.Warn is double warn)
                parts.Add("жёлтый — " + Edge(warn));
            if (Item.Critical is double critical)
                parts.Add("красный — " + Edge(critical));
            string text = string.Join(", ", parts);
            return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..] + ".";
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    internal void SetPosition(bool first, bool last)
    {
        _isFirst = first;
        _isLast = last;
        OnPropertyChanged(nameof(CanSameLine));
        OnPropertyChanged(nameof(Summary));
        CommandManager.InvalidateRequerySuggested();
    }

    internal void RefreshOrientation()
    {
        OnPropertyChanged(nameof(SameLineText));
        OnPropertyChanged(nameof(Summary));
    }

    internal void RefreshStyle() => OnPropertyChanged(nameof(DefaultColor));

    private void SetThreshold(string? text, Action<double?> apply)
    {
        string value = (text ?? "").Trim().Replace(',', '.');
        double? number = null;
        if (value.Length > 0)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed))
            {
                OnPropertyChanged(nameof(WarnText));
                OnPropertyChanged(nameof(CriticalText));
                return; // не число — оставить как было
            }

            number = parsed;
        }

        apply(number);
        OnPropertyChanged(nameof(WarnText));
        OnPropertyChanged(nameof(CriticalText));
        OnPropertyChanged(nameof(ThresholdHint));
        OnPropertyChanged(nameof(Summary));
        _owner.ItemChanged();
    }
}
