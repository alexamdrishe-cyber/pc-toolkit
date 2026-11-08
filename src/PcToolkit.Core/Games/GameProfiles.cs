using System.Diagnostics;
using System.Text.Json;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Games;

/// <summary>
/// Профиль игры: что сделать, пока игра запущена. Всё временное — после выхода из игры возвращается как было.
/// </summary>
public sealed class GameProfile
{
    public string GameId { get; set; } = "";

    public string Name { get; set; } = "";

    public string InstallDir { get; set; } = "";

    public string Executable { get; set; } = "";

    /// <summary>Применять профиль автоматически, когда игра запускается.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Схема питания «Высокая производительность» на время игры.</summary>
    public bool PowerPlan { get; set; } = true;

    /// <summary>Высокий приоритет процесса игры.</summary>
    public bool HighPriority { get; set; }

    /// <summary>Показывать оверлей с FPS и датчиками.</summary>
    public bool Overlay { get; set; } = true;

    /// <summary>Записывать замер FPS за всю игровую сессию.</summary>
    public bool RecordFps { get; set; }

    /// <summary>Видеокарта AMD: «» — не менять, «antilag» — Radeon Anti-Lag, «chill» — Radeon Chill (<see cref="RadeonModes"/>).</summary>
    public string RadeonMode { get; set; } = RadeonModes.None;

    /// <summary>Radeon Chill: частота кадров, когда в игре ничего не происходит.</summary>
    public int ChillMinFps { get; set; } = 60;

    /// <summary>Radeon Chill: частота кадров в движении.</summary>
    public int ChillMaxFps { get; set; } = 144;

    /// <summary>Видеокарта AMD: Radeon Image Sharpening на время игры.</summary>
    public bool Sharpening { get; set; }

    /// <summary>Сила резкости, %.</summary>
    public int Sharpness { get; set; } = 80;

    public bool HasRadeon => RadeonMode.Length > 0 || Sharpening;

    public bool HasActions => PowerPlan || HighPriority || Overlay || RecordFps || HasRadeon;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (PowerPlan)
                parts.Add("схема питания");
            if (HighPriority)
                parts.Add("приоритет");
            if (Overlay)
                parts.Add("оверлей");
            if (RecordFps)
                parts.Add("запись FPS");
            if (RadeonMode == RadeonModes.AntiLag)
                parts.Add("Anti-Lag");
            else if (RadeonMode == RadeonModes.Chill)
                parts.Add($"Chill {ChillMinFps}–{ChillMaxFps} FPS");
            if (Sharpening)
                parts.Add("резкость");
            return parts.Count == 0 ? "ничего не делает" : string.Join(", ", parts);
        }
    }

    public static GameProfile For(GameEntry game) => new()
    {
        GameId = game.Id,
        Name = game.Name,
        InstallDir = game.InstallDir,
        Executable = game.Executable,
    };

    /// <summary>Относится ли процесс к игре: тот самый exe или любой exe из папки игры.</summary>
    public bool Matches(RunningProcess process)
    {
        if (process.Path.Length == 0)
            return false;
        if (Executable.Length > 0 && string.Equals(process.Path, Executable, StringComparison.OrdinalIgnoreCase))
            return true;
        return ProcessInfo.IsInside(process.Path, InstallDir);
    }
}

/// <summary>Профили и добавленные вручную игры: %LocalAppData%\PcToolkit\games.json.</summary>
public sealed class GameStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "games.json");

    public List<GameProfile> Profiles { get; set; } = new();

    public List<GameEntry> CustomGames { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; private set; } = DefaultPath;

    public static GameStore Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                GameStore? store = JsonSerializer.Deserialize<GameStore>(File.ReadAllText(path));
                if (store is not null)
                {
                    store.FilePath = path;
                    return store;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать профили игр.", ex);
        }

        return new GameStore { FilePath = path };
    }

    public GameProfile? Find(string gameId) => Profiles.FirstOrDefault(p => p.GameId == gameId);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить профили игр.", ex);
        }
    }
}

/// <summary>Запущенная игра с профилем: главный процесс и что было сделано.</summary>
/// <param name="Profile">Профиль в том виде, в каком он сейчас применён.</param>
public sealed record RunningGame(GameProfile Profile, int MainProcessId, string MainProcessName, DateTime StartedAt)
{
    /// <summary>Что сделано со схемой питания; null — профиль её не трогает.</summary>
    public string? PowerNote { get; init; }

    /// <summary>Что сделано с приоритетом; null — профиль его не трогает.</summary>
    public string? PriorityNote { get; init; }

    /// <summary>Процессы, которым повышен приоритет: после игры (или выключения пункта) он возвращается.</summary>
    public IReadOnlyList<int> RaisedProcesses { get; init; } = Array.Empty<int>();

    /// <summary>Что сделано с настройками видеокарты AMD; null — профиль их не трогает.</summary>
    public string? GraphicsNote { get; init; }

    /// <summary>Игра держит настройки видеокарты (после неё их нужно вернуть).</summary>
    public bool HoldsGraphics { get; init; }

    public IReadOnlyList<string> Applied
    {
        get
        {
            var list = new List<string>();
            if (PowerNote is not null)
                list.Add(PowerNote);
            if (PriorityNote is not null)
                list.Add(PriorityNote);
            if (GraphicsNote is not null)
                list.Add(GraphicsNote);
            if (Profile.Overlay)
                list.Add("Оверлей с FPS включён");
            if (Profile.RecordFps)
                list.Add("Идёт запись FPS");
            return list;
        }
    }
}

/// <summary>
/// Применяет профиль, когда игра запускается, и возвращает всё обратно, когда она закрывается.
/// Схема питания возвращается, только если её с тех пор не меняли вручную. Что было до игры,
/// записано в файл: если приложение закроется или упадёт посреди игры, при следующем запуске
/// схема вернётся сама (или продолжит действовать, если игра всё ещё запущена).
/// </summary>
public sealed class GameSessionManager
{
    public static readonly Guid HighPerformanceScheme = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformanceScheme = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly Func<Guid?, ISettingTarget> _powerTarget;
    private readonly Func<int, string?> _setHighPriority;
    private readonly Action<int> _restorePriority;
    private readonly string _statePath;
    private readonly Dictionary<string, RunningGame> _running = new();
    private readonly Dictionary<int, ProcessPriorityClass> _originalPriority = new();
    private readonly IRadeonGraphics _radeon;
    private readonly string _graphicsPath;
    private SessionState? _state;
    private GraphicsState? _graphics;

    /// <param name="powerTarget">Параметр «активная схема питания»; аргумент — схема, наличие которой проверить.</param>
    /// <param name="setHighPriority">Повышает приоритет процесса; возвращает текст ошибки или null.</param>
    /// <param name="restorePriority">Возвращает приоритет процесса, повышенный через <paramref name="setHighPriority"/>.</param>
    /// <param name="radeon">Настройки видеокарты AMD (Anti-Lag, Chill, резкость).</param>
    public GameSessionManager(Func<Guid?, ISettingTarget>? powerTarget = null, string? statePath = null,
        Func<int, string?>? setHighPriority = null, Action<int>? restorePriority = null, IRadeonGraphics? radeon = null)
    {
        _powerTarget = powerTarget ?? (scheme => new PowerSchemeTarget(scheme));
        _setHighPriority = setHighPriority ?? SetHighPriority;
        _restorePriority = restorePriority ?? RestorePriority;
        _radeon = radeon ?? AdlxRadeonGraphics.Instance;
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "game-session.json");
        _graphicsPath = Path.Combine(Path.GetDirectoryName(_statePath)!, Path.GetFileNameWithoutExtension(_statePath) + "-graphics.json");
        _state = LoadState();
        _graphics = LoadGraphics();
    }

    public event EventHandler<RunningGame>? GameStarted;

    public event EventHandler<RunningGame>? GameStopped;

    /// <summary>Профиль запущенной игры изменили: новые пункты применены, снятые — возвращены.</summary>
    public event EventHandler<RunningGame>? GameChanged;

    public IReadOnlyList<RunningGame> Running
    {
        get
        {
            lock (_lock)
                return _running.Values.ToList();
        }
    }

    /// <summary>Есть незавершённая сессия с прошлого запуска (схема питания или настройки видеокарты ещё не возвращены).</summary>
    public bool HasPendingState
    {
        get
        {
            lock (_lock)
                return _state is not null || _graphics is not null;
        }
    }

    /// <summary>Сверяет запущенные процессы с профилями: применяет и откатывает. Вызывать раз в пару секунд.</summary>
    public void Update(IReadOnlyList<GameProfile> profiles, IReadOnlyList<RunningProcess> processes)
    {
        var started = new List<RunningGame>();
        var stopped = new List<RunningGame>();
        var changed = new List<RunningGame>();
        lock (_lock)
        {
            var seen = new HashSet<string>();
            foreach (GameProfile profile in profiles.Where(p => p.Enabled && p.HasActions))
            {
                List<RunningProcess> matched = processes.Where(profile.Matches).ToList();
                if (matched.Count == 0)
                    continue;

                seen.Add(profile.GameId);
                RunningProcess main = matched.MaxBy(p => p.WorkingSet)!;
                if (!_running.TryGetValue(profile.GameId, out RunningGame? game))
                {
                    game = Start(profile, main);
                    _running[profile.GameId] = game;
                    started.Add(game);
                    continue;
                }

                RunningGame synced = Sync(game, profile, main);
                _running[profile.GameId] = synced;
                if (!SameOptions(game.Profile, profile))
                    changed.Add(synced);
            }

            foreach (string id in _running.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                RunningGame game = _running[id];
                _running.Remove(id);
                RestoreRaised(game);
                stopped.Add(game);
                bool stillRunning = profiles.Any(p => p.GameId == id && processes.Any(p.Matches));
                AppLog.Info(stillRunning
                    ? $"Профиль игры «{game.Profile.Name}» выключен, пока она запущена: возвращаю, что он менял."
                    : $"Игра «{game.Profile.Name}» закрыта.");
            }

            // Схему питания больше не держит ни одна игра (или она осталась от сбоя прошлого запуска): возвращаем.
            if (_state is not null && !_running.Values.Any(g => g.PowerNote is not null))
                RevertPower();
            if (_graphics is not null && !_running.Values.Any(g => g.HoldsGraphics))
                RevertGraphics();
        }

        foreach (RunningGame game in stopped)
            GameStopped?.Invoke(this, game);
        foreach (RunningGame game in started)
            GameStarted?.Invoke(this, game);
        foreach (RunningGame game in changed)
            GameChanged?.Invoke(this, game);
    }

    /// <summary>Закрытие приложения: возвращает всё, что сделано профилями.</summary>
    public void RevertAll()
    {
        lock (_lock)
        {
            foreach (RunningGame game in _running.Values)
                RestoreRaised(game);
            _running.Clear();
            if (_state is not null)
                RevertPower();
            if (_graphics is not null)
                RevertGraphics();
        }
    }

    private RunningGame Start(GameProfile profile, RunningProcess main)
    {
        AppLog.Info($"Запущена игра «{profile.Name}» ({main.Name}, PID {main.Id}): применяю профиль ({profile.Summary}).");
        var game = new RunningGame(profile, main.Id, main.Name, DateTime.Now);
        if (profile.PowerPlan)
            game = game with { PowerNote = ApplyPower(profile) };
        if (profile.HighPriority)
            game = RaisePriority(game, main);
        if (profile.HasRadeon)
            game = ApplyGraphics(game, profile);
        return game;
    }

    /// <summary>
    /// Игра уже идёт: сверяет сделанное с профилем. Пункты, включённые посреди игры, применяются сразу,
    /// выключенные — возвращаются (схема питания — когда её не держит ни одна другая игра).
    /// </summary>
    private RunningGame Sync(RunningGame game, GameProfile profile, RunningProcess main)
    {
        RunningGame result = game with { Profile = profile };
        bool mainChanged = game.MainProcessId != main.Id && main.WorkingSet > 0;
        if (mainChanged)
        {
            // Сначала запускается лаунчер игры, потом сама игра: главный процесс меняется.
            result = result with { MainProcessId = main.Id, MainProcessName = main.Name };
        }

        if (profile.PowerPlan && result.PowerNote is null)
        {
            AppLog.Info($"«{profile.Name}»: схема питания включена в профиле посреди игры.");
            result = result with { PowerNote = ApplyPower(profile) };
        }
        else if (!profile.PowerPlan && result.PowerNote is not null)
        {
            AppLog.Info($"«{profile.Name}»: схема питания выключена в профиле посреди игры.");
            result = result with { PowerNote = null };
        }

        if (profile.HighPriority && (result.PriorityNote is null || mainChanged))
        {
            result = RaisePriority(result, main);
        }
        else if (!profile.HighPriority && result.PriorityNote is not null)
        {
            AppLog.Info($"«{profile.Name}»: высокий приоритет выключен в профиле посреди игры.");
            RestoreRaised(result);
            result = result with { PriorityNote = null, RaisedProcesses = Array.Empty<int>() };
        }

        if (!SameRadeon(game.Profile, profile))
        {
            // Настройки видеокарты поменяли посреди игры: возвращаем прежние (если эта игра их держала) и применяем новые.
            AppLog.Info($"«{profile.Name}»: настройки видеокарты в профиле изменены посреди игры.");
            result = result with { GraphicsNote = null, HoldsGraphics = false };
            if (_graphics is not null && !_running.Values.Any(g => g.HoldsGraphics && g.Profile.GameId != profile.GameId))
                RevertGraphics();
            if (profile.HasRadeon)
                result = ApplyGraphics(result, profile);
        }

        return result;
    }

    private static bool SameOptions(GameProfile a, GameProfile b) =>
        a.PowerPlan == b.PowerPlan && a.HighPriority == b.HighPriority && a.Overlay == b.Overlay && a.RecordFps == b.RecordFps
        && SameRadeon(a, b);

    private static bool SameRadeon(GameProfile a, GameProfile b) =>
        a.RadeonMode == b.RadeonMode && a.Sharpening == b.Sharpening
        && (a.RadeonMode != RadeonModes.Chill || (a.ChillMinFps == b.ChillMinFps && a.ChillMaxFps == b.ChillMaxFps))
        && (!a.Sharpening || a.Sharpness == b.Sharpness);

    /// <summary>
    /// Включает Anti-Lag, Chill или резкость на время игры. Что было до игры, сначала записывается в файл:
    /// после сбоя приложения настройки вернутся при следующем запуске.
    /// </summary>
    private RunningGame ApplyGraphics(RunningGame game, GameProfile profile)
    {
        try
        {
            if (_graphics is not null)
            {
                // Настройки уже держит другая игра (или остались от сбоя): не перебиваем, вернутся, когда их отпустят все.
                _graphics.Games.Add(profile.GameId);
                SaveGraphics();
                return game with { GraphicsNote = "Видеокарта: " + RadeonPlan.Describe(_graphics.Applied), HoldsGraphics = true };
            }

            RadeonState? state = _radeon.Read(out string? error);
            if (state is null)
                return game with { GraphicsNote = "Настройки видеокарты AMD не менялись: " + error };

            RadeonValues wanted = RadeonPlan.Wanted(profile, state);
            if (wanted.IsEmpty)
                return game with { GraphicsNote = "Видеокарта не поддерживает выбранные настройки AMD — ничего не менялось" };
            if (!RadeonPlan.Differs(wanted, state.Values))
                return game with { GraphicsNote = "Видеокарта: " + RadeonPlan.Describe(wanted) + " — уже было включено" };

            _graphics = new GraphicsState { Before = RadeonPlan.Before(wanted, state.Values), Applied = wanted, Games = { profile.GameId } };
            SaveGraphics();
            _radeon.Apply(wanted);
            string text = "Видеокарта: " + RadeonPlan.Describe(wanted);
            AppLog.Info($"«{profile.Name}»: {text} (было: {RadeonPlan.Describe(_graphics.Before)}).");
            return game with { GraphicsNote = text, HoldsGraphics = true };
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось включить настройки видеокарты AMD для игры.", ex);
            if (_graphics is not null)
                RevertGraphics(); // могло примениться частично — возвращаем, что успели
            return game with { GraphicsNote = "Настройки видеокарты AMD не изменены: " + ex.Message };
        }
    }

    private void RevertGraphics()
    {
        GraphicsState state = _graphics!;
        try
        {
            RadeonState? now = _radeon.Read(out string? error);
            if (now is null)
            {
                AppLog.Warn("Не удалось вернуть настройки видеокарты AMD после игры: " + error);
                return;
            }

            RadeonValues restore = RadeonPlan.Restore(state.Before, state.Applied, now.Values);
            if (restore.IsEmpty)
            {
                AppLog.Info("Настройки видеокарты после игры поменяли вручную — оставляю как есть.");
                return;
            }

            _radeon.Apply(restore);
            AppLog.Info("Настройки видеокарты AMD возвращены после игры.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось вернуть настройки видеокарты AMD после игры.", ex);
        }
        finally
        {
            _graphics = null;
            DeleteFile(_graphicsPath);
        }
    }

    private RunningGame RaisePriority(RunningGame game, RunningProcess process)
    {
        string? error = _setHighPriority(process.Id);
        if (error is null)
        {
            AppLog.Info($"Высокий приоритет для {process.Name} (PID {process.Id}).");
            return game with
            {
                PriorityNote = $"Высокий приоритет: {process.Name}",
                RaisedProcesses = game.RaisedProcesses.Append(process.Id).Distinct().ToList(),
            };
        }

        AppLog.Warn($"Не удалось повысить приоритет {process.Name}: {error}");
        return game with { PriorityNote = $"Приоритет не изменён ({process.Name}): игра не разрешила" };
    }

    private void RestoreRaised(RunningGame game)
    {
        foreach (int processId in game.RaisedProcesses)
            _restorePriority(processId);
    }

    private string ApplyPower(GameProfile profile)
    {
        try
        {
            ISettingTarget current = _powerTarget(null);
            if (_state is not null)
            {
                _state.Games.Add(profile.GameId);
                SaveState();
                return "Схема питания: " + current.Describe(new ValueSnapshot(true, PowerSchemeTarget.KindName, _state.AppliedScheme));
            }

            ValueSnapshot before = current.Read();
            if (Guid.TryParse(before.Data, out Guid active) && (active == HighPerformanceScheme || active == UltimatePerformanceScheme))
                return "Схема питания уже «" + current.Describe(before) + "»";

            Guid? target = null;
            foreach (Guid scheme in new[] { HighPerformanceScheme, UltimatePerformanceScheme })
            {
                if (_powerTarget(scheme).IsAvailable(out _))
                {
                    target = scheme;
                    break;
                }
            }

            if (target is null)
                return "Схемы «Высокая производительность» нет в этой Windows — схема питания не менялась";

            var desired = new ValueSnapshot(true, PowerSchemeTarget.KindName, target.Value.ToString("D"));
            // Сначала записываем, что было: если приложение упадёт сразу после смены схемы, она всё равно вернётся.
            _state = new SessionState { PreviousScheme = before.Data, AppliedScheme = desired.Data, Games = { profile.GameId } };
            SaveState();
            current.Write(desired);
            if (!current.Read().SameAs(desired))
            {
                AppLog.Warn("Схема питания не переключилась.");
                _state = null;
                DeleteState();
                return "Схема питания не переключилась";
            }

            string text = $"Схема питания «{current.Describe(desired)}» (была «{current.Describe(before)}»)";
            AppLog.Info(text);
            return text;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось переключить схему питания для игры.", ex);
            return "Схема питания не переключилась: " + ex.Message;
        }
    }

    private void RevertPower()
    {
        SessionState state = _state!;
        try
        {
            ISettingTarget current = _powerTarget(null);
            ValueSnapshot now = current.Read();
            if (!string.Equals(now.Data, state.AppliedScheme, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Info($"Схему питания после игры поменяли вручную ({current.Describe(now)}) — оставляю как есть.");
            }
            else if (Guid.TryParse(state.PreviousScheme, out Guid previous) && _powerTarget(previous).IsAvailable(out _))
            {
                var value = new ValueSnapshot(true, PowerSchemeTarget.KindName, state.PreviousScheme);
                current.Write(value);
                AppLog.Info($"Схема питания возвращена на «{current.Describe(value)}».");
            }
            else
            {
                AppLog.Warn($"Прежней схемы питания {state.PreviousScheme} больше нет — оставляю текущую.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось вернуть схему питания после игры.", ex);
        }
        finally
        {
            _state = null;
            DeleteState();
        }
    }

    private string? SetHighPriority(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            ProcessPriorityClass before = process.PriorityClass;
            process.PriorityClass = ProcessPriorityClass.High;
            _originalPriority.TryAdd(processId, before);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void RestorePriority(int processId)
    {
        if (!_originalPriority.Remove(processId, out ProcessPriorityClass before))
            return;

        try
        {
            using Process process = Process.GetProcessById(processId);
            // Если приоритет с тех пор поменяли (игра сама или пользователь), не трогаем.
            if (process.PriorityClass == ProcessPriorityClass.High && before != ProcessPriorityClass.High)
            {
                process.PriorityClass = before;
                AppLog.Info($"Приоритет PID {processId} возвращён: {before}.");
            }
        }
        catch (ArgumentException)
        {
            // Процесс уже завершился — возвращать нечего.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось вернуть приоритет PID {processId}: {ex.Message}");
        }
    }

    private SessionState? LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                SessionState? state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(_statePath));
                if (state is { AppliedScheme.Length: > 0 })
                {
                    AppLog.Info("Найдена незавершённая игровая сессия прошлого запуска: схема питания вернётся, когда игры будут закрыты.");
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать состояние игровой сессии.", ex);
        }

        return null;
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state, Json));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить состояние игровой сессии.", ex);
        }
    }

    private void DeleteState() => DeleteFile(_statePath);

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось удалить файл состояния игровой сессии.", ex);
        }
    }

    private GraphicsState? LoadGraphics()
    {
        try
        {
            if (File.Exists(_graphicsPath))
            {
                GraphicsState? state = JsonSerializer.Deserialize<GraphicsState>(File.ReadAllText(_graphicsPath));
                if (state is { Applied.IsEmpty: false })
                {
                    AppLog.Info("Найдены настройки видеокарты незавершённой игровой сессии: вернутся, когда игры будут закрыты.");
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать состояние настроек видеокарты.", ex);
        }

        return null;
    }

    private void SaveGraphics()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_graphicsPath)!);
            File.WriteAllText(_graphicsPath, JsonSerializer.Serialize(_graphics, Json));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить состояние настроек видеокарты.", ex);
        }
    }

    /// <summary>Настройки видеокарты на время игры: что было до и что поставлено.</summary>
    public sealed class GraphicsState
    {
        public RadeonValues Before { get; set; } = new();

        public RadeonValues Applied { get; set; } = new();

        public HashSet<string> Games { get; set; } = new();

        public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    }

    /// <summary>Что вернуть после игры.</summary>
    public sealed class SessionState
    {
        public string PreviousScheme { get; set; } = "";

        public string AppliedScheme { get; set; } = "";

        public HashSet<string> Games { get; set; } = new();

        public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    }
}
