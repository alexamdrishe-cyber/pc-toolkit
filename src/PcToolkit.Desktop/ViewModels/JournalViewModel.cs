using System.Windows.Input;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Logging;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

public sealed record JournalEntryRow(string Tweak, string Parameter, string Before, string After, bool IsReverted);

/// <summary>Набор изменений в журнале.</summary>
public sealed class ChangeSetViewModel
{
    public ChangeSetViewModel(ChangeSet set, Action<ChangeSetViewModel> revert)
    {
        RevertCommand = new RelayCommand(() => revert(this));
        Id = set.Id;
        Title = set.Title;
        DateText = set.CreatedAt.ToString("d MMMM yyyy, HH:mm");
        Error = set.Error ?? "";
        RestartText = set.Restart != RestartRequirement.None && set.Status == ChangeSetStatus.Applied
            ? $"Для вступления в силу {RestartRequirementText.Describe(set.Restart)}."
            : "";

        Entries = set.Entries
            .Select(e => new JournalEntryRow(e.TweakTitle, e.TargetDisplay, e.BeforeText, e.AfterText, e.RevertedBySetId is not null))
            .ToList();

        List<ChangeEntry> done = set.Entries.Where(e => e.Done).ToList();
        int reverted = done.Count(e => e.RevertedBySetId is not null);

        (string statusText, string statusKind) = set.Status switch
        {
            ChangeSetStatus.Pending => ("прервано", "warning"),
            ChangeSetStatus.RolledBack => ("не применено, изменения откатаны", "critical"),
            _ when set.Kind == ChangeSetKind.Revert => ("отмена", "info"),
            _ when done.Count > 0 && reverted == done.Count => ("отменено", "muted"),
            _ when reverted > 0 => ("отменено частично", "warning"),
            _ => ("применено", "good"),
        };
        StatusText = statusText;
        StatusKind = statusKind;

        CanRevert = set.Kind == ChangeSetKind.Apply
                    && set.Status == ChangeSetStatus.Applied
                    && done.Any(e => e.RevertedBySetId is null);
    }

    /// <summary>Команда у самого набора: кнопке в списке не нужно искать модель экрана по дереву элементов.</summary>
    public ICommand RevertCommand { get; }

    public Guid Id { get; }

    public string Title { get; }

    public string DateText { get; }

    public string StatusText { get; }

    /// <summary>good | warning | critical | info | muted — для цвета метки.</summary>
    public string StatusKind { get; }

    public string RestartText { get; }

    public bool HasRestartText => RestartText.Length > 0;

    public string Error { get; }

    public bool HasError => Error.Length > 0;

    public IReadOnlyList<JournalEntryRow> Entries { get; }

    public bool HasEntries => Entries.Count > 0;

    public bool CanRevert { get; }
}

/// <summary>Экран «Журнал и откат».</summary>
public sealed class JournalViewModel : ObservableObject
{
    private readonly ChangeEngine _engine;
    private readonly IChangePresenter _presenter;
    private IReadOnlyList<ChangeSetViewModel> _sets = Array.Empty<ChangeSetViewModel>();

    public JournalViewModel(ChangeEngine engine, IChangePresenter presenter)
    {
        _engine = engine;
        _presenter = presenter;

        OpenJournalFileCommand = new RelayCommand(() => ShellService.ShowFileInExplorer(_engine.Journal.FilePath));
    }

    public ICommand OpenJournalFileCommand { get; }

    public IReadOnlyList<ChangeSetViewModel> Sets
    {
        get => _sets;
        private set
        {
            if (SetProperty(ref _sets, value))
                OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => Sets.Count == 0;

    public void Refresh() =>
        Sets = _engine.Journal.Sets
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new ChangeSetViewModel(s, Revert))
            .ToList();

    private void Revert(ChangeSetViewModel set)
    {
        AppLog.Info($"Запрошена отмена набора «{set.Title}».");
        _presenter.ShowPlan(_engine.PlanRevertSet(set.Id), offerRestorePoint: false);
    }
}
