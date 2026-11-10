using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Repair;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>
/// «Восстановление Windows» одной кнопкой: DISM и sfc в отдельном процессе с правами администратора (Windows спросит
/// разрешение), ход и итог — из файла состояния. Окно можно закрыть: восстановление продолжится, итог покажется потом.
/// </summary>
public sealed class WindowsRepairViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private readonly string _statusPath = WindowsRepair.DefaultStatusPath;
    private RepairStatus? _status;
    private string _message = "";

    public WindowsRepairViewModel(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Poll(), dispatcher);
        _timer.Stop();
        StartCommand = new RelayCommand(Start, () => !IsRunning);
        OpenLogCommand = new RelayCommand(() => ShellService.OpenFolder(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS")));
        Poll();
    }

    public ICommand StartCommand { get; }

    public ICommand OpenLogCommand { get; }

    public bool IsRunning => _status?.IsRunning == true;

    public bool HasResult => _status is { FinishedAt: not null };

    public double Percent => _status?.Percent ?? 0;

    public string StageText => _status?.Stage switch
    {
        "dism" => $"Шаг 1 из 2: восстанавливаю хранилище компонентов Windows (DISM) — {Percent:0} %",
        "sfc" => $"Шаг 2 из 2: проверяю системные файлы (sfc) — {Percent:0} %",
        "starting" => "Запускаю…",
        _ => "",
    };

    /// <summary>Итог простыми словами (или сообщение о запуске).</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>good, warning, bad — для цвета итога.</summary>
    public string ResultKind => _status switch
    {
        { Sfc: SfcVerdict.Clean, DismExitCode: 0 } => "good",
        { Sfc: SfcVerdict.Clean or SfcVerdict.Repaired } => "warning",
        { FinishedAt: not null } => "bad",
        _ => "",
    };

    public string LastRunText => _status?.FinishedAt is { } at ? $"Последняя проверка: {at:d MMMM, HH:mm}" : "";

    private void Start()
    {
        string exe = Environment.ProcessPath ?? "";
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"{LaunchArguments.RepairWindows} \"{_statusPath}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            _status = new RepairStatus();
            Message = "Идёт восстановление: обычно 10–30 минут. Компьютером можно пользоваться, а это окно — закрыть: итог появится здесь.";
            AppLog.Info("Восстановление Windows: запущено.");
            _timer.Start();
            Notify();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Message = "Отменено в окне Windows — без разрешения администратора восстановление не запустить.";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Message = "Не удалось запустить: " + ex.Message;
            AppLog.Error("Восстановление Windows: не удалось запустить.", ex);
        }
    }

    private void Poll()
    {
        RepairStatus? status = WindowsRepair.Load(_statusPath);
        if (status is not null)
            _status = status;

        if (_status?.IsRunning == true)
        {
            if (!_timer.IsEnabled)
                _timer.Start();
        }
        else
        {
            _timer.Stop();
            if (_status?.FinishedAt is not null)
                Message = Describe(_status);
        }

        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(StageText));
        OnPropertyChanged(nameof(ResultKind));
        OnPropertyChanged(nameof(LastRunText));
        RelayCommand.Refresh();
    }

    /// <summary>Итог DISM и sfc простыми словами.</summary>
    public static string Describe(RepairStatus status)
    {
        if (status.Error is { } error)
            return "Восстановление прервалось: " + error;

        string dism = status.DismExitCode is 0 or null
            ? ""
            : $" Хранилище компонентов восстановить не удалось (DISM, код 0x{status.DismExitCode:X8}) — обычно нет связи с Центром обновления Windows; " +
              "проверьте интернет и запустите ещё раз.";
        return status.Sfc switch
        {
            SfcVerdict.Clean => "Системные файлы в порядке — повреждений нет." + dism,
            SfcVerdict.Repaired => "Нашлись повреждённые системные файлы — они восстановлены. Перезагрузите компьютер, чтобы исправления заработали." + dism,
            SfcVerdict.Partial => "Нашлись повреждённые системные файлы, но часть восстановить не удалось. Запустите восстановление ещё раз после перезагрузки; " +
                                  "если не поможет — понадобится установочный образ Windows. Подробности — в журнале CBS." + dism,
            SfcVerdict.Failed => "Проверка системных файлов не смогла выполниться. Перезагрузите компьютер и запустите ещё раз." + dism,
            _ => "Проверка закончилась, но её итог не удалось прочитать — подробности в журнале CBS." + dism,
        };
    }
}
