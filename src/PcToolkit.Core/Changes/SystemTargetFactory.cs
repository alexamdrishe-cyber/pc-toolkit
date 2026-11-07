using Microsoft.Win32;
using PcToolkit.Core.Overclock;

namespace PcToolkit.Core.Changes;

/// <summary>Создаёт настоящие параметры системы по описанию из tweaks.json или журнала.</summary>
public sealed class SystemTargetFactory : ITargetFactory
{
    public ResolvedChange Resolve(ChangeDefinition definition)
    {
        switch (definition.Type.ToLowerInvariant())
        {
            case "registry":
            {
                RegistryValueKind kind = ParseRegistryKind(definition.Kind);
                var target = new RegistryTarget(Require(definition.Path, "path"), definition.Name ?? "", kind);
                ValueSnapshot desired = definition.Value is null
                    ? ValueSnapshot.Missing(kind.ToString())
                    : new ValueSnapshot(true, kind.ToString(), definition.Value);

                // Сразу проверяем, что значение записывается в указанном формате.
                if (desired.Exists)
                    RegistryTarget.Deserialize(desired.Data, kind);

                return new ResolvedChange(target, desired);
            }

            case "service":
            {
                if (definition.Start is not null && !ServiceStartTarget.IsValidStartMode(definition.Start))
                    throw new ArgumentException($"Неизвестный тип запуска службы «{definition.Start}».");

                var target = new ServiceStartTarget(Require(definition.Service, "service"));
                return new ResolvedChange(target, new ValueSnapshot(true, ServiceStartTarget.KindName, definition.Start ?? ""));
            }

            case "powerscheme":
            {
                Guid? scheme = definition.Scheme is null ? null : Guid.Parse(definition.Scheme);
                var target = new PowerSchemeTarget(scheme);
                return new ResolvedChange(target, new ValueSnapshot(true, PowerSchemeTarget.KindName, scheme?.ToString("D") ?? ""));
            }

            case GpuTuningTarget.TypeName:
            {
                var target = new GpuTuningTarget(Require(definition.Name, "name"));
                return new ResolvedChange(target, new ValueSnapshot(true, GpuTuningTarget.KindName, Require(definition.Value, "value")));
            }

            default:
                throw new NotSupportedException($"Неизвестный тип параметра «{definition.Type}».");
        }
    }

    private static RegistryValueKind ParseRegistryKind(string? kind)
    {
        if (Enum.TryParse(kind, ignoreCase: true, out RegistryValueKind parsed)
            && parsed is RegistryValueKind.DWord or RegistryValueKind.QWord or RegistryValueKind.String
                or RegistryValueKind.ExpandString or RegistryValueKind.MultiString or RegistryValueKind.Binary)
        {
            return parsed;
        }

        throw new ArgumentException($"Неподдерживаемый тип значения реестра «{kind}».");
    }

    private static string Require(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"Не заполнено поле «{field}».") : value;
}
