using PcToolkit.Core.Changes;
using PcToolkit.Core.Tweaks;

namespace PcToolkit.Core.Tests;

/// <summary>«Система» в памяти: параметры — это просто записи в словаре. Настоящую систему тесты не трогают.</summary>
internal sealed class FakeSystem : ITargetFactory
{
    public const string Kind = "Fake";

    public Dictionary<string, ValueSnapshot> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Параметры, запись в которые завершается ошибкой.</summary>
    public HashSet<string> FailOnWrite { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Параметры, для изменения которых нужны права администратора.</summary>
    public HashSet<string> AdminOnly { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static ValueSnapshot V(string data) => new(true, Kind, data);

    public ResolvedChange Resolve(ChangeDefinition definition)
    {
        string key = definition.Path ?? throw new ArgumentException("Нет ключа");
        ValueSnapshot desired = definition.Value is null ? ValueSnapshot.Missing(Kind) : V(definition.Value);
        return new ResolvedChange(new FakeTarget(this, key), desired);
    }

    public void Set(string key, string value) => Values[key] = V(value);

    public string? Get(string key) => Values.TryGetValue(key, out ValueSnapshot? value) ? value.Data : null;

    public static TweakDefinition Tweak(string id, params (string Key, string? Value)[] changes) => new()
    {
        Id = id,
        Title = id,
        Description = "тест",
        Category = "test",
        Changes = changes.Select(c => new ChangeDefinition { Type = "fake", Path = c.Key, Value = c.Value }).ToList(),
    };

    private sealed class FakeTarget(FakeSystem system, string key) : ISettingTarget
    {
        public string Key => "fake:" + key;

        public string DisplayName => key;

        public bool RequiresAdmin => system.AdminOnly.Contains(key);

        public bool IsAvailable(out string? reason)
        {
            reason = null;
            return true;
        }

        public ValueSnapshot Read() =>
            system.Values.TryGetValue(key, out ValueSnapshot? value) ? value : ValueSnapshot.Missing(FakeSystem.Kind);

        public void Write(ValueSnapshot value)
        {
            if (system.FailOnWrite.Contains(key))
                throw new IOException("сбой записи");

            if (value.Exists)
                system.Values[key] = value;
            else
                system.Values.Remove(key);
        }

        public string Describe(ValueSnapshot value) => value.Exists ? value.Data : "не задано";
    }
}
