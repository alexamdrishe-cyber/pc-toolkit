namespace PcToolkit.Core.Tweaks;

public sealed record QuestionOption(string Id, string Title, string Description, IReadOnlyList<string> Tags);

public sealed record Question(string Id, string Title, IReadOnlyList<QuestionOption> Options, string DefaultOptionId);

/// <summary>
/// Режим анкеты: несколько простых вопросов → набор рекомендованных настроек.
/// Рекомендуются только настройки с низким риском; остальные доступны в экспертном режиме.
/// </summary>
public static class Questionnaire
{
    public static IReadOnlyList<Question> Questions { get; } =
    [
        new Question(
            "usage",
            "Для чего вы чаще всего используете компьютер?",
            [
                new QuestionOption("gaming", "Игры", "Важны кадры в секунду, отклик мыши и сети.", ["gaming"]),
                new QuestionOption("streaming", "Стримы и запись видео", "Игры плюс OBS или другая программа записи.", ["streaming"]),
                new QuestionOption("work", "Работа и учёба", "Браузер, офис, мессенджеры.", ["work"]),
                new QuestionOption("general", "Всё понемногу", "Немного игр, немного работы.", ["general"]),
            ],
            "general"),
        new Question(
            "interface",
            "Что важнее в интерфейсе Windows?",
            [
                new QuestionOption("beauty", "Красота", "Оставить анимации и прозрачность.", []),
                new QuestionOption("speed", "Отклик", "Меньше эффектов и задержек в меню и окнах.", ["speed"]),
            ],
            "beauty"),
        new Question(
            "privacy",
            "Убрать рекламу и советы Windows, отправлять меньше данных в Microsoft?",
            [
                new QuestionOption("yes", "Да", "Отключить рекламный идентификатор, советы и рекомендации.", ["privacy"]),
                new QuestionOption("no", "Нет", "Оставить как есть.", []),
            ],
            "yes"),
    ];

    /// <param name="answers">Ответы: идентификатор вопроса → идентификатор варианта.</param>
    public static IReadOnlyList<TweakDefinition> Recommend(
        TweakCatalog catalog,
        IReadOnlyDictionary<string, string> answers,
        bool isLaptop,
        int windowsBuild)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Question question in Questions)
        {
            string optionId = answers.TryGetValue(question.Id, out string? answer) ? answer : question.DefaultOptionId;
            QuestionOption? option = question.Options.FirstOrDefault(o => o.Id == optionId);
            if (option is not null)
                tags.UnionWith(option.Tags);
        }

        return catalog.Tweaks
            .Where(t => t.Risk == TweakRisk.Low
                        && t.RecommendedFor.Any(tags.Contains)
                        && !(t.DesktopOnly && isLaptop)
                        && t.IsApplicableToBuild(windowsBuild))
            .ToList();
    }
}
