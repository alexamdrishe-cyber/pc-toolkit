using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows.Input;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Network;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Итог проверки скорости для одной стороны: «рядом» или «далеко».</summary>
public sealed class SpeedResultViewModel : ObservableObject
{
    private SpeedResult? _result;

    public SpeedResultViewModel(string title) => Title = title;

    public string Title { get; }

    public bool HasResult => _result is not null;

    public string Server => _result?.Server.Name ?? "";

    public string Download => _result is null ? "—" : Mbps(_result.DownloadMbps);

    public string Upload => _result is null ? "—" : Mbps(_result.UploadMbps);

    public string Ping => _result is null ? "—" : $"{_result.PingMs:0} мс";

    public string Jitter => _result is null ? "—" : $"{_result.JitterMs:0.0} мс";

    internal void Set(SpeedResult result)
    {
        _result = result;
        OnPropertyChanged(string.Empty);
    }

    internal static string Mbps(double value) => value >= 100 ? $"{value:0} Мбит/с" : $"{value:0.0} Мбит/с";
}

/// <summary>Проверка скорости: ближайший сервер (по пингу) и выбранный далёкий — рядом друг с другом.</summary>
public sealed class SpeedTestViewModel : ObservableObject
{
    private CancellationTokenSource? _cancel;
    private bool _running;
    private bool _far;
    private string _stage = "";
    private double _fraction;
    private string _error = "";
    private SpeedServer? _farServer;

    public SpeedTestViewModel()
    {
        StartCommand = new RelayCommand(() => _ = RunAsync(), () => !_running && (!_far || _farServer is not null));
        StopCommand = new RelayCommand(() => _cancel?.Cancel(), () => _running);
    }

    public ObservableCollection<SpeedServer> Servers { get; } = new();

    public SpeedResultViewModel Near { get; } = new("Ближайший сервер");

    public SpeedResultViewModel Far { get; } = new("Далёкий сервер");

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public bool IsNear
    {
        get => !_far;
        set
        {
            if (value)
                SetFar(false);
        }
    }

    public bool IsFar
    {
        get => _far;
        set
        {
            if (value)
                SetFar(true);
        }
    }

    public SpeedServer? FarServer
    {
        get => _farServer;
        set
        {
            if (SetProperty(ref _farServer, value))
                RelayCommand.Refresh();
        }
    }

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (SetProperty(ref _running, value))
                RelayCommand.Refresh();
        }
    }

    public string Stage
    {
        get => _stage;
        private set => SetProperty(ref _stage, value);
    }

    public double Fraction
    {
        get => _fraction;
        private set => SetProperty(ref _fraction, value);
    }

    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Загрузить список серверов (при первом открытии раздела).</summary>
    public async Task LoadServersAsync()
    {
        if (Servers.Count > 0)
            return;
        using var test = new SpeedTest();
        IReadOnlyList<SpeedServer> servers = await test.ServersAsync(CancellationToken.None);
        foreach (SpeedServer server in servers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            Servers.Add(server);
        FarServer ??= Servers.FirstOrDefault(s => s.Name.Contains("New York", StringComparison.OrdinalIgnoreCase)) ?? Servers.LastOrDefault();
    }

    private void SetFar(bool far)
    {
        _far = far;
        OnPropertyChanged(nameof(IsNear));
        OnPropertyChanged(nameof(IsFar));
        RelayCommand.Refresh();
    }

    private async Task RunAsync()
    {
        IsRunning = true;
        Error = "";
        _cancel = new CancellationTokenSource();
        bool far = _far;
        try
        {
            using var test = new SpeedTest();
            await LoadServersAsync();
            SpeedServer server;
            if (far)
            {
                server = FarServer ?? throw new InvalidOperationException("Выберите сервер.");
            }
            else
            {
                Stage = "Ищу ближайший сервер…";
                (SpeedServer Server, double PingMs)? nearest = await test.NearestAsync(Servers.ToList(), _cancel.Token);
                server = nearest?.Server ?? throw new HttpRequestException("Ни один сервер не ответил — нет интернета?");
            }

            var progress = new Progress<SpeedProgress>(p =>
            {
                Stage = p.Stage switch
                {
                    "ping" => $"{server.Name}: пинг {p.Value:0} мс",
                    "download" => $"{server.Name}: скачивание {SpeedResultViewModel.Mbps(p.Value)}",
                    _ => $"{server.Name}: отдача {SpeedResultViewModel.Mbps(p.Value)}",
                };
                Fraction = p.Stage switch { "ping" => p.Fraction * 0.1, "download" => 0.1 + p.Fraction * 0.45, _ => 0.55 + p.Fraction * 0.45 };
            });
            SpeedResult result = await test.RunAsync(server, progress, _cancel.Token);
            (far ? Far : Near).Set(result);
            Stage = Traceroute.ActiveTunnel() is { } tunnel ? $"Включён VPN «{tunnel}» — скорость измерена через него." : "";
        }
        catch (OperationCanceledException)
        {
            Stage = "Остановлено.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.IO.IOException)
        {
            Error = ex.Message;
            Stage = "";
            AppLog.Warn("Проверка скорости не удалась.", ex);
        }
        finally
        {
            Fraction = 0;
            IsRunning = false;
            _cancel?.Dispose();
            _cancel = null;
        }
    }
}

/// <summary>Строка трассировки.</summary>
public sealed class TraceHopViewModel : ObservableObject
{
    public TraceHopViewModel(TraceHop hop) => Hop = hop;

    public TraceHop Hop { get; }

    public int Number => Hop.Number;

    public string Host => Hop.Address is null ? "* (узел не отвечает)" : Hop.HostName.Length > 0 ? $"{Hop.HostName}  ({Hop.Address})" : Hop.Address.ToString();

    public string Loss => Hop.Sent == 0 ? "" : $"{Hop.LossPercent:0} %";

    public bool HasLoss => Hop.LossPercent >= 2;

    public string Average => Ms(Hop.Average);

    public string Best => Ms(Hop.Best);

    public string Worst => Ms(Hop.Worst);

    internal void Refresh() => OnPropertyChanged(string.Empty);

    private static string Ms(double? value) => value is double v ? v.ToString(v < 10 ? "0.0" : "0", CultureInfo.GetCultureInfo("ru-RU")) : "—";
}

/// <summary>Трассировка маршрута с потерями по узлам (как WinMTR).</summary>
public sealed class TracerouteViewModel : ObservableObject
{
    private const int Rounds = 15;
    private CancellationTokenSource? _cancel;
    private bool _running;
    private string _target = "8.8.8.8";
    private string _status = "";
    private string _verdictTitle = "";
    private string _verdictDetails = "";
    private HealthLevel _level = HealthLevel.Fact;

    public TracerouteViewModel()
    {
        StartCommand = new RelayCommand(() => _ = RunAsync(), () => !_running && _target.Trim().Length > 0);
        StopCommand = new RelayCommand(() => _cancel?.Cancel(), () => _running);
        PresetCommand = new RelayCommand(host =>
        {
            if (host is string text)
                Target = text;
        });
    }

    public ObservableCollection<TraceHopViewModel> Hops { get; } = new();

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand PresetCommand { get; }

    public string Target
    {
        get => _target;
        set
        {
            if (SetProperty(ref _target, value ?? ""))
                RelayCommand.Refresh();
        }
    }

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (SetProperty(ref _running, value))
                RelayCommand.Refresh();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string VerdictTitle
    {
        get => _verdictTitle;
        private set => SetProperty(ref _verdictTitle, value);
    }

    public string VerdictDetails
    {
        get => _verdictDetails;
        private set => SetProperty(ref _verdictDetails, value);
    }

    /// <summary>Good, Notice, Bad — для цвета итога.</summary>
    public string Level => _level.ToString();

    public bool HasHops => Hops.Count > 0;

    private async Task RunAsync()
    {
        IsRunning = true;
        _cancel = new CancellationTokenSource();
        Hops.Clear();
        VerdictTitle = "";
        VerdictDetails = "";
        OnPropertyChanged(nameof(HasHops));
        try
        {
            string host = Target.Trim();
            Status = $"Ищу адрес {host}…";
            IPAddress target = IPAddress.TryParse(host, out IPAddress? parsed)
                ? parsed
                : (await Dns.GetHostAddressesAsync(host, _cancel.Token)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                  ?? throw new SocketException((int)SocketError.HostNotFound);

            Status = "Ищу узлы маршрута…";
            List<TraceHop> hops = await Traceroute.DiscoverAsync(target, hop =>
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    Hops.Add(new TraceHopViewModel(hop));
                    OnPropertyChanged(nameof(HasHops));
                }), _cancel.Token);

            _ = Traceroute.ResolveNamesAsync(hops).ContinueWith(_ =>
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    foreach (TraceHopViewModel row in Hops)
                        row.Refresh();
                }), TaskScheduler.Default);

            // Узлы, которые не найдены при поиске (хвост), из списка убираем.
            foreach (TraceHopViewModel extra in Hops.Where(h => !hops.Contains(h.Hop)).ToList())
                Hops.Remove(extra);

            for (int round = 1; round <= Rounds; round++)
            {
                Status = $"Проверяю потери: круг {round} из {Rounds}";
                await Traceroute.RoundAsync(target, hops, _cancel.Token);
                foreach (TraceHopViewModel row in Hops)
                    row.Refresh();
                await Task.Delay(400, _cancel.Token);
            }

            TraceVerdict verdict = Traceroute.Judge(hops, Traceroute.ActiveTunnel());
            _level = verdict.Level;
            VerdictTitle = verdict.Title;
            VerdictDetails = verdict.Details;
            Status = "";
            AppLog.Info($"Трассировка до {host}: {verdict.Title}.");
        }
        catch (OperationCanceledException)
        {
            Status = "Остановлено.";
        }
        catch (SocketException)
        {
            Status = $"Адрес «{Target}» не найден.";
        }
        finally
        {
            OnPropertyChanged(nameof(Level));
            IsRunning = false;
            _cancel?.Dispose();
            _cancel = null;
        }
    }
}
