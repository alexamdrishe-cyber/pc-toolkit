namespace PcToolkit.Core.Changes;

/// <summary>
/// Значение параметра системы. <see cref="Exists"/> = false означает, что параметра нет
/// (например, значение в реестре отсутствует). Данные хранятся строкой, чтобы их можно было записать в журнал.
/// </summary>
public sealed record ValueSnapshot(bool Exists, string Kind, string Data)
{
    public static ValueSnapshot Missing(string kind) => new(false, kind, "");

    public bool SameAs(ValueSnapshot other) =>
        Exists == other.Exists
        && (!Exists
            || (string.Equals(Kind, other.Kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Data, other.Data, StringComparison.Ordinal)));
}

/// <summary>Что нужно сделать, чтобы изменение вступило в силу.</summary>
public enum RestartRequirement
{
    None,
    Logoff,
    Reboot,
}

public static class RestartRequirementText
{
    public static string Describe(RestartRequirement restart) => restart switch
    {
        RestartRequirement.Logoff => "нужен повторный вход в Windows",
        RestartRequirement.Reboot => "нужна перезагрузка",
        _ => "сразу",
    };

    public static RestartRequirement Max(RestartRequirement a, RestartRequirement b) => (RestartRequirement)Math.Max((int)a, (int)b);
}
