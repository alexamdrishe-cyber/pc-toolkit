using System.Net.Http;
using System.Windows.Input;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Settings;
using PcToolkit.Core.Updates;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Карточка «Обновления» в настройках: проверить, скачать и установить новую версию.</summary>
public sealed class UpdatesViewModel : ObservableObject
{
    private UpdateInfo? _available;
    private bool _busy;
    private string _status = "";
    private double _progress;
    private bool _autoCheck;

    public UpdatesViewModel()
    {
        _autoCheck = AppSettings.Load().AutoCheckUpdates;
        CheckCommand = new RelayCommand(() => _ = CheckAsync(silent: false), () => !_busy && IsConfigured);
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => !_busy && _available is not null);
        if (!IsConfigured)
            _status = "Адрес обновлений ещё не задан: новые версии пока ставятся вручную.";
    }

    public ICommand CheckCommand { get; }

    public ICommand InstallCommand { get; }

    /// <summary>Адрес обновлений: из настроек или заданный при сборке (выпуски репозитория GitHub).</summary>
    public static string Url
    {
        get
        {
            string? own = AppSettings.Load().UpdateUrl;
            return !string.IsNullOrWhiteSpace(own) ? own.Trim() : ProjectLinks.BuiltInUpdateUrl;
        }
    }

    public bool IsConfigured => Url.Length > 0;

    public string CurrentVersion => $"Версия {UpdateChecker.Current.ToString(3)}";

    public bool HasUpdate => _available is not null;

    public string UpdateTitle => _available is null ? "" : $"Доступна версия {_available.Version.ToString(3)}";

    public string Notes => _available?.Notes ?? "";

    public bool IsBusy => _busy;

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool AutoCheck
    {
        get => _autoCheck;
        set
        {
            if (SetProperty(ref _autoCheck, value))
                AppSettings.Update(s => s.AutoCheckUpdates = value);
        }
    }

    /// <summary>Тихая проверка вскоре после запуска (если включена и адрес задан).</summary>
    public async Task CheckOnStartupAsync()
    {
        if (!_autoCheck || !IsConfigured)
            return;
        await Task.Delay(TimeSpan.FromSeconds(20));
        await CheckAsync(silent: true);
    }

    private async Task CheckAsync(bool silent)
    {
        SetBusy(true);
        try
        {
            if (!silent)
                Status = "Проверяю…";
            _available = await UpdateChecker.CheckAsync(Url, CancellationToken.None);
            Status = _available is null ? "У вас последняя версия." : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AppLog.Warn("Проверка обновлений не удалась.", ex);
            if (!silent)
                Status = "Не удалось проверить обновления: нет связи с сервером.";
        }
        finally
        {
            SetBusy(false);
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateTitle));
            OnPropertyChanged(nameof(Notes));
        }
    }

    private async Task InstallAsync()
    {
        if (_available is null)
            return;
        SetBusy(true);
        try
        {
            Status = "Скачиваю установщик…";
            var progress = new Progress<double>(p => Progress = p);
            string setup = await UpdateChecker.DownloadAsync(_available, progress, CancellationToken.None);
            Status = "Устанавливаю: Windows спросит разрешение, программа закроется и откроется уже новой.";
            UpdateChecker.Install(setup);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            AppLog.Warn("Обновление не установилось.", ex);
            Status = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? "Отменено в окне Windows."
                : "Не получилось обновиться: " + ex.Message;
        }
        finally
        {
            Progress = 0;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnPropertyChanged(nameof(IsBusy));
        RelayCommand.Refresh();
    }
}
