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
/// Обороты вентиляторов по кривым — раз в секунду, по свежим температурам. Температуры для кривых сглаживаются
/// за несколько секунд: процессор Ryzen подскакивает на 10–15 °C на долю секунды, и вентилятор не должен «вздыхать»
/// от каждого такого скачка. Обороты растут быстро и падают медленно, с запасом в пару процентов.
/// Защита от перегрева — по мгновенным температурам: процессор или видеокарта от <see cref="OverheatOn"/> °C —
/// все управляемые вентиляторы на 100 %, пока обе температуры не опустятся ниже <see cref="OverheatOff"/> °C.
/// </summary>
public sealed class FanController
{
    public const double OverheatOn = 90;
    public const double OverheatOff = 80;

    /// <summary>Доля новой температуры за шаг (секунду): за 3 секунды — около 70 % скачка, секундный всплеск — треть.</summary>
    public const double DefaultSmoothing = 0.35;

    /// <summary>На сколько процентов за секунду обороты могут вырасти и упасть.</summary>
    public const double RiseStep = 8;
    public const double FallStep = 3;

    /// <summary>Пока цель ниже текущих оборотов меньше чем на столько, обороты не снижаются.</summary>
    public const double DeadBand = 2;

    private readonly Dictionary<string, double> _current = new();
    private readonly double _smoothing;
    private double? _cpu;
    private double? _gpu;
    private double? _hotSpot;

    /// <param name="smoothing">Доля новой температуры за шаг: 1 — без сглаживания.</param>
    public FanController(double smoothing = DefaultSmoothing)
    {
        _smoothing = Math.Clamp(smoothing, 0.05, 1);
    }

    public bool Overheat { get; private set; }

    /// <summary>
    /// Обороты на этот шаг: Id → % (null — отдать вентилятор BIOS или драйверу видеокарты). Вентиляторы с ролью
    /// «помпа» и «не управлять», видеокарта на автоматике драйвера, а также те, для чьей кривой нет температуры,
    /// отдаются BIOS и драйверу.
    /// </summary>
    /// <param name="gpuHotSpot">Горячая точка видеокарты: по ней кривая её вентиляторов (если нет — по температуре ядра).</param>
    public IReadOnlyDictionary<string, double?> Tick(FanSettings settings, IEnumerable<FanChannel> channels, double? cpuTemperature, double? gpuTemperature,
                                                     double? gpuHotSpot = null)
    {
        if (cpuTemperature >= OverheatOn || gpuTemperature >= OverheatOn)
            Overheat = true;
        else if (Overheat && (cpuTemperature ?? 0) < OverheatOff && (gpuTemperature ?? 0) < OverheatOff)
            Overheat = false;

        _cpu = Smooth(_cpu, cpuTemperature);
        _gpu = Smooth(_gpu, gpuTemperature);
        _hotSpot = Smooth(_hotSpot, gpuHotSpot);
        double? cpu = _cpu, gpu = _gpu, gpuFans = _hotSpot ?? _gpu;

        FanTemplate template = settings.Active;
        var result = new Dictionary<string, double?>();
        foreach (FanChannel channel in channels)
        {
            FanChannelConfig config = settings.Channels.FirstOrDefault(c => c.Id == channel.Id) ?? new FanChannelConfig { Id = channel.Id, Role = DefaultRole(channel) };
            FanRole role = channel.Kind == FanChannelKind.GpuNvidia ? FanRole.Gpu : config.Role;
            double? temperature = role switch
            {
                FanRole.Cpu => cpu,
                FanRole.Gpu => gpuFans,
                FanRole.Case => template.CaseSource switch
                {
                    CaseSource.Cpu => cpu,
                    CaseSource.Hottest => Max(cpu, gpu),
                    _ => gpu ?? cpu,
                },
                _ => null,
            };

            if (!settings.Enabled || role is FanRole.Pump or FanRole.Ignore || (role == FanRole.Gpu && template.GpuByDriver) ||
                config.Unresponsive || temperature is null)
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

    private double? Smooth(double? previous, double? value) =>
        value is not double v || double.IsNaN(v) ? null
        : previous is not double p ? v
        : p + (v - p) * _smoothing;

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
