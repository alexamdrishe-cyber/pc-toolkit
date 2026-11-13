using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PcToolkit.Core.Logging;
using PcToolkit.Desktop.Services;
using PcToolkit.Desktop.ViewModels;

namespace PcToolkit.Desktop.Views;

public partial class MainWindow : Window
{

    private readonly Dictionary<object, FrameworkElement> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old)
                old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is INotifyPropertyChanged now)
                now.PropertyChanged += OnViewModelChanged;
            ShowCurrentPage();
        };

        // Настройки оверлея «живут», только пока окно видно: свернули или спрятали в трей — оверлей больше не двигают.
        IsVisibleChanged += (_, _) => ViewModel?.Games.OnMainWindowStateChanged();
        StateChanged += (_, _) => ViewModel?.Games.OnMainWindowStateChanged();

        // Когда окно нарисовано и приложение ничего не делает, заранее собираем самый тяжёлый экран — мониторинг.
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (ViewModel is { } vm)
                Prepare(vm.Monitoring);
        }, DispatcherPriority.ApplicationIdle);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Заголовок окна в цвет темы приложения.
        ThemeService.StyleTitleBar(this);
    }

    /// <summary>
    /// Экран раздела: создаётся по шаблону один раз и дальше переиспользуется. Так переход в раздел
    /// не разбирает разметку заново, а прокрутка, вкладки и фильтры остаются, как их оставили.
    /// </summary>
    private FrameworkElement ViewFor(object page)
    {
        if (!_pages.TryGetValue(page, out FrameworkElement? view))
        {
            var template = (DataTemplate)FindResource(new DataTemplateKey(page.GetType()));
            var content = (FrameworkElement)template.LoadContent();
            content.DataContext = page;
            // Страница прокручивается целиком — с заголовком и вкладками (см. PageScroll).
            var scroller = new System.Windows.Controls.ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
                Focusable = false,
            };
            Controls.PageScroll.SetIsPage(scroller, true);
            view = scroller;
            _pages[page] = view;
        }

        return view;
    }

    private void ShowCurrentPage() =>
        PageHost.Content = ViewModel?.CurrentPage is { } page ? ViewFor(page) : null;

    /// <summary>Создать и разложить экран заранее, вне окна: первое открытие раздела без задержки.</summary>
    private void Prepare(object page)
    {
        if (_pages.ContainsKey(page))
            return;

        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            FrameworkElement view = ViewFor(page);
            var size = new Size(Math.Max(800, PageHost.ActualWidth), Math.Max(600, PageHost.ActualHeight));
            view.Measure(size);
            view.Arrange(new Rect(size));
            AppLog.Info($"Экран «{page.GetType().Name}» подготовлен заранее за " +
                        $"{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} мс.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось заранее подготовить экран.", ex);
        }
    }

    // Поиск открылся — сразу ставим курсор в поле, чтобы можно было печатать.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            ShowCurrentPage();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsSearchOpen) && ViewModel is { IsSearchOpen: true })
        {
            Dispatcher.BeginInvoke(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
                SearchBox.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSearchHighlight(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveSearchHighlight(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.RunHighlightedSearch();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.IsSearchOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnSearchBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.IsSearchOpen = false;
    }

}
