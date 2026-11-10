using System.Buffers.Binary;
using System.Text;
using PcToolkit.Core.Diagnostics;

namespace PcToolkit.Core.Tests;

public sealed class KernelDumpTests
{
    /// <summary>Синтетический triage-дамп: заголовок PAGEDU64, список драйверов (запись 0xA8 байт), строки и стек.</summary>
    private static byte[] Dump(uint code, ulong[] parameters, (string Name, ulong Base, uint Size)[] drivers, ulong[] stack)
    {
        var data = new byte[0x8000];
        Encoding.ASCII.GetBytes("PAGEDU64").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x38), code);
        for (int i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x40 + i * 8), parameters[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xF98), 4);

        const int list = 0x3000, pool = 0x5000, stackAt = 0x6000;
        int t = 0x2000;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(t + 0x28), stackAt);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(t + 0x2C), (uint)(stack.Length * 8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(t + 0x30), list);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(t + 0x34), (uint)drivers.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(t + 0x38), pool);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(t + 0x3C), 0x800);

        int stringAt = pool;
        for (int i = 0; i < drivers.Length; i++)
        {
            int entry = list + i * 0xA8;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entry), (uint)stringAt);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entry + 0x38), drivers[i].Base);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entry + 0x48), drivers[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(stringAt), (uint)drivers[i].Name.Length);
            Encoding.Unicode.GetBytes(drivers[i].Name).CopyTo(data, stringAt + 4);
            stringAt += 4 + (drivers[i].Name.Length + 1) * 2;
        }

        for (int i = 0; i < stack.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(stackAt + i * 8), stack[i]);
        return data;
    }

    private static readonly (string, ulong, uint)[] Drivers =
    {
        ("ntoskrnl.exe", 0xFFFFF80000000000, 0x1000000),
        ("hal.dll", 0xFFFFF80001000000, 0x100000),
        ("nvlddmkm.sys", 0xFFFFF80010000000, 0x3000000),
        ("dxgkrnl.sys", 0xFFFFF80020000000, 0x400000),
    };

    [Fact]
    public void CulpritFromParameterAddress()
    {
        byte[] data = Dump(0x116, new ulong[] { 0xFFFFA00000001000, 0xFFFFF80010123456, 0, 2 }, Drivers,
                           new ulong[] { 0xFFFFF80000001234, 0xFFFFF80020000010 });
        KernelDumpInfo info = KernelDumpReader.Parse(data, "x.dmp", DateTime.Now)!;

        Assert.Equal(0x116u, info.BugCheckCode);
        Assert.Equal("nvlddmkm.sys", info.Culprit);
        Assert.False(info.CulpritIsWindows);
        Assert.Contains("ntoskrnl.exe", info.StackDrivers);
    }

    [Fact]
    public void CulpritFromStackWhenParametersPointNowhere()
    {
        byte[] data = Dump(0x133, new ulong[] { 1, 0x1E00, 0, 0 }, Drivers,
                           new ulong[] { 0x1234, 0xFFFFF80000001234, 0xFFFFF80010000100 });
        Assert.Equal("nvlddmkm.sys", KernelDumpReader.Parse(data, "x.dmp", DateTime.Now)!.Culprit);
    }

    [Fact]
    public void NotADumpReturnsNull() => Assert.Null(KernelDumpReader.Parse(new byte[0x3000], "x.dmp", DateTime.Now));

    [Fact]
    public void KnownDriversAreDescribed()
    {
        Assert.Contains("NVIDIA", KernelDumpReader.Describe("nvlddmkm.sys"));
        Assert.Contains("AMD", KernelDumpReader.Describe("amdkmdag.sys"));
        Assert.Equal("", KernelDumpReader.Describe("unknown123.sys"));
    }
}
