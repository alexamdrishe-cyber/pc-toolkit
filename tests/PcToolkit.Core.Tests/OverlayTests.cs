using System.Text.Json;
using PcToolkit.Core.Games;
using PcToolkit.Core.Overlay;
using PcToolkit.Core.Sensors;
using PcToolkit.Core.Settings;

namespace PcToolkit.Core.Tests;

public sealed class OverlayLayoutTests
{
    [Fact]
    public void DefaultLayoutLooksLikeTheOldOverlay()
    {
        OverlayLayout layout = OverlayLayout.CreateDefault();

        Assert.Equal(OverlayLayout.Vertical, layout.Orientation);
        Assert.Equal(new[] { "fps", "fps.low1", "frametime", "cpu.load", "cpu.temp", "gpu.load", "gpu.temp", "gpu.power", "process" },
            layout.Items.Select(i => i.Metric));
        OverlayItem fps = layout.Items[0];
        Assert.True(fps.Big);
        Assert.Equal(60, fps.Warn);
        Assert.Equal(30, fps.Critical);
        Assert.True(layout.Items.Single(i => i.Metric == "frametime").Graph);
        Assert.True(layout.Items.Single(i => i.Metric == "cpu.temp").SameLine);
        Assert.Equal("TopLeft", layout.Position.Anchor);
        Assert.Equal(0.78, layout.Style.BackgroundOpacity);
    }

    [Fact]
    public void LegacySettingsBecomeLayout()
    {
        OverlayLayout layout = OverlayLayout.FromLegacy("BottomRight", showDetails: false, showGraph: false, showSensors: false,
            showProcess: false, scale: 1.25, opacity: 0.3);

        Assert.Equal(new[] { "fps" }, layout.Items.Select(i => i.Metric));
        Assert.Equal("BottomRight", layout.Position.Anchor);
        Assert.Equal(16.5, layout.Style.FontSize);
        Assert.Equal(0.3, layout.Style.BackgroundOpacity);
    }

    [Fact]
    public void GraphWithoutDetailsKeepsFrameTimeGraph()
    {
        OverlayLayout layout = OverlayLayout.FromLegacy("TopLeft", showDetails: false, showGraph: true, showSensors: false,
            showProcess: false, scale: 1, opacity: 0.78);

        OverlayItem frametime = Assert.Single(layout.Items, i => i.Metric == "frametime");
        Assert.True(frametime.Graph);
        Assert.False(frametime.SameLine);
    }

    [Fact]
    public void NormalizeRepairsHandEditedFile()
    {
        var layout = new OverlayLayout
        {
            Orientation = "Diagonal",
            Items = new List<OverlayItem> { new() { Metric = "  fps " }, new() { Metric = "" }, new() { Metric = "cpu.temp", Color = "f00" } },
            Style = new OverlayStyle { FontSize = 200, BackgroundOpacity = -1, ValueColor = "red", FontWeight = "Heavy", Outline = double.NaN },
            Position = new OverlayPosition { Anchor = "bottomright", X = 5, Y = double.NaN },
        }.Normalize();

        Assert.Equal(OverlayLayout.Vertical, layout.Orientation);
        Assert.Equal(new[] { "fps", "cpu.temp" }, layout.Items.Select(i => i.Metric));
        Assert.Equal("#FF0000", layout.Items[1].Color);
        Assert.Equal(40, layout.Style.FontSize);
        Assert.Equal(0, layout.Style.BackgroundOpacity);
        Assert.Equal(OverlayStyle.DefaultValueColor, layout.Style.ValueColor);
        Assert.Equal(OverlayStyle.SemiBold, layout.Style.FontWeight);
        Assert.Equal(0, layout.Style.Outline);
        Assert.Equal("BottomRight", layout.Position.Anchor);
        Assert.Equal(1, layout.Position.X);
        Assert.Equal(OverlayPosition.DefaultMarginY, layout.Position.Y);
    }

    [Theory]
    [InlineData("#34d399", "#34D399")]
    [InlineData("34D399", "#34D399")]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("#FF34D399", "#34D399")]
    [InlineData("#12345", null)]
    [InlineData("green", null)]
    [InlineData("", null)]
    public void ColorsAreValidated(string text, string? expected) => Assert.Equal(expected, OverlayStyle.ValidColor(text));

    [Fact]
    public void CloneIsDeep()
    {
        OverlayLayout layout = OverlayLayout.CreateDefault();
        OverlayLayout copy = layout.Clone();
        copy.Items[0].Big = false;
        copy.Style.FontSize = 20;
        copy.Position.Anchor = "BottomLeft";

        Assert.True(layout.Items[0].Big);
        Assert.Equal(OverlayStyle.DefaultFontSize, layout.Style.FontSize);
        Assert.Equal("TopLeft", layout.Position.Anchor);
    }

    [Fact]
    public void CornerPresetsUseUsualMargins()
    {
        Assert.Equal(0, OverlayPosition.Corner("TopCenter").X);
        Assert.Equal(OverlayPosition.DefaultMarginX, OverlayPosition.Corner("BottomRight").X);
        Assert.Equal("TopLeft", OverlayPosition.Corner("nonsense").Anchor);
    }
}

public sealed class OverlayMetricsTests
{
    private static HardwareSnapshot Snapshot() => new(DateTime.Now, new[]
    {
        new DeviceSnapshot("/amdcpu/0", "AMD Ryzen 7 5800X3D", DeviceKind.Cpu, new[]
        {
            new SensorReading("/amdcpu/0/load/0", "CPU Total", SensorKind.Load, 45.4f, null, null),
            new SensorReading("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", SensorKind.Temperature, 71.6f, null, null),
            new SensorReading("/amdcpu/0/clock/1", "Cores (Average)", SensorKind.Clock, 4512f, null, null),
            new SensorReading("/amdcpu/0/power/0", "Package", SensorKind.Power, 0f, null, null),
        }),
        new DeviceSnapshot("/gpu-amd/0", "AMD Radeon RX 6700 XT", DeviceKind.GpuAmd, new[]
        {
            new SensorReading("/gpu-amd/0/load/0", "GPU Core", SensorKind.Load, 98f, null, null),
            new SensorReading("/gpu-amd/0/temperature/0", "GPU Core", SensorKind.Temperature, 66f, null, null),
            new SensorReading("/gpu-amd/0/temperature/2", "GPU Hot Spot", SensorKind.Temperature, 84f, null, null),
            new SensorReading("/gpu-amd/0/clock/0", "GPU Core", SensorKind.Clock, 2581f, null, null),
            new SensorReading("/gpu-amd/0/clock/1", "GPU Memory", SensorKind.Clock, 1990f, null, null),
            new SensorReading("/gpu-amd/0/fan/0", "GPU Fan", SensorKind.Fan, 1430f, null, null),
            new SensorReading("/gpu-amd/0/smalldata/0", "GPU Memory Used", SensorKind.SmallData, 5324f, null, null),
            new SensorReading("/gpu-amd/0/smalldata/2", "GPU Memory Total", SensorKind.SmallData, 12272f, null, null),
            new SensorReading("/gpu-amd/0/temperature/9", "Warning Temperature", SensorKind.Temperature, 100f, null, null),
        }),
        new DeviceSnapshot("/ram", "Total Memory", DeviceKind.Memory, new[]
        {
            new SensorReading("/ram/load/0", "Memory", SensorKind.Load, 61f, null, null),
            new SensorReading("/ram/data/0", "Memory Used", SensorKind.Data, 19.6f, null, null),
            new SensorReading("/ram/data/1", "Memory Available", SensorKind.Data, 12.3f, null, null),
        }),
    });

    private static OverlaySources Sources(FrameMonitorState? frames = null, bool sample = false)
    {
        HardwareSnapshot snapshot = Snapshot();
        return new OverlaySources
        {
            Snapshot = snapshot,
            Metrics = KeyMetrics.From(snapshot),
            Frames = frames,
            Sample = sample,
            Now = new DateTime(2026, 11, 12, 21, 7, 30),
        };
    }

    private static FrameMonitorState Frames(double fps, double low = 0) =>
        new(1234, "cs2", "DX11", fps, fps > 0 ? 1000 / fps : 0, low, new[] { 7.0, 7.1 }, Array.Empty<FramePresenter>(), null, true);

    private static OverlayReading Read(string key, OverlaySources sources)
    {
        OverlayMetric metric = OverlayMetrics.Find(key, sources.Snapshot) ?? throw new InvalidOperationException(key);
        return OverlayMetrics.Read(metric, sources);
    }

    [Fact]
    public void BuiltInMetricsReadFromSensors()
    {
        OverlaySources sources = Sources();

        Assert.Equal(("45", "%"), (Read("cpu.load", sources).Text, Read("cpu.load", sources).Unit));
        Assert.Equal("72", Read("cpu.temp", sources).Text);
        Assert.Equal(("4,51", "ГГц"), (Read("cpu.clock", sources).Text, Read("cpu.clock", sources).Unit));
        Assert.Equal("2581", Read("gpu.clock", sources).Text);
        Assert.Equal("1990", Read("gpu.memclock", sources).Text);
        Assert.Equal("1430", Read("gpu.fan", sources).Text);
        Assert.Equal(("5,2", "ГБ"), (Read("gpu.vram", sources).Text, Read("gpu.vram", sources).Unit));
        Assert.Equal("84", Read("gpu.hotspot", sources).Text);
        Assert.Equal(("19,6", "ГБ"), (Read("ram.used", sources).Text, Read("ram.used", sources).Unit));
        Assert.Equal("21:07", Read("time", sources).Text);
        Assert.Equal("AMD Radeon RX 6700 XT", Read("gpu.name", sources).Text);
    }

    [Fact]
    public void MissingValuesShowDash()
    {
        OverlaySources sources = Sources();

        // Мощность процессора 0 Вт — драйвера нет, значения нет.
        Assert.Equal(OverlayReading.Missing, Read("cpu.power", sources));
        Assert.Equal(OverlayReading.Missing, Read("fps", sources));
        Assert.Equal(OverlayReading.Missing, Read("process", sources));
    }

    [Fact]
    public void FramesComeFromCounter()
    {
        OverlaySources sources = Sources(Frames(143.6, 117.2));

        Assert.Equal(("144", "FPS"), (Read("fps", sources).Text, Read("fps", sources).Unit));
        Assert.Equal("117", Read("fps.low1", sources).Text);
        Assert.Equal(("7,0", "мс"), (Read("frametime", sources).Text, Read("frametime", sources).Unit));
        Assert.Equal("cs2 · DX11", Read("process", sources).Text);
    }

    [Fact]
    public void SampleFillsFramesOnlyWithoutGame()
    {
        OverlaySources preview = Sources(sample: true);
        Assert.InRange(Read("fps", preview).Value!.Value, 130, 150);
        Assert.Equal("game · DX12", Read("process", preview).Text);

        OverlaySources live = Sources(Frames(60), sample: true);
        Assert.Equal("60", Read("fps", live).Text);
        Assert.Equal("cs2 · DX11", Read("process", live).Text);
    }

    [Fact]
    public void AnySensorCanBeShown()
    {
        HardwareSnapshot snapshot = Snapshot();
        List<OverlayMetric> sensors = OverlayMetrics.Sensors(snapshot).ToList();

        // Пороговые «температуры» — не измерения, их в списке нет.
        Assert.DoesNotContain(sensors, m => m.Key.EndsWith("temperature/9", StringComparison.Ordinal));
        Assert.Equal(15, sensors.Count);

        OverlayMetric core = Assert.Single(sensors, m => m.Key == "sensor:/amdcpu/0/temperature/2");
        Assert.Equal("CPU Tctl/Tdie", core.Label);
        Assert.Contains("AMD Ryzen 7 5800X3D", core.Title);
        Assert.Equal(85, core.Warn);

        OverlaySources sources = Sources();
        Assert.Equal(("72", "°C"), (OverlayMetrics.Read(core, sources).Text, OverlayMetrics.Read(core, sources).Unit));
        OverlayMetric vram = OverlayMetrics.Find("sensor:/gpu-amd/0/smalldata/0", snapshot)!;
        Assert.Equal(("5,2", "ГБ"), (OverlayMetrics.Read(vram, sources).Text, OverlayMetrics.Read(vram, sources).Unit));
    }

    [Fact]
    public void UnknownSensorIsNotFound()
    {
        Assert.Null(OverlayMetrics.Find("sensor:/nope", Snapshot()));
        Assert.Null(OverlayMetrics.Find("bogus"));
        OverlayMetric missing = OverlayMetrics.Missing(new OverlayItem { Metric = "sensor:/nope", Title = "Старый датчик" });
        Assert.Equal(OverlayReading.Missing, OverlayMetrics.Read(missing, Sources()));
    }

    [Theory]
    [InlineData(144, OverlayLevel.Normal)]
    [InlineData(60, OverlayLevel.Normal)]
    [InlineData(59.9, OverlayLevel.Warning)]
    [InlineData(29, OverlayLevel.Critical)]
    public void FpsThresholdsWarnWhenFalling(double fps, OverlayLevel expected) =>
        Assert.Equal(expected, OverlayMetrics.Level(fps, 60, 30, lowerIsWorse: true));

    [Theory]
    [InlineData(70, OverlayLevel.Normal)]
    [InlineData(85, OverlayLevel.Warning)]
    [InlineData(96, OverlayLevel.Critical)]
    public void TemperatureThresholdsWarnWhenRising(double temperature, OverlayLevel expected) =>
        Assert.Equal(expected, OverlayMetrics.Level(temperature, 85, 95, lowerIsWorse: false));

    [Fact]
    public void ThresholdOrderDecidesDirection()
    {
        // Пользователь задал пороги «наоборот» для датчика, где плохо падение (обороты вентилятора).
        Assert.Equal(OverlayLevel.Critical, OverlayMetrics.Level(300, 800, 500, lowerIsWorse: false));
        // Только один порог — направление из показателя.
        Assert.Equal(OverlayLevel.Warning, OverlayMetrics.Level(50, 60, null, lowerIsWorse: true));
        Assert.Equal(OverlayLevel.Normal, OverlayMetrics.Level(50, 60, null, lowerIsWorse: false));
        Assert.Equal(OverlayLevel.Normal, OverlayMetrics.Level(50, null, null, lowerIsWorse: true));
    }

    [Fact]
    public void GraphRangesOfMemoryUseTotal()
    {
        OverlaySources sources = Sources();
        Assert.Equal(((double?)0, (double?)12272), OverlayMetrics.GraphRange(OverlayMetrics.Find("gpu.vram")!, sources));
        (double? min, double? max) = OverlayMetrics.GraphRange(OverlayMetrics.Find("ram.used")!, sources);
        Assert.Equal(0, min);
        Assert.Equal(31.9, max!.Value, 3);
        Assert.Equal(((double?)0, (double?)100), OverlayMetrics.GraphRange(OverlayMetrics.Find("cpu.load")!, sources));
    }

    [Fact]
    public void NumbersAreRussian()
    {
        Assert.Equal("4,51", OverlayMetrics.Number(4.512, 2));
        Assert.Equal("0", OverlayMetrics.Number(-0.2, 0));
        Assert.Equal("-12", OverlayMetrics.Number(-12.4, 0));
        Assert.Equal("2352", OverlayMetrics.Number(2352, 0));
    }

    [Fact]
    public void SampleFrameTimesMoveWithTime()
    {
        DateTime now = new(2026, 11, 12, 21, 0, 0);
        double[] first = OverlayMetrics.SampleFrameTimes(now);
        double[] later = OverlayMetrics.SampleFrameTimes(now.AddSeconds(1));

        Assert.Equal(300, first.Length);
        Assert.All(first, v => Assert.InRange(v, 5, 25));
        // Через секунду (60 «кадров») картинка сдвинулась влево на 60 значений.
        Assert.Equal(first[60..], later[..240]);
    }

    [Fact]
    public void HistoryKeepsLastValues()
    {
        var history = new OverlayHistory(3);
        history.Push("fps", 1);
        history.Push("fps", null);
        history.Push("fps", 3);
        history.Push("fps", 4);
        history.Push("cpu.temp", 60);

        double[] fps = history.Values("fps");
        Assert.Equal(3, fps.Length);
        Assert.True(double.IsNaN(fps[0]));
        Assert.Equal(new[] { 3.0, 4.0 }, fps[1..]);

        history.Keep(new HashSet<string> { "cpu.temp" });
        Assert.Empty(history.Values("fps"));
        Assert.Single(history.Values("cpu.temp"));
    }
}

public sealed class OverlayPlacementTests
{
    private static readonly PixelRect Monitor = new(1920, 0, 1920 + 2560, 1440);

    [Fact]
    public void CornersPlaceInsideMonitor()
    {
        Assert.Equal((1920 + 19, 19), OverlayPlacement.Place(Monitor, 200, 100, OverlayPosition.Corner("TopLeft")));
        Assert.Equal((1920 + 2560 - 200 - 19, 1440 - 100 - 19), OverlayPlacement.Place(Monitor, 200, 100, OverlayPosition.Corner("BottomRight")));
        Assert.Equal((1920 + 1180, 19), OverlayPlacement.Place(Monitor, 200, 100, OverlayPosition.Corner("TopCenter")));
    }

    [Fact]
    public void OverlayNeverLeavesTheScreen()
    {
        var far = new OverlayPosition { Anchor = "TopLeft", X = 1, Y = 1 };
        Assert.Equal((1920 + 2560 - 200, 1440 - 100), OverlayPlacement.Place(Monitor, 200, 100, far));
    }

    [Fact]
    public void DroppedWindowSnapsToNearestCorner()
    {
        OverlayPosition right = OverlayPlacement.FromWindow(Monitor, new PixelRect(1920 + 2300, 50, 1920 + 2500, 150));
        Assert.Equal("TopRight", right.Anchor);
        Assert.Equal(60 / 2560.0, right.X, 3);
        Assert.Equal(50 / 1440.0, right.Y, 3);

        OverlayPosition bottomCenter = OverlayPlacement.FromWindow(Monitor, new PixelRect(1920 + 1300, 1300, 1920 + 1500, 1400));
        Assert.Equal("BottomCenter", bottomCenter.Anchor);
        Assert.Equal(120 / 2560.0, bottomCenter.X, 3);
        Assert.Equal(40 / 1440.0, bottomCenter.Y, 3);
    }

    [Fact]
    public void DroppedPositionRoundTrips()
    {
        var window = new PixelRect(1920 + 2100, 900, 1920 + 2400, 1000);
        OverlayPosition position = OverlayPlacement.FromWindow(Monitor, window);
        Assert.Equal((window.Left, window.Top), OverlayPlacement.Place(Monitor, window.Width, window.Height, position));

        // Тот же оверлей на мониторе 1920×1080 стоит в том же месте относительно угла.
        var small = new PixelRect(0, 0, 1920, 1080);
        (int x, int y) = OverlayPlacement.Place(small, window.Width, window.Height, position);
        Assert.Equal(1920 - 300 - (int)Math.Round(160 / 2560.0 * 1920), x);
        Assert.Equal(1080 - 100 - (int)Math.Round(440 / 1440.0 * 1080), y);
    }
}

public sealed class OverlaySettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pctoolkit-overlay-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, "overlay.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void FirstLoadTakesOldSettings()
    {
        var old = new AppSettings { OverlayCorner = "BottomLeft", OverlayShowSensors = false, OverlayScale = 0.8, OverlayOpacity = 0.5 };
        OverlaySettings settings = OverlaySettings.Load(FilePath, () => old);

        Assert.Equal("BottomLeft", settings.Default.Position.Anchor);
        Assert.DoesNotContain(settings.Default.Items, i => i.Metric.StartsWith("cpu", StringComparison.Ordinal));
        Assert.Equal(10.5, settings.Default.Style.FontSize);
        Assert.Equal(0.5, settings.Default.Style.BackgroundOpacity);
    }

    [Fact]
    public void SavedSettingsLoadBack()
    {
        OverlaySettings settings = OverlaySettings.Load(FilePath, () => new AppSettings());
        settings.Default.Orientation = OverlayLayout.Horizontal;
        OverlayLayout own = settings.SetOwn("steam:730", "Counter-Strike 2", own: true);
        own.Items.RemoveAll(i => i.Metric != "fps");
        own.Style.ValueColor = "#FFFFFF";
        settings.Save();

        OverlaySettings loaded = OverlaySettings.Load(FilePath, () => throw new InvalidOperationException("старые настройки не нужны"));
        Assert.True(loaded.Default.IsHorizontal);
        Assert.True(loaded.HasOwn("steam:730"));
        Assert.Equal("Counter-Strike 2", loaded.Games["steam:730"].Name);
        Assert.Equal(new[] { "fps" }, loaded.For("steam:730").Items.Select(i => i.Metric));
        Assert.Equal("#FFFFFF", loaded.For("steam:730").Style.ValueColor);
        Assert.Same(loaded.Default, loaded.For("other"));
        Assert.Same(loaded.Default, loaded.For(null));
    }

    [Fact]
    public void OwnLayoutStartsAsCopyAndSurvivesSwitchingOff()
    {
        OverlaySettings settings = OverlaySettings.Load(FilePath, () => new AppSettings());
        OverlayLayout own = settings.SetOwn("g1", "Игра", own: true);
        Assert.NotSame(settings.Default, own);
        Assert.Equal(settings.Default.Items.Count, own.Items.Count);
        own.Items.Clear();

        Assert.Same(settings.Default, settings.SetOwn("g1", "", own: false));
        Assert.False(settings.HasOwn("g1"));
        Assert.Empty(settings.SetOwn("g1", "", own: true).Items);
        Assert.Equal("Игра", settings.Games["g1"].Name);
    }

    [Fact]
    public void BrokenFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "{ not json");

        OverlaySettings settings = OverlaySettings.Load(FilePath, () => throw new InvalidOperationException());
        Assert.Equal(OverlayLayout.CreateDefault().Items.Count, settings.Default.Items.Count);
    }

    [Fact]
    public void NullsInFileAreRepaired()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, """{ "Default": { "Items": null, "Style": null }, "Games": { "g": { "Layout": null } } }""");

        OverlaySettings settings = OverlaySettings.Load(FilePath);
        Assert.Empty(settings.Default.Items);
        Assert.NotNull(settings.Default.Style);
        Assert.NotNull(settings.Games["g"].Layout);
        Assert.Equal("", settings.Games["g"].Name);
        JsonSerializer.Serialize(settings); // сохраняется без ошибок
    }
}
