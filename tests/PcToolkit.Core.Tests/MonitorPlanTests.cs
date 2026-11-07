using PcToolkit.Core.Sensors;

namespace PcToolkit.Core.Tests;

public sealed class SensorTextTests
{
    [Theory]
    [InlineData(DeviceKind.Cpu, SensorKind.Load, "CPU Core #3", "Поток 3")]
    [InlineData(DeviceKind.Cpu, SensorKind.Load, "CPU Total", "Общая")]
    [InlineData(DeviceKind.Cpu, SensorKind.Clock, "Core #2", "Ядро 2")]
    [InlineData(DeviceKind.Cpu, SensorKind.Clock, "Core #2 (Effective)", "Ядро 2, эффективная")]
    [InlineData(DeviceKind.Cpu, SensorKind.Power, "Core #4 (SMU)", "Ядро 4")]
    [InlineData(DeviceKind.Cpu, SensorKind.Voltage, "Core #1 VID", "Ядро 1, VID")]
    [InlineData(DeviceKind.Cpu, SensorKind.Factor, "Core #1", "Ядро 1, множитель")]
    [InlineData(DeviceKind.Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)", "Tctl/Tdie")]
    [InlineData(DeviceKind.Cpu, SensorKind.Temperature, "CCD1 (Tdie)", "CCD1")]
    [InlineData(DeviceKind.GpuAmd, SensorKind.Load, "GPU Memory", "Контроллер памяти")]
    [InlineData(DeviceKind.GpuAmd, SensorKind.Clock, "GPU Memory", "Память")]
    [InlineData(DeviceKind.GpuAmd, SensorKind.Temperature, "GPU Core", "Ядро")]
    [InlineData(DeviceKind.Motherboard, SensorKind.Temperature, "Temperature #2", "Датчик 2")]
    [InlineData(DeviceKind.Motherboard, SensorKind.Fan, "Fan #3", "Вентилятор 3")]
    [InlineData(DeviceKind.Storage, SensorKind.Temperature, "Composite Temperature", "Температура")]
    [InlineData(DeviceKind.Memory, SensorKind.Load, "Memory", "Загрузка")]
    [InlineData(DeviceKind.Network, SensorKind.Throughput, "Download Speed", "Загрузка ↓")]
    [InlineData(DeviceKind.Other, SensorKind.Load, "D3D 3D", "D3D 3D")]
    public void TranslatesNames(DeviceKind device, SensorKind kind, string name, string expected)
    {
        Assert.Equal(expected, SensorText.Name(device, kind, name));
    }

    [Theory]
    [InlineData(SensorKind.Temperature, 66.12, ValueStyle.Default, "66,1 °C")]
    [InlineData(SensorKind.Temperature, 53, ValueStyle.Default, "53 °C")]
    [InlineData(SensorKind.Load, 24.4, ValueStyle.Default, "24 %")]
    [InlineData(SensorKind.Load, 0.056, ValueStyle.Default, "0,1 %")]
    [InlineData(SensorKind.Clock, 2352, ValueStyle.Default, "2352 МГц")]
    [InlineData(SensorKind.Clock, 4030, ValueStyle.Gigahertz, "4,03 ГГц")]
    [InlineData(SensorKind.SmallData, 2324, ValueStyle.GigabytesFromMegabytes, "2,3 ГБ")]
    [InlineData(SensorKind.Data, 27109, ValueStyle.Default, "26,5 ТБ")]
    [InlineData(SensorKind.Data, 9.63, ValueStyle.Default, "9,6 ГБ")]
    [InlineData(SensorKind.Throughput, 1300000, ValueStyle.Default, "1,2 МБ/с")]
    [InlineData(SensorKind.Throughput, 250000, ValueStyle.Megabits, "2,0 Мбит/с")]
    [InlineData(SensorKind.Power, 58.04, ValueStyle.Default, "58,0 Вт")]
    [InlineData(SensorKind.Power, 146, ValueStyle.Default, "146 Вт")]
    [InlineData(SensorKind.Fan, 1046.4, ValueStyle.Default, "1046 об/мин")]
    [InlineData(SensorKind.Voltage, 1.2, ValueStyle.Default, "1,200 В")]
    public void FormatsValues(SensorKind kind, double value, ValueStyle style, string expected)
    {
        Assert.Equal(expected, SensorText.Format(kind, value, style));
    }

    [Fact]
    public void BareDropsUnit()
    {
        Assert.Equal("66,1", SensorText.Bare(SensorKind.Temperature, 66.1));
        Assert.Equal("4,03", SensorText.Bare(SensorKind.Clock, 4030, ValueStyle.Gigahertz));
        Assert.Equal("—", SensorText.Bare(SensorKind.Clock, null));
    }

    private static readonly DateTime T0 = new(2026, 10, 24, 12, 0, 0);

    private static HardwareSnapshot At(double seconds, params (string Id, float? Value)[] sensors) =>
        new(T0.AddSeconds(seconds), new[]
        {
            new DeviceSnapshot("d", "d", DeviceKind.Cpu,
                sensors.Select(s => new SensorReading(s.Id, s.Id, SensorKind.Load, s.Value, null, null)).ToArray()),
        });

    [Fact]
    public void HistoryReturnsTail()
    {
        var history = new SensorHistory(5);
        for (int i = 1; i <= 7; i++)
            history.Add(At(i, ("s", i)));

        Assert.Equal(new[] { 6.0, 7 }, history.GetLast("s", 2));
        Assert.Equal(new[] { 3.0, 4, 5, 6, 7 }, history.GetLast("s", 100));
        Assert.Equal(new[] { 3.0, 4, 5, 6, 7 }, history.Get("s"));
        Assert.Empty(history.GetLast("missing", 3));
    }

    [Fact]
    public void HistoryWindowFollowsTime()
    {
        // 4 раза в секунду, 10 секунд.
        var history = new SensorHistory(100);
        for (int i = 0; i <= 40; i++)
            history.Add(At(i * 0.25, ("s", i)));

        HistoryWindow window = history.GetWindow("s", 5);
        Assert.Equal(21, window.Count);
        Assert.Equal(0.25, window.SampleSeconds, 3);
        Assert.Equal(20, window.Values[0]);
        Assert.Equal(40, window.Values[^1]);
        Assert.Equal(20, window.FirstIndex);
        Assert.Equal(0.25, history.RecentSampleSeconds, 3);

        // Окно длиннее истории — всё, что есть.
        Assert.Equal(41, history.GetWindow("s", 300).Count);
        Assert.Equal(0, history.GetWindow("missing", 5).Count);
    }

    [Fact]
    public void HistoryAveragesByClockIntervals()
    {
        // 4 раза в секунду, 10 секунд; окно 10 с на 5 точек — отрезки по 2 с, в каждом 8 снимков.
        var history = new SensorHistory(100);
        for (int i = 0; i < 40; i++)
            history.Add(At(i * 0.25, ("s", i)));

        HistoryWindow window = history.GetWindow("s", 10, maxPoints: 5);
        Assert.Equal(2.0, window.SampleSeconds, 3);
        Assert.Equal(new[] { 3.5, 11.5, 19.5, 27.5, 35.5 }, window.Values);
        Assert.Equal(T0.Ticks / TimeSpan.FromSeconds(2).Ticks, window.FirstIndex);
    }

    [Fact]
    public void AveragedHistoryKeepsPointsWhenNewSnapshotArrives()
    {
        // Шумная загрузка 4 раза в секунду, окно 5 минут на 600 точек (по 0,5 с).
        var random = new Random(1);
        var history = new SensorHistory(2000);
        int i = 0;
        for (; i < 1401; i++)
            history.Add(At(i * 0.25, ("load", random.Next(0, 100))));

        HistoryWindow before = history.GetWindow("load", 300, 600);
        for (int k = 0; k < 3; k++, i++)
            history.Add(At(i * 0.25, ("load", random.Next(0, 100))));
        HistoryWindow after = history.GetWindow("load", 300, 600);

        Assert.Equal(0.5, after.SampleSeconds, 3);
        Assert.InRange(after.Count, 600, 602);
        // Те же отрезки времени — те же значения: новые снимки меняют только последнюю, неполную точку.
        int compared = 0;
        for (int k = 0; k < before.Count - 1; k++)
        {
            long j = before.FirstIndex + k - after.FirstIndex;
            if (j < 0)
                continue;
            Assert.Equal(before.Values[k], after.Values[j]);
            compared++;
        }

        Assert.True(compared > 590);
    }

    [Fact]
    public void SparseHistoryIsNotAveraged()
    {
        // Раз в секунду (служба старой версии): окно 5 минут на 600 точек — усреднять нечего.
        var history = new SensorHistory(1000);
        for (int i = 0; i < 400; i++)
            history.Add(At(i, ("s", i)));

        HistoryWindow window = history.GetWindow("s", 300, 600);
        Assert.Equal(301, window.Count);
        Assert.Equal(1.0, window.SampleSeconds, 3);
        Assert.Equal(99, window.FirstIndex);
        Assert.Equal(99, window.Values[0]);
    }

    [Fact]
    public void ChartGroupsStayPutWhenLineScrolls()
    {
        var random = new Random(2);
        double[] all = Enumerable.Range(0, 200).Select(_ => (double)random.Next(0, 100)).ToArray();

        // Окно в 100 значений, по 3 значения на пиксель; потом линия сдвинулась на одно значение.
        var before = ChartGroups.Average(all[50..150], 0, firstIndex: 50, group: 3);
        var after = ChartGroups.Average(all[51..151], 0, firstIndex: 51, group: 3);

        // Группы те же (от 51-го значения по три), просто на шаг левее.
        Assert.Equal(33, before.Count);
        var shifted = after.Select(p => (p.StepsFromEnd - 1, p.Value)).ToList();
        foreach ((double StepsFromEnd, double Value) point in before)
            Assert.Contains(point, shifted);
    }

    [Fact]
    public void ChartGroupSizeIgnoresTimingJitter()
    {
        Assert.Equal(1, ChartGroups.Size(0.25, 4));
        Assert.Equal(2, ChartGroups.Size(0.25, 2));
        // Снимки пришли чуть чаще или реже — группа та же, иначе линия перестраивалась бы.
        Assert.Equal(2, ChartGroups.Size(0.2498, 2));
        Assert.Equal(2, ChartGroups.Size(0.2502, 2));
        Assert.Equal(3, ChartGroups.Size(0.5, 0.7));
    }

    [Fact]
    public void HistorySkipsRepeatedSnapshotAndFillsGaps()
    {
        var history = new SensorHistory(100);
        history.Add(At(0, ("a", 1), ("b", 10)));
        history.Add(At(0, ("a", 99), ("b", 99)));   // тот же снимок от службы ещё раз
        history.Add(At(1, ("a", 2)));               // «b» на миг пропал
        history.Add(At(2, ("a", 3), ("b", 12)));

        Assert.Equal(new[] { 1.0, 2, 3 }, history.Get("a"));
        Assert.Equal(new[] { 10.0, 10, 12 }, history.Get("b"));

        // Пропал надолго — история датчика забывается, а не тянется прямой линией.
        for (int i = 3; i < 30; i++)
            history.Add(At(i, ("a", i)));
        Assert.Empty(history.Get("b"));
    }

    [Fact]
    public void ScalesFollowKind()
    {
        Assert.Equal(new ChartRange(0, 100), ChartScale.For(SensorKind.Load, new[] { 3.0, 40 }));
        Assert.Equal(new ChartRange(20, 100), ChartScale.For(SensorKind.Temperature, new[] { 37.0, 78 }, limit: 85));
        Assert.Equal(new ChartRange(20, 110), ChartScale.For(SensorKind.Temperature, new[] { 40.0, 77 }, limit: 105));
        Assert.Equal(new ChartRange(0, 32), ChartScale.For(SensorKind.Data, new[] { 9.6 }, fixedMax: 32));
        Assert.Equal(new ChartRange(0, 100), ChartScale.For(SensorKind.Power, new[] { 5.0, 86 }));
        Assert.Equal(new ChartRange(0, 1), ChartScale.For(SensorKind.Throughput, Array.Empty<double>()));
        Assert.Equal(new ChartRange(0, 6), ChartScale.For(SensorKind.Clock, new[] { 3.6, 4.65 }));
        Assert.Equal(8, ChartScale.NiceCeiling(7.2));
        Assert.Equal(2500, ChartScale.NiceCeiling(2403));
    }
}

public sealed class MonitorPlanTests
{
    /// <summary>Набор датчиков, как на компьютере с Ryzen 5 5600 и RX 6700 XT (по sensors.txt).</summary>
    internal static HardwareSnapshot Pc()
    {
        static SensorReading S(string id, string name, SensorKind kind, float? value, float? min = null, float? max = null) =>
            new(id, name, kind, value, min ?? value, max ?? value);

        var cpu = new List<SensorReading>
        {
            S("/amdcpu/0/load/0", "CPU Total", SensorKind.Load, 26.3f, 0, 99),
            S("/amdcpu/0/load/1", "CPU Core Max", SensorKind.Load, 72, 0, 100),
            S("/amdcpu/0/power/0", "Package", SensorKind.Power, 49.4f, 6, 86),
            S("/amdcpu/0/clock/1", "Cores (Average)", SensorKind.Clock, 4030, 0, 4650),
            S("/amdcpu/0/clock/2", "Cores (Average Effective)", SensorKind.Clock, 1121, 0, 4554),
            S("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", SensorKind.Temperature, 53.9f, 37, 78),
            S("/amdcpu/0/temperature/3", "CCD1 (Tdie)", SensorKind.Temperature, 52.5f, 32, 78),
            S("/amdcpu/0/voltage/0", "Core (SVI2 TFN)", SensorKind.Voltage, 1.2f, 0.9f, 1.29f),
            S("/amdcpu/0/clock/0", "Bus Speed", SensorKind.Clock, 100),
        };
        for (int i = 1; i <= 12; i++)
            cpu.Add(S($"/amdcpu/0/load/{i + 1}", $"CPU Core #{i}", SensorKind.Load, 10 * (i % 7), 0, 100));
        for (int i = 1; i <= 6; i++)
        {
            cpu.Add(S($"/amdcpu/0/clock/{i * 2 + 1}", $"Core #{i}", SensorKind.Clock, 3720, 2880, 4650));
            cpu.Add(S($"/amdcpu/0/clock/{i * 2 + 2}", $"Core #{i} (Effective)", SensorKind.Clock, 1100, 0, 4550));
            cpu.Add(S($"/amdcpu/0/factor/{i - 1}", $"Core #{i}", SensorKind.Factor, 37.2f));
            cpu.Add(S($"/amdcpu/0/voltage/{i + 1}", $"Core #{i} VID", SensorKind.Voltage, 1.2f));
        }

        var gpu = new List<SensorReading>
        {
            S("/gpu-amd/0/smalldata/2", "GPU Memory Total", SensorKind.SmallData, 12272),
            S("/gpu-amd/0/factor/0", "Fullscreen FPS", SensorKind.Factor, -1, -1, 892),
            S("/gpu-amd/0/load/2", "D3D 3D", SensorKind.Load, 67, 0, 99),
            S("/gpu-amd/0/smalldata/0", "GPU Memory Used", SensorKind.SmallData, 2324, 1194, 6441),
            S("/gpu-amd/0/power/3", "GPU Package", SensorKind.Power, 35, 0, 148),
            S("/gpu-amd/0/clock/0", "GPU Core", SensorKind.Clock, 668, 0, 2533),
            S("/gpu-amd/0/clock/2", "GPU Memory", SensorKind.Clock, 2092, 0, 2098),
            S("/gpu-amd/0/temperature/0", "GPU Core", SensorKind.Temperature, 45, 33, 63),
            S("/gpu-amd/0/temperature/7", "GPU Hot Spot", SensorKind.Temperature, 55, 40, 77),
            S("/gpu-amd/0/fan/0", "GPU Fan", SensorKind.Fan, 1046, 681, 2701),
            S("/gpu-amd/0/control/0", "GPU Fan", SensorKind.Control, 31, 20, 81),
            S("/gpu-amd/0/voltage/0", "GPU Core", SensorKind.Voltage, 0.887f, 0, 1.125f),
            S("/gpu-amd/0/load/0", "GPU Core", SensorKind.Load, 76, 0, 99),
            S("/gpu-amd/0/load/1", "GPU Memory", SensorKind.Load, 1, 0, 29),
        };

        var board = new List<SensorReading>
        {
            S("/lpc/it8655e/0/control/0", "Fan #1", SensorKind.Control, null),
            S("/lpc/it8655e/0/voltage/0", "Vcore", SensorKind.Voltage, 1.21f, 0.18f, 1.31f),
            S("/lpc/it8655e/0/voltage/8", "CMOS Battery", SensorKind.Voltage, 1.613f),
            S("/lpc/it8655e/0/temperature/0", "Temperature #1", SensorKind.Temperature, 53, 37, 75),
            S("/lpc/it8655e/0/temperature/1", "Temperature #2", SensorKind.Temperature, 39, 36, 45),
            S("/lpc/it8655e/0/temperature/2", "Temperature #3", SensorKind.Temperature, 41, 36, 54),
            S("/lpc/it8655e/0/temperature/3", "Temperature #4", SensorKind.Temperature, 41, 36, 54),
            S("/lpc/it8655e/0/fan/0", "Fan #1", SensorKind.Fan, 503, 498, 1155),
            S("/lpc/it8655e/0/fan/1", "Fan #2", SensorKind.Fan, 0, 0, 741),
            S("/lpc/it8655e/0/fan/2", "Fan #3", SensorKind.Fan, 0, 0, 1487),
        };

        var devices = new List<DeviceSnapshot>
        {
            new("/lpc/it8655e/0", "ITE IT8655E", DeviceKind.Motherboard, board),
            new("/amdcpu/0", "AMD Ryzen 5 5600", DeviceKind.Cpu, cpu),
            new("/vram", "Virtual Memory", DeviceKind.Memory, new[]
            {
                S("/vram/data/2", "Memory Used", SensorKind.Data, 15.8f, 12, 23.4f),
                S("/vram/data/3", "Memory Available", SensorKind.Data, 20.8f),
                S("/vram/load/1", "Memory", SensorKind.Load, 43, 33, 64),
            }),
            new("/ram", "Total Memory", DeviceKind.Memory, new[]
            {
                S("/ram/data/0", "Memory Used", SensorKind.Data, 9.6f, 7.5f, 14.2f),
                S("/ram/data/1", "Memory Available", SensorKind.Data, 22.3f),
                S("/ram/load/0", "Memory", SensorKind.Load, 30, 23, 44),
            }),
        };
        for (int i = 0; i < 4; i++)
        {
            devices.Add(new DeviceSnapshot($"/memory/dimm/{i}", $"DIMM #{i}", DeviceKind.Memory, new[]
            {
                S($"/memory/dimm/{i}/timing/22", "tAA (CAS Latency Time)", SensorKind.Timing, 13.75f),
                S($"/memory/dimm/{i}/data/50", "Capacity", SensorKind.Data, 8),
            }));
        }

        devices.Add(new DeviceSnapshot("/gpu-amd/0", "AMD Radeon RX 6700 XT", DeviceKind.GpuAmd, gpu));
        devices.Add(new DeviceSnapshot("/nvme/1", "MSI M480 PRO 1TB", DeviceKind.Storage, new[]
        {
            S("/nvme/1/temperature/0", "Composite Temperature", SensorKind.Temperature, 44, 0, 61),
            S("/nvme/1/temperature/10", "Warning Temperature", SensorKind.Temperature, 81, 0, 81),
            S("/nvme/1/level/20", "Life", SensorKind.Level, 96, 0, 96),
            S("/nvme/1/load/53", "Total Activity", SensorKind.Load, 2.4f, 0, 100),
            S("/nvme/1/throughput/54", "Read Rate", SensorKind.Throughput, 302936, 0, 1273763000),
        }));
        devices.Add(new DeviceSnapshot("/ssd/0", "KINGSTON SEDC500M480G ", DeviceKind.Storage, new[]
        {
            S("/ssd/0/temperature/0", "Temperature", SensorKind.Temperature, 33, 0, 33),
            S("/ssd/0/load/53", "Total Activity", SensorKind.Load, 0.06f, 0, 100),
        }));

        SensorReading[] Nic(string id, float data, float speed) => new[]
        {
            S(id + "/data/0", "Data Uploaded", SensorKind.Data, data / 10),
            S(id + "/data/1", "Data Downloaded", SensorKind.Data, data),
            S(id + "/throughput/0", "Upload Speed", SensorKind.Throughput, speed / 2, 0, speed),
            S(id + "/throughput/1", "Download Speed", SensorKind.Throughput, speed, 0, speed),
        };
        devices.Add(new DeviceSnapshot("/nic/a", "Подключение по локальной сети* 5", DeviceKind.Network, Nic("/nic/a", 0, 0)));
        devices.Add(new DeviceSnapshot("/nic/b", "Ethernet", DeviceKind.Network, Nic("/nic/b", 1.1f, 195)));
        devices.Add(new DeviceSnapshot("/nic/c", "Ethernet-QoS Packet Scheduler-0000", DeviceKind.Network, Nic("/nic/c", 1.1f, 195)));
        devices.Add(new DeviceSnapshot("/nic/d", "outline-tap0", DeviceKind.Network, Nic("/nic/d", 1.5f, 195)));
        devices.Add(new DeviceSnapshot("/nic/e", "Ethernet (отладчик ядра)", DeviceKind.Network, Nic("/nic/e", 0, 0)));

        return new HardwareSnapshot(DateTime.Now, devices);
    }

    [Fact]
    public void OverviewCollapsesCoresIntoBars()
    {
        IReadOnlyList<PlanColumn> columns = MonitorPlan.Overview(Pc());
        Assert.Equal(3, columns.Count);

        PlanDevice cpu = Assert.Single(columns[0].Devices);
        Assert.Equal("AMD Ryzen 5 5600", cpu.Name);
        Assert.Equal("6 ядер · 12 потоков", cpu.Sub);
        PlanItem threads = Assert.Single(cpu.Items, i => i.Kind == PlanItemKind.Bars && i.Text == "Потоки 1–12");
        Assert.Equal(12, threads.SensorIds.Count);
        PlanItem cores = Assert.Single(cpu.Items, i => i.Kind == PlanItemKind.Bars && i.Text == "Ядра 1–6");
        Assert.Equal(6, cores.SensorIds.Count);
        Assert.Equal(4650, cores.BarsMax);
        Assert.Contains(cpu.Items, i => i.Kind == PlanItemKind.Row && i.Text == "Tctl/Tdie");
        Assert.Contains(cpu.Items, i => i.Kind == PlanItemKind.Row && i.Text == "Общая" && i.BarSensorId == "/amdcpu/0/load/0");
        Assert.Contains(cpu.Items, i => i.Text == "Средняя" && i.Style == ValueStyle.Gigahertz);
        Assert.DoesNotContain(cpu.Items, i => i.Text.Contains("VID"));

        Assert.Equal(2, columns[0].Chart.Count);
        Assert.False(columns[0].Chart[0].RightAxis);
        Assert.True(columns[0].Chart[1].RightAxis);
    }

    [Fact]
    public void OverviewShowsGpuMemoryAndFan()
    {
        PlanDevice gpu = Assert.Single(MonitorPlan.Overview(Pc())[1].Devices);
        Assert.Equal("12 ГБ видеопамяти", gpu.Sub);
        Assert.Contains(gpu.Items, i => i.Text == "Горячая точка");
        Assert.Contains(gpu.Items, i => i.Text == "Контроллер памяти" && i.BarSensorId == "/gpu-amd/0/load/1");
        PlanItem vram = Assert.Single(gpu.Items, i => i.Text == "Видеопамять");
        Assert.Equal("/gpu-amd/0/smalldata/2", vram.BarTotalId);
        Assert.Equal(ValueStyle.GigabytesFromMegabytes, vram.Style);
        PlanItem fan = Assert.Single(gpu.Items, i => i.Text == "Вентилятор");
        Assert.Equal("/gpu-amd/0/control/0", fan.BarSensorId);
    }

    [Fact]
    public void OverviewThirdColumnHasBoardMemoryAndDisks()
    {
        IReadOnlyList<PlanDevice> devices = MonitorPlan.Overview(Pc())[2].Devices;
        Assert.Equal(new[] { "MB", "RAM", "SSD" }, devices.Select(d => d.Tag));

        PlanDevice board = devices[0];
        Assert.Equal("ITE IT8655E", board.Sub);
        Assert.Equal(3, board.Items.Count(i => i.Kind == PlanItemKind.Row && i.SensorKind == SensorKind.Temperature));
        Assert.Equal(3, board.Items.Count(i => i.Kind == PlanItemKind.Row && i.SensorKind == SensorKind.Fan));

        Assert.Equal("4 × 8 ГБ", devices[1].Sub);
        Assert.Contains(devices[1].Items, i => i.Text == "Занято" && i.BarSensorId == "/ram/load/0");

        PlanDevice disks = devices[2];
        Assert.Contains(disks.Items, i => i.Kind == PlanItemKind.Label && i.Text == "KINGSTON SEDC500M480G");
        Assert.DoesNotContain(disks.Items, i => i.SensorId == "/nvme/1/temperature/10");
        Assert.Equal(PlanItemKind.Note, disks.Items[^1].Kind);
    }

    [Fact]
    public void HidesEmptyAndServiceSensors()
    {
        HardwareSnapshot pc = Pc();
        SensorReading Sensor(string id) => pc.Devices.SelectMany(d => d.Sensors).First(s => s.Id == id);

        Assert.True(MonitorPlan.IsHiddenByDefault(Sensor("/lpc/it8655e/0/control/0")));
        Assert.True(MonitorPlan.IsHiddenByDefault(Sensor("/memory/dimm/0/timing/22")));
        Assert.True(MonitorPlan.IsHiddenByDefault(Sensor("/nvme/1/temperature/10")));
        Assert.True(MonitorPlan.IsHiddenByDefault(Sensor("/gpu-amd/0/factor/0")));
        Assert.True(MonitorPlan.IsHiddenByDefault(Sensor("/nic/a/throughput/1")));
        Assert.False(MonitorPlan.IsHiddenByDefault(Sensor("/lpc/it8655e/0/fan/1")));
        Assert.False(MonitorPlan.IsHiddenByDefault(Sensor("/amdcpu/0/temperature/2")));
    }

    [Fact]
    public void PicksPhysicalNetworkAdapter()
    {
        HardwareSnapshot pc = Pc();
        Assert.Equal("Ethernet", MonitorPlan.MainNetwork(pc)?.Name);
        Assert.True(MonitorPlan.IsDuplicateAdapter(pc.Devices.First(d => d.Name.Contains("QoS"))));
        Assert.True(MonitorPlan.IsDuplicateAdapter(pc.Devices.First(d => d.Name.Contains("отладчик"))));
        Assert.False(MonitorPlan.IsDuplicateAdapter(pc.Devices.First(d => d.Name == "outline-tap0")));
    }

    [Fact]
    public void GraphsCoverMainMetrics()
    {
        IReadOnlyList<GraphPlan> graphs = MonitorPlan.Graphs(Pc());
        Assert.Equal(new[]
        {
            "Температура процессора", "Загрузка процессора", "Частота процессора", "Мощность процессора",
            "Температура видеокарты", "Загрузка видеокарты", "Частота видеокарты", "Мощность видеокарты",
            "Оперативная память", "Видеопамять", "Активность дисков", "Сеть · Ethernet",
        }, graphs.Select(g => g.Title));

        GraphPlan gpuTemp = graphs[4];
        Assert.Equal(new[] { "Ядро", "Горячая точка" }, gpuTemp.Series.Select(s => s.Label));
        Assert.NotNull(gpuTemp.Limit);
        Assert.Equal(32, graphs[8].FixedMax);
        Assert.Equal(12, graphs[9].FixedMax);
        Assert.Equal(2, graphs[10].Series.Count);
        Assert.Equal(ValueStyle.Megabits, graphs[11].Series[0].Style);
    }
}
