using System.Windows.Input;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Platform;
using PcToolkit.Core.Tweaks;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>
/// Экран «Оптимизация»: анкета, список настроек с состояниями и запуск предпросмотра.
/// Сам экран систему не меняет — он только просит движок построить план.
/// </summary>
public sealed class OptimizationViewModel : ObservableObject
{
    private readonly ChangeEngine _engine;
    private readonly TweakCatalog _catalog;
    private readonly IChangePresenter _presenter;
    private readonly bool _isElevated;
    private readonly List<TweakItemViewModel> _items;
    private bool _isQuestionnaireMode = true;
    private bool _isRefreshing;
    private string _recommendationText = "";

    public OptimizationViewModel(
        ChangeEngine engine,
        TweakCatalog catalog,
        ITargetFactory factory,
        IChangePresenter presenter,
        bool isElevated,
        ICommand restartAsAdminCommand)
    {
        _engine = engine;
        _catalog = catalog;
        _presenter = presenter;
        _isElevated = isElevated;
        RestartAsAdminCommand = restartAsAdminCommand;

        Categories = catalog.Categories
            .Select(category => new TweakCategoryViewModel(
                category,
                catalog.Tweaks
                    .Where(t => string.Equals(t.Category, category.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new TweakItemViewModel(t, RequiresAdmin(t, factory), isElevated, Revert))
                    .ToList()))
            .Where(category => category.Items.Count > 0)
            .ToList();

        _items = Categories.SelectMany(c => c.Items).ToList();
        foreach (TweakItemViewModel item in _items)
            item.SelectionChanged += (_, _) => OnSelectionChanged();

        Questions = Questionnaire.Questions.Select(q => new QuestionViewModel(q)).ToList();

        RecommendCommand = new RelayCommand(Recommend);
        PreviewCommand = new RelayCommand(Preview, () => SelectedCount > 0 && !_isRefreshing);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => SelectedCount > 0);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !_isRefreshing);
    }

    public IReadOnlyList<TweakCategoryViewModel> Categories { get; }

    public IReadOnlyList<QuestionViewModel> Questions { get; }

    public ICommand RecommendCommand { get; }

    public ICommand PreviewCommand { get; }

    public ICommand ClearSelectionCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand RestartAsAdminCommand { get; }

    public bool ShowAdminBanner => !_isElevated;

    public bool IsQuestionnaireMode
    {
        get => _isQuestionnaireMode;
        set
        {
            if (SetProperty(ref _isQuestionnaireMode, value))
                OnPropertyChanged(nameof(IsListMode));
        }
    }

    public bool IsListMode
    {
        get => !_isQuestionnaireMode;
        set => IsQuestionnaireMode = !value;
    }

    public string RecommendationText
    {
        get => _recommendationText;
        private set
        {
            if (SetProperty(ref _recommendationText, value))
                OnPropertyChanged(nameof(HasRecommendation));
        }
    }

    public bool HasRecommendation => RecommendationText.Length > 0;

    public int SelectedCount => _items.Count(i => i.IsSelected);

    public string SelectedText => SelectedCount == 0
        ? "Отметьте настройки, которые хотите применить."
        : $"Выбрано настроек: {SelectedCount}";

    /// <summary>Перечитывает состояние всех настроек (в фоне: чтение реестра и служб).</summary>
    public async Task RefreshAsync()
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        RelayCommand.Refresh();
        try
        {
            var statuses = await Task.Run(() =>
            {
                _ = DeviceInfo.IsLaptop; // заранее, чтобы анкета не ждала запроса WMI
                return _items.Select(item => (Item: item, Status: _engine.GetStatus(item.Definition))).ToList();
            });

            foreach ((TweakItemViewModel item, TweakStatus status) in statuses)
                item.ApplyStatus(status);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось проверить состояние настроек.", ex);
        }
        finally
        {
            _isRefreshing = false;
            OnSelectionChanged();
        }
    }

    /// <summary>Вызывается после успешного применения: выбор сбрасывается.</summary>
    public void OnApplied() => ClearSelection();

    private void Recommend()
    {
        var answers = Questions.ToDictionary(q => q.Id, q => q.SelectedOptionId);
        var recommended = Questionnaire
            .Recommend(_catalog, answers, DeviceInfo.IsLaptop, _engine.WindowsBuild)
            .Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int alreadyDone = 0;
        int needsAdmin = 0;
        foreach (TweakItemViewModel item in _items)
        {
            bool isRecommended = recommended.Contains(item.Id);
            item.IsSelected = isRecommended && item.CanSelect;

            if (!isRecommended)
                continue;
            if (item.State is TweakState.Applied or TweakState.AppliedOutside)
                alreadyDone++;
            else if (item.RequiresAdmin && !_isElevated)
                needsAdmin++;
        }

        RecommendationText = recommended.Count == 0
            ? "Под ваши ответы безопасных настроек не нашлось — Windows уже настроена разумно."
            : $"Подобрано настроек: {recommended.Count}. Отмечено к применению: {SelectedCount}"
              + (alreadyDone > 0 ? $", уже применено: {alreadyDone}" : "")
              + (needsAdmin > 0 ? $", нужны права администратора: {needsAdmin}" : "")
              + ". Проверьте список и нажмите «Предпросмотр изменений».";

        IsQuestionnaireMode = false;
    }

    private void Preview()
    {
        List<TweakDefinition> selected = _items.Where(i => i.IsSelected).Select(i => i.Definition).ToList();
        if (selected.Count == 0)
            return;

        _presenter.ShowPlan(_engine.PlanApply(selected), offerRestorePoint: _isElevated);
    }

    private void Revert(TweakItemViewModel item)
    {
        AppLog.Info($"Запрошен возврат настройки «{item.Title}».");
        _presenter.ShowPlan(_engine.PlanRevertTweak(item.Definition), offerRestorePoint: false);
    }

    private void ClearSelection()
    {
        foreach (TweakItemViewModel item in _items)
            item.IsSelected = false;

        RecommendationText = "";
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedText));
        RelayCommand.Refresh();
    }

    private static bool RequiresAdmin(TweakDefinition tweak, ITargetFactory factory)
    {
        try
        {
            return tweak.Changes.Any(change => factory.Resolve(change).Target.RequiresAdmin);
        }
        catch
        {
            return true;
        }
    }
}
