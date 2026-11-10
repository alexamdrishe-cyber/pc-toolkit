using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PcToolkit.Desktop.Controls;

/// <summary>
/// Прокрутка страницы целиком — вместе с заголовком и вкладками, чтобы под содержимое уходило всё окно.
/// <see cref="IsPageProperty"/> ставится на внешний ScrollViewer страницы: колесо сначала прокручивает страницу
/// (заголовок уезжает), а уже потом — внутренний список под курсором; вверх — наоборот: сначала список до начала,
/// потом страница. Большие таблицы, которым нужна своя прокрутка (все датчики, список программ), помечаются
/// <see cref="FillHeightProperty"/>: их высота равна высоте окна, и когда заголовок уехал, таблица занимает всё место.
/// </summary>
public static class PageScroll
{
    /// <summary>Пикселей на одно деление колеса (120 единиц): как у обычной прокрутки Windows — три строки.</summary>
    private const double PixelsPerNotch = 60;

    public static readonly DependencyProperty IsPageProperty = DependencyProperty.RegisterAttached(
        "IsPage", typeof(bool), typeof(PageScroll), new PropertyMetadata(false, OnIsPageChanged));

    /// <summary>Сколько вычесть из высоты окна страницы (отступы снизу); NaN — не растягивать.</summary>
    public static readonly DependencyProperty FillHeightProperty = DependencyProperty.RegisterAttached(
        "FillHeight", typeof(double), typeof(PageScroll), new PropertyMetadata(double.NaN, OnFillHeightChanged));

    private static readonly ConditionalWeakTable<FrameworkElement, Fill> Fills = new();

    public static bool GetIsPage(DependencyObject element) => (bool)element.GetValue(IsPageProperty);

    public static void SetIsPage(DependencyObject element, bool value) => element.SetValue(IsPageProperty, value);

    public static double GetFillHeight(DependencyObject element) => (double)element.GetValue(FillHeightProperty);

    public static void SetFillHeight(DependencyObject element, double value) => element.SetValue(FillHeightProperty, value);

    private static void OnIsPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer page)
            return;
        page.PreviewMouseWheel -= OnPageWheel;
        if ((bool)e.NewValue)
            page.PreviewMouseWheel += OnPageWheel;
    }

    private static void OnPageWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled)
            return;

        var page = (ScrollViewer)sender;
        ScrollViewer? inner = InnerScroller(e.OriginalSource as DependencyObject, page);
        bool down = e.Delta < 0;
        bool pageCanScroll = down ? page.VerticalOffset < page.ScrollableHeight - 0.5 : page.VerticalOffset > 0.5;

        if (inner is not null)
        {
            // Вниз: сначала уезжает заголовок страницы. Вверх: сначала список возвращается к началу.
            bool innerFirst = down ? !pageCanScroll : inner.VerticalOffset > 0.5;
            if (innerFirst)
                return; // обычная прокрутка внутреннего списка
        }

        if (!pageCanScroll)
        {
            e.Handled = inner is null; // внутренние «пустые» ScrollViewer не должны съедать колесо впустую
            return;
        }

        page.ScrollToVerticalOffset(Math.Clamp(page.VerticalOffset - e.Delta / 120.0 * PixelsPerNotch, 0, page.ScrollableHeight));
        e.Handled = true;
    }

    /// <summary>Ближайший к курсору прокручиваемый по вертикали ScrollViewer внутри страницы.</summary>
    private static ScrollViewer? InnerScroller(DependencyObject? element, ScrollViewer page)
    {
        while (element is not null && !ReferenceEquals(element, page))
        {
            if (element is ScrollViewer scroller && scroller.ScrollableHeight > 0.5 &&
                scroller.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
                return scroller;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }

    private static void OnFillHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if (!Fills.TryGetValue(element, out Fill? fill))
        {
            fill = new Fill(element);
            Fills.Add(element, fill);
        }

        fill.Offset = (double)e.NewValue;
        fill.Update();
    }

    /// <summary>Держит высоту элемента равной высоте окна страницы.</summary>
    private sealed class Fill
    {
        private readonly FrameworkElement _element;
        private ScrollViewer? _page;

        public Fill(FrameworkElement element)
        {
            _element = element;
            element.Loaded += (_, _) => Attach();
            element.Unloaded += (_, _) => Detach();
            if (element.IsLoaded)
                Attach();
        }

        public double Offset { get; set; } = double.NaN;

        public void Update()
        {
            if (double.IsNaN(Offset))
            {
                _element.ClearValue(FrameworkElement.HeightProperty);
                return;
            }

            if (_page is null || _page.ActualHeight <= 0)
                return;

            double height = Math.Max(_element.MinHeight > 0 ? _element.MinHeight : 320, _page.ActualHeight - Offset);
            if (Math.Abs(_element.Height - height) > 0.5 || double.IsNaN(_element.Height))
                _element.Height = height;
        }

        private void Attach()
        {
            Detach();
            DependencyObject? parent = VisualTreeHelper.GetParent(_element);
            while (parent is not null && !(parent is ScrollViewer candidate && GetIsPage(candidate)))
                parent = VisualTreeHelper.GetParent(parent);
            _page = parent as ScrollViewer;
            if (_page is null)
                return;
            _page.SizeChanged += OnPageSizeChanged;
            Update();
        }

        private void Detach()
        {
            if (_page is not null)
                _page.SizeChanged -= OnPageSizeChanged;
            _page = null;
        }

        private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => Update();
    }
}
