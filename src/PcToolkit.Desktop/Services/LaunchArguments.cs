namespace PcToolkit.Desktop.Services;

/// <summary>Аргументы командной строки, с которыми приложение перезапускает само себя.</summary>
public static class LaunchArguments
{
    /// <summary>Перезапуск: подождать, пока закроется прежняя копия.</summary>
    public const string Restarted = "--restarted";

    /// <summary>Сразу после запуска (уже с правами администратора) установить драйвер PawnIO.</summary>
    public const string InstallDriver = "--install-pawnio";

    /// <summary>Сразу после запуска (уже с правами администратора) переустановить драйвер PawnIO: удалить и поставить заново.</summary>
    public const string RepairDriver = "--repair-pawnio";

    /// <summary>Установить или обновить службу PC Toolkit и выйти (запускается с правами администратора).</summary>
    public const string InstallService = "--install-service";

    /// <summary>Удалить службу PC Toolkit и выйти (запускается с правами администратора).</summary>
    public const string UninstallService = "--uninstall-service";

    /// <summary>Удалить следы программ из файла-запроса (следующий аргумент) и выйти (с правами администратора).</summary>
    public const string RemoveLeftovers = "--remove-leftovers";

    /// <summary>Запуск вместе с Windows: без окна, только значок в трее.</summary>
    public const string Tray = "--tray";

    /// <summary>Для проверки: читать датчики в самом приложении, даже если служба установлена (как на компьютере без службы).</summary>
    public const string NoService = "--no-service";

    /// <summary>Восстановление Windows (DISM и sfc) с правами администратора; дальше — путь к файлу хода.</summary>
    public const string RepairWindows = "--repair-windows";

    /// <summary>Закрыть уже запущенную копию (как просьба установщика) и выйти — для скриптов разработки.</summary>
    public const string Exit = "--exit";

    /// <summary>Значение после аргумента: «--remove-leftovers путь».</summary>
    public static string? ValueAfter(IReadOnlyList<string> args, string argument)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], argument, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    public static bool Has(IEnumerable<string> args, string argument) =>
        args.Contains(argument, StringComparer.OrdinalIgnoreCase);
}
