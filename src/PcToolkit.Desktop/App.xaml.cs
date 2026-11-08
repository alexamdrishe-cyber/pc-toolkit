using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Service;
using PcToolkit.Core.Settings;
using PcToolkit.Desktop.Services;
using PcToolkit.Desktop.ViewModels;
using PcToolkit.Desktop.Views;

namespace PcToolkit.Desktop;

public partial class App : Application
{
    private SingleInstance? _instance;
    private string? _notificationArea;
    private MainViewModel? _main;
    private TrayIcon? _tray;
    private GlobalHotkeys? _hotkeys;
    private OverlayWindow? _overlay;
    private bool _exiting;
    private bool _started;

    private static readonly DateTime ProcessStart = GetProcessStart();

    /// <summary>Сколько миллисекунд прошло с запуска процесса — для строк журнала «окно на экране через N мс».</summary>
    public static long SinceStartMs => (long)(DateTime.Now - ProcessStart).TotalMilliseconds;

    private static DateTime GetProcessStart()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.StartTime;
        }
        catch
        {
            return DateTime.Now;
        }
    }

    public App()
    {
        // Обработчики ставятся раньше всего, ещё до загрузки XAML: любая ошибка при запуске попадёт в журнал
        // и покажется понятным окном, а не системным «Unknown Hard Error».
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ReportCrash(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Необработанное исключение в задаче.", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Info("Старт: " + StartupDescription());

        // Служебные запуски с правами администратора: сделать одно действие со службой и выйти, без окна.
        if (LaunchArguments.Has(e.Args, LaunchArguments.InstallService))
        {
            Shutdown(ServiceInstaller.Install());
            return;
        }

        if (LaunchArguments.Has(e.Args, LaunchArguments.UninstallService))
        {
            Shutdown(ServiceInstaller.Uninstall());
            return;
        }

        if (LaunchArguments.ValueAfter(e.Args, LaunchArguments.RemoveLeftovers) is { } request)
        {
            Shutdown(PcToolkit.Core.Apps.LeftoverRemover.ExecuteRequest(request));
            return;
        }

        bool restarted = LaunchArguments.Has(e.Args, LaunchArguments.Restarted);
        bool installDriver = LaunchArguments.Has(e.Args, LaunchArguments.InstallDriver);
        bool repairDriver = LaunchArguments.Has(e.Args, LaunchArguments.RepairDriver);
        _instance = SingleInstance.Acquire(restarted ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        if (!_instance.IsFirstInstance)
        {
            AppLog.Info("Приложение уже запущено: показываю открытое окно.");
            SingleInstance.ActivateExisting();
            Shutdown();
            return;
        }

        AppLog.Info($"Запуск PC Toolkit {typeof(App).Assembly.GetName().Version}.");
        BindingErrorListener.Install();
#if DEBUG
        ClickDiagnostics.Install();
#endif

        // Если установлена служба, датчики и системные настройки идут через неё: права администратора не нужны.
        var service = new ServiceClient();
        // Проверка состояния службы мгновенная; соединяемся, только если она запущена, чтобы не ждать зря.
        bool noService = LaunchArguments.Has(e.Args, LaunchArguments.NoService);
        ServiceInfo? serviceInfo = !noService && ServiceInstaller.QueryState() == ServiceInstallState.Running ? service.Hello() : null;
        bool useService = serviceInfo is { IsCompatible: true } && !EnvironmentIsElevated();
        if (noService)
            AppLog.Info("Запуск с --no-service: датчики читаются в самом приложении.");
        AppLog.Info(serviceInfo is null
            ? "Служба PC Toolkit не найдена."
            : $"Служба PC Toolkit отвечает (протокол {serviceInfo.Version}, {(serviceInfo.IsCurrent ? "актуальная" : "устаревшая")} сборка).");

        ISensorSource source = useService
            ? new ServiceSensorSource(service)
            : new LhmSensorSource(TimeSpan.FromSeconds(1));
        _main = new MainViewModel(source, Dispatcher, serviceInfo is { IsCompatible: true } ? service : null, serviceInfo);
        var window = new MainWindow { DataContext = _main, Icon = AppIconFactory.Render(64) };
        AppLog.Info($"Интерфейс собран через {SinceStartMs} мс после запуска.");
        window.ContentRendered += (_, _) => AppLog.Info($"Окно на экране через {SinceStartMs} мс после запуска.");
        MainWindow = window;
        window.Closing += OnMainWindowClosing;
        SessionEnding += (_, _) => _exiting = true;

        StartBackgroundServices();
        _instance.ListenForActivation(() => Dispatcher.InvokeAsync(ShowMainWindow));
        _instance.ListenForExit(() => Dispatcher.InvokeAsync(() =>
        {
            AppLog.Info("Установщик попросил закрыть приложение.");
            ExitApplication();
        }));

        // Автозапуск с Windows: только значок в трее, окно — по щелчку.
        if (LaunchArguments.Has(e.Args, LaunchArguments.Tray) && _tray is not null)
            AppLog.Info("Запуск в трее (автозапуск).");
        else
            window.Show();

        _ = _main.StartAsync(installDriver, repairDriver);
        _started = true;
    }

    /// <summary>Система, путь и вид сборки — первая строка журнала, чтобы по журналу с чужого компьютера было понятно, где он.</summary>
    private static string StartupDescription()
    {
        try
        {
            bool singleFile = !File.Exists(Path.Combine(AppContext.BaseDirectory, "PcToolkit.dll"));
            return $"{PcToolkit.Core.Platform.OsInfo.FriendlyName}, {RuntimeInformation.OSArchitecture}, " +
                   $"{RuntimeInformation.FrameworkDescription}, {(singleFile ? "один файл" : "папка")}, " +
                   $"права администратора: {(EnvironmentIsElevated() ? "да" : "нет")}, {Environment.ProcessPath}";
        }
        catch (Exception ex)
        {
            return "не удалось описать систему: " + ex.Message;
        }
    }

    /// <summary>
    /// Ошибка, после которой процесс завершится (необработанное исключение в фоновом потоке):
    /// записать в журнал и показать текст. Окно системное — работает из любого потока и даже без WPF.
    /// </summary>
    private static void ReportCrash(Exception? exception)
    {
        AppLog.Error("Необработанное исключение — приложение будет закрыто.", exception);
        try
        {
            string text = $"PC Toolkit аварийно завершает работу.\n\n{exception?.GetType().Name}: {exception?.Message}\n\n" +
                          $"Подробности записаны в журнал:\n{AppLog.LogFile}";
            MessageBoxW(IntPtr.Zero, text, "PC Toolkit — ошибка", MbOk | MbIconError | MbTopMost | MbSetForeground);
        }
        catch
        {
            // Показать окно не вышло — остаётся запись в журнале.
        }
    }

    private const uint MbOk = 0x0;
    private const uint MbIconError = 0x10;
    private const uint MbSetForeground = 0x10000;
    private const uint MbTopMost = 0x40000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    /// <summary>Трей, глобальные клавиши и окно оверлея: работают, даже когда главное окно скрыто.</summary>
    private void StartBackgroundServices()
    {
        GamesViewModel games = _main!.Games;
        try
        {
            _overlay = new OverlayWindow(games.Overlay);

            _tray = new TrayIcon("PC Toolkit", () => new[]
            {
                new TrayMenuItem("Открыть PC Toolkit", ShowMainWindow),
                TrayMenuItem.Separator,
                new TrayMenuItem("Оверлей с FPS\tCtrl+Shift+F10", games.ToggleOverlay, games.OverlayActive),
                new TrayMenuItem(games.IsRecording ? "Остановить запись FPS\tCtrl+Shift+F11" : "Записать FPS\tCtrl+Shift+F11",
                    games.ToggleRecording, Enabled: games.CanCountFrames || games.IsRecording),
                TrayMenuItem.Separator,
                new TrayMenuItem("Выход", ExitApplication),
            });
            _tray.Activated += (_, _) => ShowMainWindow();
            games.Notification += (_, text) =>
            {
                _notificationArea = null;
                _tray?.ShowBalloon("PC Toolkit", text);
            };

            // Сбои, перегрев, диски, место: щелчок по уведомлению открывает экран с подробностями.
            _main.HealthNotification += notification => Dispatcher.InvokeAsync(() =>
            {
                _notificationArea = notification.Area;
                _tray?.ShowBalloon(notification.Title, notification.Text);
            });
            _tray.BalloonClicked += (_, _) =>
            {
                ShowMainWindow();
                if (_notificationArea is { } area)
                    _main?.OpenArea(area);
            };

            _hotkeys = new GlobalHotkeys();
            var busy = new List<string>();
            if (!_hotkeys.Register(ModifierKeys.Control | ModifierKeys.Shift, Key.F10, games.ToggleOverlay))
                busy.Add("Ctrl+Shift+F10");
            if (!_hotkeys.Register(ModifierKeys.Control | ModifierKeys.Shift, Key.F11, games.ToggleRecording))
                busy.Add("Ctrl+Shift+F11");
            if (busy.Count > 0)
                games.HotkeyWarning = $"Сочетание {string.Join(" и ", busy)} уже занято другой программой. Пользуйтесь кнопками здесь или меню значка в трее.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось запустить трей, горячие клавиши или оверлей.", ex);
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow is not { } window || _exiting)
            return;

        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _tray is null || _main is null || !_main.Games.CloseToTray)
            return;

        // Крестик прячет окно в трей: профили игр и оверлей продолжают работать.
        e.Cancel = true;
        try
        {
            MainWindow?.Hide();
        }
        catch (InvalidOperationException)
        {
            return; // окно уже закрывается вместе с приложением
        }

        AppSettings settings = AppSettings.Load();
        if (!settings.TrayHintShown)
        {
            _tray.ShowBalloon("PC Toolkit работает в трее",
                "Профили игр и оверлей продолжают работать. Открыть — щелчок по значку, выйти — правой кнопкой → «Выход».");
            AppSettings.Update(s => s.TrayHintShown = true);
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        Shutdown();
    }

    /// <summary>Закрыть приложение совсем (а не свернуть в трей): выход, перезапуск с правами администратора.</summary>
    public static void Quit()
    {
        if (Current is App app)
            app._exiting = true;
        Current?.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _hotkeys?.Dispose();
        _tray?.Dispose();
        try
        {
            _overlay?.Close();
        }
        catch (InvalidOperationException)
        {
            // Окна уже закрыты при выходе.
        }

        if (_main is not null)
        {
            _main.Dispose();
            AppLog.Info("Приложение закрыто.");
        }

        // Освобождаем последним: после этого может стартовать новая копия (например, с правами администратора).
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static bool EnvironmentIsElevated() => PcToolkit.Core.Platform.EnvironmentStatus.Detect().IsElevated;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        bool duringStartup = !_started;
        AppLog.Error(duringStartup ? "Ошибка при запуске." : "Ошибка в интерфейсе.", e.Exception);
        MessageBoxW(IntPtr.Zero,
            (duringStartup ? "PC Toolkit не смог запуститься.\n\n" : "Произошла ошибка.\n\n") +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\nПодробности записаны в журнал:\n{AppLog.LogFile}",
            "PC Toolkit — ошибка",
            MbOk | MbIconError | MbTopMost | MbSetForeground);
        e.Handled = true;

        // Если сломался сам запуск, окна нет — без этого процесс остался бы висеть невидимым.
        if (duringStartup)
        {
            _exiting = true;
            Shutdown(1);
        }
    }
}
