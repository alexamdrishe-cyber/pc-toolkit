using System.Globalization;
using PcToolkit.Core.Changes;

namespace PcToolkit.Core.Overclock;

/// <summary>
/// Параметр видеокарты AMD для движка изменений: максимальная частота, напряжение, предел мощности, кривая вентилятора…
/// Так разгон попадает в предпросмотр «было → станет», в журнал и откатывается так же, как остальные настройки.
/// </summary>
public sealed class GpuTuningTarget(string parameter) : ISettingTarget
{
    public const string TypeName = "gpu";
    public const string KindName = "GpuTuning";

    public string Parameter { get; } = parameter;

    public string Key => "gpu:" + Parameter;

    public string DisplayName => GpuParameters.Title(Parameter);

    // ADLX работает от имени пользователя, как AMD Adrenalin: права администратора не нужны.
    public bool RequiresAdmin => false;

    public bool IsAvailable(out string? reason)
    {
        AdlxGpuTuning? tuning = AdlxGpuTuning.Shared(out reason);
        if (tuning is null)
            return false;

        if (!tuning.Supports(Parameter))
        {
            reason = $"видеокарта не поддерживает параметр «{DisplayName}»";
            return false;
        }

        reason = null;
        return true;
    }

    public ValueSnapshot Read() => new(true, KindName, Tuning.Get(Parameter));

    public void Write(ValueSnapshot value)
    {
        if (!value.Exists)
            throw new InvalidOperationException("У параметра видеокарты нет значения «отсутствует».");
        Tuning.Set(Parameter, value.Data);
    }

    public string Describe(ValueSnapshot value) => value.Exists ? GpuParameters.Describe(Parameter, value.Data) : "не задано";

    private static AdlxGpuTuning Tuning =>
        AdlxGpuTuning.Shared(out string? error) ?? throw new InvalidOperationException("Настройки видеокарты недоступны: " + error);

    public static ChangeDefinition Definition(string parameter, string value) => new()
    {
        Type = TypeName,
        Name = parameter,
        Value = value,
    };
}

/// <summary>Названия параметров видеокарты и запись значений с единицами.</summary>
public static class GpuParameters
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string Title(string parameter) => parameter switch
    {
        AdlxGpuTuning.GfxMinFrequency => "Видеокарта: минимальная частота",
        AdlxGpuTuning.GfxMaxFrequency => "Видеокарта: максимальная частота",
        AdlxGpuTuning.GfxVoltage => "Видеокарта: напряжение",
        AdlxGpuTuning.VramMaxFrequency => "Видеопамять: частота",
        AdlxGpuTuning.VramTiming => "Видеопамять: тайминги",
        AdlxGpuTuning.PowerLimit => "Видеокарта: предел мощности",
        AdlxGpuTuning.PowerTdc => "Видеокарта: предел тока (TDC)",
        AdlxGpuTuning.FanCurve => "Вентиляторы: кривая",
        AdlxGpuTuning.FanZeroRpm => "Вентиляторы: остановка без нагрузки (Zero RPM)",
        AdlxGpuTuning.FanMinAcoustic => "Вентиляторы: акустический предел",
        AdlxGpuTuning.FanTargetSpeed => "Вентиляторы: целевая скорость",
        AdlxGpuTuning.FanMinSpeed => "Вентиляторы: минимальная скорость",
        AdlxGpuTuning.Preset => "Видеокарта: режим",
        _ => "Видеокарта: " + parameter,
    };

    public static string Describe(string parameter, string value)
    {
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number);
        return parameter switch
        {
            AdlxGpuTuning.GfxMinFrequency or AdlxGpuTuning.GfxMaxFrequency or AdlxGpuTuning.VramMaxFrequency
                or AdlxGpuTuning.FanMinAcoustic => $"{number} МГц",
            AdlxGpuTuning.GfxVoltage => $"{number} мВ",
            AdlxGpuTuning.PowerLimit or AdlxGpuTuning.PowerTdc => number > 0 ? $"+{number} %" : $"{number} %",
            AdlxGpuTuning.FanTargetSpeed or AdlxGpuTuning.FanMinSpeed => $"{number} об/мин",
            AdlxGpuTuning.FanZeroRpm => value == "1" ? "включена" : "выключена",
            AdlxGpuTuning.VramTiming => TimingName(number),
            AdlxGpuTuning.FanCurve => string.Join(", ", AdlxGpuTuning.ParseCurve(value).Select(p => $"{p.Temperature}° → {p.Speed} %")),
            AdlxGpuTuning.Preset => PresetName(value),
            _ => value,
        };
    }

    public static string PresetName(string preset) => preset switch
    {
        "PowerSaver" => "Энергосбережение",
        "Quiet" => "Тихий",
        "Balanced" => "Сбалансированный",
        "Turbo" => "Турбо",
        "Rage" => "Rage",
        "Custom" => "Свой (ручная настройка)",
        _ => preset,
    };

    /// <summary>ADLX_MEMORYTIMING_DESCRIPTION.</summary>
    public static string TimingName(int timing) => timing switch
    {
        0 => "по умолчанию",
        1 => "быстрые",
        2 => "быстрые, уровень 2",
        3 => "автоматически",
        4 => "уровень 1",
        5 => "уровень 2",
        _ => timing.ToString(Russian),
    };
}
