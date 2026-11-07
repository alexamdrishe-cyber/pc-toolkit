using System.Windows.Input;
using PcToolkit.Core;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Logging;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Показывает окно предпросмотра плана изменений и сообщает, чем оно закончилось.</summary>
public interface IChangePresenter
{
    /// <param name="completed">Вызывается, когда окно предпросмотра закрыто: результат или null, если ничего не применяли.</param>
    void ShowPlan(ChangePlan plan, bool offerRestorePoint, Action<ChangeResult?>? completed = null);
}

public sealed record PreviewRow(string Tweak, string Parameter, string Before, string After);

/// <summary>
/// Окно предпросмотра «было → станет». Единое для всех модулей: что бы ни меняло систему,
/// пользователь видит один и тот же порядок — список изменений, проблемы, кнопка «Применить».
/// </summary>
public sealed class ChangePreviewViewModel : ObservableObject
{
    private readonly ChangePlan _plan;
    private readonly ChangeEngine _engine;
    private readonly OperationGate _gate;
    private readonly Action<ChangeResult?> _onClosed;
    private ChangeResult? _result;
    private bool _createRestorePoint;
    private bool _isBusy;
    private bool _isDone;
    private string _busyText = "";
    private string _resultTitle = "";
    private string _resultText = "";
    private bool _resultIsSuccess;

    public ChangePreviewViewModel(ChangePlan plan, ChangeEngine engine, OperationGate gate, bool offerRestorePoint, Action<ChangeResult?> onClosed)
    {
        _plan = plan;
        _engine = engine;
        _gate = gate;
        _onClosed = onClosed;

        Title = plan.Title;
        Rows = plan.Items.Select(i => new PreviewRow(i.TweakTitle, i.Target.DisplayName, i.BeforeText, i.AfterText)).ToList();
        Problems = plan.Problems.ToList();
        Skipped = plan.Skipped.ToList();
        Summary = BuildSummary(plan);

        OfferRestorePoint = offerRestorePoint && plan.Kind == ChangeSetKind.Apply && plan.Items.Count > 0 && plan.CanExecute;
        _createRestorePoint = OfferRestorePoint;

        ApplyCommand = new RelayCommand(Apply, () => _plan.CanExecute && !IsBusy && !IsDone);
        CloseCommand = new RelayCommand(Close, () => !IsBusy);
    }

    public string Title { get; }

    public string Summary { get; }

    public IReadOnlyList<PreviewRow> Rows { get; }

    public IReadOnlyList<string> Problems { get; }

    public IReadOnlyList<string> Skipped { get; }

    public bool HasRows => Rows.Count > 0;

    public bool HasProblems => Problems.Count > 0;

    public bool HasSkipped => Skipped.Count > 0;

    public bool OfferRestorePoint { get; }

    public string ApplyButtonText => _plan.Kind == ChangeSetKind.Revert ? "Вернуть" : "Применить";

    public ICommand ApplyCommand { get; }

    public ICommand CloseCommand { get; }

    public bool CreateRestorePoint
    {
        get => _createRestorePoint;
        set => SetProperty(ref _createRestorePoint, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsDone
    {
        get => _isDone;
        private set
        {
            if (SetProperty(ref _isDone, value))
            {
                OnPropertyChanged(nameof(ShowPlan));
                OnPropertyChanged(nameof(CloseButtonText));
            }
        }
    }

    /// <summary>Пока результата нет, показываем сам план.</summary>
    public bool ShowPlan => !IsDone;

    public string CloseButtonText => IsDone ? "Закрыть" : "Отмена";

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string ResultTitle
    {
        get => _resultTitle;
        private set => SetProperty(ref _resultTitle, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public bool ResultIsSuccess
    {
        get => _resultIsSuccess;
        private set => SetProperty(ref _resultIsSuccess, value);
    }

    private static string BuildSummary(ChangePlan plan)
    {
        if (plan.Items.Count == 0)
        {
            return plan.Released.Count > 0
                ? "Менять в системе ничего не нужно: приложение просто перестанет считать эти параметры своими."
                : "Изменений нет.";
        }

        string summary = plan.Kind == ChangeSetKind.Revert
            ? $"Будет возвращено прежних значений: {plan.Items.Count}."
            : $"Будет изменено параметров: {plan.Items.Count}.";

        if (plan.Restart != RestartRequirement.None)
            summary += $" После этого {RestartRequirementText.Describe(plan.Restart)}.";

        return summary;
    }

    private async void Apply()
    {
        IDisposable? slot = _gate.TryEnter(_plan.Title, out string? busyWith);
        if (slot is null)
        {
            ResultIsSuccess = false;
            ResultTitle = "Подождите";
            ResultText = $"Сейчас выполняется другая операция: «{busyWith}». Применить можно, когда она закончится.";
            IsDone = true;
            RelayCommand.Refresh();
            return;
        }

        try
        {
            IsBusy = true;
            RelayCommand.Refresh();

            string? restorePointNote = null;
            if (OfferRestorePoint && CreateRestorePoint)
            {
                BusyText = "Создаю точку восстановления Windows… Это может занять до минуты.";
                var (_, message) = await Task.Run(() => RestorePoint.Create($"PC Toolkit: {_plan.Title}"));
                restorePointNote = message;
            }

            BusyText = _plan.Kind == ChangeSetKind.Revert ? "Возвращаю прежние значения…" : "Применяю изменения…";
            _result = await Task.Run(() => _engine.Execute(_plan));

            ResultIsSuccess = _result.Success;
            ResultTitle = _result.Success ? "Готово" : "Не получилось";
            ResultText = restorePointNote is null ? _result.Message : $"{_result.Message}\n{restorePointNote}";
            if (_result.Success)
            {
                ResultText += _plan.Kind == ChangeSetKind.Revert
                    ? "\nПрежние значения возвращены. Запись об отмене есть на экране «Журнал и откат»."
                    : "\nВсе изменения записаны в журнал, их можно отменить на экране «Журнал и откат».";
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка при выполнении плана изменений.", ex);
            ResultIsSuccess = false;
            ResultTitle = "Не получилось";
            ResultText = ex.Message;
        }
        finally
        {
            slot.Dispose();
            IsBusy = false;
            IsDone = true;
            BusyText = "";
            RelayCommand.Refresh();
        }
    }

    private void Close() => _onClosed(_result);
}
