using System.Windows.Input;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Tweaks;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Строка настройки на экране «Оптимизация».</summary>
public sealed class TweakItemViewModel : ObservableObject
{
    private readonly bool _isElevated;
    private TweakState? _state;
    private string _stateText = "проверяю…";
    private bool _isSelected;
    private bool _canRevert;

    public TweakItemViewModel(TweakDefinition definition, bool requiresAdmin, bool isElevated, Action<TweakItemViewModel> revert)
    {
        Definition = definition;
        RequiresAdmin = requiresAdmin;
        _isElevated = isElevated;
        RevertCommand = new RelayCommand(() => revert(this), () => CanRevert);
    }

    public event EventHandler? SelectionChanged;

    /// <summary>Команда у самой строки: кнопке в списке не нужно искать модель экрана по дереву элементов.</summary>
    public ICommand RevertCommand { get; }

    public TweakDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Title => Definition.Title;

    public string Description => Definition.Description;

    public bool RequiresAdmin { get; }

    public bool IsMediumRisk => Definition.Risk != TweakRisk.Low;

    public string RiskText => Definition.Risk switch
    {
        TweakRisk.Low => "низкий риск",
        TweakRisk.Medium => "средний риск",
        _ => "высокий риск",
    };

    public bool HasRestart => Definition.Restart != RestartRequirement.None;

    public string RestartText => RestartRequirementText.Describe(Definition.Restart);

    public bool ShowAdminBadge => RequiresAdmin;

    public TweakState? State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public bool CanRevert
    {
        get => _canRevert;
        private set => SetProperty(ref _canRevert, value);
    }

    /// <summary>Выбрать можно то, что ещё не применено, доступно и не требует недоступных прав.</summary>
    public bool CanSelect =>
        State is TweakState.NotApplied or TweakState.Partial
        && (!RequiresAdmin || _isElevated);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            bool newValue = value && CanSelect;
            if (SetProperty(ref _isSelected, newValue))
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyStatus(TweakStatus status)
    {
        State = status.State;
        CanRevert = status.CanRevert;
        StateText = status.State switch
        {
            TweakState.NotApplied => "не применено",
            TweakState.Applied => "применено",
            TweakState.AppliedOutside => "уже настроено",
            TweakState.Partial => "применено частично",
            TweakState.NotApplicable => status.Reason ?? "недоступно",
            _ => "не удалось проверить",
        };

        OnPropertyChanged(nameof(CanSelect));
        if (!CanSelect)
            IsSelected = false;
    }
}

public sealed class TweakCategoryViewModel
{
    public TweakCategoryViewModel(TweakCategory category, IReadOnlyList<TweakItemViewModel> items)
    {
        Title = category.Title;
        Description = category.Description;
        Items = items;
    }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<TweakItemViewModel> Items { get; }
}

/// <summary>Вариант ответа в анкете.</summary>
public sealed class QuestionOptionViewModel : ObservableObject
{
    private readonly QuestionViewModel _owner;
    private bool _isSelected;

    public QuestionOptionViewModel(QuestionViewModel owner, QuestionOption option, bool isSelected)
    {
        _owner = owner;
        Id = option.Id;
        Title = option.Title;
        Description = option.Description;
        _isSelected = isSelected;
    }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>Группа переключателей: у каждого вопроса своя.</summary>
    public string GroupName => "question-" + _owner.Id;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value)
                _owner.Select(this);
        }
    }

    internal void Deselect()
    {
        _isSelected = false;
        OnPropertyChanged(nameof(IsSelected));
    }
}

public sealed class QuestionViewModel
{
    public QuestionViewModel(Question question)
    {
        Id = question.Id;
        Title = question.Title;
        Options = question.Options
            .Select(o => new QuestionOptionViewModel(this, o, o.Id == question.DefaultOptionId))
            .ToList();
    }

    public string Id { get; }

    public string Title { get; }

    public IReadOnlyList<QuestionOptionViewModel> Options { get; }

    public string SelectedOptionId => Options.FirstOrDefault(o => o.IsSelected)?.Id ?? "";

    internal void Select(QuestionOptionViewModel selected)
    {
        foreach (QuestionOptionViewModel option in Options.Where(o => o != selected && o.IsSelected))
            option.Deselect();
    }
}
