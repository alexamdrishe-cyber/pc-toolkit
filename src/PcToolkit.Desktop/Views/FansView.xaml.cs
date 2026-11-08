using System.Windows.Controls;
using System.Windows.Input;

namespace PcToolkit.Desktop.Views;

public partial class FansView : UserControl
{
    public FansView()
    {
        InitializeComponent();
        // Enter в поле названия шаблона — сохранить (поле сохраняет при потере фокуса).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.OriginalSource is TextBox box)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };
    }
}
