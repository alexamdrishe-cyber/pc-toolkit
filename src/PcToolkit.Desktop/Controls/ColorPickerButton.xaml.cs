using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PcToolkit.Core.Overlay;

namespace PcToolkit.Desktop.Controls;

/// <summary>
/// Выбор цвета: образец, по щелчку — палитра и поле «#RRGGBB». <see cref="Color"/> — строка «#RRGGBB»;
/// null — «обычный» цвет (<see cref="DefaultColor"/>), если <see cref="AllowDefault"/>.
/// </summary>
public partial class ColorPickerButton : UserControl
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorPickerButton),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorChanged));

    /// <summary>Цвет, когда свой не задан (и куда возвращает кнопка «Обычный»).</summary>
    public static readonly DependencyProperty DefaultColorProperty = DependencyProperty.Register(
        nameof(DefaultColor), typeof(string), typeof(ColorPickerButton), new PropertyMetadata("#E8EBF1", OnColorChanged));

    /// <summary>«Обычный» — это null (общий цвет), а не конкретный <see cref="DefaultColor"/>.</summary>
    public static readonly DependencyProperty AllowDefaultProperty = DependencyProperty.Register(
        nameof(AllowDefault), typeof(bool), typeof(ColorPickerButton), new PropertyMetadata(false));

    public static readonly DependencyProperty SwatchBrushProperty = DependencyProperty.Register(
        nameof(SwatchBrush), typeof(Brush), typeof(ColorPickerButton), new PropertyMetadata(Brushes.Transparent));

    private static readonly string[] PaletteColors =
    {
        "#FFFFFF", "#E8EBF1", "#9AA3B5", "#5B6475", "#000000",
        "#F2645F", "#FB7185", "#F59E42", "#F5B544", "#FDE047",
        "#A3E635", "#34D399", "#2DD4BF", "#22D3EE", "#60A5FA",
        "#818CF8", "#A78BFA", "#F472B6", "#0E1116", "#1F2937",
    };

    private DateTime _closedAt;

    public ColorPickerButton()
    {
        InitializeComponent();
        Swatches.ItemsSource = PaletteColors;
        UpdateSwatch();
    }

    public string? Color
    {
        get => (string?)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public string DefaultColor
    {
        get => (string)GetValue(DefaultColorProperty);
        set => SetValue(DefaultColorProperty, value);
    }

    public bool AllowDefault
    {
        get => (bool)GetValue(AllowDefaultProperty);
        set => SetValue(AllowDefaultProperty, value);
    }

    public Brush SwatchBrush
    {
        get => (Brush)GetValue(SwatchBrushProperty);
        private set => SetValue(SwatchBrushProperty, value);
    }

    private string Shown => OverlayStyle.ValidColor(Color) ?? OverlayStyle.ValidColor(DefaultColor) ?? "#E8EBF1";

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ColorPickerButton)d).UpdateSwatch();

    private void UpdateSwatch()
    {
        (byte r, byte g, byte b) = OverlayStyle.Rgb(Shown);
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        SwatchBrush = brush;
        ToolTip = Color is null && AllowDefault ? $"{Shown} (обычный)" : Shown;
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        // Щелчок по образцу при открытой палитре сначала закрывает её (StaysOpen=False) — не открывать снова.
        if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 250)
            return;
        Palette.IsOpen = true;
    }

    private void OnPopupOpened(object? sender, EventArgs e) => Hex.Text = Shown;

    private void OnPopupClosed(object? sender, EventArgs e) => _closedAt = DateTime.UtcNow;

    private void OnSwatch(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string color })
        {
            Color = color;
            Palette.IsOpen = false;
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        Color = AllowDefault ? null : DefaultColor;
        Palette.IsOpen = false;
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyHex();
            Palette.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Palette.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnHexLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyHex();

    private void ApplyHex()
    {
        // Текст не меняли — ничего не делать: иначе «обычный» цвет превратился бы в такой же, но заданный явно.
        if (OverlayStyle.ValidColor(Hex.Text) is { } color && color != Shown)
            Color = color;
    }
}
