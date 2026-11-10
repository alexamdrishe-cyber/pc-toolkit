using System.Collections.ObjectModel;
using System.Windows.Input;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Service;
using PcToolkit.Core.Startup;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Программа в автозагрузке с переключателем «запускать вместе с Windows».</summary>
public sealed class StartupItemViewModel : ObservableObject
{
    private readonly StartupViewModel _owner;
    private bool _enabled;
    private bool _busy;

    public StartupItemViewModel(StartupEntry entry, StartupViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        _enabled = entry.Enabled;
    }

    public StartupEntry Entry { get; private set; }

    public string Name => Entry.DisplayName;

    public string Publisher => Entry.Publisher.Length > 0 ? Entry.Publisher : Entry.Description;

    public string Command => Entry.Command;

    public string Note => Entry.Note;

    public bool HasNote => Entry.Note.Length > 0;

    public string Place => Entry.Location switch
    {
        StartupLocation.UserRun => "реестр, этот пользователь",
        StartupLocation.MachineRun or StartupLocation.MachineRun32 => "реестр, все пользователи",
        StartupLocation.UserFolder => "папка «Автозагрузка»",
        _ => "общая папка «Автозагрузка»",
    };

    public string ImpactText => Entry.Impact switch
    {
        StartupImpact.High => "Сильно тормозит включение",
        StartupImpact.Medium => "Заметно тормозит",
        StartupImpact.Low => "Почти не влияет",
        _ => "",
    };

    /// <summary>High, Medium, Low, Unknown — для цвета метки.</summary>
    public string Impact => Entry.Impact.ToString();

    public string AdviceText => Entry.Advice switch
    {
        StartupAdvice.CanDisable => "Можно выключить",
        StartupAdvice.Keep => "Лучше оставить",
        _ => "",
    };

    public string Advice => Entry.Advice.ToString();

    public bool IsBusy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    /// <summary>Запускать вместе с Windows. Переключение сразу записывается (как в «Диспетчере задач»).</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled || _busy)
                return;
            _ = ToggleAsync(value);
        }
    }

    private async Task ToggleAsync(bool enabled)
    {
        IsBusy = true;
        string? error = await Task.Run(() => _owner.Write(Entry, enabled));
        IsBusy = false;
        if (error is null)
        {
            _enabled = enabled;
            Entry = Entry with { Enabled = enabled };
        }
        else
        {
            _owner.ShowError(error);
        }

        OnPropertyChanged(nameof(IsEnabled));
        _owner.Recount();
    }
}

/// <summary>
/// Экран «Автозагрузка»: что запускается вместе с Windows, насколько тормозит включение и что можно выключить.
/// Выключение — как в «Диспетчере задач» (запись остаётся, меняется её состояние), поэтому вернуть можно в любой момент.
/// Записи «для всех пользователей» меняет служба PC Toolkit или приложение с правами администратора.
/// </summary>
public sealed class StartupViewModel : ObservableObject
{
    private readonly Func<ServiceClient?> _service;
    private readonly bool _elevated;
    private bool _loading;
    private bool _loaded;
    private string _summary = "";
    private string _error = "";

    public StartupViewModel(Func<ServiceClient?> service, bool elevated)
    {
        _service = service;
        _elevated = elevated;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !_loading);
        DisableSuggestedCommand = new RelayCommand(DisableSuggested, () => Items.Any(i => i.IsEnabled && i.Entry.Advice == StartupAdvice.CanDisable));
    }

    public ObservableCollection<StartupItemViewModel> Items { get; } = new();

    public ICommand RefreshCommand { get; }

    /// <summary>Выключить всё, что помечено «можно выключить».</summary>
    public ICommand DisableSuggestedCommand { get; }

    public bool IsLoading
    {
        get => _loading;
        private set
        {
            if (SetProperty(ref _loading, value))
                RelayCommand.Refresh();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;

    public bool CanChangeMachine => _elevated || _service() is not null;

    /// <summary>При первом открытии экрана.</summary>
    public void OnShown()
    {
        if (!_loaded)
            _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (IsLoading)
            return;

        IsLoading = true;
        Error = "";
        try
        {
            IReadOnlyList<StartupEntry> entries = await Task.Run(StartupScanner.Scan);
            Items.Clear();
            foreach (StartupEntry entry in entries)
                Items.Add(new StartupItemViewModel(entry, this));
            _loaded = true;
            AppLog.Info($"Автозагрузка: {entries.Count} записей, включено {entries.Count(e => e.Enabled)}.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось прочитать автозагрузку.", ex);
            Error = "Не удалось прочитать автозагрузку: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            Recount();
        }
    }

    internal void Recount()
    {
        int enabled = Items.Count(i => i.IsEnabled);
        int heavy = Items.Count(i => i.IsEnabled && i.Entry.Impact == StartupImpact.High);
        int suggested = Items.Count(i => i.IsEnabled && i.Entry.Advice == StartupAdvice.CanDisable);
        Summary = Items.Count == 0
            ? "Вместе с Windows ничего не запускается."
            : $"Вместе с Windows запускается {Plural(enabled, "программа", "программы", "программ")}" +
              (heavy > 0 ? $", из них сильно тормозят включение — {heavy}" : "") + "." +
              (suggested > 0 ? $" Без вреда можно выключить: {suggested}." : "");
        RelayCommand.Refresh();
    }

    internal void ShowError(string error) => Error = error;

    /// <summary>Записать состояние. null — получилось, иначе текст ошибки.</summary>
    internal string? Write(StartupEntry entry, bool enabled)
    {
        string subKey = StartupApproved.SubKey(entry.Location);
        try
        {
            if (!entry.NeedsAdmin || _elevated)
            {
                StartupApproved.Write(StartupApproved.Hive(entry.Location), subKey, entry.Name, enabled);
                return null;
            }

            ServiceClient? service = _service();
            if (service is null)
                return $"«{entry.DisplayName}» запускается для всех пользователей: чтобы её выключить, нужна служба PC Toolkit (Настройки) или запуск от имени администратора.";
            ServiceResponse? response = service.SetStartup(subKey, entry.Name, enabled);
            return response switch
            {
                null => "Служба PC Toolkit не отвечает.",
                { Ok: false, Error: { } error } when error.Contains("Неизвестная команда", StringComparison.Ordinal) =>
                    "Служба PC Toolkit устарела — обновите её в «Настройках».",
                { Ok: false } => "Служба отказалась: " + response.Error,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException or ArgumentException)
        {
            AppLog.Warn($"Автозагрузка: не удалось изменить «{entry.Name}».", ex);
            return $"Не получилось изменить «{entry.DisplayName}»: {ex.Message}";
        }
    }

    private void DisableSuggested()
    {
        foreach (StartupItemViewModel item in Items.Where(i => i.IsEnabled && i.Entry.Advice == StartupAdvice.CanDisable).ToList())
            item.IsEnabled = false;
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int last = n % 10, lastTwo = n % 100;
        string word = last == 1 && lastTwo != 11 ? one : last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? few : many;
        return $"{n} {word}";
    }
}
