namespace PcToolkit.Core.Startup;

/// <summary>Оценка известной программы: насколько тормозит включение, что посоветовать и почему.</summary>
public sealed record StartupRule(StartupImpact Impact, StartupAdvice Advice, string Note)
{
    public static StartupRule Unknown { get; } = new(StartupImpact.Unknown, StartupAdvice.Optional, "");
}

/// <summary>
/// Известные программы автозагрузки. Сопоставление — по имени файла программы (без .exe) и по имени записи.
/// Оценка «замедляет запуск» — по тому, что программа делает при старте: лаунчеры игр и мессенджеры грузят
/// сеть и диск, мелкие помощники драйверов — почти ничего.
/// </summary>
public static class StartupCatalog
{
    private static readonly (string[] Keys, StartupRule Rule)[] Rules =
    {
        // Лаунчеры игр: при старте проверяют обновления, качают их и держат сеть
        (new[] { "steam" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Лаунчер игр: при включении проверяет и качает обновления. Запустится сам, когда откроете игру.")),
        (new[] { "epicgameslauncher" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Лаунчер игр: при включении проверяет и качает обновления.")),
        (new[] { "eadesktop", "origin" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Лаунчер игр EA: можно запускать, когда нужен.")),
        (new[] { "battle.net", "battlenet" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Лаунчер Blizzard: можно запускать, когда нужен.")),
        (new[] { "upc", "ubisoftconnect" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Лаунчер Ubisoft: можно запускать, когда нужен.")),
        (new[] { "galaxyclient" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Лаунчер GOG: можно запускать, когда нужен.")),
        (new[] { "lgc", "lestagamecenter" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Lesta Game Center: можно запускать, когда нужен.")),
        (new[] { "wgc", "wargaminggamecenter" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Wargaming Game Center: можно запускать, когда нужен.")),
        (new[] { "riotclientservices", "riotclient" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Лаунчер Riot: можно запускать, когда нужен.")),
        (new[] { "vkplay", "gamecenter" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Лаунчер VK Play: можно запускать, когда нужен.")),

        // Мессенджеры и общение
        (new[] { "discord" }, new(StartupImpact.High, StartupAdvice.Optional, "Мессенджер: заметно тормозит включение. Оставьте, если нужен сразу.")),
        (new[] { "teams", "ms-teams" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Microsoft Teams: тяжёлый при запуске, нужен обычно только для работы.")),
        (new[] { "skype" }, new(StartupImpact.Medium, StartupAdvice.CanDisable, "Skype: можно запускать, когда нужен.")),
        (new[] { "telegram" }, new(StartupImpact.Medium, StartupAdvice.Optional, "Мессенджер. Оставьте, если нужен сразу.")),
        (new[] { "whatsapp" }, new(StartupImpact.Medium, StartupAdvice.Optional, "Мессенджер. Оставьте, если нужен сразу.")),
        (new[] { "viber" }, new(StartupImpact.Medium, StartupAdvice.Optional, "Мессенджер. Оставьте, если нужен сразу.")),
        (new[] { "zoom" }, new(StartupImpact.Medium, StartupAdvice.CanDisable, "Zoom: нужен только на время звонка.")),
        (new[] { "teamspeak", "ts3client_win64" }, new(StartupImpact.Medium, StartupAdvice.CanDisable, "TeamSpeak: можно запускать, когда нужен.")),

        // Облака, музыка, браузеры в фоне
        (new[] { "onedrive" }, new(StartupImpact.High, StartupAdvice.Optional, "OneDrive синхронизирует файлы при включении. Не пользуетесь — выключите.")),
        (new[] { "dropbox" }, new(StartupImpact.Medium, StartupAdvice.Optional, "Синхронизирует файлы при включении.")),
        (new[] { "googledrivefs" }, new(StartupImpact.Medium, StartupAdvice.Optional, "Синхронизирует файлы при включении.")),
        (new[] { "yandexdisk2", "yandexdisk" }, new(StartupImpact.Medium, StartupAdvice.Optional, "Синхронизирует файлы при включении.")),
        (new[] { "spotify" }, new(StartupImpact.Medium, StartupAdvice.CanDisable, "Музыка: можно запускать, когда нужна.")),
        (new[] { "yandexmusic" }, new(StartupImpact.Medium, StartupAdvice.CanDisable, "Музыка: можно запускать, когда нужна.")),
        (new[] { "msedge", "microsoftedgeautolaunch" }, new(StartupImpact.Medium, StartupAdvice.CanDisable, "Браузер Edge заранее загружается в фоне — тратит память, даже если вы им не пользуетесь.")),
        (new[] { "chrome" }, new(StartupImpact.Medium, StartupAdvice.CanDisable, "Браузер в фоне тратит память ещё до того, как вы его открыли.")),
        (new[] { "browser", "yandex" }, new(StartupImpact.Medium, StartupAdvice.CanDisable, "Браузер в фоне тратит память ещё до того, как вы его открыли.")),
        (new[] { "opera", "opera_autoupdate" }, new(StartupImpact.Medium, StartupAdvice.CanDisable, "Браузер в фоне тратит память ещё до того, как вы его открыли.")),

        // Торренты и «ускорители»
        (new[] { "utorrent", "bittorrent", "qbittorrent", "utweb" }, new(StartupImpact.Medium, StartupAdvice.Optional, "Торрент-клиент: раздаёт в фоне и занимает сеть. Не нужен сразу — выключите.")),
        (new[] { "ccleaner", "ccleaner64", "ccleanerbrowser" }, new(StartupImpact.Medium, StartupAdvice.CanDisable, "Чистильщик в фоне не нужен: очистку можно запускать вручную.")),
        (new[] { "driverbooster", "iobitupdater", "advancedsystemcare" }, new(StartupImpact.Medium, StartupAdvice.CanDisable, "Утилита IObit в фоне не нужна.")),

        // Обои, оверлеи, периферия
        (new[] { "wallpaper32", "wallpaper64", "wallpaperengine" }, new(StartupImpact.Medium, StartupAdvice.Optional, "Живые обои постоянно нагружают видеокарту (и греют её в простое).")),
        (new[] { "rtss", "msiafterburner" }, new(StartupImpact.Low, StartupAdvice.Optional, "Оверлей и разгон видеокарты. Оставьте, если пользуетесь.")),
        (new[] { "razer synapse", "razer synapse 3", "rzsynapse", "razercentral" }, new(StartupImpact.High, StartupAdvice.Optional, "Настройки мыши и клавиатуры Razer: без неё часть настроек не применится.")),
        (new[] { "icue" }, new(StartupImpact.High, StartupAdvice.Optional, "Corsair iCUE: подсветка, кнопки и вентиляторы устройств Corsair.")),
        (new[] { "armourycrate", "armourycrate.usersessionhelper" }, new(StartupImpact.High, StartupAdvice.Optional, "ASUS Armoury Crate: подсветка и настройки платы ASUS.")),
        (new[] { "lghub", "lghub_agent" }, new(StartupImpact.Medium, StartupAdvice.Optional, "Logitech G HUB: настройки мыши и клавиатуры Logitech.")),
        (new[] { "steelseriesgg", "steelseriesengine" }, new(StartupImpact.Medium, StartupAdvice.Optional, "Настройки устройств SteelSeries.")),
        (new[] { "nvidia geforce experience", "nvidia share", "nvcontainer" }, new(StartupImpact.Medium, StartupAdvice.Optional, "Оверлей и запись NVIDIA. Драйвер работает и без неё.")),
        (new[] { "radeonsoftware", "cncmd", "amdrsserv" }, new(StartupImpact.Low, StartupAdvice.Optional, "Панель драйвера AMD: горячие клавиши и оверлей Radeon.")),
        (new[] { "amdnoisesuppression" }, new(StartupImpact.Low, StartupAdvice.Optional, "Шумоподавление микрофона AMD. Не пользуетесь — выключите.")),
        (new[] { "adobe creative cloud", "creative cloud", "adobegcclient", "ccxprocess" }, new(StartupImpact.High, StartupAdvice.CanDisable, "Adobe Creative Cloud: тяжёлый при запуске, запускается сам с программами Adobe.")),
        (new[] { "acrotray", "adobearm" }, new(StartupImpact.Low, StartupAdvice.CanDisable, "Помощник Adobe Acrobat: для чтения PDF не нужен.")),

        // Системное и драйверы: лучше оставить
        (new[] { "securityhealthsystray", "securityhealth" }, new(StartupImpact.Low, StartupAdvice.Keep, "Значок «Безопасность Windows»: предупреждения антивируса.")),
        (new[] { "rtkauduservice64", "rtkaudservice", "rthdvcpl", "rtkngui64", "ravcpl64" }, new(StartupImpact.Low, StartupAdvice.Keep, "Звук Realtek: без него могут не работать разъёмы на корпусе.")),
        (new[] { "igfxtray", "igfxem", "igfxhk" }, new(StartupImpact.Low, StartupAdvice.Keep, "Драйвер видео Intel.")),
        (new[] { "syntpenh", "synaptics", "etdctrl" }, new(StartupImpact.Low, StartupAdvice.Keep, "Тачпад ноутбука: жесты и прокрутка.")),
        (new[] { "ctfmon" }, new(StartupImpact.Low, StartupAdvice.Keep, "Переключение языков ввода Windows.")),
        (new[] { "pctoolkit", "pc toolkit" }, new(StartupImpact.Low, StartupAdvice.Keep, "PC Toolkit: вентиляторы, профили игр и уведомления, пока он запущен в трее.")),
        (new[] { "punto", "puntoswitcher" }, new(StartupImpact.Low, StartupAdvice.Optional, "Переключатель раскладки Punto Switcher.")),
    };

    /// <summary>Правило для программы: по имени файла, по имени записи и по тексту команды.</summary>
    public static StartupRule Match(string name, string filePath, string command, string publisher)
    {
        string file = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        string entry = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        string text = (command + " " + name).ToLowerInvariant();

        foreach ((string[] keys, StartupRule rule) in Rules)
        {
            if (keys.Any(k => k == file || k == entry))
                return rule;
        }

        // Discord, Teams и т. п. запускаются через Update.exe --processStart Discord.exe
        foreach ((string[] keys, StartupRule rule) in Rules)
        {
            if (keys.Any(k => k.Length >= 5 && text.Contains("--processstart " + k, StringComparison.Ordinal)))
                return rule;
        }

        // Системные файлы Windows — оставить
        if (filePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) &&
            publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            return new StartupRule(StartupImpact.Low, StartupAdvice.Keep, "Часть Windows.");

        return StartupRule.Unknown;
    }
}
