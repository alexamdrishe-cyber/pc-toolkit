using System.Text.Json;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Games;

/// <summary>Сохранённый замер FPS: итоги, график по секундам и средние показания датчиков.</summary>
public sealed class FpsRecording
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset At { get; set; }

    public string GameName { get; set; } = "";

    public string GameId { get; set; } = "";

    public string ProcessName { get; set; } = "";

    /// <summary>Во время замера действовал профиль игры.</summary>
    public bool ProfileActive { get; set; }

    /// <summary>Что делал профиль: «схема питания, приоритет».</summary>
    public string ProfileText { get; set; } = "";

    public double Seconds { get; set; }

    public int Frames { get; set; }

    public double AverageFps { get; set; }

    public double Low1Fps { get; set; }

    public double Low01Fps { get; set; }

    public double AverageFrameMs { get; set; }

    public double P99FrameMs { get; set; }

    public double MaxFrameMs { get; set; }

    /// <summary>Кадров дольше 50 мс — заметные глазу подтормаживания.</summary>
    public int LongFrames { get; set; }

    /// <summary>FPS за каждую секунду замера.</summary>
    public List<double> FpsTimeline { get; set; } = new();

    public double? CpuLoad { get; set; }

    public double? GpuLoad { get; set; }

    public double? CpuTemperatureMax { get; set; }

    public double? GpuTemperatureMax { get; set; }

    public double? GpuPower { get; set; }

    public string Note { get; set; } = "";
}

/// <summary>
/// Запись замера: получает длительности кадров от <see cref="FrameMonitor"/> и раз в секунду показания датчиков.
/// Хранит все кадры (полчаса при 300 FPS — около 4 МБ) — так 1% и 0,1% low считаются точно.
/// </summary>
public sealed class FpsRecorder : IFrameListener
{
    public const double LongFrameMs = 50;

    private readonly object _lock = new();
    private readonly List<double> _frameTimes = new();
    private readonly List<double> _timeline = new();
    private readonly List<double> _cpuLoad = new();
    private readonly List<double> _gpuLoad = new();
    private readonly List<double> _gpuPower = new();
    private double? _cpuTempMax;
    private double? _gpuTempMax;
    private double _bucketMs;
    private int _bucketFrames;
    private double _totalMs;

    public FpsRecorder(int processId, string gameName, string gameId, string processName, bool profileActive, string profileText)
    {
        ProcessId = processId;
        GameName = gameName;
        GameId = gameId;
        ProcessName = processName;
        ProfileActive = profileActive;
        ProfileText = profileText;
        StartedAt = DateTimeOffset.Now;
    }

    public int ProcessId { get; }

    public string GameName { get; }

    public string GameId { get; }

    public string ProcessName { get; }

    public bool ProfileActive { get; }

    public string ProfileText { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>Записанное время по кадрам, секунды.</summary>
    public double RecordedSeconds
    {
        get
        {
            lock (_lock)
                return _totalMs / 1000;
        }
    }

    public int FrameCount
    {
        get
        {
            lock (_lock)
                return _frameTimes.Count;
        }
    }

    public void OnFrames(int processId, IReadOnlyList<double> frameTimesMs)
    {
        if (processId != ProcessId)
            return;

        lock (_lock)
        {
            foreach (double ms in frameTimesMs)
            {
                // Пауза больше 5 секунд — игра была свёрнута или грузилась: такой «кадр» не считаем.
                if (ms <= 0 || ms > 5000)
                    continue;

                _frameTimes.Add(ms);
                _totalMs += ms;
                _bucketMs += ms;
                _bucketFrames++;
                if (_bucketMs >= 1000)
                {
                    _timeline.Add(_bucketFrames * 1000 / _bucketMs);
                    _bucketMs = 0;
                    _bucketFrames = 0;
                }
            }
        }
    }

    /// <summary>Показания датчиков раз в секунду (null — датчика нет).</summary>
    public void OnSensors(double? cpuLoad, double? gpuLoad, double? cpuTemperature, double? gpuTemperature, double? gpuPower)
    {
        lock (_lock)
        {
            if (cpuLoad is double c)
                _cpuLoad.Add(c);
            if (gpuLoad is double g)
                _gpuLoad.Add(g);
            if (gpuPower is double p)
                _gpuPower.Add(p);
            if (cpuTemperature is double ct)
                _cpuTempMax = Math.Max(_cpuTempMax ?? ct, ct);
            if (gpuTemperature is double gt)
                _gpuTempMax = Math.Max(_gpuTempMax ?? gt, gt);
        }
    }

    /// <summary>Итог замера. null — записано меньше 5 секунд или меньше 30 кадров.</summary>
    public FpsRecording? Finish()
    {
        lock (_lock)
        {
            FrameStatistics? stats = FrameStatistics.FromFrameTimes(_frameTimes);
            if (stats is null || stats.Seconds < 5 || stats.Frames < 30)
                return null;

            var timeline = _timeline.ToList();
            if (_bucketMs >= 500)
                timeline.Add(_bucketFrames * 1000 / _bucketMs);

            return new FpsRecording
            {
                At = StartedAt,
                GameName = GameName,
                GameId = GameId,
                ProcessName = ProcessName,
                ProfileActive = ProfileActive,
                ProfileText = ProfileText,
                Seconds = stats.Seconds,
                Frames = stats.Frames,
                AverageFps = stats.AverageFps,
                Low1Fps = stats.Low1Fps,
                Low01Fps = stats.Low01Fps,
                AverageFrameMs = stats.AverageFrameMs,
                P99FrameMs = stats.P99FrameMs,
                MaxFrameMs = stats.MaxFrameMs,
                LongFrames = _frameTimes.Count(t => t > LongFrameMs),
                FpsTimeline = timeline.Select(v => Math.Round(v, 1)).ToList(),
                CpuLoad = Average(_cpuLoad),
                GpuLoad = Average(_gpuLoad),
                CpuTemperatureMax = _cpuTempMax,
                GpuTemperatureMax = _gpuTempMax,
                GpuPower = Average(_gpuPower),
            };
        }
    }

    private static double? Average(List<double> values) => values.Count > 0 ? Math.Round(values.Average(), 1) : null;
}

/// <summary>Замеры FPS: %LocalAppData%\PcToolkit\fps-sessions.json (последние 300).</summary>
public sealed class FpsRecordingStore
{
    private const int MaxRecordings = 300;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _path;

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "fps-sessions.json");

    private FpsRecordingStore(string path, List<FpsRecording> recordings)
    {
        _path = path;
        Recordings = recordings;
    }

    /// <summary>Сначала новые.</summary>
    public List<FpsRecording> Recordings { get; }

    public static FpsRecordingStore Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                List<FpsRecording>? list = JsonSerializer.Deserialize<List<FpsRecording>>(File.ReadAllText(path));
                if (list is not null)
                    return new FpsRecordingStore(path, list.OrderByDescending(r => r.At).ToList());
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать замеры FPS.", ex);
        }

        return new FpsRecordingStore(path, new List<FpsRecording>());
    }

    public void Add(FpsRecording recording)
    {
        Recordings.Insert(0, recording);
        if (Recordings.Count > MaxRecordings)
            Recordings.RemoveRange(MaxRecordings, Recordings.Count - MaxRecordings);
        Save();
    }

    public void Remove(Guid id)
    {
        Recordings.RemoveAll(r => r.Id == id);
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Recordings, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить замеры FPS.", ex);
        }
    }
}
