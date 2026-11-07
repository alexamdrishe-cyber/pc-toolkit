using System.Windows;
using System.Windows.Input;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Service;
using PcToolkit.Core.Settings;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Экран «Настройки»: служба PC Toolkit и частота обновления датчиков.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ServiceInfo? _info;
    private readonly bool _isElevated;
    private readonly bool _usingService;
    private ServiceInstallState _state;
    private bool _isBusy;
    private string _resultText = "";
    private int _sensorIntervalMs = (int)AppSettings.SensorInterval(AppSettings.Load().SensorIntervalMs).TotalMilliseconds;

    public SettingsViewModel(ServiceInfo? info, bool isElevated, bool usingService)
    {
        _info = info;
        _isElevated = isElevated;
        _usingService = usingService;
        _state = ServiceInstaller.QueryState();

        InstallCommand = new RelayCommand(() => _ = RunAsync(LaunchArguments.InstallService),
                                          () => !IsBusy && ServiceInstaller.PackageAvailable);
        UninstallCommand = new RelayCommand(() => _ = RunAsync(LaunchArguments.UninstallService),
                                            () => !IsBusy && IsInstalled);
    }

    /// <summary>Частоту опроса датчиков поменяли в настройках.</summary>
    public event Action<TimeSpan>? SensorIntervalChanged;

    public TimeSpan SensorInterval => TimeSpan.FromMilliseconds(_sensorIntervalMs);

    // Переключатель «4 / 2 / 1 раз в секунду»: снятие отметки с соседней кнопки игнорируем.
    public bool IsRate4 { get => _sensorIntervalMs == 250; set { if (value) SetSensorInterval(250); } }

    public bool IsRate2 { get => _sensorIntervalMs == 500; set { if (value) SetSensorInterval(500); } }

    public bool IsRate1 { get => _sensorIntervalMs == 1000; set { if (value) SetSensorInterval(1000); } }

    public ICommand InstallCommand { get; }

    public ICommand UninstallCommand { get; }

    public bool IsInstalled => _state is ServiceInstallState.Running or ServiceInstallState.Stopped;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RelayCommand.Refresh();
        }
    }

    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (SetProperty(ref _resultText, value))
                OnPropertyChanged(nameof(HasResult));
        }
    }

    public bool HasResult => ResultText.Length > 0;

    public string InstallButtonText => !IsInstalled ? "Установить службу" : NeedsUpdate ? "Обновить службу" : "Переустановить";

    public bool ShowUninstall => IsInstalled;

    /// <summary>good | warning | info — цвет метки состояния.</summary>
    public string StatusKind => _state switch
    {
        ServiceInstallState.Running when _info is { IsCurrent: true } => "good",
        ServiceInstallState.NotInstalled => "info",
        _ => "warning",
    };

    public string StatusTitle => _state switch
    {
        ServiceInstallState.NotInstalled => "не установлена",
        ServiceInstallState.Stopped => "остановлена",
        ServiceInstallState.Running when _info is null => "не отвечает",
        ServiceInstallState.Running when !_info.IsCurrent => "устарела",
        ServiceInstallState.Running => "работает",
        _ => "состояние неизвестно",
    };

    public string StatusText => _state switch
    {
        ServiceInstallState.NotInstalled =>
            "Без службы для полного набора датчиков и системных настроек приложение приходится запускать от имени администратора. " +
            "Служба ставится один раз (Windows спросит разрешение), после этого приложение работает с обычными правами.",
        ServiceInstallState.Stopped =>
            "Служба установлена, но не запущена. Переустановка запустит её снова.",
        ServiceInstallState.Running when _info is null =>
            "Служба запущена, но приложение не смогло с ней связаться. Подробности — в журнале службы.",
        ServiceInstallState.Running when !_info.IsCurrent =>
            "Служба собрана из другой версии приложения. Обновите её, чтобы версии совпадали.",
        ServiceInstallState.Running when _isElevated =>
            "Служба работает. Сейчас приложение запущено от имени администратора и обходится без неё.",
        ServiceInstallState.Running when _usingService =>
            "Датчики и системные настройки идут через службу. Запускать приложение от имени администратора не нужно.",
        ServiceInstallState.Running => "Служба работает.",
        _ => "Не удалось узнать состояние службы.",
    };

    private void SetSensorInterval(int milliseconds)
    {
        if (_sensorIntervalMs == milliseconds)
            return;

        _sensorIntervalMs = milliseconds;
        AppSettings.Update(s => s.SensorIntervalMs = milliseconds);
        AppLog.Info($"Частота опроса датчиков: раз в {milliseconds} мс.");
        OnPropertyChanged(nameof(IsRate4));
        OnPropertyChanged(nameof(IsRate2));
        OnPropertyChanged(nameof(IsRate1));
        SensorIntervalChanged?.Invoke(SensorInterval);
    }

    private bool NeedsUpdate => _state == ServiceInstallState.Stopped || _info is null || !_info.IsCurrent;

    private async Task RunAsync(string argument)
    {
        if (IsBusy)
            return;

        bool install = argument == LaunchArguments.InstallService;
        IsBusy = true;
        ResultText = install
            ? "Устанавливаю службу… Подтвердите запрос Windows."
            : "Удаляю службу… Подтвердите запрос Windows.";

        bool ok = await ServiceInstaller.RunElevatedAsync(argument);
        IsBusy = false;
        if (!ok)
        {
            Refresh();
            ResultText = "Не получилось: запрос Windows отклонён или произошла ошибка. Подробности — в журнале приложения.";
            return;
        }

        // Перезапуск: приложение заново решит, читать датчики само или через службу.
        ResultText = install ? "Служба установлена. Перезапускаю приложение…" : "Служба удалена. Перезапускаю приложение…";
        if (ShellService.RestartApplication())
            App.Quit();
        else
            Refresh();
    }

    private void Refresh()
    {
        _state = ServiceInstaller.QueryState();
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(ShowUninstall));
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusText));
        RelayCommand.Refresh();
    }
}
