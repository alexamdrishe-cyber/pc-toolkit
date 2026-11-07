using System.Windows.Media;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Пункт бокового меню.</summary>
public sealed class NavItemViewModel : ObservableObject
{
    private bool _isSelected;

    public NavItemViewModel(string title, Geometry icon, object page, string keywords = "")
    {
        Title = title;
        Icon = icon;
        Page = page;
        Keywords = keywords;
    }

    public string Title { get; }

    /// <summary>Контурный значок 24×24.</summary>
    public Geometry Icon { get; }

    /// <summary>Модель представления страницы; подходящий экран выбирается по её типу.</summary>
    public object Page { get; }

    /// <summary>Слова для поиска по программе (Ctrl+K): «андервольт вентиляторы» для «Разгона».</summary>
    public string Keywords { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>Группа пунктов меню: «Обзор», «Игры и скорость», «Обслуживание».</summary>
public sealed class NavGroupViewModel
{
    public NavGroupViewModel(string title, IReadOnlyList<NavItemViewModel> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }

    /// <summary>Заголовок группы заглавными буквами, как маленькая подпись над пунктами.</summary>
    public string Caption => Title.ToUpperInvariant();

    public IReadOnlyList<NavItemViewModel> Items { get; }
}

/// <summary>Строка в поиске по программе: раздел или действие.</summary>
public sealed class SearchResultViewModel : ObservableObject
{
    private bool _isHighlighted;

    public SearchResultViewModel(string title, string detail, Geometry icon, Action run)
    {
        Title = title;
        Detail = detail;
        Icon = icon;
        Run = run;
    }

    public string Title { get; }

    public string Detail { get; }

    public Geometry Icon { get; }

    public Action Run { get; }

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }
}

/// <summary>Страница-заглушка для разделов следующих этапов.</summary>
public sealed class PlaceholderViewModel
{
    public PlaceholderViewModel(string title, string stage, string description)
    {
        Title = title;
        Stage = stage;
        Description = description;
    }

    public string Title { get; }

    public string Stage { get; }

    public string Description { get; }
}
