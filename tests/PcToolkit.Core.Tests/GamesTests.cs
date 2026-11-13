using System.Diagnostics;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Games;

namespace PcToolkit.Core.Tests;

public sealed class FrameStatisticsTests
{
    [Fact]
    public void SteadyFramesGiveExactFps()
    {
        double[] times = Enumerable.Repeat(10.0, 1000).ToArray(); // 100 FPS ровно
        FrameStatistics stats = FrameStatistics.FromFrameTimes(times)!;

        Assert.Equal(100, stats.AverageFps, 3);
        Assert.Equal(10, stats.AverageFrameMs, 3);
        Assert.Equal(100, stats.Low1Fps, 3);
        Assert.Equal(10, stats.Seconds, 3);
    }

    [Fact]
    public void StuttersLowerOnePercentLowButNotAverageMuch()
    {
        // 980 кадров по 10 мс и 20 фризов по 50 мс (2%): средний FPS около 93, а 1% low — 20.
        var times = Enumerable.Repeat(10.0, 980).Concat(Enumerable.Repeat(50.0, 20)).ToArray();
        FrameStatistics stats = FrameStatistics.FromFrameTimes(times)!;

        Assert.InRange(stats.AverageFps, 92, 93.5);
        Assert.Equal(20, stats.Low1Fps, 3);
        Assert.Equal(50, stats.MaxFrameMs, 3);
    }

    [Fact]
    public void PercentileUsesNearestRank()
    {
        double[] sorted = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        Assert.Equal(99, FrameMath.Percentile(sorted, 99));
        Assert.Equal(100, FrameMath.Percentile(sorted, 99.9));
        Assert.Equal(50, FrameMath.Percentile(sorted, 50));
    }

    [Fact]
    public void FrameTimesFromTimestamps()
    {
        long f = Stopwatch.Frequency;
        double[] times = FrameMath.ToFrameTimes(new[] { 0L, f / 100, f / 50 }, f);
        Assert.Equal(2, times.Length);
        Assert.Equal(10, times[0], 3);
        Assert.Equal(10, times[1], 3);
    }
}

public sealed class GameLibraryTests
{
    [Fact]
    public void ParsesNewSteamLibraryFormat()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "label"		""
                    "apps"
                    {
                        "228980"		"0"
                    }
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
            }
            """;

        IReadOnlyList<string> libraries = GameLibrary.SteamLibraries(vdf);
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, libraries);
    }

    [Fact]
    public void ParsesOldSteamLibraryFormat()
    {
        const string vdf = """
            "LibraryFolders"
            {
                "TimeNextStatsReport"		"1690000000"
                "ContentStatsID"		"-123"
                "1"		"E:\\Games\\Steam"
            }
            """;

        Assert.Equal(new[] { @"E:\Games\Steam" }, GameLibrary.SteamLibraries(vdf));
    }

    [Fact]
    public void ReadsSteamManifestAndSkipsTools()
    {
        const string game = """
            "AppState"
            {
                "appid"		"730"
                "name"		"Counter-Strike 2"
                "StateFlags"		"4"
                "installdir"		"Counter-Strike Global Offensive"
            }
            """;
        GameEntry entry = GameLibrary.SteamGame(game, @"D:\SteamLibrary")!;
        Assert.Equal("steam:730", entry.Id);
        Assert.Equal("Counter-Strike 2", entry.Name);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive", entry.InstallDir);

        const string tool = """
            "AppState" { "appid" "228980" "name" "Steamworks Common Redistributables" "StateFlags" "4" "installdir" "Steamworks Shared" }
            """;
        Assert.Null(GameLibrary.SteamGame(tool, @"C:\Steam"));

        const string downloading = """
            "AppState" { "appid" "570" "name" "Dota 2" "StateFlags" "1026" "installdir" "dota 2 beta" }
            """;
        Assert.Null(GameLibrary.SteamGame(downloading, @"C:\Steam"));
    }

    [Fact]
    public void ReadsEpicManifest()
    {
        const string json = """
            {
              "DisplayName": "Fortnite",
              "InstallLocation": "C:\\Program Files\\Epic Games\\Fortnite",
              "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe",
              "AppName": "Fortnite",
              "AppCategories": ["public", "games", "applications"],
              "bIsIncompleteInstall": false
            }
            """;
        GameEntry entry = GameLibrary.EpicGame(json)!;
        Assert.Equal("epic:Fortnite", entry.Id);
        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite", entry.InstallDir);

        Assert.Null(GameLibrary.EpicGame("""{ "DisplayName": "UE", "InstallLocation": "C:\\UE", "AppCategories": ["engines"] }"""));
    }

    [Theory]
    [InlineData("Мир танков", "Lesta Games", true)]
    [InlineData("Lesta Game Center", "Lesta Games", false)]
    [InlineData("VALORANT", "Riot Games, Inc", true)]
    [InlineData("Riot Vanguard", "Riot Games, Inc", false)]
    [InlineData("Battle.net", "Blizzard Entertainment", false)]
    [InlineData("7-Zip", "Igor Pavlov", false)]
    public void RecognizesPublisherGames(string name, string publisher, bool expected)
    {
        Assert.Equal(expected, GameLibrary.IsLikelyGame(name, publisher));
    }

    [Fact]
    public void MatchesProcessesInsideGameFolder()
    {
        var profile = new GameProfile { InstallDir = @"D:\Games\World_of_Tanks", Executable = "" };
        Assert.True(profile.Matches(new RunningProcess(1, "WorldOfTanks", @"D:\Games\World_of_Tanks\win64\WorldOfTanks.exe")));
        Assert.False(profile.Matches(new RunningProcess(2, "other", @"D:\Games\World_of_Tanks_old\x.exe")));
        Assert.False(profile.Matches(new RunningProcess(3, "nopath", "")));
    }
}

public sealed class GameSessionTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), $"pctoolkit-session-{Guid.NewGuid():N}.json");
    private readonly FakePower _power = new();

    public void Dispose()
    {
        if (File.Exists(_state))
            File.Delete(_state);
    }

    private GameSessionManager Manager(Func<int, string?>? priority = null, Action<int>? restore = null) =>
        new(scheme => new FakePowerTarget(_power, scheme), _state, priority ?? (_ => null), restore ?? (_ => { }));

    private static GameProfile Profile(bool power = true, bool priority = false) => new()
    {
        GameId = "steam:1",
        Name = "Игра",
        InstallDir = @"D:\Games\Game",
        PowerPlan = power,
        HighPriority = priority,
        Overlay = false,
    };

    private static RunningProcess GameProcess(int id = 100, long memory = 1000) => new(id, "game", @"D:\Games\Game\game.exe", memory);

    [Fact]
    public void AppliesHighPerformanceWhileGameRunsAndRevertsAfter()
    {
        GameSessionManager manager = Manager();
        GameProfile profile = Profile();
        RunningGame? started = null;
        RunningGame? stopped = null;
        manager.GameStarted += (_, g) => started = g;
        manager.GameStopped += (_, g) => stopped = g;

        manager.Update(new[] { profile }, new[] { GameProcess() });
        Assert.NotNull(started);
        Assert.Equal(GameSessionManager.HighPerformanceScheme.ToString("D"), _power.Active);
        Assert.True(File.Exists(_state));

        manager.Update(new[] { profile }, Array.Empty<RunningProcess>());
        Assert.NotNull(stopped);
        Assert.Equal(FakePower.Balanced, _power.Active);
        Assert.False(File.Exists(_state));
    }

    [Fact]
    public void DoesNotRevertSchemeChangedByUser()
    {
        GameSessionManager manager = Manager();
        GameProfile profile = Profile();
        manager.Update(new[] { profile }, new[] { GameProcess() });

        const string userChoice = "11111111-2222-3333-4444-555555555555";
        _power.Active = userChoice;
        manager.Update(new[] { profile }, Array.Empty<RunningProcess>());

        Assert.Equal(userChoice, _power.Active);
        Assert.False(File.Exists(_state));
    }

    [Fact]
    public void RecoversAfterCrash()
    {
        Manager().Update(new[] { Profile() }, new[] { GameProcess() });
        Assert.Equal(GameSessionManager.HighPerformanceScheme.ToString("D"), _power.Active);

        // «Перезапуск» приложения: новый менеджер видит файл состояния; игры уже нет — схема возвращается.
        GameSessionManager restarted = Manager();
        Assert.True(restarted.HasPendingState);
        restarted.Update(new[] { Profile() }, Array.Empty<RunningProcess>());
        Assert.Equal(FakePower.Balanced, _power.Active);
    }

    [Fact]
    public void KeepsSchemeWhileGameStillRunsAfterRestart()
    {
        Manager().Update(new[] { Profile() }, new[] { GameProcess() });

        GameSessionManager restarted = Manager();
        restarted.Update(new[] { Profile() }, new[] { GameProcess() });
        Assert.Equal(GameSessionManager.HighPerformanceScheme.ToString("D"), _power.Active);

        restarted.Update(new[] { Profile() }, Array.Empty<RunningProcess>());
        Assert.Equal(FakePower.Balanced, _power.Active);
    }

    [Fact]
    public void LeavesSchemeAloneWhenAlreadyHighPerformance()
    {
        _power.Active = GameSessionManager.HighPerformanceScheme.ToString("D");
        GameSessionManager manager = Manager();
        manager.Update(new[] { Profile() }, new[] { GameProcess() });
        Assert.False(File.Exists(_state));
        manager.Update(new[] { Profile() }, Array.Empty<RunningProcess>());
        Assert.Equal(GameSessionManager.HighPerformanceScheme.ToString("D"), _power.Active);
    }

    [Fact]
    public void RaisesPriorityOfLargestProcess()
    {
        var raised = new List<int>();
        GameSessionManager manager = Manager(id =>
        {
            raised.Add(id);
            return null;
        });

        manager.Update(new[] { Profile(power: false, priority: true) },
            new[] { GameProcess(1, 100), GameProcess(2, 5000) });
        Assert.Equal(new[] { 2 }, raised);
        Assert.Equal(2, manager.Running.Single().MainProcessId);
    }

    [Fact]
    public void AppliesAndRevertsOptionsChangedWhileGameRuns()
    {
        var raised = new List<int>();
        var restored = new List<int>();
        GameSessionManager manager = Manager(id =>
        {
            raised.Add(id);
            return null;
        }, restored.Add);
        var changed = new List<RunningGame>();
        manager.GameChanged += (_, g) => changed.Add(g);

        GameProfile profile = Profile(power: false, priority: false);
        profile.Overlay = true;
        manager.Update(new[] { profile }, new[] { GameProcess() });
        Assert.Equal(FakePower.Balanced, _power.Active);
        Assert.Empty(raised);

        // Посреди игры включили схему питания и приоритет — применяются сразу, без перезапуска игры.
        GameProfile edited = Profile(power: true, priority: true);
        edited.Overlay = true;
        manager.Update(new[] { edited }, new[] { GameProcess() });
        Assert.Equal(GameSessionManager.HighPerformanceScheme.ToString("D"), _power.Active);
        Assert.Equal(new[] { 100 }, raised);
        RunningGame running = Assert.Single(changed);
        Assert.Contains(running.Applied, a => a.StartsWith("Высокий приоритет"));

        // Ничего не поменялось — повторно не применяется и событие не приходит.
        manager.Update(new[] { edited }, new[] { GameProcess() });
        Assert.Single(raised);
        Assert.Single(changed);

        // Выключили оба пункта — схема и приоритет возвращаются, игра продолжает идти.
        GameProfile reverted = Profile(power: false, priority: false);
        reverted.Overlay = true;
        manager.Update(new[] { reverted }, new[] { GameProcess() });
        Assert.Equal(FakePower.Balanced, _power.Active);
        Assert.Equal(new[] { 100 }, restored);
        Assert.Single(manager.Running);
        Assert.Equal(2, changed.Count);
        Assert.DoesNotContain(changed[1].Applied, a => a.Contains("приоритет", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DisablingProfileMidGameRestoresPriority()
    {
        var restored = new List<int>();
        GameSessionManager manager = Manager(restore: restored.Add);
        GameProfile profile = Profile(power: true, priority: true);
        manager.Update(new[] { profile }, new[] { GameProcess() });

        GameProfile disabled = Profile(power: true, priority: true);
        disabled.Enabled = false;
        manager.Update(new[] { disabled }, new[] { GameProcess() });
        Assert.Empty(manager.Running);
        Assert.Equal(new[] { 100 }, restored);
        Assert.Equal(FakePower.Balanced, _power.Active);
    }

    [Fact]
    public void DisabledProfileDoesNothing()
    {
        GameSessionManager manager = Manager();
        GameProfile profile = Profile();
        profile.Enabled = false;
        manager.Update(new[] { profile }, new[] { GameProcess() });
        Assert.Empty(manager.Running);
        Assert.Equal(FakePower.Balanced, _power.Active);
    }

    private sealed class FakePower
    {
        public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

        public string Active { get; set; } = Balanced;

        public HashSet<Guid> Installed { get; } = new() { Guid.Parse(Balanced), GameSessionManager.HighPerformanceScheme };
    }

    private sealed class FakePowerTarget(FakePower power, Guid? required) : ISettingTarget
    {
        public string Key => "power:active-scheme";

        public string DisplayName => "схема";

        public bool RequiresAdmin => false;

        public bool IsAvailable(out string? reason)
        {
            reason = null;
            return required is not Guid scheme || power.Installed.Contains(scheme);
        }

        public ValueSnapshot Read() => new(true, PowerSchemeTarget.KindName, power.Active);

        public void Write(ValueSnapshot value) => power.Active = value.Data;

        public string Describe(ValueSnapshot value) => value.Data;
    }
}

public sealed class FpsRecorderTests
{
    [Fact]
    public void RecordsTimelineAndStatistics()
    {
        var recorder = new FpsRecorder(42, "Игра", "steam:1", "game", profileActive: true, "схема питания");
        recorder.OnFrames(7, new[] { 10.0, 10.0 }); // чужой процесс — не считается
        for (int i = 0; i < 10; i++)
            recorder.OnFrames(42, Enumerable.Repeat(1000.0 / 60, 60).ToArray()); // 10 секунд по 60 FPS
        recorder.OnSensors(50, 95, 60, 70, 200);
        recorder.OnSensors(60, 97, 65, 72, 210);

        FpsRecording recording = recorder.Finish()!;
        Assert.Equal(600, recording.Frames);
        Assert.Equal(60, recording.AverageFps, 1);
        Assert.InRange(recording.FpsTimeline.Count, 9, 11);
        Assert.All(recording.FpsTimeline, fps => Assert.InRange(fps, 59, 61));
        Assert.Equal(55.0, recording.CpuLoad!.Value, 3);
        Assert.Equal(72.0, recording.GpuTemperatureMax!.Value, 3);
        Assert.True(recording.ProfileActive);
    }

    [Fact]
    public void TooShortRecordingIsDropped()
    {
        var recorder = new FpsRecorder(1, "Игра", "", "game", false, "");
        recorder.OnFrames(1, Enumerable.Repeat(10.0, 100).ToArray()); // 1 секунда
        Assert.Null(recorder.Finish());
    }

    [Fact]
    public void StoreKeepsNewestFirst()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pctoolkit-fps-{Guid.NewGuid():N}.json");
        try
        {
            FpsRecordingStore store = FpsRecordingStore.Load(path);
            store.Add(new FpsRecording { At = DateTimeOffset.Now.AddMinutes(-5), GameName = "старый" });
            store.Add(new FpsRecording { At = DateTimeOffset.Now, GameName = "новый" });

            FpsRecordingStore reloaded = FpsRecordingStore.Load(path);
            Assert.Equal(new[] { "новый", "старый" }, reloaded.Recordings.Select(r => r.GameName));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class FrameMonitorTests
{
    [Fact]
    public void FollowsForegroundPresenterAndFeedsListener()
    {
        var source = new FakeFrameSource();
        long f = Stopwatch.Frequency;
        source.Presenters.Add(new FramePresenter(500, 100, "DXGI"));
        source.Presenters.Add(new FramePresenter(600, 60, "DXGI"));
        for (int i = 1; i <= 200; i++)
            source.Frames.Add(source.Now - (200 - i) * f / 100); // последние 2 секунды по 100 FPS

        using var monitor = new FrameMonitor(source, () => 500);
        var listener = new CollectingListener();
        monitor.AddListener(listener);

        FrameMonitorState? state = null;
        monitor.Updated += (_, s) => state = s;
        monitor.Acquire();
        SpinWait.SpinUntil(() => state is { HasTarget: true, Fps: > 0 }, TimeSpan.FromSeconds(5));
        monitor.Release();

        Assert.NotNull(state);
        Assert.Equal(500, state!.ProcessId);
        Assert.InRange(state.Fps, 95, 105);
        Assert.Equal(500, source.LastQueriedProcess);
    }

    [Fact]
    public void IgnoresFramesWhileGameIsInBackground()
    {
        var source = new FakeFrameSource();
        long f = Stopwatch.Frequency;
        source.Presenters.Add(new FramePresenter(500, 5, "DXGI"));
        for (int i = 1; i <= 10; i++)
            source.Frames.Add(source.Now - (10 - i) * f / 5); // свёрнутая игра: 5 FPS

        // Запись закреплена за игрой, но на переднем плане другая программа.
        using var monitor = new FrameMonitor(source, () => 600) { PinnedProcessId = 500 };
        var listener = new CollectingListener();
        monitor.AddListener(listener);

        int updates = 0;
        FrameMonitorState? state = null;
        monitor.Updated += (_, s) =>
        {
            state = s;
            Interlocked.Increment(ref updates);
        };
        monitor.Acquire();
        SpinWait.SpinUntil(() => Volatile.Read(ref updates) >= 4, TimeSpan.FromSeconds(5));
        monitor.Release();

        Assert.Equal(500, source.LastQueriedProcess);
        Assert.Empty(listener.Times);
        Assert.False(state!.InForeground);
        Assert.Equal(0, state.Fps);
    }

    [Fact]
    public void PollIntervalIsClampedAndAppliesWhileRunning()
    {
        using var monitor = new FrameMonitor(new FakeFrameSource(), () => 600);
        Assert.Equal(TimeSpan.FromMilliseconds(250), monitor.PollInterval);

        monitor.PollInterval = TimeSpan.FromMilliseconds(100);
        Assert.Equal(TimeSpan.FromMilliseconds(100), monitor.PollInterval);
        monitor.PollInterval = TimeSpan.Zero;
        Assert.Equal(TimeSpan.FromMilliseconds(50), monitor.PollInterval);
        monitor.PollInterval = TimeSpan.FromMinutes(1);
        Assert.Equal(TimeSpan.FromSeconds(2), monitor.PollInterval);

        // Частоту можно менять на ходу: опрос продолжается.
        int updates = 0;
        monitor.Updated += (_, _) => Interlocked.Increment(ref updates);
        monitor.PollInterval = TimeSpan.FromMilliseconds(50);
        monitor.Acquire();
        SpinWait.SpinUntil(() => Volatile.Read(ref updates) >= 3, TimeSpan.FromSeconds(5));
        monitor.PollInterval = TimeSpan.FromMilliseconds(60);
        int before = Volatile.Read(ref updates);
        SpinWait.SpinUntil(() => Volatile.Read(ref updates) >= before + 3, TimeSpan.FromSeconds(5));
        monitor.Release();

        Assert.True(Volatile.Read(ref updates) >= before + 3);
    }

    [Fact]
    public void CountsBackgroundGameForOverlaySettingsButNotForRecording()
    {
        var source = new FakeFrameSource();
        long f = Stopwatch.Frequency;
        source.Presenters.Add(new FramePresenter(500, 100, "DXGI"));
        for (int i = 1; i <= 200; i++)
            source.Frames.Add(source.Now - (200 - i) * f / 100);

        // Открыты настройки оверлея: игра на другом мониторе, на переднем плане PC Toolkit.
        using var monitor = new FrameMonitor(source, () => 600) { PinnedProcessId = 500, CountInBackground = true };
        var listener = new CollectingListener();
        monitor.AddListener(listener);

        FrameMonitorState? state = null;
        monitor.Updated += (_, s) => state = s;
        monitor.Acquire();
        SpinWait.SpinUntil(() => state is { Fps: > 0 }, TimeSpan.FromSeconds(5));
        monitor.Release();

        Assert.InRange(state!.Fps, 95, 105);
        Assert.False(state.InForeground);
        Assert.Empty(listener.Times);
    }

    private sealed class CollectingListener : IFrameListener
    {
        public List<double> Times { get; } = new();

        public void OnFrames(int processId, IReadOnlyList<double> frameTimesMs) => Times.AddRange(frameTimesMs);
    }

    private sealed class FakeFrameSource : IFrameSource
    {
        public List<FramePresenter> Presenters { get; } = new();

        public List<long> Frames { get; } = new();

        public long Now { get; } = Stopwatch.GetTimestamp();

        public int? LastQueriedProcess { get; private set; }

        public string Name => "тест";

        public FrameQueryResult? Query(int? processId, long since, out string? error)
        {
            error = null;
            if (processId is not null)
                LastQueriedProcess = processId;
            long[] frames = processId == 500 ? Frames.Where(t => t > since).ToArray() : Array.Empty<long>();
            return new FrameQueryResult(Presenters.ToList(), frames, Now);
        }

        public void Dispose()
        {
        }
    }
}

public sealed class EtwFrameCaptureTests
{
    [Fact]
    public void StartsOrExplainsWhyNot()
    {
        // Без прав администратора Windows не даёт запустить сеанс ETW — нужен понятный текст ошибки.
        try
        {
            using EtwFrameCapture capture = EtwFrameCapture.Start("PcToolkit Frames (test)");
            Assert.True(capture.IsRunning);
            Assert.NotNull(capture.GetPresenters());
        }
        catch (FrameCaptureException ex)
        {
            Assert.Contains("администратора", ex.Message);
        }
    }
}
