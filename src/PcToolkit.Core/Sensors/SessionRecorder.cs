using System.Globalization;
using System.Text;

namespace PcToolkit.Core.Sensors;

/// <summary>
/// Записывает все показания датчиков в CSV-файл, по строке на каждый снимок.
/// Файл открывается в Excel: разделитель «;», кодировка UTF-8 с BOM.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private const char Separator = ';';

    private StreamWriter? _writer;
    private string[] _columnIds = Array.Empty<string>();

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PC Toolkit", "Sessions");

    public bool IsRecording => _writer is not null;

    public string? FilePath { get; private set; }

    public DateTime StartedAt { get; private set; }

    public int RowCount { get; private set; }

    /// <summary>Начинает запись. Набор столбцов фиксируется по первому снимку.</summary>
    public void Start(HardwareSnapshot snapshot, string? directory = null)
    {
        if (IsRecording)
            return;

        string folder = directory ?? DefaultDirectory;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var columns = snapshot.Devices
            .SelectMany(device => device.Sensors.Select(sensor => (Device: device, Sensor: sensor)))
            .ToList();

        var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var header = new StringBuilder("Время");
        foreach ((DeviceSnapshot device, SensorReading sensor) in columns)
        {
            string unit = SensorFormatter.Unit(sensor.Kind);
            string title = unit.Length > 0
                ? $"{device.Name} / {sensor.Name} ({unit})"
                : $"{device.Name} / {sensor.Name}";
            header.Append(Separator).Append(Escape(title));
        }

        writer.WriteLine(header.ToString());

        _writer = writer;
        _columnIds = columns.Select(c => c.Sensor.Id).ToArray();
        FilePath = path;
        StartedAt = DateTime.Now;
        RowCount = 0;

        Append(snapshot);
    }

    public void Append(HardwareSnapshot snapshot)
    {
        if (_writer is null)
            return;

        var values = new Dictionary<string, float?>();
        foreach (DeviceSnapshot device in snapshot.Devices)
        {
            foreach (SensorReading sensor in device.Sensors)
                values[sensor.Id] = sensor.Value;
        }

        var line = new StringBuilder(snapshot.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        foreach (string id in _columnIds)
        {
            line.Append(Separator);
            if (values.TryGetValue(id, out float? value) && value is float number)
                line.Append(number.ToString("0.###", CultureInfo.CurrentCulture));
        }

        _writer.WriteLine(line.ToString());
        RowCount++;

        if (RowCount % 10 == 0)
            _writer.Flush();
    }

    public void Stop()
    {
        StreamWriter? writer = _writer;
        _writer = null;
        if (writer is null)
            return;

        writer.Flush();
        writer.Dispose();
    }

    public void Dispose() => Stop();

    private static string Escape(string text) =>
        text.Contains(Separator) || text.Contains('"')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
}
