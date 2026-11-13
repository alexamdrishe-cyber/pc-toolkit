using System.Text.Json;
using System.Text.Json.Serialization;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Settings;

namespace PcToolkit.Core.Overlay;

/// <summary>
/// Настройки оверлея: %LocalAppData%\PcToolkit\overlay.json. Общий оверлей для всех игр
/// и свои оверлеи отдельных игр.
/// </summary>
public sealed class OverlaySettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "overlay.json");

    /// <summary>Оверлей для всех игр, у которых нет своего.</summary>
    public OverlayLayout Default { get; set; } = OverlayLayout.CreateDefault();

    /// <summary>Свои оверлеи игр по их Id (как в списке игр).</summary>
    public Dictionary<string, GameOverlay> Games { get; set; } = new();

    [JsonIgnore]
    public string FilePath { get; private set; } = DefaultPath;

    /// <summary>
    /// Прочитать настройки. Если файла ещё нет (первый запуск версии с настраиваемым оверлеем), оверлей
    /// собирается из прежних настроек: угол, «что показывать», размер и плотность фона.
    /// </summary>
    public static OverlaySettings Load(string? path = null, Func<AppSettings>? legacy = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                OverlaySettings? settings = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(path));
                if (settings is not null)
                {
                    settings.FilePath = path;
                    return settings.Normalize();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать настройки оверлея, используются обычные.", ex);
            return new OverlaySettings { FilePath = path };
        }

        AppSettings old = (legacy ?? AppSettings.Load)();
        return new OverlaySettings
        {
            FilePath = path,
            Default = OverlayLayout.FromLegacy(old.OverlayCorner, old.OverlayShowDetails, old.OverlayShowGraph,
                old.OverlayShowSensors, old.OverlayShowProcess, old.OverlayScale, old.OverlayOpacity),
        };
    }

    /// <summary>Оверлей для игры: свой, если он включён, иначе общий.</summary>
    public OverlayLayout For(string? gameId) =>
        gameId is not null && Games.TryGetValue(gameId, out GameOverlay? game) && game.Own ? game.Layout : Default;

    /// <summary>Есть ли у игры свой оверлей (включённый).</summary>
    public bool HasOwn(string gameId) => Games.TryGetValue(gameId, out GameOverlay? game) && game.Own;

    /// <summary>
    /// Включить или выключить свой оверлей игры. Свой оверлей создаётся копией общего; выключенный
    /// не удаляется — если включить снова, вернётся настроенный.
    /// </summary>
    public OverlayLayout SetOwn(string gameId, string gameName, bool own)
    {
        if (!Games.TryGetValue(gameId, out GameOverlay? game))
        {
            if (!own)
                return Default;
            Games[gameId] = game = new GameOverlay { Layout = Default.Clone() };
        }

        game.Own = own;
        if (gameName.Length > 0)
            game.Name = gameName;
        return For(gameId);
    }

    public OverlaySettings Normalize()
    {
        Default = (Default ?? OverlayLayout.CreateDefault()).Normalize();
        Games = Games ?? new Dictionary<string, GameOverlay>();
        foreach (string id in Games.Keys.ToList())
        {
            if (Games[id] is not { } game || string.IsNullOrWhiteSpace(id))
            {
                Games.Remove(id);
                continue;
            }

            game.Name ??= "";
            game.Layout = (game.Layout ?? Default.Clone()).Normalize();
        }

        return this;
    }

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
            AppLog.Warn("Не удалось сохранить настройки оверлея.", ex);
        }
    }
}

/// <summary>Свой оверлей игры.</summary>
public sealed class GameOverlay
{
    /// <summary>Название игры — чтобы показать его, даже если игра пропала из списка.</summary>
    public string Name { get; set; } = "";

    /// <summary>Включён ли свой оверлей (выключенный хранится, чтобы не потерять настройки).</summary>
    public bool Own { get; set; } = true;

    public OverlayLayout Layout { get; set; } = OverlayLayout.CreateDefault();
}
