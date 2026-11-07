namespace PcToolkit.Core.Changes;

/// <summary>
/// Описание одного изменяемого параметра. Используется в tweaks.json (вместе с желаемым значением)
/// и в журнале (там важно только, какой это параметр).
/// </summary>
public sealed class ChangeDefinition
{
    /// <summary>registry | service | powerScheme</summary>
    public string Type { get; set; } = "";

    /// <summary>Реестр: путь вида HKCU\Software\... или HKLM\SOFTWARE\...</summary>
    public string? Path { get; set; }

    /// <summary>Реестр: имя значения. Пустая строка — значение по умолчанию раздела.</summary>
    public string? Name { get; set; }

    /// <summary>Реестр: DWord, QWord, String, ExpandString, MultiString, Binary.</summary>
    public string? Kind { get; set; }

    /// <summary>Реестр: желаемое значение строкой. null — удалить значение.</summary>
    public string? Value { get; set; }

    /// <summary>Служба: имя службы.</summary>
    public string? Service { get; set; }

    /// <summary>Служба: auto | auto-delayed | manual | disabled.</summary>
    public string? Start { get; set; }

    /// <summary>Электропитание: GUID схемы, которую нужно сделать активной.</summary>
    public string? Scheme { get; set; }
}
