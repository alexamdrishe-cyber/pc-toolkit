using System.Text.Json;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Service;
using PcToolkit.Core.Tweaks;

namespace PcToolkit.Core.Tests;

public sealed class ServiceTests
{
    private static PrivilegedChangeValidator RealValidator()
    {
        var factory = new SystemTargetFactory();
        return new PrivilegedChangeValidator(TweakCatalog.LoadEmbedded(factory), factory);
    }

    [Fact]
    public void ValidatorAllowsAdminChangeFromCatalog()
    {
        var factory = new SystemTargetFactory();
        TweakCatalog catalog = TweakCatalog.LoadEmbedded(factory);
        ChangeDefinition adminChange = catalog.Tweaks
            .SelectMany(t => t.Changes)
            .First(c => factory.Resolve(c).Target.RequiresAdmin);
        ValueSnapshot desired = factory.Resolve(adminChange).Desired;

        bool allowed = RealValidator().IsAllowed(adminChange, desired, out _, out string? reason);

        Assert.True(allowed, reason);
    }

    [Fact]
    public void ValidatorRejectsArbitraryMachineKey()
    {
        var change = new ChangeDefinition
        {
            Type = "registry",
            Path = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            Name = "Evil",
            Kind = "String",
            Value = @"C:\evil.exe",
        };

        Assert.False(RealValidator().IsAllowed(change, new ValueSnapshot(true, "String", @"C:\evil.exe"), out _, out _));
    }

    [Fact]
    public void ValidatorRejectsUserLevelChange()
    {
        var change = new ChangeDefinition
        {
            Type = "registry", Path = @"HKCU\Control Panel\Desktop", Name = "MenuShowDelay", Kind = "String", Value = "100",
        };

        Assert.False(RealValidator().IsAllowed(change, new ValueSnapshot(true, "String", "100"), out _, out _));
    }

    [Fact]
    public void ValidatorRejectsUnknownService()
    {
        var change = new ChangeDefinition { Type = "service", Service = "WinDefend", Start = "disabled" };

        Assert.False(RealValidator().IsAllowed(change, new ValueSnapshot(true, ServiceStartTarget.KindName, "disabled"), out _, out _));
    }

    [Fact]
    public void AdminTargetsAreRoutedThroughServiceWhenNotElevated()
    {
        var system = new FakeSystem();
        system.AdminOnly.Add("machine");
        var writer = new RecordingWriter();
        var factory = new ServiceRoutingTargetFactory(system, isElevated: () => false, writer: () => writer);

        ResolvedChange machine = factory.Resolve(new ChangeDefinition { Type = "fake", Path = "machine", Value = "1" });
        ResolvedChange user = factory.Resolve(new ChangeDefinition { Type = "fake", Path = "user", Value = "1" });
        machine.Target.Write(machine.Desired);
        user.Target.Write(user.Desired);

        Assert.False(machine.Target.RequiresAdmin);
        Assert.Single(writer.Writes);
        Assert.Equal("machine", writer.Writes[0].Path);
        Assert.Equal("1", system.Get("user"));
        Assert.Null(system.Get("machine"));
    }

    [Fact]
    public void WithoutServiceAdminTargetsStayAdminOnly()
    {
        var system = new FakeSystem();
        system.AdminOnly.Add("machine");
        var factory = new ServiceRoutingTargetFactory(system, isElevated: () => false, writer: () => null);

        Assert.True(factory.Resolve(new ChangeDefinition { Type = "fake", Path = "machine", Value = "1" }).Target.RequiresAdmin);
    }

    [Fact]
    public void SnapshotSurvivesJsonRoundTrip()
    {
        var snapshot = new HardwareSnapshot(DateTime.UtcNow, new[]
        {
            new DeviceSnapshot("cpu", "Ryzen", DeviceKind.Cpu, new[]
            {
                new SensorReading("t", "Core (Tctl/Tdie)", SensorKind.Temperature, 50.5f, 40f, null),
            }),
        });
        var response = new ServiceResponse { Ok = true, Snapshot = snapshot, SensorState = SensorSourceState.Running };

        string json = JsonSerializer.Serialize(response, ServiceProtocol.Json);
        ServiceResponse? back = JsonSerializer.Deserialize<ServiceResponse>(json, ServiceProtocol.Json);

        Assert.NotNull(back?.Snapshot);
        SensorReading reading = back!.Snapshot!.Devices[0].Sensors[0];
        Assert.Equal(50.5f, reading.Value);
        Assert.Null(reading.Max);
        Assert.Equal(SensorKind.Temperature, reading.Kind);
        Assert.Equal(SensorSourceState.Running, back.SensorState);
    }

    private sealed class RecordingWriter : IPrivilegedWriter
    {
        public List<ChangeDefinition> Writes { get; } = new();

        public void Write(ChangeDefinition change, ValueSnapshot value) => Writes.Add(change);
    }
}
