using System.Text.Json;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Overclock;

/// <summary>
/// Разгона в программе больше нет (решено 7 ноября 2026: неопытные пользователи могут навредить железу и данным).
/// Остаётся только уборка за прежними версиями: вернуть настройки видеокарты после оборванной проверки
/// или автоподбора и отменить настройки, которые всё ещё действуют.
/// </summary>
public static class GpuTuningRecovery
{
    /// <summary>Есть ли в журнале применённые и не отменённые настройки видеокарты.</summary>
    public static bool HasActiveTuning(ChangeEngine engine) =>
        engine.ActiveEntries().Values.Any(e => string.Equals(e.Target.Type, GpuTuningTarget.TypeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>План: вернуть все действующие настройки видеокарты к значениям до программы.</summary>
    public static ChangePlan PlanRevertAll(ChangeEngine engine) =>
        engine.PlanRevertType(GpuTuningTarget.TypeName, "Вернуть заводские настройки видеокарты");

    /// <summary>
    /// Если прошлая проверка разгона или автоподбор оборвались (зависание, перезагрузка), возвращает настройки видеокарты.
    /// Возвращает текст для пользователя или null, если возвращать нечего.
    /// </summary>
    public static string? RecoverInterrupted(ChangeEngine engine)
    {
        TuningMarker? marker = TuningMarker.Load();
        if (marker is null)
            return null;

        try
        {
            AppLog.Warn($"Найдена незавершённая проверка разгона ({marker.Kind}, начата {marker.StartedAt:g}).");
            if (marker.Kind == TuningMarker.CheckKind)
            {
                ChangePlan plan = engine.PlanRevertSet(marker.SetId);
                ChangeResult result = plan.CanExecute ? engine.Execute(plan) : ChangeResult.Fail(string.Join(" ", plan.Problems.Concat(plan.Skipped)));
                return "Прошлая проверка разгона не завершилась — похоже, компьютер завис или перезагрузился. " +
                       (result.Success ? "Настройки видеокарты возвращены к прежним." : "Вернуть настройки не удалось: " + result.Message +
                                                                                        " Драйвер AMD после сбоя обычно сам сбрасывает разгон.");
            }

            if (marker.Kind == TuningMarker.TuneKind && AdlxGpuTuning.Shared(out _) is { } tuning)
            {
                tuning.Set(marker.Parameter, marker.Original);
                return "Прошлый автоподбор не завершился — похоже, компьютер завис или перезагрузился. " +
                       $"{GpuParameters.Title(marker.Parameter)}: возвращено {GpuParameters.Describe(marker.Parameter, marker.Original)}.";
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось вернуть настройки видеокарты после прерванной проверки.", ex);
            return "Прошлая проверка разгона не завершилась, и вернуть настройки не удалось: " + ex.Message;
        }
        finally
        {
            TuningMarker.Delete();
        }
    }
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
