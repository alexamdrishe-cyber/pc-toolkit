using PcToolkit.Core.Logging;
using PcToolkit.Core.Service;

namespace PcToolkit.Core.Disks;

/// <summary>Диски и пояснение, если SMART прочитать не удалось целиком.</summary>
public sealed record DiskReadResult(List<DiskHealthData> Disks, string? Note);

/// <summary>
/// Откуда брать SMART: с правами администратора — напрямую, без них — через службу PC Toolkit.
/// Если нет ни того ни другого, диски показываются без SMART с объяснением, что сделать.
/// </summary>
public static class DiskHealthSource
{
    public const string InstallServiceNote =
        "Чтобы видеть здоровье дисков, установите службу PC Toolkit в «Настройках» или запустите приложение от имени администратора.";

    public const string UpdateServiceNote =
        "Служба PC Toolkit устарела и не умеет читать SMART. Обновите её в «Настройках» (кнопка «Обновить службу»).";

    public static DiskReadResult Read(bool isElevated, ServiceClient? service)
    {
        if (isElevated)
            return new DiskReadResult(DiskHealthReader.ReadAll(), null);

        if (service is not null)
        {
            ServiceResponse? response = service.GetDisks();
            if (response is { Ok: true, Disks: { } disks })
                return new DiskReadResult(disks, null);

            if (response is { Ok: false })
            {
                AppLog.Warn($"Служба не отдала диски: {response.Error}");
                bool unknownCommand = response.Error?.Contains(ServiceOps.Disks, StringComparison.Ordinal) == true;
                return new DiskReadResult(DiskHealthReader.ReadAll(), unknownCommand ? UpdateServiceNote : response.Error);
            }
        }

        // Без прав администратора ReadAll всё равно перечислит диски (модель, размер, разделы), а NVMe иногда отдаст и SMART.
        List<DiskHealthData> list = DiskHealthReader.ReadAll();
        return new DiskReadResult(list, list.All(d => d.HasSmart) ? null : InstallServiceNote);
    }
}
