namespace PcToolkit.Core.Cleanup;

/// <summary>Что делает правило очистки.</summary>
public enum CleanupKind
{
    /// <summary>Удалить файлы по маскам в заданных папках.</summary>
    Files,

    /// <summary>Очистить корзину Windows (через системную функцию, а не удалением файлов).</summary>
    RecycleBin,
}

/// <summary>Одно правило очистки из cleanup.json.</summary>
public sealed class CleanupRule
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    public string Category { get; set; } = "";

    public CleanupKind Kind { get; set; } = CleanupKind.Files;

    /// <summary>
    /// Папки с переменными окружения (%LOCALAPPDATA%, %TEMP%, %WINDIR%…).
    /// «*» в середине пути означает «любая папка на этом уровне», например профили браузера.
    /// </summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>Маски имён файлов. По умолчанию — все файлы.</summary>
    public List<string> Patterns { get; set; } = new() { "*" };

    /// <summary>Искать во вложенных папках.</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>Не трогать файлы, изменённые недавно: ими может пользоваться работающая программа.</summary>
    public double MinAgeHours { get; set; }

    public bool RequiresAdmin { get; set; }

    /// <summary>Удалять опустевшие вложенные папки (сама папка правила остаётся).</summary>
    public bool RemoveEmptyFolders { get; set; } = true;

    /// <summary>Отмечено ли правило при первом показе экрана.</summary>
    public bool SelectedByDefault { get; set; } = true;

    /// <summary>Имена процессов, которые держат эти файлы открытыми (например, браузер).</summary>
    public List<string> Processes { get; set; } = new();

    /// <summary>Что важно знать перед удалением (необязательно).</summary>
    public string? Note { get; set; }
}

public sealed class CleanupCategory
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";
}

public sealed class CleanupCatalogFile
{
    public int Version { get; set; }

    public List<CleanupCategory> Categories { get; set; } = new();

    public List<CleanupRule> Rules { get; set; } = new();
}
