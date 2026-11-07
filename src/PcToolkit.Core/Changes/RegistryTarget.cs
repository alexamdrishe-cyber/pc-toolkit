using System.Globalization;
using Microsoft.Win32;

namespace PcToolkit.Core.Changes;

/// <summary>Значение в реестре (HKCU или HKLM, 64-битное представление).</summary>
public sealed class RegistryTarget : ISettingTarget
{
    private readonly RegistryHive _hive;
    private readonly string _subKey;
    private readonly string _valueName;
    private readonly RegistryValueKind _kind;

    public RegistryTarget(string path, string valueName, RegistryValueKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        int slash = path.IndexOf('\\');
        string root = slash < 0 ? path : path[..slash];
        _subKey = slash < 0 ? "" : path[(slash + 1)..].Trim('\\');
        _valueName = valueName;
        _kind = kind;

        (_hive, string shortRoot) = root.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => (RegistryHive.CurrentUser, "HKCU"),
            "HKLM" or "HKEY_LOCAL_MACHINE" => (RegistryHive.LocalMachine, "HKLM"),
            _ => throw new ArgumentException($"Неподдерживаемый раздел реестра «{root}». Допустимы HKCU и HKLM."),
        };

        if (_subKey.Length == 0)
            throw new ArgumentException("Путь в реестре должен содержать раздел, а не только корень.");

        string shownName = valueName.Length == 0 ? "(по умолчанию)" : valueName;
        DisplayName = $@"{shortRoot}\{_subKey} → {shownName}";
        Key = $@"registry:{shortRoot}\{_subKey}\{valueName}".ToLowerInvariant();
    }

    public string Key { get; }

    public string DisplayName { get; }

    public bool RequiresAdmin => _hive == RegistryHive.LocalMachine;

    public bool IsAvailable(out string? reason)
    {
        reason = null;
        return true;
    }

    public ValueSnapshot Read()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey(_subKey);
        object? value = key?.GetValue(_valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (key is null || value is null)
            return ValueSnapshot.Missing(_kind.ToString());

        RegistryValueKind actualKind = key.GetValueKind(_valueName);
        return new ValueSnapshot(true, actualKind.ToString(), Serialize(value, actualKind));
    }

    public void Write(ValueSnapshot value)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);

        if (!value.Exists)
        {
            using RegistryKey? existing = baseKey.OpenSubKey(_subKey, writable: true);
            existing?.DeleteValue(_valueName, throwOnMissingValue: false);
            return;
        }

        RegistryValueKind kind = Enum.Parse<RegistryValueKind>(value.Kind, ignoreCase: true);
        using RegistryKey key = baseKey.CreateSubKey(_subKey, writable: true);
        key.SetValue(_valueName, Deserialize(value.Data, kind), kind);
    }

    public string Describe(ValueSnapshot value)
    {
        if (!value.Exists)
            return "не задано";

        RegistryValueKind kind = Enum.TryParse(value.Kind, ignoreCase: true, out RegistryValueKind parsed)
            ? parsed
            : RegistryValueKind.Unknown;

        return kind switch
        {
            RegistryValueKind.DWord or RegistryValueKind.QWord
                when ulong.TryParse(value.Data, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) && number > 0xFFFF
                => $"{number} (0x{number:X})",
            RegistryValueKind.DWord or RegistryValueKind.QWord => value.Data,
            RegistryValueKind.String or RegistryValueKind.ExpandString or RegistryValueKind.MultiString => $"«{value.Data}»",
            _ => value.Data.Length > 32 ? value.Data[..32] + "…" : value.Data,
        };
    }

    public static string Serialize(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((uint)Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => string.Join("\n", (string[])value),
        RegistryValueKind.Binary => Convert.ToHexString((byte[])value),
        _ => value is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    public static object Deserialize(string data, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)uint.Parse(data, NumberStyles.None, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => unchecked((long)ulong.Parse(data, NumberStyles.None, CultureInfo.InvariantCulture)),
        RegistryValueKind.MultiString => data.Split('\n'),
        RegistryValueKind.Binary => Convert.FromHexString(data),
        _ => data,
    };
}
