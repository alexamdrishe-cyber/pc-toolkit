using System.Diagnostics;
using System.IO.Enumeration;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Storage;

/// <summary>Папка в дереве размеров.</summary>
public sealed class FolderNode
{
    private readonly List<FolderNode> _children = new();

    public FolderNode(string name, string fullPath, FolderNode? parent)
    {
        Name = name;
        FullPath = fullPath;
        Parent = parent;
    }

    public string Name { get; }

    public string FullPath { get; }

    public FolderNode? Parent { get; }

    /// <summary>Размер вместе со всеми вложенными папками.</summary>
    public long Size { get; internal set; }

    /// <summary>Размер файлов, лежащих прямо в этой папке.</summary>
    public long OwnFilesSize { get; internal set; }

    public long FileCount { get; internal set; }

    public long OwnFileCount { get; internal set; }

    /// <summary>Папку не удалось прочитать (нет прав).</summary>
    public bool Inaccessible { get; internal set; }

    /// <summary>Вложенные папки, от больших к маленьким (после завершения сканирования).</summary>
    public IReadOnlyList<FolderNode> Children => _children;

    internal void AddChild(FolderNode child) => _children.Add(child);

    internal void SortChildren() => _children.Sort((a, b) => b.Size.CompareTo(a.Size));
}

public sealed record LargeFile(string Path, long Size, DateTime LastWriteUtc);

public sealed record DiskScanProgress(long Files, long Bytes, string CurrentFolder);

public sealed class DiskScanResult
{
    public required FolderNode Root { get; init; }

    /// <summary>Самые крупные файлы, от большего к меньшему.</summary>
    public required IReadOnlyList<LargeFile> LargestFiles { get; init; }

    public int InaccessibleFolders { get; init; }

    public long Files { get; init; }

    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Считает, сколько места занимает каждая папка. Только читает: ничего не меняет и не удаляет.
/// Не заходит по ссылкам на другие папки (junction, symlink) и не учитывает облачные файлы-заглушки
/// (OneDrive и подобные), которые на диске места не занимают.
/// </summary>
public static class DiskScanner
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;
    private const FileAttributes CloudOnly = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    public static DiskScanResult Scan(
        string rootPath,
        IProgress<DiskScanProgress>? progress = null,
        CancellationToken cancellation = default,
        int largestCount = 50)
    {
        var watch = Stopwatch.StartNew();
        string fullRoot = Path.GetFullPath(rootPath);
        var root = new FolderNode(DisplayName(fullRoot), fullRoot, null);
        var discovered = new List<FolderNode> { root };
        var pending = new Stack<FolderNode>();
        pending.Push(root);

        var largest = new PriorityQueue<LargeFile, long>();
        long largestThreshold = 0;
        long files = 0;
        long bytes = 0;
        int inaccessible = 0;
        long lastReport = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            FolderNode folder = pending.Pop();
            long threshold = largestThreshold;

            try
            {
                var entries = new FileSystemEnumerable<Entry>(
                    folder.FullPath,
                    (ref FileSystemEntry e) =>
                    {
                        bool isDirectory = e.IsDirectory;
                        bool include = isDirectory || e.Length > threshold;
                        return new Entry(
                            include ? e.ToFullPath() : null,
                            isDirectory ? e.FileName.ToString() : null,
                            isDirectory,
                            e.Length,
                            e.Attributes,
                            isDirectory ? default : e.LastWriteTimeUtc.UtcDateTime);
                    },
                    options);

                foreach (Entry entry in entries)
                {
                    if (entry.IsDirectory)
                    {
                        // Ссылки на другие папки не открываем: иначе один и тот же объём посчитается дважды.
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;

                        var child = new FolderNode(entry.Name!, entry.FullPath!, folder);
                        folder.AddChild(child);
                        discovered.Add(child);
                        pending.Push(child);
                        continue;
                    }

                    if ((entry.Attributes & CloudOnly) != 0)
                        continue;

                    folder.OwnFilesSize += entry.Length;
                    folder.OwnFileCount++;
                    files++;
                    bytes += entry.Length;

                    if (entry.FullPath is not null && largestCount > 0)
                    {
                        largest.Enqueue(new LargeFile(entry.FullPath, entry.Length, entry.LastWriteUtc), entry.Length);
                        if (largest.Count > largestCount)
                            largest.Dequeue();
                        if (largest.Count == largestCount)
                            largestThreshold = largest.Peek().Size;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                folder.Inaccessible = true;
                inaccessible++;
            }
            catch (IOException)
            {
                // Папку удалили во время сканирования или путь слишком длинный — пропускаем.
                folder.Inaccessible = true;
                inaccessible++;
            }

            if (progress is not null && watch.ElapsedMilliseconds - lastReport >= 200)
            {
                lastReport = watch.ElapsedMilliseconds;
                progress.Report(new DiskScanProgress(files, bytes, folder.FullPath));
            }
        }

        // Размеры снизу вверх: каждая папка найдена позже своей родительской.
        for (int i = discovered.Count - 1; i >= 0; i--)
        {
            FolderNode node = discovered[i];
            node.Size += node.OwnFilesSize;
            node.FileCount += node.OwnFileCount;
            node.SortChildren();
            if (node.Parent is not null)
            {
                node.Parent.Size += node.Size;
                node.Parent.FileCount += node.FileCount;
            }
        }

        var top = new List<LargeFile>(largest.Count);
        while (largest.Count > 0)
            top.Add(largest.Dequeue());
        top.Reverse();

        watch.Stop();
        progress?.Report(new DiskScanProgress(files, bytes, fullRoot));
        AppLog.Info($"Анализ диска {fullRoot}: файлов {files}, {Formatting.Bytes((ulong)bytes)}, " +
                    $"недоступно папок {inaccessible}, {watch.Elapsed.TotalSeconds:0.0} с.");

        return new DiskScanResult
        {
            Root = root,
            LargestFiles = top,
            InaccessibleFolders = inaccessible,
            Files = files,
            Duration = watch.Elapsed,
        };
    }

    private static string DisplayName(string fullPath)
    {
        string trimmed = fullPath.TrimEnd('\\');
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private readonly record struct Entry(
        string? FullPath,
        string? Name,
        bool IsDirectory,
        long Length,
        FileAttributes Attributes,
        DateTime LastWriteUtc);
}
