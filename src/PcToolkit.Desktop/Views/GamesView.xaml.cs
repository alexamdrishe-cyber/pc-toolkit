using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PcToolkit.Desktop.Views;

public partial class GamesView : UserControl
{
    public GamesView()
    {
        InitializeComponent();
    }

    /// <summary>Порог применяется по Enter, не дожидаясь, пока поле потеряет фокус.</summary>
    private void OnApplyOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    /// <summary>Список «Добавить показатель» открылся — сразу можно печатать в поиске.</summary>
    private void OnAddMetricOpened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => Keyboard.Focus(AddMetricSearch), DispatcherPriority.Input);
}
