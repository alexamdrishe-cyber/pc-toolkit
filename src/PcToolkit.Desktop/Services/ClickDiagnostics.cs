using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PcToolkit.Core.Logging;

namespace PcToolkit.Desktop.Services;

/// <summary>
/// Только для отладочной сборки: пишет в журнал нажатия кнопок мыши на кнопках и сами нажатия.
/// Помогает понять, почему кнопка «ничего не делает»: не дошла мышь, нет команды или команда недоступна.
/// </summary>
public static class ClickDiagnostics
{
    public static void Install()
    {
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((sender, _) => Log("мышь нажата", sender)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((sender, _) => Log("мышь отпущена", sender)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
            new RoutedEventHandler((sender, _) => Log("нажатие", sender)), handledEventsToo: true);
    }

    private static void Log(string what, object sender)
    {
        if (sender is not ButtonBase button)
            return;

        string command = button.Command is null
            ? "команды нет"
            : $"команда {button.Command.GetType().Name}, доступна: {button.Command.CanExecute(button.CommandParameter)}";
        AppLog.Info($"Отладка кнопок: {what} — «{button.Content}» ({button.GetType().Name}), " +
                    $"IsEnabled={button.IsEnabled}, IsPressed={button.IsPressed}, {command}.");
    }
}
