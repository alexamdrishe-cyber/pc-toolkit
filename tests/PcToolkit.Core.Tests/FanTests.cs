using PcToolkit.Core.Fans;
using PcToolkit.Core.Overclock;
using PcToolkit.Core.Sensors;

namespace PcToolkit.Core.Tests;

public sealed class FanTests
{
    private static FanChannel Board(int index, double? rpm = 900) =>
        new($"/lpc/it8655e/0/control/{index}", $"Fan #{index + 1}", "ITE IT8655E", FanChannelKind.Motherboard, rpm, null);

    private static FanSettings Enabled(FanMode mode = FanMode.Quiet)
    {
        var settings = new FanSettings { Enabled = true, Mode = mode };
        settings.Channels.Add(new FanChannelConfig { Id = Board(0).Id, Role = FanRole.Cpu, MinDuty = 20 });
        settings.Channels.Add(new FanChannelConfig { Id = Board(1).Id, Role = FanRole.Case, MinDuty = 20 });
        return settings;
    }

    [Fact]
    public void CurveInterpolatesAndClamps()
    {
        FanCurve curve = FanCurve.Of((40, 20), (50, 30), (60, 50), (70, 70), (80, 100));

        Assert.Equal(20, curve.DutyAt(25));
        Assert.Equal(40, curve.DutyAt(55), 6);
        Assert.Equal(100, curve.DutyAt(95));
    }

    [Fact]
    public void CurveIsAlwaysFiveRisingPoints()
    {
        var messy = new FanCurve(new[] { new CurvePoint(70, 50), new CurvePoint(40, 60), new CurvePoint(40, 10) });

        Assert.Equal(FanCurve.PointCount, messy.Points.Count);
        for (int i = 1; i < messy.Points.Count; i++)
        {
            Assert.True(messy.Points[i].Temperature >= messy.Points[i - 1].Temperature + FanCurve.MinGap);
            Assert.True(messy.Points[i].Duty >= messy.Points[i - 1].Duty);
        }
    }

    [Fact]
    public void DraggingAPointKeepsOrderAndPushesNeighbours()
    {
        FanCurve curve = FanCurve.Of((40, 20), (50, 30), (60, 50), (70, 70), (80, 100));

        FanCurve moved = curve.WithPoint(2, 95, 90); // правее соседа нельзя, выше правого — тянет его вверх
        Assert.Equal(68, moved.Points[2].Temperature);
        Assert.Equal(90, moved.Points[2].Duty);
        Assert.Equal(90, moved.Points[3].Duty);
        Assert.Equal(100, moved.Points[4].Duty);

        FanCurve lowered = curve.WithPoint(2, 60, 10); // ниже левого — тянет его вниз
        Assert.Equal(10, lowered.Points[1].Duty);
        Assert.Equal(10, lowered.Points[0].Duty);
    }

    [Fact]
    public void CaseFansFollowGpuTemperature()
    {
        var controller = new FanController();
        IReadOnlyDictionary<string, double?> duties = controller.Tick(Enabled(), new[] { Board(0), Board(1) }, cpuTemperature: 40, gpuTemperature: 75);

        Assert.Equal(FanPresets.QuietCpu.DutyAt(40), duties[Board(0).Id]);
        Assert.Equal(FanPresets.QuietCase.DutyAt(75), duties[Board(1).Id]);
    }

    [Fact]
    public void SpeedRisesFastAndFallsSlowly()
    {
        var controller = new FanController();
        FanSettings settings = Enabled(FanMode.Cool);
        FanChannel cpu = Board(0);

        Assert.Equal(40, controller.Tick(settings, new[] { cpu }, 30, 30)[cpu.Id]);
        Assert.Equal(48, controller.Tick(settings, new[] { cpu }, 80, 30)[cpu.Id]); // +8 за шаг
        Assert.Equal(56, controller.Tick(settings, new[] { cpu }, 80, 30)[cpu.Id]);
        Assert.Equal(53, controller.Tick(settings, new[] { cpu }, 30, 30)[cpu.Id]); // −3 за шаг
        Assert.Equal(50, controller.Tick(settings, new[] { cpu }, 30, 30)[cpu.Id]);
    }

    [Fact]
    public void SmallDropsDoNotChangeSpeed()
    {
        var controller = new FanController();
        FanSettings settings = Enabled(FanMode.Cool);
        FanChannel cpu = Board(0);
        double first = controller.Tick(settings, new[] { cpu }, 55, 30)[cpu.Id]!.Value;
        double second = controller.Tick(settings, new[] { cpu }, 54.5, 30)[cpu.Id]!.Value; // цель ниже меньше чем на 2 %

        Assert.Equal(first, second);
    }

    [Fact]
    public void OverheatForcesFullSpeedUntilCooledDown()
    {
        var controller = new FanController();
        FanSettings settings = Enabled();
        FanChannel[] fans = { Board(0), Board(1) };

        IReadOnlyDictionary<string, double?> hot = controller.Tick(settings, fans, 40, 91);
        Assert.True(controller.Overheat);
        Assert.All(hot.Values, v => Assert.Equal(100, v));

        controller.Tick(settings, fans, 40, 85);
        Assert.True(controller.Overheat); // ещё не остыла ниже 80

        controller.Tick(settings, fans, 40, 79);
        Assert.False(controller.Overheat);
    }

    [Fact]
    public void PumpIgnoredUnresponsiveAndDisabledAreReleased()
    {
        FanSettings settings = Enabled();
        settings.Channels[0].Role = FanRole.Pump;
        settings.Channels[1].Unresponsive = true;
        var controller = new FanController();

        IReadOnlyDictionary<string, double?> duties = controller.Tick(settings, new[] { Board(0), Board(1) }, 50, 50);
        Assert.Null(duties[Board(0).Id]);
        Assert.Null(duties[Board(1).Id]);

        settings.Enabled = false;
        settings.Channels[0].Role = FanRole.Cpu;
        Assert.Null(controller.Tick(settings, new[] { Board(0) }, 50, 50)[Board(0).Id]);
    }

    [Fact]
    public void MinimumDutyIsRespected()
    {
        FanSettings settings = Enabled();
        settings.Channels[1].MinDuty = 45;
        var controller = new FanController();

        Assert.Equal(45, controller.Tick(settings, new[] { Board(1) }, 30, 30)[Board(1).Id]);
        settings.Channels[1].MinDuty = 0;
        controller.Reset();
        Assert.Equal(25, controller.Tick(settings, new[] { Board(1) }, 20, 20)[Board(1).Id]); // первая точка «Тихо», выше общего минимума 10 %
    }

    [Fact]
    public void DefaultRoles()
    {
        Assert.Equal(FanRole.Cpu, FanController.DefaultRole(Board(0)));
        Assert.Equal(FanRole.Case, FanController.DefaultRole(Board(1)));
        Assert.Equal(FanRole.Ignore, FanController.DefaultRole(Board(2, rpm: 0)));
        Assert.Equal(FanRole.Pump, FanController.DefaultRole(Board(3) with { Name = "Pump Fan" }));
    }

    [Fact]
    public void ChannelsFromSnapshotPairRpmAndSkipAmdGpu()
    {
        var board = new DeviceSnapshot("/lpc/it8655e/0", "ITE IT8655E", DeviceKind.Motherboard, new[]
        {
            new SensorReading("/lpc/it8655e/0/control/0", "Fan #1", SensorKind.Control, null, null, null),
            new SensorReading("/lpc/it8655e/0/fan/0", "Fan #1", SensorKind.Fan, 503, 486, 755),
            new SensorReading("/lpc/it8655e/0/control/1", "Fan #2", SensorKind.Control, 40, null, null),
        });
        var amd = new DeviceSnapshot("/gpu-amd/0", "AMD Radeon RX 6700 XT", DeviceKind.GpuAmd, new[]
        {
            new SensorReading("/gpu-amd/0/control/0", "GPU Fan", SensorKind.Control, 18, 0, 61),
        });

        IReadOnlyList<FanChannel> channels = FanChannels.FromSnapshot(new HardwareSnapshot(DateTime.Now, new[] { board, amd }));

        Assert.Equal(2, channels.Count);
        Assert.Equal(503, channels[0].Rpm);
        Assert.Null(channels[1].Rpm);
        Assert.Equal("Вентилятор 1", FanChannels.DisplayName(channels[0]));
    }

    [Fact]
    public void SettingsRoundTripWithTemplates()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pct-fans-{Guid.NewGuid():N}.json");
        try
        {
            FanSettings settings = FanSettings.Load(path);
            Assert.False(settings.Enabled);
            Assert.Single(settings.Templates);

            settings.Enabled = true;
            settings.Mode = FanMode.Manual;
            settings.Templates.Add(new FanTemplate { Name = "Игры", Case = FanPresets.CoolCase, CaseSource = CaseSource.Hottest });
            settings.SelectedTemplate = "Игры";
            settings.Channels.Add(new FanChannelConfig { Id = "/lpc/x/control/0", Role = FanRole.Pump, MinDuty = 33, Calibrated = true });
            settings.Save();

            FanSettings back = FanSettings.Load(path);
            Assert.True(back.Enabled);
            Assert.Equal(FanMode.Manual, back.Mode);
            Assert.Equal("Игры", back.Active.Name);
            Assert.True(back.Active.Case.SameAs(FanPresets.CoolCase));
            Assert.Equal(CaseSource.Hottest, back.Active.CaseSource);
            Assert.Equal(FanRole.Pump, back.Channels.Single().Role);
            Assert.Equal(33, back.Channels.Single().MinDuty);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void DriverCurveFitsRangesAndStaysMonotonic()
    {
        FanCurve curve = FanCurve.Of((22, 5), (40, 20), (41, 18), (95, 70), (100, 100));
        List<FanPoint> points = AmdGpuFans.ToDriver(curve, 5, new TuningRange(25, 100, 1), new TuningRange(15, 100, 1));

        Assert.Equal(5, points.Count);
        Assert.Equal(25, points[0].Temperature); // ниже диапазона драйвера — к его минимуму
        Assert.Equal(15, points[0].Speed);
        for (int i = 1; i < points.Count; i++)
        {
            Assert.True(points[i].Temperature > points[i - 1].Temperature);
            Assert.True(points[i].Speed >= points[i - 1].Speed);
        }

        Assert.Equal(100, points[^1].Speed);
    }

    [Fact]
    public void DriverCurveResamplesOtherPointCounts()
    {
        List<FanPoint> points = AmdGpuFans.ToDriver(FanPresets.CoolGpu, 3, new TuningRange(25, 100, 5), new TuningRange(0, 100, 5));

        Assert.Equal(3, points.Count);
        Assert.Equal(40, points[0].Temperature);
        Assert.Equal(85, points[^1].Temperature);
        Assert.All(points, p => Assert.Equal(0, p.Speed % 5));
    }

    [Fact]
    public void CloneIsIndependent()
    {
        FanSettings settings = Enabled(FanMode.Manual);
        settings.Templates.Add(new FanTemplate { Name = "Мой" });
        settings.SelectedTemplate = "Мой";
        FanSettings copy = settings.Clone();

        settings.Templates[0].Case = FanPresets.CoolCase;
        settings.Channels[0].Role = FanRole.Pump;

        Assert.True(copy.Templates[0].Case.SameAs(FanPresets.QuietCase));
        Assert.Equal(FanRole.Cpu, copy.Channels[0].Role);
        Assert.Equal("Мой", copy.Active.Name);
    }
}
