using System.Reflection;
using System.Text.RegularExpressions;

namespace PcToolkit.Core.Updates;

/// <summary>
/// Адреса проекта, заданные при сборке (Directory.Build.props): репозиторий GitHub, откуда берутся
/// новые версии и файл pctoolkit.json с адресом сервера рейтинга (его можно поменять без выпуска версии).
/// </summary>
public static partial class ProjectLinks
{
    /// <summary>Репозиторий «владелец/имя» или пусто, если не задан.</summary>
    public static string GitHubRepo { get; } = ValidRepo(Metadata("GitHubRepo"));

    /// <summary>Адрес обновлений из сборки: свой UpdateUrl или последние выпуски на GitHub.</summary>
    public static string BuiltInUpdateUrl
    {
        get
        {
            string own = Metadata("UpdateUrl");
            if (own.Length > 0)
                return own;
            return GitHubRepo.Length > 0 ? $"https://api.github.com/repos/{GitHubRepo}/releases/latest" : "";
        }
    }

    /// <summary>
    /// Где лежит pctoolkit.json: сначала raw.githubusercontent.com (ветка по умолчанию), если он недоступен —
    /// то же через api.github.com.
    /// </summary>
    public static IReadOnlyList<string> ConfigUrls => GitHubRepo.Length == 0
        ? Array.Empty<string>()
        : new[]
        {
            $"https://raw.githubusercontent.com/{GitHubRepo}/HEAD/pctoolkit.json",
            $"https://api.github.com/repos/{GitHubRepo}/contents/pctoolkit.json",
        };

    /// <summary>Страница выпусков (ссылка «что нового»).</summary>
    public static string ReleasesPage => GitHubRepo.Length > 0 ? $"https://github.com/{GitHubRepo}/releases" : "";

    /// <summary>«владелец/имя» из латиницы, цифр, «-», «_» и «.», иначе пусто.</summary>
    public static string ValidRepo(string? repo)
    {
        string text = (repo ?? "").Trim().Trim('/');
        return RepoPattern().IsMatch(text) ? text : "";
    }

    private static string Metadata(string key) =>
        typeof(ProjectLinks).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? "";

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoPattern();
}
