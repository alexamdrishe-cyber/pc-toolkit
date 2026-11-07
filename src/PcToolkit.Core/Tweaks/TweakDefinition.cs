using PcToolkit.Core.Changes;

namespace PcToolkit.Core.Tweaks;

public enum TweakRisk
{
    Low,
    Medium,
    High,
}

/// <summary>Одна настройка (твик) из tweaks.json.</summary>
public sealed class TweakDefinition
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public TweakRisk Risk { get; set; }
    public RestartRequirement Restart { get; set; }

    /// <summary>Для каких ответов анкеты настройка рекомендуется (gaming, streaming, work, general, speed, privacy).</summary>
    public List<string> RecommendedFor { get; set; } = new();

    /// <summary>Не рекомендовать на ноутбуках (например, план «Высокая производительность»).</summary>
    public bool DesktopOnly { get; set; }

    /// <summary>Минимальная и максимальная сборка Windows, на которой настройка имеет смысл.</summary>
    public int? MinBuild { get; set; }
    public int? MaxBuild { get; set; }

    /// <summary>Настройки, которые нельзя применять вместе с этой.</summary>
    public List<string> ConflictsWith { get; set; } = new();

    public List<ChangeDefinition> Changes { get; set; } = new();

    public bool IsApplicableToBuild(int build) =>
        (MinBuild is null || build >= MinBuild) && (MaxBuild is null || build <= MaxBuild);
}

public sealed class TweakCategory
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>Содержимое tweaks.json.</summary>
public sealed class TweakCatalogFile
{
    public int Version { get; set; }
    public List<TweakCategory> Categories { get; set; } = new();
    public List<TweakDefinition> Tweaks { get; set; } = new();
}
