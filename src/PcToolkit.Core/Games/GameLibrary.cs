using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PcToolkit.Core.Apps;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Games;

/// <summary>Установленная игра.</summary>
public sealed class GameEntry
{
    /// <summary>Постоянный ключ: «steam:730», «epic:Fortnite», «gog:1207658924», «app:…», «manual:…».</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Откуда известна игра: Steam, Epic Games, GOG, издатель, «Добавлена вручную».</summary>
    public string Source { get; set; } = "";

    /// <summary>Папка игры: процессы из неё считаются игрой.</summary>
    public string InstallDir { get; set; } = "";

    /// <summary>Главный exe, если известен (для значка и точного определения).</summary>
    public string Executable { get; set; } = "";

    public override string ToString() => Name;
}

/// <summary>
/// Поиск установленных игр: библиотеки Steam (все диски), Epic Games, GOG и программы известных издателей
/// (Lesta, Wargaming, Riot, Blizzard, EA, Ubisoft и др.) из списка установленных программ. Только чтение.
/// </summary>
public static class GameLibrary
{
    // Издатели игр в записях «Программы и компоненты». Лаунчеры и служебное отсекаются по названию ниже.
    private static readonly string[] GamePublishers =
    {
        "Lesta", "Wargaming", "Riot Games", "Blizzard", "Electronic Arts", "Ubisoft", "Rockstar Games", "Roblox",
        "MY.GAMES", "Mail.Ru", "VK Play", "Innova", "4game", "Gaijin", "miHoYo", "COGNOSPHERE", "HoYoverse",
        "Bungie", "Activision", "Bethesda", "CD PROJEKT", "Mojang", "Krafton", "Kuro Games", "Smilegate", "NetEase",
        "Square Enix", "Bandai Namco", "CAPCOM", "SEGA", "Paradox", "Warhorse", "Grinding Gear", "Digital Extremes",
    };

    private static readonly string[] SteamTools = { "Proton", "Steamworks", "SteamVR", "Redistributable", "Steam Linux Runtime" };

    private static readonly string[] NotGames =
    {
        "Launcher", "Game Center", "Игровой центр", "Battle.net", "EA app", "Origin", "Ubisoft Connect", "Uplay",
        "Riot Client", "Riot Vanguard", "Social Club", "Steam", "Epic Games", "GOG GALAXY", "VK Play", "Easy Anti-Cheat",
        "EasyAntiCheat", "BattlEye", "Redistributable", "Runtime", "DirectX", "PhysX", "Uninstall", "Updater", "Driver",
        "Proton", "Steamworks", "SteamVR", "Overlay", "Anti-Cheat", "Agent", "Service",
    };

    /// <summary>Все найденные игры, без повторов (одна папка — одна игра), по алфавиту.</summary>
    public static IReadOnlyList<GameEntry> Discover()
    {
        var games = new List<GameEntry>();
        Collect(games, "Steam", FindSteamGames);
        Collect(games, "Epic Games", FindEpicGames);
        Collect(games, "GOG", FindGogGames);
        Collect(games, "издатели", FindPublisherGames);

        var unique = new List<GameEntry>();
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameEntry game in games)
        {
            string dir = Normalize(game.InstallDir);
            if (dir.Length < 4 || !Directory.Exists(dir) || !dirs.Add(dir))
                continue;

            // Запись издателя, которая указывает внутрь уже найденной папки (или наоборот), — та же игра.
            if (unique.Any(u => ProcessInfo.IsInside(dir + "\\x", u.InstallDir) || ProcessInfo.IsInside(u.InstallDir + "\\x", dir)))
                continue;

            game.InstallDir = dir;
            if (game.Executable.Length == 0)
                game.Executable = GuessExecutable(dir) ?? "";
            unique.Add(game);
        }

        return unique.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Игра по exe, выбранному вручную или найденному среди процессов, выводящих кадры.</summary>
    public static GameEntry FromExecutable(string exePath, string? name = null)
    {
        string dir = Path.GetDirectoryName(exePath) ?? "";
        string? title = name;
        if (string.IsNullOrWhiteSpace(title))
        {
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                title = !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName!
                    : !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription!
                    : Path.GetFileNameWithoutExtension(exePath);
            }
            catch (Exception)
            {
                title = Path.GetFileNameWithoutExtension(exePath);
            }
        }

        return new GameEntry
        {
            Id = "manual:" + exePath.ToLowerInvariant(),
            Name = title!.Trim(),
            Source = "Добавлена вручную",
            InstallDir = dir,
            Executable = exePath,
        };
    }

    /// <summary>
    /// Самый крупный exe в папке игры и её подпапках (до 3 уровней: game\bin\win64 у CS2, TslGame\Binaries\Win64 у PUBG),
    /// кроме установщиков и служебных.
    /// </summary>
    public static string? GuessExecutable(string dir)
    {
        try
        {
            var candidates = new List<FileInfo>();
            var folders = new List<string> { dir };
            for (int depth = 0; depth < 4 && folders.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (string folder in folders.Take(60))
                {
                    candidates.AddRange(new DirectoryInfo(folder).EnumerateFiles("*.exe").Where(f => !IsHelperExe(f.Name)));
                    if (depth < 3)
                        next.AddRange(Directory.EnumerateDirectories(folder).Where(d => !IsSkippedFolder(Path.GetFileName(d))));
                }

                folders = next;
            }

            return candidates.OrderByDescending(f => f.Length).FirstOrDefault()?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsHelperExe(string name)
    {
        string n = name.ToLowerInvariant();
        return n.StartsWith("unins") || n.Contains("setup") || n.Contains("install") || n.Contains("crash") || n.Contains("report")
               || n.Contains("redist") || n.Contains("vc_") || n.Contains("dxsetup") || n.Contains("update") || n.Contains("helper")
               || n.Contains("eac") || n.Contains("anticheat") || n.Contains("battleye") || n.Contains("launcher")
               || n.Contains("cefprocess") || n.Contains("webhelper") || n.Contains("dotnet") || n.Contains("ue4prereq");
    }

    private static bool IsSkippedFolder(string name)
    {
        string n = name.ToLowerInvariant();
        return n is "_commonredist" or "redist" or "redistributables" or "directx" or "support" or "installers" or "__installer"
            or "easyanticheat" or "battleye" or "engine" or "logs" or "crashes" or "cache";
    }

    private static void Collect(List<GameEntry> games, string source, Func<IEnumerable<GameEntry>> find)
    {
        try
        {
            games.AddRange(find());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось прочитать игры ({source}).", ex);
        }
    }

    // ----- Steam -----

    private static IEnumerable<GameEntry> FindSteamGames()
    {
        string? steam = ReadString(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Valve\Steam", "SteamPath")
                        ?? ReadString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (steam is null)
            yield break;

        steam = Normalize(steam);
        var libraries = new List<string> { steam };
        string libraryFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
            libraries.AddRange(SteamLibraries(File.ReadAllText(libraryFile)));

        foreach (string library in libraries.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps))
                continue;

            foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                GameEntry? game = null;
                try
                {
                    game = SteamGame(File.ReadAllText(manifest), library);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Не удалось прочитать {manifest}.", ex);
                }

                if (game is not null)
                    yield return game;
            }
        }
    }

    /// <summary>Папки библиотек Steam из libraryfolders.vdf (новый формат с «path» и старый «"1" "D:\\Steam"»).</summary>
    public static IReadOnlyList<string> SteamLibraries(string vdf)
    {
        var result = new List<string>();
        if (Vdf.Parse(vdf) is not { } root)
            return result;

        foreach (object value in root.Values)
        {
            if (value is not Dictionary<string, object> folders)
                continue;

            foreach ((string key, object entry) in folders)
            {
                if (!key.All(char.IsDigit))
                    continue;
                if (entry is string path)
                    result.Add(path);
                else if (entry is Dictionary<string, object> folder && folder.TryGetValue("path", out object? p) && p is string text)
                    result.Add(text);
            }
        }

        return result;
    }

    /// <summary>Игра из appmanifest_*.acf. null — не игра (инструменты Steam) или установлена не полностью.</summary>
    public static GameEntry? SteamGame(string acf, string library)
    {
        if (Vdf.Parse(acf) is not { } root || root.Values.FirstOrDefault() is not Dictionary<string, object> state)
            return null;

        string appId = Get(state, "appid");
        string name = Get(state, "name");
        string installDir = Get(state, "installdir");
        if (appId.Length == 0 || name.Length == 0 || installDir.Length == 0)
            return null;
        if (int.TryParse(Get(state, "StateFlags"), out int flags) && (flags & 4) == 0)
            return null; // не установлена до конца
        if (appId is "228980" or "1070560" or "1391110" or "1628350" or "250820" or "1493710" or "2180100" or "1887720"
            || SteamTools.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            return null; // инструменты Steam, а не игры
        if (appId is "431960" or "993090" or "1905180" or "365670")
            return null; // программы из Steam: Wallpaper Engine, Lossless Scaling, OBS Studio, Blender

        return new GameEntry
        {
            Id = "steam:" + appId,
            Name = name,
            Source = "Steam",
            InstallDir = Path.Combine(library, "steamapps", "common", installDir),
        };

        static string Get(Dictionary<string, object> d, string key) =>
            d.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value as string ?? "";
    }

    // ----- Epic Games -----

    private static IEnumerable<GameEntry> FindEpicGames()
    {
        string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
            yield break;

        foreach (string file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            GameEntry? game = null;
            try
            {
                game = EpicGame(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось прочитать {file}.", ex);
            }

            if (game is not null)
                yield return game;
        }
    }

    public static GameEntry? EpicGame(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string Str(string name) => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

        if (root.TryGetProperty("bIsIncompleteInstall", out JsonElement incomplete) && incomplete.ValueKind == JsonValueKind.True)
            return null;
        bool isGame = root.TryGetProperty("AppCategories", out JsonElement categories)
                      && categories.ValueKind == JsonValueKind.Array
                      && categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "games");
        string location = Str("InstallLocation");
        if (!isGame || location.Length == 0)
            return null;

        string exe = Str("LaunchExecutable");
        return new GameEntry
        {
            Id = "epic:" + (Str("AppName") is { Length: > 0 } app ? app : Str("CatalogItemId")),
            Name = Str("DisplayName"),
            Source = "Epic Games",
            InstallDir = location,
            Executable = exe.Length > 0 ? Path.Combine(location, exe) : "",
        };
    }

    // ----- GOG -----

    private static IEnumerable<GameEntry> FindGogGames()
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using RegistryKey? games = root.OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (games is null)
            yield break;

        foreach (string id in games.GetSubKeyNames())
        {
            using RegistryKey? key = games.OpenSubKey(id);
            string name = key?.GetValue("gameName") as string ?? "";
            string path = key?.GetValue("path") as string ?? "";
            if (name.Length == 0 || path.Length == 0)
                continue;

            yield return new GameEntry
            {
                Id = "gog:" + id,
                Name = name,
                Source = "GOG",
                InstallDir = path,
                Executable = key?.GetValue("exe") as string ?? "",
            };
        }
    }

    // ----- Издатели из «Программ и компонентов» -----

    private static IEnumerable<GameEntry> FindPublisherGames()
    {
        foreach (InstalledApp app in InstalledAppsProvider.ListDesktop())
        {
            if (!IsLikelyGame(app.Name, app.Publisher) || app.InstallLocation.Length < 4)
                continue;

            yield return new GameEntry
            {
                Id = "app:" + (app.Key?.KeyName ?? app.Name).ToLowerInvariant(),
                Name = app.Name,
                Source = app.Publisher,
                InstallDir = app.InstallLocation,
            };
        }
    }

    /// <summary>Программа известного издателя игр, но не лаунчер и не служебный компонент.</summary>
    public static bool IsLikelyGame(string name, string publisher) =>
        GamePublishers.Any(p => publisher.Contains(p, StringComparison.OrdinalIgnoreCase))
        && !NotGames.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(RegistryHive hive, RegistryView view, string path, string name)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = root.OpenSubKey(path);
            return key?.GetValue(name) as string is { Length: > 0 } value ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Normalize(string path)
    {
        string text = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');
        return text.Length > 3 ? text.TrimEnd('\\') : text;
    }
}

/// <summary>Разбор текстового формата KeyValues (VDF), в котором Steam хранит библиотеки и манифесты игр.</summary>
public static class Vdf
{
    /// <summary>Корень: имя верхнего ключа → вложенный словарь. null — текст не разобрать.</summary>
    public static Dictionary<string, object>? Parse(string text)
    {
        try
        {
            int position = 0;
            return ParseObject(text, ref position, topLevel: true);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Dictionary<string, object> ParseObject(string text, ref int position, bool topLevel)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string? key = NextToken(text, ref position);
            if (key is null)
            {
                if (topLevel)
                    return result;
                throw new FormatException("Нет закрывающей скобки.");
            }

            if (key == "}")
            {
                if (topLevel)
                    throw new FormatException("Лишняя закрывающая скобка.");
                return result;
            }

            string? value = NextToken(text, ref position) ?? throw new FormatException("Ключ без значения.");
            if (value == "{")
                result[key] = ParseObject(text, ref position, topLevel: false);
            else
                result[key] = value;
        }
    }

    /// <summary>Строка в кавычках (с \\ и \"), «{», «}» или null в конце текста. Комментарии // пропускаются.</summary>
    private static string? NextToken(string text, ref int position)
    {
        while (position < text.Length)
        {
            char c = text[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
                continue;
            }

            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n')
                    position++;
                continue;
            }

            if (c is '{' or '}')
            {
                position++;
                return c.ToString();
            }

            if (c == '"')
            {
                var sb = new StringBuilder();
                position++;
                while (position < text.Length && text[position] != '"')
                {
                    if (text[position] == '\\' && position + 1 < text.Length)
                    {
                        char escaped = text[position + 1];
                        sb.Append(escaped switch { 'n' => '\n', 't' => '\t', _ => escaped });
                        position += 2;
                        continue;
                    }

                    sb.Append(text[position++]);
                }

                position++; // закрывающая кавычка
                return sb.ToString();
            }

            // Значение без кавычек (бывает в старых файлах): до пробела.
            int start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not '{' and not '}')
                position++;
            return text[start..position];
        }

        return null;
    }
}
