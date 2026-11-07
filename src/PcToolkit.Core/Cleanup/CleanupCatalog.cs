using System.Text.Json;
using System.Text.Json.Serialization;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Cleanup;

/// <summary>Правила очистки из cleanup.json. Правила с ошибками отбрасываются и попадают в <see cref="Problems"/>.</summary>
public sealed class CleanupCatalog
{
    private const string ResourceName = "PcToolkit.Core.Cleanup.cleanup.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private CleanupCatalog(IReadOnlyList<CleanupCategory> categories, IReadOnlyList<CleanupRule> rules, IReadOnlyList<string> problems)
    {
        Categories = categories;
        Rules = rules;
        Problems = problems;
    }

    public IReadOnlyList<CleanupCategory> Categories { get; }

    public IReadOnlyList<CleanupRule> Rules { get; }

    public IReadOnlyList<string> Problems { get; }

    public static CleanupCatalog LoadEmbedded()
    {
        using Stream stream = typeof(CleanupCatalog).Assembly.GetManifestResourceStream(ResourceName)
                              ?? throw new InvalidOperationException("Правила очистки не найдены в сборке.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static CleanupCatalog Parse(string json)
    {
        CleanupCatalogFile file = JsonSerializer.Deserialize<CleanupCatalogFile>(json, JsonOptions)
                                  ?? throw new InvalidOperationException("Правила очистки пусты.");

        var problems = new List<string>();
        var categoryIds = new HashSet<string>(file.Categories.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<CleanupRule>();

        foreach (CleanupRule rule in file.Rules)
        {
            string? problem = Validate(rule, categoryIds);
            if (problem is null && !ids.Add(rule.Id))
                problem = "повторяющийся идентификатор";

            if (problem is null)
                valid.Add(rule);
            else
                problems.Add($"«{rule.Id}»: {problem}");
        }

        foreach (string problem in problems)
            AppLog.Warn("Правила очистки: " + problem);

        return new CleanupCatalog(file.Categories, valid, problems);
    }

    private static string? Validate(CleanupRule rule, HashSet<string> categoryIds)
    {
        if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Title))
            return "нет идентификатора или названия";

        if (!categoryIds.Contains(rule.Category))
            return $"неизвестная категория «{rule.Category}»";

        if (rule.Kind == CleanupKind.RecycleBin)
            return null;

        if (rule.Paths.Count == 0)
            return "не указаны папки";

        if (rule.Patterns.Count == 0 || rule.Patterns.Any(string.IsNullOrWhiteSpace))
            return "пустая маска файлов";

        foreach (string path in rule.Paths)
        {
            // Путь должен начинаться с переменной окружения: так правило не привязано к букве диска
            // и не может указывать в произвольное место.
            if (!path.StartsWith('%') || path.Contains("..", StringComparison.Ordinal))
                return $"путь «{path}» должен начинаться с переменной окружения и не содержать «..»";

            string expanded = Environment.ExpandEnvironmentVariables(path);
            string firstWildcard = expanded.Split('*', '?')[0];
            string probe = firstWildcard.Length < expanded.Length ? firstWildcard.TrimEnd('\\') : expanded;
            if (!CleanupPaths.IsSafeRoot(probe, out string? reason) && !IsAllowedWildcardBase(probe))
                return $"путь «{path}» небезопасен: {reason}";
        }

        return null;
    }

    /// <summary>
    /// Для путей с маской проверяем часть до маски. Она бывает неглубокой (…\User Data),
    /// а итоговые папки всё равно проверяются при сканировании.
    /// </summary>
    private static bool IsAllowedWildcardBase(string probe)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return !string.IsNullOrEmpty(local) && CleanupPaths.IsUnder(probe, local);
    }
}
