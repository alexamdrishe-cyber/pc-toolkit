using System.Text.Json;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Settings;

/// <summary>Настройки пользователя в %LocalAppData%\PcToolkit\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "settings.json");

    /// <summary>Предложение установить драйвер PawnIO уже показывалось (чтобы не спрашивать при каждом запуске).</summary>
    public bool DriverOfferShown { get; set; }

    /// <summary>Оверлей с FPS во всех играх (кроме этого, оверлей включают профили игр).</summary>
    public bool OverlayEnabled { get; set; }

    /// <summary>Угол экрана: TopLeft, TopRight, BottomLeft, BottomRight.</summary>
    public string OverlayCorner { get; set; } = "TopLeft";

    public bool OverlayShowSensors { get; set; } = true;

    public bool OverlayShowGraph { get; set; } = true;

    /// <summary>Строка «1% low · время кадра» под FPS.</summary>
    public bool OverlayShowDetails { get; set; } = true;

    /// <summary>Строка с именем процесса игры внизу оверлея.</summary>
    public bool OverlayShowProcess { get; set; } = true;

    /// <summary>Размер оверлея: 0.8, 1 или 1.25.</summary>
    public double OverlayScale { get; set; } = 1;

    /// <summary>Плотность фона оверлея, 0–1.</summary>
    public double OverlayOpacity { get; set; } = 0.78;

    /// <summary>Длительность записи FPS вручную, секунды; 0 — до остановки.</summary>
    public int RecordingSeconds { get; set; } = 60;

    /// <summary>Крестик окна сворачивает приложение в трей: профили игр и оверлей продолжают работать.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Подсказка «приложение работает в трее» уже показывалась.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>Уведомлять в трее о проблемах: сбои, перегрев, здоровье дисков, место на диске.</summary>
    public bool ProblemNotifications { get; set; } = true;

    /// <summary>Когда журнал сбоев проверялся в прошлый раз: уведомления только о том, что случилось после.</summary>
    public DateTime? LastCrashCheck { get; set; }

    /// <summary>Тема оформления: dark, light или system (как в Windows).</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Боковое меню свёрнуто в полоску со значками (Ctrl+B).</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>Открытый вид мониторинга: overview, table, graphs.</summary>
    public string MonitoringTab { get; set; } = "overview";

    /// <summary>Окно графиков на вкладке «Графики», секунды: 60, 300 или 1800.</summary>
    public int GraphWindowSeconds { get; set; } = 300;

    /// <summary>Датчики, закреплённые на общем графике в таблице «Все датчики».</summary>
    public List<string> MonitorPins { get; set; } = new();

    /// <summary>Как часто обновлять датчики, мс: 250 (4 раза в секунду), 500 или 1000.</summary>
    public int SensorIntervalMs { get; set; } = 250;

    /// <summary>Интервал опроса из настроек, приведённый к одному из допустимых значений.</summary>
    public static TimeSpan SensorInterval(int milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds switch { <= 250 => 250, <= 500 => 500, _ => 1000 });

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать настройки, используются значения по умолчанию.", ex);
        }

        return new AppSettings();
    }

    /// <summary>Прочитать свежие настройки, изменить и сохранить — так разные экраны не затирают изменения друг друга.</summary>
    public static AppSettings Update(Action<AppSettings> change)
    {
        lock (Gate)
        {
            AppSettings settings = Load();
            change(settings);
            settings.Save();
            return settings;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить настройки.", ex);
        }
    }
}
