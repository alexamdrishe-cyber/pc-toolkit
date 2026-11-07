using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PcToolkit.Core.Changes;

/// <summary>Активная схема электропитания Windows.</summary>
public sealed class PowerSchemeTarget : ISettingTarget
{
    public const string KindName = "PowerScheme";

    private readonly Guid? _requiredScheme;

    /// <param name="requiredScheme">Схема, которую собираемся включить: по ней проверяется, есть ли она на компьютере.</param>
    public PowerSchemeTarget(Guid? requiredScheme = null)
    {
        _requiredScheme = requiredScheme;
    }

    public string Key => "power:active-scheme";

    public string DisplayName => "Активная схема электропитания";

    public bool RequiresAdmin => false;

    public bool IsAvailable(out string? reason)
    {
        if (_requiredScheme is Guid scheme && ReadFriendlyName(scheme) is null)
        {
            reason = "нужной схемы электропитания нет на этом компьютере";
            return false;
        }

        reason = null;
        return true;
    }

    public ValueSnapshot Read()
    {
        uint error = PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer);
        if (error != 0)
            throw new Win32Exception((int)error, "Не удалось прочитать активную схему электропитания.");

        try
        {
            Guid scheme = Marshal.PtrToStructure<Guid>(pointer);
            return new ValueSnapshot(true, KindName, scheme.ToString("D"));
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public void Write(ValueSnapshot value)
    {
        if (!value.Exists || !Guid.TryParse(value.Data, out Guid scheme))
            throw new InvalidOperationException($"Неверная схема электропитания «{value.Data}».");

        uint error = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (error != 0)
            throw new Win32Exception((int)error, "Не удалось включить схему электропитания.");
    }

    public string Describe(ValueSnapshot value)
    {
        if (!value.Exists || !Guid.TryParse(value.Data, out Guid scheme))
            return "не задано";

        return ReadFriendlyName(scheme) ?? scheme.ToString("D");
    }

    /// <summary>Название схемы на языке системы или null, если такой схемы нет.</summary>
    private static string? ReadFriendlyName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0)
            return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
                return null;

            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid,
        IntPtr buffer,
        ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
