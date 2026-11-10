using System.Globalization;
using PcToolkit.Core.Overclock;

namespace PcToolkit.Core.Games;

/// <summary>
/// Игровые настройки драйвера AMD, которые меняет профиль игры. null — параметр не трогаем.
/// Действуют на все игры сразу (как «Глобальная графика» в AMD Software), поэтому профиль включает их только
/// на время игры и потом возвращает прежние значения.
/// </summary>
public sealed record RadeonValues
{
    public bool? AntiLag { get; init; }

    public bool? Chill { get; init; }

    public int? ChillMinFps { get; init; }

    public int? ChillMaxFps { get; init; }

    public bool? Sharpening { get; init; }

    public int? Sharpness { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => AntiLag is null && Chill is null && ChillMinFps is null && ChillMaxFps is null && Sharpening is null && Sharpness is null;
}

/// <summary>Что поддерживает видеокарта и что включено сейчас.</summary>
public sealed record RadeonState(
    RadeonValues Values,
    bool AntiLagSupported,
    bool ChillSupported,
    bool SharpeningSupported,
    TuningRange? ChillRange,
    TuningRange? SharpnessRange)
{
    public bool AnySupported => AntiLagSupported || ChillSupported || SharpeningSupported;
}

/// <summary>Чтение и запись игровых настроек драйвера AMD (в тестах — подделка).</summary>
public interface IRadeonGraphics
{
    /// <summary>null — видеокарта не AMD или драйвер не даёт этих настроек; причина в <paramref name="error"/>.</summary>
    RadeonState? Read(out string? error);

    /// <summary>Записывает заданные (не null) значения: сначала выключения, потом включения.</summary>
    void Apply(RadeonValues values);
}

/// <summary>Настройки через ADLX — официальную библиотеку AMD из драйвера.</summary>
public sealed class AdlxRadeonGraphics : IRadeonGraphics
{
    public static AdlxRadeonGraphics Instance { get; } = new();

    public RadeonState? Read(out string? error)
    {
        AdlxGpuTuning? adlx = AdlxGpuTuning.Shared(out error);
        if (adlx is null)
            return null;
        try
        {
            RadeonState state = adlx.Read3DSettings();
            if (state.AnySupported)
                return state;
            error = "эта видеокарта не поддерживает Anti-Lag, Chill и резкость";
            return null;
        }
        catch (AdlxException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public void Apply(RadeonValues values)
    {
        AdlxGpuTuning adlx = AdlxGpuTuning.Shared(out string? error) ?? throw new AdlxException(error ?? "ADLX недоступен", -1);
        adlx.Apply3DSettings(values);
    }
}

/// <summary>Режим задержки в профиле: Anti-Lag и Chill одновременно не работают — драйвер выключает второй сам.</summary>
public static class RadeonModes
{
    public const string None = "";
    public const string AntiLag = "antilag";
    public const string Chill = "chill";

    /// <summary>Ограничение FPS: Radeon Chill с одинаковыми минимумом и максимумом — драйвер держит ровно столько кадров.</summary>
    public const string Limit = "limit";
}

/// <summary>Что профиль хочет поменять, что вернуть после игры и как это описать словами.</summary>
public static class RadeonPlan
{
    /// <summary>
    /// Значения, которые нужно записать на время игры (только поддерживаемые). Включение Anti-Lag заодно записывает
    /// «Chill выключен» и наоборот: драйвер всё равно выключит второй, а так прежнее состояние обоих вернётся после игры.
    /// </summary>
    public static RadeonValues Wanted(GameProfile profile, RadeonState state)
    {
        bool? antiLag = null;
        bool? chill = null;
        int? min = null;
        int? max = null;
        if (profile.RadeonMode == RadeonModes.AntiLag && state.AntiLagSupported)
        {
            antiLag = true;
            if (state.ChillSupported)
                chill = false;
        }
        else if (profile.RadeonMode == RadeonModes.Limit && state.ChillSupported)
        {
            chill = true;
            min = max = state.ChillRange is { } range ? range.Snap(profile.LimitFps) : profile.LimitFps;
            if (state.AntiLagSupported)
                antiLag = false;
        }
        else if (profile.RadeonMode == RadeonModes.Chill && state.ChillSupported)
        {
            chill = true;
            (int low, int high) = (Math.Min(profile.ChillMinFps, profile.ChillMaxFps), Math.Max(profile.ChillMinFps, profile.ChillMaxFps));
            min = state.ChillRange is { } range ? range.Snap(low) : low;
            max = state.ChillRange is { } range2 ? range2.Snap(high) : high;
            if (state.AntiLagSupported)
                antiLag = false;
        }

        bool? sharpening = null;
        int? sharpness = null;
        if (profile.Sharpening && state.SharpeningSupported)
        {
            sharpening = true;
            sharpness = state.SharpnessRange is { } range ? range.Snap(profile.Sharpness) : profile.Sharpness;
        }

        return new RadeonValues
        {
            AntiLag = antiLag,
            Chill = chill,
            ChillMinFps = min,
            ChillMaxFps = max,
            Sharpening = sharpening,
            Sharpness = sharpness,
        };
    }

    /// <summary>Нужно ли что-то менять: хоть одно значение отличается от текущего.</summary>
    public static bool Differs(RadeonValues wanted, RadeonValues current) =>
        (wanted.AntiLag is not null && wanted.AntiLag != current.AntiLag)
        || (wanted.Chill is not null && wanted.Chill != current.Chill)
        || (wanted.ChillMinFps is not null && wanted.ChillMinFps != current.ChillMinFps)
        || (wanted.ChillMaxFps is not null && wanted.ChillMaxFps != current.ChillMaxFps)
        || (wanted.Sharpening is not null && wanted.Sharpening != current.Sharpening)
        || (wanted.Sharpness is not null && wanted.Sharpness != current.Sharpness);

    /// <summary>Текущие значения тех параметров, которые профиль меняет: их и вернуть после игры.</summary>
    public static RadeonValues Before(RadeonValues wanted, RadeonValues current) => new()
    {
        AntiLag = wanted.AntiLag is null ? null : current.AntiLag,
        Chill = wanted.Chill is null ? null : current.Chill,
        ChillMinFps = wanted.ChillMinFps is null ? null : current.ChillMinFps,
        ChillMaxFps = wanted.ChillMaxFps is null ? null : current.ChillMaxFps,
        Sharpening = wanted.Sharpening is null ? null : current.Sharpening,
        Sharpness = wanted.Sharpness is null ? null : current.Sharpness,
    };

    /// <summary>
    /// Что вернуть после игры: параметр возвращается, только если он всё ещё такой, каким его поставил профиль
    /// (поменяли вручную в AMD Software — не трогаем). Anti-Lag и Chill возвращаются парой: включение одного
    /// выключает другой, поэтому если вручную не меняли ни один, возвращаются оба.
    /// </summary>
    public static RadeonValues Restore(RadeonValues before, RadeonValues applied, RadeonValues now)
    {
        bool Same<T>(T? applied, T? current) where T : struct => applied is null || EqualityComparer<T?>.Default.Equals(applied, current);

        bool latencyUntouched = Same(applied.AntiLag, now.AntiLag) && Same(applied.Chill, now.Chill);
        bool chillUntouched = latencyUntouched && Same(applied.ChillMinFps, now.ChillMinFps) && Same(applied.ChillMaxFps, now.ChillMaxFps);
        bool sharpUntouched = Same(applied.Sharpening, now.Sharpening) && Same(applied.Sharpness, now.Sharpness);
        return new RadeonValues
        {
            AntiLag = latencyUntouched ? before.AntiLag : null,
            Chill = latencyUntouched ? before.Chill : null,
            ChillMinFps = chillUntouched ? before.ChillMinFps : null,
            ChillMaxFps = chillUntouched ? before.ChillMaxFps : null,
            Sharpening = sharpUntouched ? before.Sharpening : null,
            Sharpness = sharpUntouched ? before.Sharpness : null,
        };
    }

    /// <summary>«Anti-Lag, резкость 80 %» — что включено (для подписи в профиле и журнала).</summary>
    public static string Describe(RadeonValues values)
    {
        var parts = new List<string>();
        if (values.AntiLag == true)
            parts.Add("Radeon Anti-Lag");
        if (values.Chill == true)
        {
            parts.Add(values.ChillMinFps is int min && values.ChillMaxFps is int max
                ? $"Radeon Chill {min.ToString(CultureInfo.InvariantCulture)}–{max.ToString(CultureInfo.InvariantCulture)} FPS"
                : "Radeon Chill");
        }

        if (values.Sharpening == true)
            parts.Add(values.Sharpness is int s ? $"резкость {s.ToString(CultureInfo.InvariantCulture)} %" : "резкость");
        if (parts.Count == 0)
        {
            if (values.AntiLag == false && values.Chill == false)
                parts.Add("Anti-Lag и Chill выключены");
            else if (values.AntiLag == false)
                parts.Add("Anti-Lag выключен");
            else if (values.Chill == false)
                parts.Add("Chill выключен");
            if (values.Sharpening == false)
                parts.Add("резкость выключена");
        }

        return string.Join(", ", parts);
    }
}
