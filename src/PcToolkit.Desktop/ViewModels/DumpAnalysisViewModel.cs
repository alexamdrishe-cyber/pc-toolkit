using System.Collections.ObjectModel;
using System.Globalization;
using PcToolkit.Core.Diagnostics;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Service;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Один разобранный дамп: когда, какая ошибка и какой драйвер виноват.</summary>
public sealed class DumpItemViewModel
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public DumpItemViewModel(KernelDumpInfo dump)
    {
        (string name, string what, string advice) = CrashAnalyzer.DescribeBugCheck(dump.BugCheckCode);
        When = dump.Time.ToString("d MMMM yyyy, HH:mm", Russian);
        Code = $"0x{dump.BugCheckCode:X8} {name}";
        What = what;
        string description = dump.Culprit is null ? "" : KernelDumpReader.Describe(dump.Culprit);
        Culprit = dump.Culprit is null
            ? "Виновника по дампу определить не удалось"
            : dump.Culprit + (description.Length > 0 ? " — " + description : "");
        Reason = dump.Culprit is null ? "" : dump.CulpritReason;
        IsThirdParty = dump.Culprit is not null && !dump.CulpritIsWindows;
        Advice = IsThirdParty
            ? "Обновите или переустановите этот драйвер (с сайта производителя устройства); если сбои начались после его обновления — верните прежнюю версию. " + advice
            : advice;
        Stack = dump.StackDrivers.Count == 0 ? "" : "На стеке в момент сбоя: " + string.Join(", ", dump.StackDrivers);
        File = System.IO.Path.GetFileName(dump.File);
    }

    public string When { get; }

    public string Code { get; }

    public string What { get; }

    public string Culprit { get; }

    public string Reason { get; }

    /// <summary>Виноват сторонний драйвер (а не часть Windows).</summary>
    public bool IsThirdParty { get; }

    public string Advice { get; }

    public string Stack { get; }

    public string File { get; }
}

/// <summary>
/// Разбор дампов синих экранов (C:\Windows\Minidump): виновник — драйвер. Папку читает служба PC Toolkit или
/// приложение с правами администратора.
/// </summary>
public sealed class DumpAnalysisViewModel : ObservableObject
{
    private readonly Func<ServiceClient?> _service;
    private readonly bool _elevated;
    private string _status = "";
    private bool _loading;

    public DumpAnalysisViewModel(Func<ServiceClient?> service, bool elevated)
    {
        _service = service;
        _elevated = elevated;
    }

    public ObservableCollection<DumpItemViewModel> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task LoadAsync()
    {
        if (_loading)
            return;

        _loading = true;
        try
        {
            ServiceClient? service = _service();
            List<KernelDumpInfo>? dumps;
            if (_elevated)
            {
                dumps = await Task.Run(() => KernelDumpReader.ReadAll());
            }
            else if (service is not null)
            {
                ServiceResponse? response = await Task.Run(() => service.ReadDumps());
                dumps = response is { Ok: true } ? response.Dumps ?? new List<KernelDumpInfo>() : null;
                if (dumps is null)
                {
                    Status = response?.Error is { } error && error.Contains("Неизвестная команда", StringComparison.Ordinal)
                        ? "Чтобы разобрать дампы синих экранов, обновите службу PC Toolkit в «Настройках»."
                        : "Служба PC Toolkit не смогла прочитать дампы.";
                    return;
                }
            }
            else
            {
                Status = "Дампы синих экранов лежат в папке, доступной только администратору: чтобы найти драйвер-виновник, " +
                         "установите службу PC Toolkit («Настройки») или запустите приложение от имени администратора.";
                return;
            }

            Items.Clear();
            foreach (KernelDumpInfo dump in dumps)
                Items.Add(new DumpItemViewModel(dump));
            Status = Items.Count == 0 ? "Дампов синих экранов нет." : "";
            AppLog.Info($"Дампы синих экранов: {dumps.Count}" +
                        (dumps.Count > 0 ? ", виновники: " + string.Join(", ", dumps.Select(d => d.Culprit ?? "?")) : "") + ".");
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось разобрать дампы.", ex);
            Status = "Не удалось разобрать дампы: " + ex.Message;
        }
        finally
        {
            _loading = false;
            OnPropertyChanged(nameof(HasItems));
        }
    }
}
