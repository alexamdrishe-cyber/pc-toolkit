using PcToolkit.Core.Changes;
using PcToolkit.Core.Games;
using PcToolkit.Core.Overclock;

namespace PcToolkit.Core.Tests;

public sealed class RadeonGraphicsTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), $"pctoolkit-radeon-{Guid.NewGuid():N}.json");
    private readonly FakeRadeon _radeon = new();

    public void Dispose()
    {
        foreach (string file in new[] { _state, GraphicsFile })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    private string GraphicsFile => Path.Combine(Path.GetDirectoryName(_state)!, Path.GetFileNameWithoutExtension(_state) + "-graphics.json");

    private GameSessionManager Manager() => new(_ => new NoPower(), _state, _ => null, _ => { }, _radeon);

    private static GameProfile Profile(string mode = RadeonModes.AntiLag, bool sharpening = false) => new()
    {
        GameId = "steam:1",
        Name = "Игра",
        InstallDir = @"D:\Games\Game",
        PowerPlan = false,
        Overlay = false,
        RadeonMode = mode,
        ChillMinFps = 50,
        ChillMaxFps = 141,
        Sharpening = sharpening,
        Sharpness = 74,
    };

    private static RunningProcess Game() => new(100, "game", @"D:\Games\Game\game.exe", 1000);

    [Fact]
    public void ChillModeSnapsToDriverRangeAndTurnsAntiLagOff()
    {
        RadeonValues wanted = RadeonPlan.Wanted(Profile(RadeonModes.Chill, sharpening: true), FakeRadeon.Supported(new RadeonValues()));

        Assert.True(wanted.Chill);
        Assert.False(wanted.AntiLag);
        Assert.Equal(50, wanted.ChillMinFps);
        Assert.Equal(140, wanted.ChillMaxFps); // шаг 5 у драйвера
        Assert.True(wanted.Sharpening);
        Assert.Equal(70, wanted.Sharpness);    // шаг 10
        Assert.Equal("Radeon Chill 50–140 FPS, резкость 70 %", RadeonPlan.Describe(wanted));
    }

    [Fact]
    public void UnsupportedFeaturesAreSkipped()
    {
        RadeonState noAntiLag = FakeRadeon.Supported(new RadeonValues()) with { AntiLagSupported = false };
        Assert.True(RadeonPlan.Wanted(Profile(RadeonModes.AntiLag), noAntiLag).IsEmpty);
    }

    [Fact]
    public void AntiLagDuringGameThenEverythingComesBack()
    {
        _radeon.Current = new RadeonValues { AntiLag = false, Chill = true, ChillMinFps = 70, ChillMaxFps = 144, Sharpening = false, Sharpness = 80 };
        GameSessionManager manager = Manager();

        manager.Update(new[] { Profile() }, new[] { Game() });
        Assert.True(_radeon.Current.AntiLag);
        Assert.False(_radeon.Current.Chill);
        Assert.True(File.Exists(GraphicsFile));
        Assert.Contains("Radeon Anti-Lag", manager.Running.Single().Applied.Single());

        manager.Update(new[] { Profile() }, Array.Empty<RunningProcess>());
        Assert.False(_radeon.Current.AntiLag);
        Assert.True(_radeon.Current.Chill);
        Assert.Equal(70, _radeon.Current.ChillMinFps);
        Assert.False(File.Exists(GraphicsFile));
    }

    [Fact]
    public void ManualChangeDuringGameIsKept()
    {
        _radeon.Current = new RadeonValues { AntiLag = false, Chill = false, Sharpening = false, Sharpness = 80 };
        GameSessionManager manager = Manager();
        manager.Update(new[] { Profile(RadeonModes.AntiLag, sharpening: true) }, new[] { Game() });
        Assert.True(_radeon.Current.Sharpening);

        // Пользователь сам поменял резкость в AMD Software посреди игры.
        _radeon.Current = _radeon.Current with { Sharpness = 30 };
        manager.Update(new[] { Profile(RadeonModes.AntiLag, sharpening: true) }, Array.Empty<RunningProcess>());

        Assert.False(_radeon.Current.AntiLag);      // Anti-Lag не трогали — вернулся
        Assert.True(_radeon.Current.Sharpening);    // резкость трогали — оставлена как есть
        Assert.Equal(30, _radeon.Current.Sharpness);
    }

    [Fact]
    public void RevertsAfterCrashOnNextStart()
    {
        _radeon.Current = new RadeonValues { AntiLag = false, Chill = false, Sharpening = false, Sharpness = 80 };
        Manager().Update(new[] { Profile() }, new[] { Game() });
        Assert.True(_radeon.Current.AntiLag);

        GameSessionManager restarted = Manager();
        Assert.True(restarted.HasPendingState);
        restarted.Update(new[] { Profile() }, Array.Empty<RunningProcess>());
        Assert.False(_radeon.Current.AntiLag);
        Assert.False(File.Exists(GraphicsFile));
    }

    [Fact]
    public void AlreadyEnabledIsLeftAlone()
    {
        _radeon.Current = new RadeonValues { AntiLag = true, Chill = false };
        GameSessionManager manager = Manager();
        manager.Update(new[] { Profile() }, new[] { Game() });

        Assert.Equal(0, _radeon.Writes);
        Assert.False(File.Exists(GraphicsFile));
        Assert.Contains("уже было включено", manager.Running.Single().Applied.Single());
    }

    [Fact]
    public void ProfileChangedMidGameSwitchesSettings()
    {
        _radeon.Current = new RadeonValues { AntiLag = false, Chill = false, ChillMinFps = 70, ChillMaxFps = 144 };
        GameSessionManager manager = Manager();
        manager.Update(new[] { Profile(RadeonModes.AntiLag) }, new[] { Game() });
        Assert.True(_radeon.Current.AntiLag);

        manager.Update(new[] { Profile(RadeonModes.None) }, new[] { Game() });
        Assert.False(_radeon.Current.AntiLag);
        Assert.False(File.Exists(GraphicsFile));

        manager.Update(new[] { Profile(RadeonModes.Chill) }, new[] { Game() });
        Assert.True(_radeon.Current.Chill);
        Assert.Equal(50, _radeon.Current.ChillMinFps);

        manager.Update(new[] { Profile(RadeonModes.Chill) }, Array.Empty<RunningProcess>());
        Assert.False(_radeon.Current.Chill);
        Assert.Equal(70, _radeon.Current.ChillMinFps);
        Assert.Equal(144, _radeon.Current.ChillMaxFps);
    }

    [Fact]
    public void RestoreSkipsLatencyPairWhenUserTouchedIt()
    {
        var before = new RadeonValues { AntiLag = false, Chill = true };
        var applied = new RadeonValues { AntiLag = true, Chill = false };
        var userTurnedChillBack = new RadeonValues { AntiLag = false, Chill = true };

        Assert.True(RadeonPlan.Restore(before, applied, userTurnedChillBack).IsEmpty);
        Assert.Equal(before, RadeonPlan.Restore(before, applied, applied));
    }

    /// <summary>Подделка драйвера: Anti-Lag и Chill взаимно выключают друг друга, как настоящий.</summary>
    private sealed class FakeRadeon : IRadeonGraphics
    {
        public RadeonValues Current { get; set; } = new() { AntiLag = false, Chill = false, ChillMinFps = 70, ChillMaxFps = 144, Sharpening = false, Sharpness = 80 };

        public int Writes { get; private set; }

        public static RadeonState Supported(RadeonValues values) =>
            new(values, true, true, true, new TuningRange(30, 300, 5), new TuningRange(10, 100, 10));

        public RadeonState? Read(out string? error)
        {
            error = null;
            return Supported(Current);
        }

        public void Apply(RadeonValues values)
        {
            Writes++;
            RadeonValues next = Current;
            if (values.AntiLag is bool antiLag)
                next = next with { AntiLag = antiLag, Chill = antiLag ? false : next.Chill };
            if (values.ChillMinFps is int min)
                next = next with { ChillMinFps = min };
            if (values.ChillMaxFps is int max)
                next = next with { ChillMaxFps = max };
            if (values.Chill is bool chill)
                next = next with { Chill = chill, AntiLag = chill ? false : next.AntiLag };
            if (values.Sharpening is bool sharpening)
                next = next with { Sharpening = sharpening };
            if (values.Sharpness is int sharpness)
                next = next with { Sharpness = sharpness };
            Current = next;
        }
    }

    private sealed class NoPower : ISettingTarget
    {
        public string Key => "power:active-scheme";

        public string DisplayName => "схема";

        public bool RequiresAdmin => false;

        public bool IsAvailable(out string? reason)
        {
            reason = null;
            return true;
        }

        public ValueSnapshot Read() => new(true, PowerSchemeTarget.KindName, "381b4222-f694-41f0-9685-ff5bb260df2e");

        public void Write(ValueSnapshot value)
        {
        }

        public string Describe(ValueSnapshot value) => value.Data;
    }
}
