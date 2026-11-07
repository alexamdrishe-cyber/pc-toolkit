using System.Globalization;
using PcToolkit.Core.Games;
using PcToolkit.Core.Sensors;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Что показывает оверлей поверх игры.</summary>
public sealed class OverlayViewModel : ObservableObject
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private bool _isVisible;
    private string _corner = "TopLeft";
    private bool _showSensors = true;
    private bool _showGraph = true;
    private string _fpsText = "—";
    private string _fpsLevel = "None";
    private string _detailText = "";
    private string _processText = "";
    private string _cpuText = "";
    private string _gpuText = "";
    private double[] _frameTimes = Array.Empty<double>();
    private bool _isRecording;
    private string _recordingText = "";

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>TopLeft, TopRight, BottomLeft, BottomRight.</summary>
    public string Corner
    {
        get => _corner;
        set => SetProperty(ref _corner, value);
    }

    public bool ShowSensors
    {
        get => _showSensors;
        set => SetProperty(ref _showSensors, value);
    }

    public bool ShowGraph
    {
        get => _showGraph;
        set => SetProperty(ref _showGraph, value);
    }

    public string FpsText
    {
        get => _fpsText;
        private set => SetProperty(ref _fpsText, value);
    }

    /// <summary>Good (60+), Fair (30–60), Poor (меньше 30), None — для цвета числа.</summary>
    public string FpsLevel
    {
        get => _fpsLevel;
        private set => SetProperty(ref _fpsLevel, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string ProcessText
    {
        get => _processText;
        private set => SetProperty(ref _processText, value);
    }

    public string CpuText
    {
        get => _cpuText;
        private set => SetProperty(ref _cpuText, value);
    }

    public string GpuText
    {
        get => _gpuText;
        private set => SetProperty(ref _gpuText, value);
    }

    public double[] FrameTimes
    {
        get => _frameTimes;
        private set => SetProperty(ref _frameTimes, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        set => SetProperty(ref _isRecording, value);
    }

    public string RecordingText
    {
        get => _recordingText;
        set => SetProperty(ref _recordingText, value);
    }

    public void UpdateFrames(FrameMonitorState state)
    {
        if (!state.HasTarget || state.Fps <= 0)
        {
            FpsText = "—";
            FpsLevel = "None";
            DetailText = state.HasTarget ? "нет кадров" : "";
            FrameTimes = Array.Empty<double>();
        }
        else
        {
            FpsText = state.Fps.ToString("0", Russian);
            FpsLevel = state.Fps >= 60 ? "Good" : state.Fps >= 30 ? "Fair" : "Poor";
            string low = state.Low1Fps > 0 ? $"1% low {state.Low1Fps.ToString("0", Russian)}  ·  " : "";
            DetailText = $"{low}{state.FrameMs.ToString("0.0", Russian)} мс";
            FrameTimes = state.RecentFrameTimes;
        }

        ProcessText = state.ProcessName is { Length: > 0 } name ? $"{name} · {state.Api}" : "";
    }

    public void UpdateSensors(KeyMetrics metrics)
    {
        CpuText = Join("CPU", metrics.CpuLoad?.Value, metrics.CpuTemperature?.Value, null);
        GpuText = Join("GPU", metrics.GpuLoad?.Value, metrics.GpuTemperature?.Value, metrics.GpuPower?.Value);
    }

    private static string Join(string name, float? load, float? temperature, float? power)
    {
        var parts = new List<string> { name };
        if (load is float l)
            parts.Add($"{l.ToString("0", Russian)}%");
        if (temperature is float t)
            parts.Add($"{t.ToString("0", Russian)}°C");
        if (power is float p && p > 0)
            parts.Add($"{p.ToString("0", Russian)} Вт");
        return parts.Count > 1 ? string.Join("   ", parts) : "";
    }
}
