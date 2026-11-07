using PcToolkit.Core.Changes;
using PcToolkit.Core.Tweaks;

namespace PcToolkit.Core.Service;

/// <summary>
/// Решает, можно ли службе выполнить запрос на изменение.
/// Служба работает с правами SYSTEM, поэтому принимает только параметры, которые описаны в каталоге настроек
/// и действительно требуют прав администратора. Произвольный раздел реестра или службу через неё изменить нельзя.
/// </summary>
public sealed class PrivilegedChangeValidator
{
    private readonly ITargetFactory _factory;
    private readonly HashSet<string> _allowedKeys;

    public PrivilegedChangeValidator(TweakCatalog catalog, ITargetFactory factory)
    {
        _factory = factory;
        _allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChangeDefinition change in catalog.Tweaks.SelectMany(t => t.Changes))
        {
            ISettingTarget target = factory.Resolve(change).Target;
            if (target.RequiresAdmin)
                _allowedKeys.Add(target.Key);
        }
    }

    public int AllowedCount => _allowedKeys.Count;

    public bool IsAllowed(ChangeDefinition change, ValueSnapshot value, out ResolvedChange? resolved, out string? reason)
    {
        resolved = null;
        try
        {
            resolved = _factory.Resolve(change);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or FormatException)
        {
            reason = "некорректное описание параметра: " + ex.Message;
            return false;
        }

        if (!resolved.Target.RequiresAdmin)
        {
            reason = "этот параметр меняется без прав администратора, служба его не трогает";
            return false;
        }

        if (!_allowedKeys.Contains(resolved.Target.Key))
        {
            reason = $"параметр «{resolved.Target.DisplayName}» не входит в каталог настроек PC Toolkit";
            return false;
        }

        if (value.Exists && !string.Equals(value.Kind, resolved.Desired.Kind, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"неверный тип значения «{value.Kind}» для «{resolved.Target.DisplayName}»";
            return false;
        }

        reason = null;
        return true;
    }
}

/// <summary>
/// Фабрика параметров для приложения без прав администратора: параметры, которым нужны права,
/// записываются через службу. Чтение остаётся в приложении — читать эти значения права не нужны.
/// </summary>
public sealed class ServiceRoutingTargetFactory : ITargetFactory
{
    private readonly ITargetFactory _inner;
    private readonly Func<bool> _isElevated;
    private readonly Func<IPrivilegedWriter?> _writer;

    public ServiceRoutingTargetFactory(ITargetFactory inner, Func<bool> isElevated, Func<IPrivilegedWriter?> writer)
    {
        _inner = inner;
        _isElevated = isElevated;
        _writer = writer;
    }

    public ResolvedChange Resolve(ChangeDefinition definition)
    {
        ResolvedChange resolved = _inner.Resolve(definition);
        if (!resolved.Target.RequiresAdmin || _isElevated() || _writer() is not { } writer)
            return resolved;

        return resolved with { Target = new ServiceRoutedTarget(definition, resolved.Target, writer) };
    }

    private sealed class ServiceRoutedTarget(ChangeDefinition definition, ISettingTarget local, IPrivilegedWriter writer) : ISettingTarget
    {
        public string Key => local.Key;

        public string DisplayName => local.DisplayName;

        /// <summary>Права есть у службы, поэтому для движка изменений параметр доступен.</summary>
        public bool RequiresAdmin => false;

        public bool IsAvailable(out string? reason) => local.IsAvailable(out reason);

        public ValueSnapshot Read() => local.Read();

        public void Write(ValueSnapshot value) => writer.Write(definition, value);

        public string Describe(ValueSnapshot value) => local.Describe(value);
    }
}
