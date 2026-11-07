namespace PcToolkit.Core.Changes;

/// <summary>
/// Один параметр системы, который движок умеет прочитать и записать.
/// Только движок изменений вызывает <see cref="Write"/>: модули сами систему не меняют.
/// </summary>
public interface ISettingTarget
{
    /// <summary>Уникальный ключ параметра. По нему движок находит конфликты и владельца.</summary>
    string Key { get; }

    /// <summary>Понятное описание: путь в реестре, имя службы и т. п.</summary>
    string DisplayName { get; }

    bool RequiresAdmin { get; }

    /// <summary>Есть ли параметр на этом компьютере (например, установлена ли служба).</summary>
    bool IsAvailable(out string? reason);

    ValueSnapshot Read();

    void Write(ValueSnapshot value);

    /// <summary>Значение в понятном человеку виде для предпросмотра и журнала.</summary>
    string Describe(ValueSnapshot value);
}

/// <summary>Параметр и желаемое для него значение.</summary>
public sealed record ResolvedChange(ISettingTarget Target, ValueSnapshot Desired);

/// <summary>Создаёт параметры по описанию. В тестах подменяется на параметры в памяти.</summary>
public interface ITargetFactory
{
    ResolvedChange Resolve(ChangeDefinition definition);
}
