using System.Collections.ObjectModel;
using System.Net;
using System.Windows.Input;
using PcToolkit.Core;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Network;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Строка результата проверки сети.</summary>
public sealed class NetworkItemViewModel
{
    public NetworkItemViewModel(NetworkItem item)
    {
        Title = item.Title;
        Value = item.Value;
        Level = item.Level;
        What = item.What;
        AdviceText = item.Advice is null ? null : "Что делать: " + item.Advice;
    }

    public string Title { get; }

    public string Value { get; }

    public HealthLevel Level { get; }

    public string What { get; }

    public string? AdviceText { get; }
}

/// <summary>Строка сравнения DNS.</summary>
public sealed record DnsRow(string Name, string Address, string Time, bool IsCurrent, bool IsBest);

/// <summary>
/// Экран «Сеть»: подключение (кабель или Wi-Fi, сигнал), пинг до роутера и до интернета с джиттером и потерями,
/// сравнение скорости DNS. Только измеряет — ничего не меняет.
/// </summary>
public sealed class NetworkViewModel : ObservableObject
{
    private const int PingCount = 60;
    private static readonly TimeSpan PingInterval = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _cancel;
    private bool _isRunning;
    private bool _loaded;
    private string _connectionTitle = "Подключение ещё не проверялось";
    private string _connectionDetails = "";
    private string _summary = "";
    private HealthLevel _level = HealthLevel.Good;
    private string _progressText = "";
    private double _progress;
    private string _dnsAdvice = "";
    private ConnectionInfo? _connection;

    public NetworkViewModel()
    {
        RunCommand = new RelayCommand(() => _ = RunAsync(), () => !IsRunning);
        CompareDnsCommand = new RelayCommand(() => _ = CompareDnsAsync(), () => !IsRunning);
        StopCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsRunning);
    }

    public ICommand RunCommand { get; }

    public ICommand CompareDnsCommand { get; }

    public ICommand StopCommand { get; }

    public ObservableCollection<NetworkItemViewModel> Items { get; } = new();

    public ObservableCollection<DnsRow> DnsRows { get; } = new();

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
                RelayCommand.Refresh();
        }
    }

    public string ConnectionTitle
    {
        get => _connectionTitle;
        private set => SetProperty(ref _connectionTitle, value);
    }

    public string ConnectionDetails
    {
        get => _connectionDetails;
        private set => SetProperty(ref _connectionDetails, value);
    }

    public string Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value))
                OnPropertyChanged(nameof(HasSummary));
        }
    }

    public bool HasSummary => _summary.Length > 0;

    public HealthLevel Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>0–1.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string DnsAdvice
    {
        get => _dnsAdvice;
        private set
        {
            if (SetProperty(ref _dnsAdvice, value))
                OnPropertyChanged(nameof(HasDns));
        }
    }

    public bool HasDns => _dnsAdvice.Length > 0;

    /// <summary>Проверка скорости интернета: ближайший и далёкий сервер.</summary>
    public SpeedTestViewModel Speed { get; } = new();

    /// <summary>Трассировка маршрута с потерями по узлам.</summary>
    public TracerouteViewModel Trace { get; } = new();

    public void EnsureLoaded()
    {
        if (_loaded)
            return;

        _ = Speed.LoadServersAsync();
        _loaded = true;
        _ = RefreshConnectionAsync();
    }

    private async Task RefreshConnectionAsync()
    {
        _connection = await Task.Run(NetworkCheck.ReadConnection);
        if (_connection is not { } c)
        {
            ConnectionTitle = "Нет подключения к сети";
            ConnectionDetails = "Не найдено ни одного подключения с выходом в интернет.";
            return;
        }

        string speed = c.SpeedBitsPerSecond >= 1_000_000_000
            ? $"{c.SpeedBitsPerSecond / 1e9:0.#} Гбит/с"
            : $"{c.SpeedBitsPerSecond / 1e6:0} Мбит/с";
        if (c.IsWireless)
        {
            WifiInfo? wifi = c.Wifi;
            ConnectionTitle = wifi is null ? "Wi-Fi" : $"Wi-Fi «{wifi.Ssid}» — сигнал {wifi.SignalPercent} %";
            var parts = new List<string>();
            if (wifi is not null)
            {
                if (wifi.Standard.Length > 0)
                    parts.Add(wifi.Standard);
                if (wifi.Band.Length > 0)
                    parts.Add($"{wifi.Band}, канал {wifi.Channel}");
                parts.Add($"скорость канала {wifi.RxMbps} Мбит/с");
            }
            else
            {
                parts.Add(speed);
            }

            parts.Add(c.Description);
            ConnectionDetails = string.Join(" · ", parts) + ". " + WifiHint(wifi);
        }
        else
        {
            ConnectionTitle = $"Кабель (Ethernet) — {speed}";
            ConnectionDetails = c.Description + (c.SpeedBitsPerSecond is > 0 and < 1_000_000_000
                ? ". Скорость порта ниже 1 Гбит/с: если тариф быстрее, проверьте кабель (нужна категория 5e или выше) и порт роутера."
                : ". Для игр кабель — лучший вариант.");
        }
    }

    private static string WifiHint(WifiInfo? wifi) => wifi switch
    {
        null => "Для игр кабель надёжнее Wi-Fi.",
        { SignalPercent: < 50 } => "Сигнал слабый: в играх возможны скачки задержки. Поставьте компьютер ближе к роутеру или подключитесь кабелем.",
        { Channel: > 0 and <= 14 } => "Сеть 2,4 ГГц часто перегружена соседними роутерами; если роутер умеет 5 ГГц — для игр лучше она.",
        _ => "Для игр кабель всё равно надёжнее Wi-Fi.",
    };

    public async Task RunAsync()
    {
        if (IsRunning)
            return;

        IsRunning = true;
        _cancel = new CancellationTokenSource();
        Items.Clear();
        Summary = "";
        try
        {
            await RefreshConnectionAsync();
            bool wireless = _connection?.IsWireless == true;
            var targets = new List<(string Name, IPAddress Address, bool Local)>();
            if (_connection?.Gateway is { } gateway)
                targets.Add(("Роутер", gateway, true));
            targets.AddRange(NetworkCheck.InternetTargets.Select(t => (t.Name, t.Address, false)));

            // Все адреса пингуются одновременно: так проверка идёт 15 секунд, а не 45, и условия у всех одинаковые.
            int[] done = new int[targets.Count];
            int total = targets.Count * PingCount;
            ProgressText = "Измеряю задержку…";
            Progress = 0;
            Task<PingStats>[] runs = targets.Select((t, index) => NetworkCheck.PingAsync(t.Name, t.Address, PingCount, PingInterval, _cancel.Token,
                count => App.Current?.Dispatcher.InvokeAsync(() =>
                {
                    done[index] = count;
                    int sum = done.Sum();
                    Progress = (double)sum / total;
                    ProgressText = $"Измеряю задержку… {sum * 100 / total} %";
                }))).ToArray();
            PingStats[] results = await Task.WhenAll(runs);

            PingStats? router = null;
            var internet = new List<PingStats>();
            for (int i = 0; i < targets.Count; i++)
            {
                Items.Add(new NetworkItemViewModel(NetworkCheck.Explain(results[i], targets[i].Local, wireless)));
                if (targets[i].Local)
                    router = results[i];
                else
                    internet.Add(results[i]);
            }

            (HealthLevel level, string summary) = NetworkCheck.Summarize(router, internet, wireless);
            Level = level;
            Summary = summary;
            ProgressText = $"Проверено в {DateTime.Now:HH:mm}";
            AppLog.Info("Проверка сети: " + string.Join("; ", results.Select(r =>
                $"{r.Name} {r.Address}: {r.AvgMs:0.#} мс, джиттер {r.JitterMs:0.#}, потери {r.LossPercent:0.#} %")) + ". " + summary);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Проверка остановлена";
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось проверить сеть.", ex);
            ProgressText = "Не удалось проверить сеть: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            Progress = 0;
        }
    }

    public async Task CompareDnsAsync()
    {
        if (IsRunning)
            return;

        IsRunning = true;
        _cancel = new CancellationTokenSource();
        DnsRows.Clear();
        DnsAdvice = "";
        try
        {
            if (_connection is null)
                await RefreshConnectionAsync();

            ProgressText = "Сравниваю DNS…";
            List<DnsResult> results = await NetworkCheck.CompareDnsAsync(_connection?.DnsServers ?? Array.Empty<IPAddress>(), _cancel.Token,
                (done, total) => App.Current?.Dispatcher.InvokeAsync(() =>
                {
                    Progress = (double)done / total;
                    ProgressText = $"Сравниваю DNS… {done * 100 / total} %";
                }));

            DnsResult? best = results.FirstOrDefault(r => r.MedianMs is not null);
            foreach (DnsResult r in results)
            {
                string time = r.MedianMs is double ms
                    ? $"{ms:0} мс" + (r.Answered < r.Asked ? $" (ответил на {r.Answered} из {r.Asked})" : "")
                    : "не отвечает";
                DnsRows.Add(new DnsRow(r.Name + (r.IsCurrent ? " — сейчас" : ""), r.Address, time, r.IsCurrent, ReferenceEquals(r, best)));
            }

            DnsAdvice = NetworkCheck.DnsAdvice(results);
            ProgressText = $"DNS проверены в {DateTime.Now:HH:mm}";
            AppLog.Info("Сравнение DNS: " + string.Join("; ", results.Select(r => $"{r.Name} {r.Address}: {r.MedianMs:0.#} мс")));
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Проверка остановлена";
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось сравнить DNS.", ex);
            ProgressText = "Не удалось сравнить DNS: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            Progress = 0;
        }
    }
}
