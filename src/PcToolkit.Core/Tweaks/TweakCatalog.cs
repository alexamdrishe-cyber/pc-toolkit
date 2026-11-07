using System.Text.Json;
using System.Text.Json.Serialization;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Tweaks;

/// <summary>
/// Каталог настроек из tweaks.json. При загрузке каждая настройка проверяется;
/// настройки с ошибками в описании отбрасываются и попадают в <see cref="Problems"/>.
/// </summary>
public sealed class TweakCatalog
{
    private const string ResourceName = "PcToolkit.Core.Tweaks.tweaks.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private TweakCatalog(IReadOnlyList<TweakCategory> categories, IReadOnlyList<TweakDefinition> tweaks, IReadOnlyList<string> problems)
    {
        Categories = categories;
        Tweaks = tweaks;
        Problems = problems;
    }

    public IReadOnlyList<TweakCategory> Categories { get; }

    public IReadOnlyList<TweakDefinition> Tweaks { get; }

    /// <summary>Ошибки в описании настроек. В рабочем каталоге должно быть пусто (это проверяет автотест).</summary>
    public IReadOnlyList<string> Problems { get; }

    public TweakDefinition? Find(string id) =>
        Tweaks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Загружает каталог, встроенный в сборку.</summary>
    public static TweakCatalog LoadEmbedded(ITargetFactory factory)
    {
        using Stream? stream = typeof(TweakCatalog).Assembly.GetManifestResourceStream(ResourceName)
                               ?? throw new InvalidOperationException("Каталог настроек не найден в сборке.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), factory);
    }

    public static TweakCatalog Parse(string json, ITargetFactory factory)
    {
        TweakCatalogFile file = JsonSerializer.Deserialize<TweakCatalogFile>(json, JsonOptions)
                                ?? throw new InvalidOperationException("Каталог настроек пуст.");

        var problems = new List<string>();
        var categoryIds = new HashSet<string>(file.Categories.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var allIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<TweakDefinition>();

        foreach (TweakDefinition tweak in file.Tweaks)
        {
            string? problem = Validate(tweak, categoryIds, factory);
            if (problem is null && !allIds.Add(tweak.Id))
                problem = "повторяющийся идентификатор";

            if (problem is null)
                valid.Add(tweak);
            else
                problems.Add($"«{tweak.Id}»: {problem}");
        }

        foreach (TweakDefinition tweak in valid)
        {
            foreach (string conflict in tweak.ConflictsWith.Where(id => !allIds.Contains(id)))
                problems.Add($"«{tweak.Id}»: несовместимость с неизвестной настройкой «{conflict}»");
        }

        foreach (string problem in problems)
            AppLog.Warn("Каталог настроек: " + problem);

        return new TweakCatalog(file.Categories, valid, problems);
    }

    private static string? Validate(TweakDefinition tweak, HashSet<string> categoryIds, ITargetFactory factory)
    {
        if (string.IsNullOrWhiteSpace(tweak.Id))
            return "нет идентификатора";
        if (string.IsNullOrWhiteSpace(tweak.Title) || string.IsNullOrWhiteSpace(tweak.Description))
            return "нет названия или описания";
        if (!categoryIds.Contains(tweak.Category))
            return $"неизвестная категория «{tweak.Category}»";
        if (tweak.Changes.Count == 0)
            return "нет ни одного изменения";

        foreach (ChangeDefinition change in tweak.Changes)
        {
            try
            {
                factory.Resolve(change);
            }
            catch (Exception ex)
            {
                return $"ошибка в изменении: {ex.Message}";
            }
        }

        return null;
    }
}
