using System.Text.Json;
using System.Text.Json.Serialization;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Games;
using PcToolkit.Core.Sensors;

namespace PcToolkit.Core.Service;

/// <summary>
/// Протокол между приложением и службой PC Toolkit: по одной строке JSON на запрос и на ответ
/// через локальный именованный канал.
/// </summary>
public static class ServiceProtocol
{
    public const string ServiceName = "PcToolkitService";

    public const string DisplayName = "PC Toolkit";

    public const string Description =
        "Датчики железа и системные настройки для приложения PC Toolkit. Принимает только изменения из каталога настроек приложения.";

    public const string PipeName = "PcToolkit.Service.v1";

    /// <summary>Меняется при несовместимых изменениях протокола.</summary>
    public const int Version = 1;

    /// <summary>Имя исполняемого файла приложения, которому служба отвечает.</summary>
    public const string ClientExecutable = "PcToolkit.exe";

    public const string ServiceExecutable = "PcToolkit.Service.exe";

    /// <summary>
    /// Отпечаток сборки ядра. Совпадает у приложения и службы, собранных из одного кода;
    /// по нему приложение понимает, что службу пора обновить.
    /// </summary>
    public static string BuildId { get; } = typeof(ServiceProtocol).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

public static class ServiceOps
{
    public const string Hello = "hello";
    public const string Sensors = "sensors";
    public const string Write = "write";

    /// <summary>Счётчик кадров: кто выводит кадры и новые кадры выбранного процесса.</summary>
    public const string Frames = "frames";

    /// <summary>Физические диски и их SMART (читается только с правами администратора).</summary>
    public const string Disks = "disks";
}

public sealed class ServiceRequest
{
    public string Op { get; set; } = "";

    public ChangeDefinition? Change { get; set; }

    public ValueSnapshot? Value { get; set; }

    /// <summary>Для <see cref="ServiceOps.Frames"/>: процесс, чьи кадры нужны (null — только список).</summary>
    public int? ProcessId { get; set; }

    /// <summary>Для <see cref="ServiceOps.Frames"/>: вернуть кадры новее этой отметки QPC.</summary>
    public long? Since { get; set; }

    /// <summary>
    /// Для <see cref="ServiceOps.Sensors"/>: как часто приложение хочет новые снимки, мс.
    /// Старая служба поле не знает и просто его пропускает (опрашивает раз в секунду).
    /// </summary>
    public int? IntervalMs { get; set; }
}

public sealed class ServiceResponse
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public int Version { get; set; }

    public string? BuildId { get; set; }

    public HardwareSnapshot? Snapshot { get; set; }

    public SensorSourceState? SensorState { get; set; }

    public string? SensorError { get; set; }

    public List<FramePresenter>? Presenters { get; set; }

    /// <summary>Отметки времени кадров (QPC).</summary>
    public long[]? Frames { get; set; }

    /// <summary>Время службы в момент ответа (QPC): общее для всех процессов компьютера.</summary>
    public long? Now { get; set; }

    public List<DiskHealthData>? Disks { get; set; }

    public static ServiceResponse Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>Что служба сообщила о себе.</summary>
public sealed record ServiceInfo(int Version, string BuildId)
{
    /// <summary>Протокол совместим: службой можно пользоваться.</summary>
    public bool IsCompatible => Version == ServiceProtocol.Version;

    /// <summary>Служба собрана из того же кода, что и приложение.</summary>
    public bool IsCurrent => IsCompatible && string.Equals(BuildId, ServiceProtocol.BuildId, StringComparison.OrdinalIgnoreCase);
}
