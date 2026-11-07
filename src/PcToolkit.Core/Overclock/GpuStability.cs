using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PcToolkit.Core.Benchmarks.Gpu;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Overclock;

/// <summary>Итог проверки стабильности видеокарты.</summary>
public sealed record StabilityResult(bool Passed, bool Cancelled, string Message, TimeSpan Duration, long Errors, bool DeviceLost);

/// <summary>
/// Проверка стабильности после разгона: 3D-сцена и вычисления из стресс-теста (как OCCT) на заданное время.
/// Нестабильный разгон выдаёт ошибки в кадрах или вычислениях, а сильно нестабильный — сброс драйвера (TDR).
/// </summary>
public static class GpuStability
{
    public static async Task<StabilityResult> RunAsync(TimeSpan duration, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        var watch = Stopwatch.StartNew();
        using var test = new GpuStressTest(render: true, compute: true, memory: false);
        test.Start();
        bool cancelled = false;
        try
        {
            while (test.IsRunning && test.ErrorCount == 0 && !test.DeviceLost)
            {
                // Время считаем с начала нагрузки: подготовка (компиляция шейдеров, эталон кадра) не в счёт.
                if (test.Phase == GpuTestPhase.Running && test.Elapsed >= duration)
                    break;
                progress?.Report(test.Phase == GpuTestPhase.Running ? Math.Min(1, test.Elapsed / duration) : 0);
                await Task.Delay(250, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        test.Stop(cancelled ? "отменён" : "проверка разгона завершена");
        progress?.Report(1);

        if (test.DeviceLost)
            return new StabilityResult(false, false, "Сброс драйвера видеокарты (TDR) — разгон нестабилен.", watch.Elapsed, test.ErrorCount, true);
        if (test.ErrorCount > 0)
            return new StabilityResult(false, false, $"Ошибки при расчётах: {test.ErrorCount} — разгон нестабилен.", watch.Elapsed, test.ErrorCount, false);
        if (test.Failure is { } failure)
            return new StabilityResult(false, false, "Тест прервался: " + failure, watch.Elapsed, 0, false);
        if (cancelled)
            return new StabilityResult(false, true, "Проверка отменена.", watch.Elapsed, 0, false);
        return new StabilityResult(true, false, $"Ошибок нет за {Format(test.Elapsed)}.", watch.Elapsed, 0, false);
    }

    public static string Format(TimeSpan time) => time.TotalMinutes >= 1
        ? $"{(int)time.TotalMinutes} мин {time.Seconds} с"
        : $"{(int)time.TotalSeconds} с";
}

/// <summary>Что подбирать.</summary>
public enum TuneGoal
{
    /// <summary>Понизить напряжение при тех же частотах: холоднее, тише, часто даже быстрее.</summary>
    Undervolt,

    /// <summary>Поднять максимальную частоту графического ядра.</summary>
    OverclockGpu,

    /// <summary>Поднять частоту видеопамяти.</summary>
    OverclockVram,
}

public sealed record TuneProgress(string Text, int Step, int StepCount, double StepProgress);

/// <summary>Итог автоподбора: найденное значение (или null) и исходное, к которому всё вернулось.</summary>
public sealed record TuneResult(TuneGoal Goal, string Parameter, int Original, int? Found, string Message, IReadOnlyList<string> Log, bool Cancelled);

/// <summary>
/// Автоподбор по тестам: шаг за шагом меняет один параметр и после каждого шага гоняет проверку стабильности.
/// На первой ошибке или сбросе драйвера останавливается и берёт последнее стабильное значение с запасом,
/// потом проверяет его дольше. Во время подбора значения пишутся напрямую, а в конце возвращается исходное —
/// найденное применяется уже через движок изменений (предпросмотр, журнал, откат). Если компьютер зависнет,
/// при следующем запуске исходное значение вернётся по файлу-отметке.
/// </summary>
public sealed class GpuAutoTuner(AdlxGpuTuning tuning, TuneGoal goal, TimeSpan stepDuration, TimeSpan confirmDuration)
{
    private readonly List<string> _log = new();

    public event EventHandler<TuneProgress>? Progress;

    public static (string Parameter, int Step, int Margin, int MaxSteps) Plan(TuneGoal goal) => goal switch
    {
        TuneGoal.Undervolt => (AdlxGpuTuning.GfxVoltage, -15, 10, 12),
        TuneGoal.OverclockGpu => (AdlxGpuTuning.GfxMaxFrequency, 25, 25, 12),
        _ => (AdlxGpuTuning.VramMaxFrequency, 10, 10, 15),
    };

    public async Task<TuneResult> RunAsync(CancellationToken cancellation)
    {
        (string parameter, int step, int margin, int maxSteps) = Plan(goal);
        GpuTuningInfo info = tuning.Read();
        TuningRange range = RangeOf(info, parameter) ?? throw new InvalidOperationException("Видеокарта не поддерживает этот параметр.");
        int original = int.Parse(tuning.Get(parameter), CultureInfo.InvariantCulture);
        int limit = step < 0 ? range.Min : range.Max;

        TuningMarker.Save(new TuningMarker { Kind = TuningMarker.TuneKind, Parameter = parameter, Original = original.ToString(CultureInfo.InvariantCulture) });
        AppLog.Info($"Автоподбор {goal}: {parameter} с {original}, шаг {step}, проверка {stepDuration.TotalSeconds:0} с.");

        int lastGood = original;
        bool failed = false;
        bool cancelled = false;
        try
        {
            for (int i = 1; i <= maxSteps; i++)
            {
                int value = range.Snap(original + step * i);
                if (value == lastGood || (step < 0 ? value < limit : value > limit))
                    break;

                Report($"Шаг {i}: {GpuParameters.Describe(parameter, Str(value))} — проверка стабильности", i, maxSteps + 1, 0);
                tuning.Set(parameter, Str(value));
                await Task.Delay(500, cancellation);

                var progress = new Progress<double>(p => Report($"Шаг {i}: {GpuParameters.Describe(parameter, Str(value))} — проверка стабильности", i, maxSteps + 1, p));
                StabilityResult result = await GpuStability.RunAsync(stepDuration, progress, cancellation);
                if (result.Cancelled)
                {
                    cancelled = true;
                    break;
                }

                Log($"{GpuParameters.Describe(parameter, Str(value))}: {(result.Passed ? "стабильно" : result.Message)}");
                if (!result.Passed)
                {
                    failed = true;
                    break;
                }

                lastGood = value;
                if (value == limit)
                    break;
            }

            if (cancelled)
                return Finish(parameter, original, null, "Автоподбор отменён — значение возвращено.", true);
            if (lastGood == original)
                return Finish(parameter, original, null, failed
                    ? "Уже первый шаг оказался нестабильным: у этой видеокарты нет запаса. Значение возвращено."
                    : "Запаса нет: параметр уже на пределе.", false);

            // Последнее стабильное значение — с запасом в сторону исходного, и проверка подольше.
            int candidate = failed ? range.Snap(lastGood - Math.Sign(step) * margin) : lastGood;
            if (step < 0 ? candidate > original : candidate < original)
                candidate = original;

            for (int attempt = 0; attempt < 2 && candidate != original; attempt++)
            {
                Report($"Итоговая проверка: {GpuParameters.Describe(parameter, Str(candidate))}", maxSteps + 1, maxSteps + 1, 0);
                tuning.Set(parameter, Str(candidate));
                await Task.Delay(500, cancellation);
                var progress = new Progress<double>(p => Report($"Итоговая проверка: {GpuParameters.Describe(parameter, Str(candidate))}", maxSteps + 1, maxSteps + 1, p));
                StabilityResult final = await GpuStability.RunAsync(confirmDuration, progress, cancellation);
                if (final.Cancelled)
                    return Finish(parameter, original, null, "Автоподбор отменён — значение возвращено.", true);

                Log($"Итоговая проверка {GpuParameters.Describe(parameter, Str(candidate))}: {(final.Passed ? "стабильно" : final.Message)}");
                if (final.Passed)
                {
                    return Finish(parameter, original, candidate,
                        $"Найдено: {GpuParameters.Describe(parameter, Str(candidate))} (было {GpuParameters.Describe(parameter, Str(original))}).", false);
                }

                candidate = range.Snap(candidate - Math.Sign(step) * margin);
                if (step < 0 ? candidate > original : candidate < original)
                    candidate = original;
            }

            return Finish(parameter, original, null, "Итоговая проверка не прошла — значение возвращено.", false);
        }
        catch (OperationCanceledException)
        {
            return Finish(parameter, original, null, "Автоподбор отменён — значение возвращено.", true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка автоподбора.", ex);
            return Finish(parameter, original, null, "Автоподбор прерван: " + ex.Message, false);
        }
    }

    private TuneResult Finish(string parameter, int original, int? found, string message, bool cancelled)
    {
        try
        {
            tuning.Set(parameter, Str(original));
            TuningMarker.Delete();
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось вернуть исходное значение после автоподбора.", ex);
            message += " Исходное значение вернуть не удалось: " + ex.Message;
        }

        AppLog.Info($"Автоподбор {goal}: {message}");
        return new TuneResult(goal, parameter, original, found, message, _log.ToList(), cancelled);
    }

    private void Report(string text, int step, int count, double progress) =>
        Progress?.Invoke(this, new TuneProgress(text, step, count, progress));

    private void Log(string line)
    {
        _log.Add(line);
        AppLog.Info("Автоподбор: " + line);
    }

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static TuningRange? RangeOf(GpuTuningInfo info, string parameter) => parameter switch
    {
        AdlxGpuTuning.GfxVoltage => info.VoltageRange,
        AdlxGpuTuning.GfxMaxFrequency => info.MaxFrequencyRange,
        AdlxGpuTuning.VramMaxFrequency => info.VramFrequencyRange,
        _ => null,
    };
}

/// <summary>
/// Отметка «идёт проверка разгона или автоподбор». Если приложение или компьютер упали посреди проверки,
/// при следующем запуске по ней возвращаются прежние настройки видеокарты.
/// </summary>
public sealed class TuningMarker
{
    public const string CheckKind = "check";
    public const string TuneKind = "tune";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "gpu-tuning-check.json");

    /// <summary>check — проверка после применения набора изменений; tune — автоподбор одного параметра.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Для check: набор изменений в журнале, который нужно откатить.</summary>
    public Guid SetId { get; set; }

    /// <summary>Для tune: параметр и его исходное значение.</summary>
    public string Parameter { get; set; } = "";

    public string Original { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    public static void Save(TuningMarker marker)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(marker));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось записать отметку проверки разгона.", ex);
        }
    }

    public static TuningMarker? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<TuningMarker>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать отметку проверки разгона.", ex);
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось удалить отметку проверки разгона.", ex);
        }
    }
}
