using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using PcToolkit.Core.Inventory;
using PcToolkit.Core.Logging;
using PcToolkit.Desktop.Mvvm;
using PcToolkit.Desktop.Services;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Экран «Система»: сведения о железе и Windows, экспорт отчёта.</summary>
public sealed class SystemInfoViewModel : ObservableObject
{
    private IReadOnlyList<InfoSection> _sections = Array.Empty<InfoSection>();
    private SystemInfoReport? _report;
    private bool _isLoading;
    private bool _loadStarted;
    private string _statusText = "";

    public SystemInfoViewModel()
    {
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !IsLoading);
        CopyCommand = new RelayCommand(Copy, () => _report is not null && !IsLoading);
        SaveCommand = new RelayCommand(Save, () => _report is not null && !IsLoading);
    }

    public static string ReportsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PC Toolkit", "Reports");

    public ICommand RefreshCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand SaveCommand { get; }

    public IReadOnlyList<InfoSection> Sections
    {
        get => _sections;
        private set => SetProperty(ref _sections, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Запускает сбор сведений один раз, при первом обращении.</summary>
    public void EnsureLoaded()
    {
        if (_loadStarted)
            return;

        _loadStarted = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (IsLoading)
            return;

        _loadStarted = true;
        IsLoading = true;
        StatusText = "";
        RelayCommand.Refresh();

        try
        {
            SystemInfoReport report = await Task.Run(() => SystemInfoProvider.Collect());
            _report = report;
            Sections = report.Sections;
            StatusText = $"Сведения собраны в {report.CreatedAt:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось собрать сведения о системе.", ex);
            StatusText = $"Не удалось собрать сведения: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            RelayCommand.Refresh();
        }
    }

    private void Copy()
    {
        if (_report is null)
            return;

        try
        {
            Clipboard.SetText(_report.ToText());
            StatusText = "Отчёт скопирован в буфер обмена";
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось скопировать: {ex.Message}";
        }
    }

    private void Save()
    {
        if (_report is null)
            return;

        try
        {
            Directory.CreateDirectory(ReportsDirectory);
            string path = Path.Combine(ReportsDirectory, $"system-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, _report.ToText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusText = $"Отчёт сохранён: {path}";
            ShellService.ShowFileInExplorer(path);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось сохранить отчёт о системе.", ex);
            StatusText = $"Не удалось сохранить отчёт: {ex.Message}";
        }
    }
}
