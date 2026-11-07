using System.Buffers.Binary;
using System.Text.Json;
using PcToolkit.Core.Disks;
using PcToolkit.Core.Service;

namespace PcToolkit.Core.Tests;

public sealed class DiskHealthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pct-disks-" + Guid.NewGuid().ToString("N"));

    public DiskHealthTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] NvmeLog(int warning = 0, int kelvin = 314, int spare = 100, int threshold = 10, int used = 3,
                                  long written = 20_000_000, long hours = 5000, long unsafeShutdowns = 40, long media = 0,
                                  uint warnMinutes = 0, uint critMinutes = 0)
    {
        byte[] log = new byte[NvmeHealth.LogSize];
        log[0] = (byte)warning;
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), (ushort)kelvin);
        log[3] = (byte)spare;
        log[4] = (byte)threshold;
        log[5] = (byte)used;
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(32), 30_000_000);
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(48), written);
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(112), 1200);
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(128), hours);
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(144), unsafeShutdowns);
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(160), media);
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(176), 77);
        BinaryPrimitives.WriteUInt32LittleEndian(log.AsSpan(192), warnMinutes);
        BinaryPrimitives.WriteUInt32LittleEndian(log.AsSpan(196), critMinutes);
        return log;
    }

    private static DiskHealthData Nvme(byte[] log) => new()
    {
        Number = 0,
        Model = "Samsung SSD 970 EVO Plus 1TB",
        Serial = "S4EWNX0R123456",
        Bus = "NVMe",
        SizeBytes = 1_000_204_886_016,
        IsSsd = true,
        Nvme = NvmeHealth.Parse(log),
    };

    private static DiskHealthData Sata(bool ssd, params SmartAttribute[] attributes) => new()
    {
        Number = 1,
        Model = ssd ? "Samsung SSD 860 EVO 500GB" : "WDC WD10EZEX-08WN4A0",
        Serial = ssd ? "S3Z1NB0K111111" : "WD-WCC6Y0000000",
        Bus = "SATA",
        SizeBytes = 500_107_862_016,
        IsSsd = ssd,
        Attributes = attributes.ToList(),
    };

    private static SmartAttribute Attr(int id, long raw, int current = 100, int threshold = 0) =>
        new(id, current, current, threshold, raw, 0x32);

    [Fact]
    public void ParsesNvmeHealthLog()
    {
        NvmeHealth health = NvmeHealth.Parse(NvmeLog(warning: 0x04, kelvin: 320, spare: 90, used: 12, media: 2, warnMinutes: 15));

        Assert.Equal(0x04, health.CriticalWarning);
        Assert.Equal(47, health.TemperatureC);
        Assert.Equal(90, health.AvailableSpare);
        Assert.Equal(10, health.AvailableSpareThreshold);
        Assert.Equal(12, health.PercentageUsed);
        Assert.Equal(20_000_000, health.DataUnitsWritten);
        Assert.Equal(5000, health.PowerOnHours);
        Assert.Equal(40, health.UnsafeShutdowns);
        Assert.Equal(2, health.MediaErrors);
        Assert.Equal(77, health.ErrorLogEntries);
        Assert.Equal(15, health.WarningTemperatureMinutes);
    }

    [Fact]
    public void HealthyNvmeIsGood()
    {
        DiskHealthReport report = DiskHealthEvaluator.Evaluate(Nvme(NvmeLog()));

        Assert.Equal(DiskVerdict.Good, report.Verdict);
        Assert.Equal(97, report.LifePercent);
        Assert.Equal(41, report.TemperatureC);
        Assert.Equal(20_000_000L * 512_000, report.WrittenBytes);
        Assert.Equal("SSD NVMe", report.Kind);
        Assert.Contains("97 %", report.Summary);
        Assert.DoesNotContain(report.Items, i => i.Level is HealthLevel.Warning or HealthLevel.Bad);
        Assert.False(report.RowsHaveThresholds);
        Assert.Contains(report.Rows, r => r.Name == "Записано");
    }

    [Fact]
    public void NvmeCriticalWarningIsBadAndExplained()
    {
        DiskHealthReport report = DiskHealthEvaluator.Evaluate(Nvme(NvmeLog(warning: 0x08)));

        Assert.Equal(DiskVerdict.Bad, report.Verdict);
        HealthItem item = report.Items[0];
        Assert.Equal(HealthLevel.Bad, item.Level);
        Assert.Contains("только чтение", item.Value);
        Assert.NotNull(item.Risk);
        Assert.NotNull(item.Advice);
        Assert.Contains("только чтение", report.Summary);
    }

    [Fact]
    public void NvmeTemperatureFlagAloneIsNotBad()
    {
        DiskHealthReport report = DiskHealthEvaluator.Evaluate(Nvme(NvmeLog(warning: 0x02, kelvin: 273 + 82)));

        Assert.Equal(DiskVerdict.Warning, report.Verdict);
        Assert.Contains("перегрев (82 °C)", report.Summary);
    }

    [Fact]
    public void NvmeSpareBelowThresholdIsBad()
    {
        DiskHealthReport report = DiskHealthEvaluator.Evaluate(Nvme(NvmeLog(spare: 5, threshold: 10)));

        Assert.Equal(DiskVerdict.Bad, report.Verdict);
        Assert.Contains("закончился запас резервной памяти", report.Summary);
    }

    [Fact]
    public void NvmeMediaErrorsAndWornOutLifeAreWarnings()
    {
        DiskHealthReport report = DiskHealthEvaluator.Evaluate(Nvme(NvmeLog(used: 95, media: 3)));

        Assert.Equal(DiskVerdict.Warning, report.Verdict);
        Assert.Equal(5, report.LifePercent);
        Assert.Contains("ресурс почти исчерпан (5 %)", report.Summary);
        Assert.Contains(report.Items, i => i.Title == "Неисправимые ошибки данных" && i.Level == HealthLevel.Warning);
    }

    [Fact]
    public void NvmeUsedOverHundredPercentShowsZeroLife()
    {
        DiskHealthReport report = DiskHealthEvaluator.Evaluate(Nvme(NvmeLog(used: 112)));

        Assert.Equal(0, report.LifePercent);
        HealthItem life = report.Items.Single(i => i.Title == "Оставшийся ресурс");
        Assert.Equal("0 % (израсходовано 112 %)", life.Value);
        Assert.Equal(HealthLevel.Warning, life.Level);
    }

    [Fact]
    public void HealthyHddIsGood()
    {
        DiskHealthReport report = DiskHealthEvaluator.Evaluate(Sata(false,
            Attr(0x01, 0, 200, 51), Attr(0x03, 4000, 170, 21), Attr(0x05, 0, 200, 140), Attr(0x09, 26_000),
            Attr(0x0A, 0, 100, 51), Attr(0xC2, 0x0000_1E00_0024), Attr(0xC5, 0, 200), Attr(0xC6, 0, 200), Attr(0xC7, 0, 200)));

        Assert.Equal(DiskVerdict.Good, report.Verdict);
        Assert.Equal("Жёсткий диск SATA", report.Kind);
        Assert.Equal(36, report.TemperatureC);
        Assert.Equal(26_000, report.PowerOnHours);
        Assert.Null(report.LifePercent);
        Assert.True(report.RowsHaveThresholds);
        Assert.Equal("36 °C", report.Rows.Single(r => r.Id == "C2").Raw);
        Assert.Contains(report.Items, i => i.Title == "Переназначенные секторы" && i.Level == HealthLevel.Good);
    }

    [Fact]
    public void ReallocatedSectorsWarnAndShowGrowth()
    {
        DiskHealthData disk = Sata(false, Attr(0x03, 4000, 170, 21), Attr(0x05, 8, 199, 140), Attr(0xC5, 0, 200));
        var history = new DiskHealthRecord
        {
            FirstSeen = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
            FirstCounters = new Dictionary<string, long> { ["05"] = 5 },
        };

        DiskHealthReport report = DiskHealthEvaluator.Evaluate(disk, history);

        Assert.Equal(DiskVerdict.Warning, report.Verdict);
        HealthItem item = report.Items[0];
        Assert.Equal("Переназначенные секторы", item.Title);
        Assert.StartsWith("8 (+3 с ", item.Value);
        Assert.Contains("растёт", item.Risk);
        Assert.Equal(8, report.Counters["05"]);
        Assert.Equal(HealthLevel.Warning, report.Rows.Single(r => r.Id == "05").Level);
    }

    [Fact]
    public void AttributeAtThresholdIsBad()
    {
        DiskHealthReport report = DiskHealthEvaluator.Evaluate(Sata(false, Attr(0x03, 4000, 170, 21), Attr(0x05, 3000, 100, 140)));

        Assert.Equal(DiskVerdict.Bad, report.Verdict);
        Assert.Equal("Диск сам сообщает о неисправности", report.Items[0].Title);
        Assert.Contains("Переназначенные секторы (100 при пороге 140)", report.Items[0].Value);
        Assert.Equal(HealthLevel.Bad, report.Rows.Single(r => r.Id == "05").Level);
    }

    [Fact]
    public void SataSsdLifeComesFromVendorAttribute()
    {
        DiskHealthReport samsung = DiskHealthEvaluator.Evaluate(Sata(true, Attr(0x05, 0), Attr(0xB1, 2, 95), Attr(0xC2, 30)));
        Assert.Equal(95, samsung.LifePercent);
        Assert.Equal(DiskVerdict.Good, samsung.Verdict);
        Assert.Equal("SSD SATA", samsung.Kind);
        Assert.Equal("Заменённые блоки памяти", samsung.Items.First(i => i.Title.StartsWith("Заменённые")).Title);

        DiskHealthReport kingston = DiskHealthEvaluator.Evaluate(Sata(true, Attr(0xE7, 0, 7), Attr(0xB1, 0, 100)));
        Assert.Equal(7, kingston.LifePercent);
        Assert.Equal(DiskVerdict.Warning, kingston.Verdict);
    }

    [Fact]
    public void OldCableErrorsAreOnlyNoticedButGrowingOnesWarn()
    {
        DiskHealthData disk = Sata(false, Attr(0x03, 4000, 170, 21), Attr(0xC7, 12, 200));

        DiskHealthReport first = DiskHealthEvaluator.Evaluate(disk);
        Assert.Equal(DiskVerdict.Good, first.Verdict);
        Assert.Equal(HealthLevel.Notice, first.Items.Single(i => i.Title == "Ошибки передачи по кабелю").Level);

        var history = new DiskHealthRecord { FirstSeen = DateTimeOffset.Now, FirstCounters = new() { ["C7"] = 4 } };
        DiskHealthReport growing = DiskHealthEvaluator.Evaluate(disk, history);
        Assert.Equal(DiskVerdict.Warning, growing.Verdict);
        Assert.Contains("растут ошибки передачи по кабелю", growing.Summary);
    }

    [Fact]
    public void HotHardDriveWarns()
    {
        DiskHealthReport report = DiskHealthEvaluator.Evaluate(Sata(false, Attr(0x03, 4000, 170, 21), Attr(0xC2, 62)));

        Assert.Equal(DiskVerdict.Warning, report.Verdict);
        Assert.Contains("перегрев (62 °C)", report.Summary);
    }

    [Fact]
    public void DiskWithoutSmartIsUnknownWithReason()
    {
        var disk = new DiskHealthData { Model = "Generic Flash Disk", Bus = "USB", SmartError = "SMART внешних USB-дисков обычно недоступен." };

        DiskHealthReport report = DiskHealthEvaluator.Evaluate(disk);

        Assert.Equal(DiskVerdict.Unknown, report.Verdict);
        Assert.Equal(disk.SmartError, report.Summary);
        Assert.Equal("USB-накопитель", report.Kind);
        Assert.Empty(report.Items);
    }

    [Fact]
    public void HistoryReportsWorseningAndGrowth()
    {
        string path = Path.Combine(_dir, "disk-health.json");
        DiskHealthHistory history = DiskHealthHistory.Load(path);
        DateTimeOffset day1 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

        // Первая проверка здорового диска: новостей нет.
        DiskHealthData healthy = Sata(false, Attr(0x03, 4000, 170, 21), Attr(0x05, 0, 200, 140), Attr(0xC7, 2, 200));
        Assert.Empty(history.Record(history.Evaluate(new[] { healthy }), day1));

        // Появились переназначенные секторы: оценка ухудшилась.
        DiskHealthData worse = Sata(false, Attr(0x03, 4000, 170, 21), Attr(0x05, 4, 200, 140), Attr(0xC7, 2, 200));
        IReadOnlyList<DiskHealthNews> news = history.Record(history.Evaluate(new[] { worse }), day1.AddDays(1));
        Assert.Equal(DiskVerdict.Warning, Assert.Single(news).Verdict);

        // Оценка та же, но счётчик вырос — тоже новость. История переживает перезапуск.
        history = DiskHealthHistory.Load(path);
        DiskHealthData growing = Sata(false, Attr(0x03, 4000, 170, 21), Attr(0x05, 9, 200, 140), Attr(0xC7, 2, 200));
        List<DiskHealthReport> reports = history.Evaluate(new[] { growing });
        Assert.StartsWith("9 (+9 с ", reports[0].Items[0].Value);
        news = history.Record(reports, day1.AddDays(2));
        Assert.Contains("было 4, стало 9", Assert.Single(news).Text);

        // Ничего не изменилось — тихо.
        Assert.Empty(history.Record(history.Evaluate(new[] { growing }), day1.AddDays(3)));
    }

    [Fact]
    public void FirstCheckOfBadDiskIsNews()
    {
        DiskHealthHistory history = DiskHealthHistory.Load(Path.Combine(_dir, "h.json"));
        DiskHealthData bad = Sata(false, Attr(0x03, 4000, 170, 21), Attr(0x05, 3000, 100, 140));

        IReadOnlyList<DiskHealthNews> news = history.Record(history.Evaluate(new[] { bad }), DateTimeOffset.Now);

        Assert.Equal(DiskVerdict.Bad, Assert.Single(news).Verdict);
    }

    [Fact]
    public void DisksSurviveServiceJson()
    {
        var response = new ServiceResponse
        {
            Ok = true,
            Disks = new List<DiskHealthData> { Nvme(NvmeLog(media: 1)), Sata(false, Attr(0x05, 7, 199, 140)) },
        };

        string json = JsonSerializer.Serialize(response, ServiceProtocol.Json);
        ServiceResponse back = JsonSerializer.Deserialize<ServiceResponse>(json, ServiceProtocol.Json)!;

        Assert.DoesNotContain("historyKey", json);
        Assert.Equal(1, back.Disks![0].Nvme!.MediaErrors);
        Assert.True(back.Disks[0].IsNvme);
        SmartAttribute attribute = Assert.Single(back.Disks[1].Attributes!);
        Assert.Equal(new SmartAttribute(0x05, 199, 199, 140, 7, 0x32), attribute);
        Assert.Equal("WD-WCC6Y0000000", back.Disks[1].HistoryKey);
    }
}
