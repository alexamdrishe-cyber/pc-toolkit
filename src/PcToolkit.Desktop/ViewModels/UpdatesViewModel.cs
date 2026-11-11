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
    private bool _bannerClosed;

    public UpdatesViewModel()
    {
        _autoCheck = AppSettings.Load().AutoCheckUpdates;
        CheckCommand = new RelayCommand(() => _ = CheckAsync(silent: false), () => !_busy && IsConfigured);
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => !_busy && _available is not null);
        SkipCommand = new RelayCommand(Skip, () => !_busy && _available is not null);
        LaterCommand = new RelayCommand(() =>
        {
            _bannerClosed = true;
            OnPropertyChanged(nameof(ShowBanner));
        });
        if (!IsConfigured)
            _status = "Адрес обновлений ещё не задан: новые версии пока ставятся вручную.";
    }

    public ICommand CheckCommand { get; }

    public ICommand InstallCommand { get; }

    /// <summary>«Пропустить эту версию»: больше не напоминать о ней (о следующей — напомнить).</summary>
    public ICommand SkipCommand { get; }

    /// <summary>Закрыть напоминание до следующего запуска.</summary>
    public ICommand LaterCommand { get; }

    /// <summary>Нашлась новая версия при проверке в фоне (и её не просили пропустить) — для уведомления в трее.</summary>
    public event Action<UpdateInfo>? UpdateFound;

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

    /// <summary>Напоминание на главной: есть новая версия, её не пропустили и не закрыли.</summary>
    public bool ShowBanner => _available is not null && !_bannerClosed && !IsSkipped(_available);

    public string BannerTitle => _available is null ? "" : $"Вышла версия {_available.Version.ToString(3)}";

    /// <summary>Коротко, что нового: первые строки описания выпуска.</summary>
    public string BannerText
    {
        get
        {
            if (_available is null)
                return "";
            string notes = string.Join(" ", _available.Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith('#'))
                .Select(line => line.TrimStart('-', '*', ' ')));
            if (notes.Length > 240)
                notes = notes[..240].TrimEnd() + "…";
            return notes.Length > 0 ? notes : "Обновление ставится само: программа закроется и через минуту откроется уже новой.";
        }
    }

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

    /// <summary>
    /// Тихая проверка вскоре после запуска и потом раз в 12 часов (программа часто живёт в трее неделями),
    /// если проверка включена и адрес задан.
    /// </summary>
    public async Task CheckOnStartupAsync()
    {
        if (!IsConfigured)
            return;
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (_autoCheck && _available is null && !_busy)
                await CheckAsync(silent: true);
            await Task.Delay(TimeSpan.FromHours(12));
        }
    }

    private static bool IsSkipped(UpdateInfo info) =>
        string.Equals(AppSettings.Load().SkippedVersion, info.Version.ToString(3), StringComparison.Ordinal);

    private void Skip()
    {
        if (_available is null)
            return;
        string version = _available.Version.ToString(3);
        AppSettings.Update(s => s.SkippedVersion = version);
        AppLog.Info($"Обновления: версию {version} попросили пропустить.");
        OnPropertyChanged(nameof(ShowBanner));
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
            if (silent && _available is not null && !IsSkipped(_available))
                UpdateFound?.Invoke(_available);
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
            OnPropertyChanged(nameof(ShowBanner));
            OnPropertyChanged(nameof(BannerTitle));
            OnPropertyChanged(nameof(BannerText));
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
