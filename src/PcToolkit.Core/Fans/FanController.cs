namespace PcToolkit.Core.Fans;

/// <summary>Вентилятор, которым можно управлять через библиотеку датчиков: материнская плата, контроллер, видеокарта NVIDIA.</summary>
public sealed record FanChannel(string Id, string Name, string Device, FanChannelKind Kind, double? Rpm, double? Duty);

public enum FanChannelKind
{
    Motherboard,
    Controller,
    GpuNvidia,
}

/// <summary>
/// Обороты вентиляторов по кривым — раз в секунду, по свежим температурам. Обороты растут быстро и падают
/// медленно, с запасом в пару процентов: вентиляторы не «качаются» от каждого скачка температуры.
/// Защита от перегрева: процессор или видеокарта от <see cref="OverheatOn"/> °C — все управляемые вентиляторы на 100 %,
/// пока обе температуры не опустятся ниже <see cref="OverheatOff"/> °C.
/// </summary>
public sealed class FanController
{
    public const double OverheatOn = 90;
    public const double OverheatOff = 80;

    /// <summary>На сколько процентов за секунду обороты могут вырасти и упасть.</summary>
    public const double RiseStep = 8;
    public const double FallStep = 3;

    /// <summary>Пока цель ниже текущих оборотов меньше чем на столько, обороты не снижаются.</summary>
    public const double DeadBand = 2;

    private readonly Dictionary<string, double> _current = new();

    public bool Overheat { get; private set; }

    /// <summary>
    /// Обороты на этот шаг: Id → % (null — отдать вентилятор BIOS). Вентиляторы с ролью «помпа» и «не управлять»,
    /// а также те, для чьей кривой нет температуры, отдаются BIOS.
    /// </summary>
    public IReadOnlyDictionary<string, double?> Tick(FanSettings settings, IEnumerable<FanChannel> channels, double? cpuTemperature, double? gpuTemperature)
    {
        if (cpuTemperature >= OverheatOn || gpuTemperature >= OverheatOn)
            Overheat = true;
        else if (Overheat && (cpuTemperature ?? 0) < OverheatOff && (gpuTemperature ?? 0) < OverheatOff)
            Overheat = false;

        FanTemplate template = settings.Active;
        var result = new Dictionary<string, double?>();
        foreach (FanChannel channel in channels)
        {
            FanChannelConfig config = settings.Channels.FirstOrDefault(c => c.Id == channel.Id) ?? new FanChannelConfig { Id = channel.Id, Role = DefaultRole(channel) };
            FanRole role = channel.Kind == FanChannelKind.GpuNvidia ? FanRole.Gpu : config.Role;
            double? temperature = role switch
            {
                FanRole.Cpu => cpuTemperature,
                FanRole.Gpu => gpuTemperature,
                FanRole.Case => template.CaseSource switch
                {
                    CaseSource.Cpu => cpuTemperature,
                    CaseSource.Hottest => Max(cpuTemperature, gpuTemperature),
                    _ => gpuTemperature ?? cpuTemperature,
                },
                _ => null,
            };

            if (!settings.Enabled || role is FanRole.Pump or FanRole.Ignore || config.Unresponsive || temperature is null)
            {
                result[channel.Id] = null;
                _current.Remove(channel.Id);
                continue;
            }

            double floor = Math.Max(FanSettings.AbsoluteMinDuty, config.MinDuty);
            double target = Math.Clamp(Math.Max(template.CurveFor(role).DutyAt(temperature.Value), floor), 0, 100);
            double next;
            if (Overheat)
            {
                next = 100;
            }
            else if (!_current.TryGetValue(channel.Id, out double previous))
            {
                next = target;
            }
            else if (target > previous)
            {
                next = Math.Min(target, previous + RiseStep);
            }
            else if (target < previous - DeadBand)
            {
                next = Math.Max(target, previous - FallStep);
            }
            else
            {
                next = previous;
            }

            next = Math.Round(next, 1);
            _current[channel.Id] = next;
            result[channel.Id] = next;
        }

        return result;
    }

    /// <summary>Сбросить сглаживание (режим или кривая сменились — сразу к новой цели).</summary>
    public void Reset() => _current.Clear();

    /// <summary>
    /// Роль по умолчанию: первый вентилятор материнской платы обычно разъём кулера процессора, остальные — корпусные;
    /// разъём, где вентилятор не крутится, не трогаем.
    /// </summary>
    public static FanRole DefaultRole(FanChannel channel)
    {
        if (channel.Kind == FanChannelKind.GpuNvidia)
            return FanRole.Gpu;
        if (channel.Rpm is not > 0)
            return FanRole.Ignore;
        if (channel.Name.Contains("pump", StringComparison.OrdinalIgnoreCase) || channel.Name.Contains("помп", StringComparison.OrdinalIgnoreCase))
            return FanRole.Pump;
        if (channel.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase))
            return FanRole.Cpu;
        return channel.Kind == FanChannelKind.Motherboard && channel.Id.EndsWith("/control/0", StringComparison.Ordinal) ? FanRole.Cpu : FanRole.Case;
    }

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
