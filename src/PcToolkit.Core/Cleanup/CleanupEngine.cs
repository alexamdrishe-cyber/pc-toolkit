using System.Diagnostics;
using System.IO.Enumeration;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Cleanup;

public sealed record CleanupFile(string Path, long Size);

public enum CleanupScanState
{
    Ready,
    Empty,
    NeedsAdmin,
    Failed,
}

/// <summary>Что нашлось по одному правилу.</summary>
public sealed class CleanupScanResult
{
    public required CleanupRule Rule { get; init; }

    public required CleanupScanState State { get; init; }

    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();

    public IReadOnlyList<CleanupFile> Files { get; init; } = Array.Empty<CleanupFile>();

    public long TotalBytes { get; init; }

    /// <summary>Сколько элементов (для корзины — из системного счётчика).</summary>
    public long ItemCount { get; init; }

    /// <summary>Запущенные программы, которые держат часть файлов.</summary>
    public IReadOnlyList<string> RunningProcesses { get; init; } = Array.Empty<string>();

    public string? Problem { get; init; }

    public bool CanClean => State == CleanupScanState.Ready && ItemCount > 0;
}

public sealed record CleanupRuleResult(string RuleId, string Title, long FreedBytes, int Deleted, int Skipped);

public sealed record CleanupRunResult(IReadOnlyList<CleanupRuleResult> Rules, int FoldersRemoved)
{
    public long FreedBytes => Rules.Sum(r => r.FreedBytes);

    public int Deleted => Rules.Sum(r => r.Deleted);

    public int Skipped => Rules.Sum(r => r.Skipped);
}

/// <summary>
/// Поиск и удаление мусора по правилам. Удаляется только то, что было найдено при сканировании,
/// и только если файл по-прежнему лежит внутри папки правила, не является ссылкой и всё ещё достаточно старый.
/// Занятые файлы пропускаются.
/// </summary>
public static class CleanupEngine
{
    public static CleanupScanResult Scan(CleanupRule rule, bool isElevated, CancellationToken cancellation = default)
    {
        if (rule.RequiresAdmin && !isElevated)
            return new CleanupScanResult { Rule = rule, State = CleanupScanState.NeedsAdmin };

        try
        {
            return rule.Kind == CleanupKind.RecycleBin ? ScanRecycleBin(rule) : ScanFiles(rule, cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Очистка: не удалось просканировать «{rule.Id}».", ex);
            return new CleanupScanResult { Rule = rule, State = CleanupScanState.Failed, Problem = ex.Message };
        }
    }

    public static CleanupRunResult Clean(IEnumerable<CleanupScanResult> scans, CancellationToken cancellation = default)
    {
        var results = new List<CleanupRuleResult>();
        int folders = 0;

        foreach (CleanupScanResult scan in scans.Where(s => s.CanClean))
        {
            cancellation.ThrowIfCancellationRequested();
            if (scan.Rule.Kind == CleanupKind.RecycleBin)
            {
                bool emptied = RecycleBin.Empty();
                results.Add(new CleanupRuleResult(scan.Rule.Id, scan.Rule.Title,
                    emptied ? scan.TotalBytes : 0, emptied ? (int)scan.ItemCount : 0, emptied ? 0 : (int)scan.ItemCount));
                continue;
            }

            (CleanupRuleResult result, int removedFolders) = CleanFiles(scan, cancellation);
            results.Add(result);
            folders += removedFolders;
        }

        var run = new CleanupRunResult(results, folders);
        AppLog.Info($"Очистка: удалено файлов {run.Deleted}, пропущено {run.Skipped}, освобождено {Formatting.Bytes((ulong)run.FreedBytes)}.");
        return run;
    }

    private static CleanupScanResult ScanRecycleBin(CleanupRule rule)
    {
        (long size, long count) = RecycleBin.Query();
        return new CleanupScanResult
        {
            Rule = rule,
            State = count > 0 ? CleanupScanState.Ready : CleanupScanState.Empty,
            TotalBytes = size,
            ItemCount = count,
        };
    }

    private static CleanupScanResult ScanFiles(CleanupRule rule, CancellationToken cancellation)
    {
        var roots = new List<string>();
        foreach (string template in rule.Paths)
        {
            foreach (string root in CleanupPaths.ExpandRoots(template))
            {
                if (CleanupPaths.IsSafeRoot(root, out string? reason))
                    roots.Add(root);
                else
                    AppLog.Warn($"Очистка: папка «{root}» правила «{rule.Id}» пропущена: {reason}.");
            }
        }

        // Папка внутри другой папки того же правила посчитается один раз.
        roots = roots.Where(r => !roots.Any(other => CleanupPaths.IsUnder(r, other))).ToList();

        var files = new List<CleanupFile>();
        bool accessDenied = false;
        DateTime threshold = DateTime.UtcNow - TimeSpan.FromHours(Math.Max(rule.MinAgeHours, 0));
        foreach (string root in roots)
        {
            try
            {
                files.AddRange(Enumerate(root, rule, threshold, cancellation));
            }
            catch (UnauthorizedAccessException)
            {
                accessDenied = true;
            }
            catch (DirectoryNotFoundException)
            {
                // Папку удалили во время сканирования.
            }
        }

        long total = files.Sum(f => f.Size);
        CleanupScanState state = files.Count > 0
            ? CleanupScanState.Ready
            : accessDenied ? CleanupScanState.NeedsAdmin : CleanupScanState.Empty;

        return new CleanupScanResult
        {
            Rule = rule,
            State = state,
            Roots = roots,
            Files = files,
            TotalBytes = total,
            ItemCount = files.Count,
            RunningProcesses = RunningProcesses(rule),
        };
    }

    private static IEnumerable<CleanupFile> Enumerate(string root, CleanupRule rule, DateTime threshold, CancellationToken cancellation)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = rule.Recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        var enumerable = new FileSystemEnumerable<CleanupFile>(
            root,
            (ref FileSystemEntry entry) => new CleanupFile(entry.ToFullPath(), entry.Length),
            options)
        {
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                (entry.Attributes & FileAttributes.ReparsePoint) == 0,
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory
                && (entry.Attributes & FileAttributes.ReparsePoint) == 0
                && entry.LastWriteTimeUtc.UtcDateTime <= threshold
                && MatchesAny(rule.Patterns, entry.FileName),
        };

        foreach (CleanupFile file in enumerable)
        {
            cancellation.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private static bool MatchesAny(List<string> patterns, ReadOnlySpan<char> name)
    {
        foreach (string pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
                return true;
        }

        return false;
    }

    private static (CleanupRuleResult Result, int Folders) CleanFiles(CleanupScanResult scan, CancellationToken cancellation)
    {
        CleanupRule rule = scan.Rule;
        DateTime threshold = DateTime.UtcNow - TimeSpan.FromHours(Math.Max(rule.MinAgeHours, 0));
        long freed = 0;
        int deleted = 0;
        int skipped = 0;

        foreach (CleanupFile file in scan.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!scan.Roots.Any(root => CleanupPaths.IsUnder(file.Path, root)))
            {
                skipped++;
                continue;
            }

            try
            {
                var info = new FileInfo(file.Path);
                if (!info.Exists)
                    continue;

                // Файл могли заменить ссылкой или обновить после сканирования.
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LastWriteTimeUtc > threshold)
                {
                    skipped++;
                    continue;
                }

                long size = info.Length;
                if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                    info.Attributes &= ~FileAttributes.ReadOnly;

                info.Delete();
                freed += size;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл занят программой или нет прав — пропускаем.
                skipped++;
            }
        }

        int folders = rule.RemoveEmptyFolders && rule.Recursive
            ? scan.Roots.Sum(root => RemoveEmptyFolders(root, threshold))
            : 0;

        return (new CleanupRuleResult(rule.Id, rule.Title, freed, deleted, skipped), folders);
    }

    /// <summary>Удаляет пустые вложенные папки снизу вверх. Сама папка правила остаётся.</summary>
    private static int RemoveEmptyFolders(string root, DateTime threshold)
    {
        int removed = 0;
        List<string> directories;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            directories = new FileSystemEnumerable<string>(root, (ref FileSystemEntry e) => e.ToFullPath(), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) == 0,
                ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
            }.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (string directory in directories.OrderByDescending(d => d.Length))
        {
            try
            {
                if (!CleanupPaths.IsUnder(directory, root) || CleanupPaths.IsReparsePoint(directory))
                    continue;

                // Время создания, а не изменения: после удаления файлов папка выглядит «свежей».
                if (Directory.GetCreationTimeUtc(directory) > threshold || Directory.EnumerateFileSystemEntries(directory).Any())
                    continue;

                Directory.Delete(directory, recursive: false);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Папка занята или появились новые файлы — оставляем.
            }
        }

        return removed;
    }

    private static IReadOnlyList<string> RunningProcesses(CleanupRule rule)
    {
        var running = new List<string>();
        foreach (string name in rule.Processes)
        {
            Process[] processes = Process.GetProcessesByName(name);
            if (processes.Length > 0)
                running.Add(name);

            foreach (Process process in processes)
                process.Dispose();
        }

        return running;
    }
}
