namespace PcToolkit.Core.Cleanup;

/// <summary>
/// Пути для очистки: раскрытие переменных и масок, а главное — защита.
/// Очистка никогда не работает в корне диска, в папке Windows, Program Files, профиле пользователя
/// и его личных папках и не переходит по ссылкам на другие папки (junction, symlink).
/// </summary>
public static class CleanupPaths
{
    private static readonly Lazy<IReadOnlyList<string>> Protected = new(BuildProtectedList);

    /// <summary>Превращает шаблон пути в список существующих папок.</summary>
    public static IReadOnlyList<string> ExpandRoots(string template)
    {
        string expanded = Environment.ExpandEnvironmentVariables(template);
        if (expanded.Contains('%') || !Path.IsPathFullyQualified(expanded))
            return Array.Empty<string>();

        string[] parts = expanded.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".."))
            return Array.Empty<string>();

        var current = new List<string> { parts[0] + Path.DirectorySeparatorChar };
        for (int i = 1; i < parts.Length; i++)
        {
            string segment = parts[i];
            var next = new List<string>();
            foreach (string directory in current)
            {
                if (segment.IndexOfAny(new[] { '*', '?' }) < 0)
                {
                    next.Add(Path.Combine(directory, segment));
                    continue;
                }

                try
                {
                    next.AddRange(Directory.EnumerateDirectories(directory, segment).Where(d => !IsReparsePoint(d)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Нет доступа или папки нет — просто нечего чистить.
                }
            }

            current = next;
        }

        return current
            .Where(Directory.Exists)
            .Where(d => !IsReparsePoint(d))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Можно ли чистить внутри этой папки.</summary>
    public static bool IsSafeRoot(string root, out string? reason)
    {
        reason = null;
        string full;
        try
        {
            full = Normalize(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "некорректный путь";
            return false;
        }

        string? driveRoot = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(driveRoot) || driveRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "сетевые пути не поддерживаются";
            return false;
        }

        int depth = full.Substring(driveRoot.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 2)
        {
            reason = "слишком близко к корню диска";
            return false;
        }

        foreach (string protectedPath in Protected.Value)
        {
            if (string.Equals(full, protectedPath, StringComparison.OrdinalIgnoreCase) || IsUnder(protectedPath, full))
            {
                reason = $"это системная или личная папка ({protectedPath})";
                return false;
            }
        }

        return true;
    }

    /// <summary>Лежит ли путь внутри папки (не равен ей).</summary>
    public static bool IsUnder(string path, string root)
    {
        string fullPath = Normalize(path);
        string fullRoot = Normalize(root) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не смогли проверить — считаем опасным.
            return true;
        }
    }

    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static IReadOnlyList<string> BuildProtectedList()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.MyMusic,
        };

        var result = new List<string>();
        foreach (Environment.SpecialFolder folder in folders)
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
                result.Add(Normalize(path));
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            result.Add(Normalize(Path.Combine(profile, "Downloads")));
            string? users = Path.GetDirectoryName(Normalize(profile));
            if (!string.IsNullOrEmpty(users))
                result.Add(Normalize(users));
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
