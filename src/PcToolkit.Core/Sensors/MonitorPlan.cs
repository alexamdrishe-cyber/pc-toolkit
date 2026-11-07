using PcToolkit.Core.Diagnostics;

namespace PcToolkit.Core.Sensors;

public enum PlanItemKind
{
    /// <summary>Заголовок группы: «Температура», «Загрузка»…</summary>
    Label,
    /// <summary>Строка: один датчик, значение и мин–макс.</summary>
    Row,
    /// <summary>Столбики по ядрам или потокам вместо десятка одинаковых строк.</summary>
    Bars,
    /// <summary>Пояснение мелким текстом.</summary>
    Note,
}

/// <summary>Элемент колонки на экране «Обзор».</summary>
public sealed record PlanItem(PlanItemKind Kind, string Text)
{
    public string SensorId { get; init; } = "";

    public SensorKind SensorKind { get; init; }

    public ValueStyle Style { get; init; }

    /// <summary>Датчик, чьё значение в процентах рисуется полоской (может совпадать с самим датчиком).</summary>
    public string? BarSensorId { get; init; }

    /// <summary>Если задан, полоска = значение / этот датчик × 100 (видеопамять: занято / всего).</summary>
    public string? BarTotalId { get; init; }

    /// <summary>Для столбиков: датчики по порядку и шкала.</summary>
    public IReadOnlyList<string> SensorIds { get; init; } = Array.Empty<string>();

    public double BarsMin { get; init; }

    public double BarsMax { get; init; } = 100;

    public static PlanItem Label(string text) => new(PlanItemKind.Label, text);

    public static PlanItem Note(string text) => new(PlanItemKind.Note, text);
}

/// <summary>Линия графика: датчик, подпись, своя шкала справа или общая слева.</summary>
public sealed record PlanSeries(string SensorId, string Label, SensorKind Kind, ValueStyle Style = ValueStyle.Default, bool RightAxis = false);

public sealed record PlanDevice(string Tag, DeviceKind Kind, string Name, string Sub, IReadOnlyList<PlanItem> Items);

/// <summary>Колонка «Обзора»: устройства и (не всегда) график за последние минуты.</summary>
public sealed record PlanColumn(IReadOnlyList<PlanDevice> Devices, IReadOnlyList<PlanSeries> Chart);

/// <summary>Карточка на экране «Графики».</summary>
public sealed record GraphPlan(string Title, string Tag, IReadOnlyList<PlanSeries> Series)
{
    /// <summary>Пунктир: порог, после которого стоит насторожиться.</summary>
    public double? Limit { get; init; }

    public string LimitLabel { get; init; } = "";

    /// <summary>Верх шкалы, если он известен заранее (объём памяти).</summary>
    public double? FixedMax { get; init; }
}

/// <summary>
/// Что показывать на экранах мониторинга. Строится по снимку датчиков: какие датчики главные,
/// какие свернуть в столбики по ядрам, какие спрятать как пустые или служебные.
/// Не зависит от интерфейса — проверяется тестами на наборе датчиков реального компьютера.
/// </summary>
public static class MonitorPlan
{
    private static readonly string[] FilterAdapterMarks =
    {
        "QoS Packet Scheduler", "WFP Native MAC", "WFP 802.3 MAC", "LightWeight Filter", "Kernel Debugger", "отладчик ядра",
    };

    private static readonly string[] PhysicalAdapterMarks = { "Ethernet", "Wi-Fi", "Беспроводная", "Wireless", "WLAN" };

    // ---------- Общие правила ----------

    /// <summary>
    /// Датчик, который по умолчанию прячется в таблице: нет значения, всегда ноль,
    /// пороги производителя, тайминги из SPD, «FPS» без игры.
    /// </summary>
    public static bool IsHiddenByDefault(SensorReading sensor)
    {
        if (sensor.Value is not float value || !float.IsFinite(value))
            return true;
        if (value == 0 && (sensor.Max ?? 0) == 0 && (sensor.Min ?? 0) == 0)
            return true;
        if (sensor.Kind == SensorKind.Timing)
            return true;
        if (sensor.Kind is SensorKind.Temperature or SensorKind.Level && KeyMetrics.IsThreshold(sensor.Name))
            return true;
        if (sensor.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase))
            return true;
        return sensor.Kind == SensorKind.Factor && value < 0;
    }

    /// <summary>Копии сетевого адаптера от фильтров Windows (QoS, WFP): у них те же цифры, что у самого адаптера.</summary>
    public static bool IsDuplicateAdapter(DeviceSnapshot device) =>
        device.Kind == DeviceKind.Network
        && FilterAdapterMarks.Any(mark => device.Name.Contains(mark, StringComparison.OrdinalIgnoreCase));

    /// <summary>Сетевой адаптер, через который реально идёт трафик: физический, с наибольшим объёмом данных.</summary>
    public static DeviceSnapshot? MainNetwork(HardwareSnapshot snapshot) =>
        snapshot.Devices
            .Where(d => d.Kind == DeviceKind.Network && !IsDuplicateAdapter(d))
            .Where(d => Traffic(d) > 0)
            .OrderByDescending(d => PhysicalAdapterMarks.Any(m => d.Name.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(Traffic)
            .FirstOrDefault();

    public static DeviceSnapshot? MainGpu(HardwareSnapshot snapshot) =>
        snapshot.Devices
            .Where(d => d.IsGpu)
            .OrderBy(d => d.Kind switch { DeviceKind.GpuNvidia => 0, DeviceKind.GpuAmd => 1, _ => 2 })
            .ThenByDescending(d => Find(d, SensorKind.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total")?.Value ?? 0)
            .FirstOrDefault();

    /// <summary>«Total Memory» — физическая память; «Virtual Memory» — с файлом подкачки.</summary>
    public static DeviceSnapshot? PhysicalMemory(HardwareSnapshot snapshot) =>
        snapshot.Devices.FirstOrDefault(d => d.Kind == DeviceKind.Memory && Find(d, SensorKind.Data, "Memory Used") is not null
                                             && !d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

    public static DeviceSnapshot? VirtualMemory(HardwareSnapshot snapshot) =>
        snapshot.Devices.FirstOrDefault(d => d.Kind == DeviceKind.Memory && d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

    /// <summary>Модули памяти: «4 × 8 ГБ» (по объёму из SPD). Пусто, если модули не видны.</summary>
    public static string MemoryModules(HardwareSnapshot snapshot)
    {
        List<float> sizes = snapshot.Devices
            .Where(d => d.Kind == DeviceKind.Memory)
            .Select(d => Find(d, SensorKind.Data, "Capacity")?.Value)
            .OfType<float>()
            .Where(v => v > 0)
            .ToList();
        if (sizes.Count == 0)
            return "";
        return sizes.Distinct().Count() == 1
            ? $"{sizes.Count} × {sizes[0]:0} ГБ"
            : $"{sizes.Count} модуля, {sizes.Sum():0} ГБ";
    }

    // ---------- «Обзор» ----------

    public static IReadOnlyList<PlanColumn> Overview(HardwareSnapshot snapshot)
    {
        var columns = new List<PlanColumn>();

        DeviceSnapshot? cpu = snapshot.Devices.FirstOrDefault(d => d.Kind == DeviceKind.Cpu);
        columns.Add(cpu is null
            ? new PlanColumn(Array.Empty<PlanDevice>(), Array.Empty<PlanSeries>())
            : CpuColumn(cpu, snapshot));

        DeviceSnapshot? gpu = MainGpu(snapshot);
        columns.Add(gpu is null
            ? new PlanColumn(Array.Empty<PlanDevice>(), Array.Empty<PlanSeries>())
            : GpuColumn(gpu));

        columns.Add(new PlanColumn(OtherDevices(snapshot), Array.Empty<PlanSeries>()));
        return columns;
    }

    private static PlanColumn CpuColumn(DeviceSnapshot cpu, HardwareSnapshot snapshot)
    {
        var items = new List<PlanItem>();
        KeyMetrics metrics = KeyMetrics.From(snapshot);

        List<SensorReading> temps = Plausible(cpu, SensorKind.Temperature)
            .Where(s => !KeyMetrics.IsThreshold(s.Name) && SensorText.IndexOf(s.Name) is null)
            .Take(3)
            .ToList();
        List<SensorReading> coreTemps = Numbered(cpu, SensorKind.Temperature, s => !KeyMetrics.IsThreshold(s.Name));
        if (temps.Count > 0 || coreTemps.Count > 1)
        {
            items.Add(PlanItem.Label("Температура"));
            items.AddRange(temps.Select(s => Row(cpu, s)));
            if (coreTemps.Count > 1)
                items.Add(Bars("Ядра", coreTemps, SensorKind.Temperature, 20, 100));
        }

        SensorReading? total = Find(cpu, SensorKind.Load, "CPU Total");
        SensorReading? coreMax = Find(cpu, SensorKind.Load, "CPU Core Max", "Core Max");
        List<SensorReading> threadLoads = Numbered(cpu, SensorKind.Load);
        List<SensorReading> coreClocks = Numbered(cpu, SensorKind.Clock, s => !s.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase));
        if (total is not null || threadLoads.Count > 0)
        {
            items.Add(PlanItem.Label("Загрузка"));
            if (total is not null)
                items.Add(Row(cpu, total, bar: total.Id));
            if (coreMax is not null)
                items.Add(Row(cpu, coreMax, bar: coreMax.Id));
            if (threadLoads.Count > 1)
            {
                string title = coreClocks.Count > 0 && threadLoads.Count > coreClocks.Count ? "Потоки" : "Ядра";
                items.Add(Bars(title, threadLoads, SensorKind.Load, 0, 100));
            }
        }

        SensorReading? average = Find(cpu, SensorKind.Clock, "Cores (Average)");
        SensorReading? effective = Find(cpu, SensorKind.Clock, "Cores (Average Effective)");
        if (average is not null || effective is not null || coreClocks.Count > 0)
        {
            items.Add(PlanItem.Label("Частота"));
            if (average is not null)
                items.Add(Row(cpu, average, style: ValueStyle.Gigahertz));
            if (effective is not null)
                items.Add(Row(cpu, effective, style: ValueStyle.Gigahertz));
            if (coreClocks.Count > 1)
            {
                double max = coreClocks.Max(s => Math.Max(s.Max ?? 0, s.Value ?? 0));
                items.Add(Bars("Ядра", coreClocks, SensorKind.Clock, 0, max > 0 ? max : 5000) with { Style = ValueStyle.Gigahertz });
            }
        }

        SensorReading? package = Find(cpu, SensorKind.Power, "Package", "CPU Package");
        SensorReading? vcore = Find(cpu, SensorKind.Voltage, "Core (SVI2 TFN)", "Core (SVI3 TFN)", "CPU Core", "Vcore");
        if (package is not null || vcore is not null)
        {
            items.Add(PlanItem.Label("Питание"));
            if (package is not null)
                items.Add(Row(cpu, package));
            if (vcore is not null)
                items.Add(Row(cpu, vcore) with { Text = "Напряжение ядра" });
        }

        int cores = coreClocks.Count;
        int threads = threadLoads.Count;
        string sub = cores > 0 && threads > 0 ? $"{cores} {Formatting.Plural(cores, "ядро", "ядра", "ядер")} · {threads} {Formatting.Plural(threads, "поток", "потока", "потоков")}" : "";

        var chart = new List<PlanSeries>();
        if (metrics.CpuTemperature is { } temperature)
            chart.Add(new PlanSeries(temperature.SensorId, "Температура", SensorKind.Temperature));
        if (total is not null)
            chart.Add(new PlanSeries(total.Id, "Загрузка", SensorKind.Load, RightAxis: chart.Count > 0));

        return new PlanColumn(new[] { new PlanDevice("CPU", cpu.Kind, cpu.Name, sub, items) }, chart);
    }

    private static PlanColumn GpuColumn(DeviceSnapshot gpu)
    {
        var items = new List<PlanItem>();

        SensorReading? core = Find(gpu, SensorKind.Temperature, "GPU Core");
        SensorReading? hotSpot = Find(gpu, SensorKind.Temperature, "GPU Hot Spot");
        List<SensorReading> temps = Plausible(gpu, SensorKind.Temperature)
            .Where(s => !KeyMetrics.IsThreshold(s.Name))
            .OrderBy(s => s == core ? 0 : s == hotSpot ? 1 : 2)
            .Take(4)
            .ToList();
        if (temps.Count > 0)
        {
            items.Add(PlanItem.Label("Температура"));
            items.AddRange(temps.Select(s => Row(gpu, s)));
        }

        SensorReading? load = Find(gpu, SensorKind.Load, "GPU Core", "D3D 3D");
        SensorReading? memoryController = Find(gpu, SensorKind.Load, "GPU Memory", "GPU Memory Controller");
        SensorReading? vramUsed = Find(gpu, SensorKind.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        SensorReading? vramTotal = Find(gpu, SensorKind.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
        if (load is not null || memoryController is not null || vramUsed is not null)
        {
            items.Add(PlanItem.Label("Загрузка"));
            if (load is not null)
                items.Add(Row(gpu, load, bar: load.Id));
            if (memoryController is not null)
                items.Add(Row(gpu, memoryController, bar: memoryController.Id));
            if (vramUsed is not null)
                items.Add(Row(gpu, vramUsed, style: ValueStyle.GigabytesFromMegabytes) with
                {
                    Text = "Видеопамять",
                    BarSensorId = vramTotal is null ? null : vramUsed.Id,
                    BarTotalId = vramTotal?.Id,
                });
        }

        SensorReading? coreClock = Find(gpu, SensorKind.Clock, "GPU Core", "GPU Shader");
        SensorReading? memoryClock = Find(gpu, SensorKind.Clock, "GPU Memory");
        if (coreClock is not null || memoryClock is not null)
        {
            items.Add(PlanItem.Label("Частота"));
            if (coreClock is not null)
                items.Add(Row(gpu, coreClock));
            if (memoryClock is not null)
                items.Add(Row(gpu, memoryClock));
        }

        SensorReading? power = Find(gpu, SensorKind.Power, "GPU Package", "GPU Power", "GPU PPT", "GPU Core");
        SensorReading? voltage = Find(gpu, SensorKind.Voltage, "GPU Core");
        SensorReading? fan = gpu.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Fan && s.Value is not null);
        SensorReading? fanControl = gpu.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Control && s.Value is not null);
        if (power is not null || voltage is not null || fan is not null)
        {
            items.Add(PlanItem.Label("Питание и охлаждение"));
            if (power is not null)
                items.Add(Row(gpu, power) with { Text = "Мощность" });
            if (voltage is not null)
                items.Add(Row(gpu, voltage) with { Text = "Напряжение ядра" });
            if (fan is not null)
                items.Add(Row(gpu, fan) with { BarSensorId = fanControl?.Id });
            else if (fanControl is not null)
                items.Add(Row(gpu, fanControl, bar: fanControl.Id));
        }

        string sub = vramTotal?.Value is float totalMb && totalMb > 0 ? $"{Math.Round(totalMb / 1024):0} ГБ видеопамяти" : "";

        var chart = new List<PlanSeries>();
        SensorReading? mainTemp = core ?? temps.FirstOrDefault();
        if (mainTemp is not null)
            chart.Add(new PlanSeries(mainTemp.Id, "Ядро", SensorKind.Temperature));
        if (hotSpot is not null && hotSpot != mainTemp)
            chart.Add(new PlanSeries(hotSpot.Id, "Горячая точка", SensorKind.Temperature));
        else if (load is not null)
            chart.Add(new PlanSeries(load.Id, "Загрузка", SensorKind.Load, RightAxis: chart.Count > 0));

        return new PlanColumn(new[] { new PlanDevice("GPU", gpu.Kind, gpu.Name, sub, items) }, chart);
    }

    private static IReadOnlyList<PlanDevice> OtherDevices(HardwareSnapshot snapshot)
    {
        var devices = new List<PlanDevice>();

        foreach (DeviceSnapshot board in snapshot.Devices.Where(d => d.Kind == DeviceKind.Motherboard))
        {
            List<SensorReading> temps = Plausible(board, SensorKind.Temperature)
                .Where(s => s.Value < 125 && !KeyMetrics.IsThreshold(s.Name))
                .Take(3)
                .ToList();
            List<SensorReading> fans = board.Sensors
                .Where(s => s.Kind == SensorKind.Fan && s.Value is not null && (s.Max ?? 0) > 0)
                .Take(4)
                .ToList();
            if (temps.Count == 0 && fans.Count == 0)
                continue;

            var items = new List<PlanItem>();
            if (temps.Count > 0)
            {
                items.Add(PlanItem.Label("Температура"));
                items.AddRange(temps.Select(s => Row(board, s)));
            }

            if (fans.Count > 0)
            {
                items.Add(PlanItem.Label("Вентиляторы"));
                items.AddRange(fans.Select(s => Row(board, s)));
            }

            devices.Add(new PlanDevice("MB", board.Kind, "Материнская плата", board.Name, items));
        }

        DeviceSnapshot? memory = PhysicalMemory(snapshot);
        if (memory is not null)
        {
            var items = new List<PlanItem>();
            SensorReading? used = Find(memory, SensorKind.Data, "Memory Used");
            SensorReading? load = Find(memory, SensorKind.Load, "Memory");
            if (used is not null)
                items.Add(Row(memory, used) with { BarSensorId = load?.Id });

            DeviceSnapshot? virtualMemory = VirtualMemory(snapshot);
            SensorReading? virtualUsed = virtualMemory is null ? null : Find(virtualMemory, SensorKind.Data, "Memory Used", "Virtual Memory Used");
            SensorReading? virtualLoad = virtualMemory is null ? null : Find(virtualMemory, SensorKind.Load, "Memory", "Virtual Memory");
            if (virtualMemory is not null && virtualUsed is not null)
                items.Add(Row(virtualMemory, virtualUsed) with { Text = "С файлом подкачки", BarSensorId = virtualLoad?.Id });

            if (items.Count > 0)
                devices.Add(new PlanDevice("RAM", DeviceKind.Memory, "Оперативная память", MemoryModules(snapshot), items));
        }

        List<DeviceSnapshot> disks = snapshot.Devices.Where(d => d.Kind == DeviceKind.Storage).ToList();
        if (disks.Count > 0)
        {
            var items = new List<PlanItem>();
            foreach (DeviceSnapshot disk in disks)
            {
                items.Add(PlanItem.Label(disk.Name.Trim()));
                MetricValue? temperature = KeyMetrics.StorageTemperature(disk);
                if (temperature is not null && disk.Sensors.FirstOrDefault(s => s.Id == temperature.SensorId) is { } t)
                    items.Add(Row(disk, t));
                SensorReading? activity = Find(disk, SensorKind.Load, "Total Activity");
                if (activity is not null)
                    items.Add(Row(disk, activity, bar: activity.Id));
            }

            devices.Add(new PlanDevice("SSD", DeviceKind.Storage, "Накопители",
                $"{disks.Count} шт.", items));
        }

        if (devices.Count > 0)
        {
            PlanDevice last = devices[^1];
            devices[^1] = last with
            {
                Items = last.Items.Append(PlanItem.Note("Напряжения платы, сеть и остальные датчики — во вкладке «Все датчики»")).ToList(),
            };
        }

        return devices;
    }

    // ---------- «Графики» ----------

    public static IReadOnlyList<GraphPlan> Graphs(HardwareSnapshot snapshot)
    {
        var graphs = new List<GraphPlan>();
        KeyMetrics metrics = KeyMetrics.From(snapshot);
        DeviceSnapshot? cpu = snapshot.Devices.FirstOrDefault(d => d.Kind == DeviceKind.Cpu);
        DeviceSnapshot? gpu = MainGpu(snapshot);

        if (cpu is not null)
        {
            if (metrics.CpuTemperature is { } temperature)
                graphs.Add(new GraphPlan("Температура процессора", "CPU", new[]
                {
                    new PlanSeries(temperature.SensorId, SensorText.Name(cpu.Kind, SensorKind.Temperature, Name(cpu, temperature.SensorId)), SensorKind.Temperature),
                })
                {
                    Limit = Thresholds.CpuWarning,
                    LimitLabel = $"порог {Thresholds.CpuWarning:0} °C",
                });

            var loads = new List<PlanSeries>();
            if (Find(cpu, SensorKind.Load, "CPU Total") is { } total)
                loads.Add(new PlanSeries(total.Id, "Общая", SensorKind.Load));
            if (Find(cpu, SensorKind.Load, "CPU Core Max", "Core Max") is { } coreMax)
                loads.Add(new PlanSeries(coreMax.Id, "Самое загруженное ядро", SensorKind.Load));
            if (loads.Count > 0)
                graphs.Add(new GraphPlan("Загрузка процессора", "CPU", loads));

            var clocks = new List<PlanSeries>();
            if (Find(cpu, SensorKind.Clock, "Cores (Average)") is { } average)
                clocks.Add(new PlanSeries(average.Id, "Средняя", SensorKind.Clock, ValueStyle.Gigahertz));
            if (Find(cpu, SensorKind.Clock, "Cores (Average Effective)") is { } effective)
                clocks.Add(new PlanSeries(effective.Id, "Эффективная", SensorKind.Clock, ValueStyle.Gigahertz));
            if (clocks.Count > 0)
                graphs.Add(new GraphPlan("Частота процессора", "CPU", clocks));

            if (Find(cpu, SensorKind.Power, "Package", "CPU Package") is { } package)
                graphs.Add(new GraphPlan("Мощность процессора", "CPU", new[] { new PlanSeries(package.Id, "Пакет", SensorKind.Power) }));
        }

        if (gpu is not null)
        {
            var temps = new List<PlanSeries>();
            if (Find(gpu, SensorKind.Temperature, "GPU Core") is { } core)
                temps.Add(new PlanSeries(core.Id, "Ядро", SensorKind.Temperature));
            if (Find(gpu, SensorKind.Temperature, "GPU Hot Spot") is { } hotSpot)
                temps.Add(new PlanSeries(hotSpot.Id, "Горячая точка", SensorKind.Temperature));
            if (temps.Count > 0)
                graphs.Add(new GraphPlan("Температура видеокарты", "GPU", temps)
                {
                    Limit = temps.Count > 1 ? Thresholds.GpuHotSpotWarning : Thresholds.GpuWarning,
                    LimitLabel = temps.Count > 1
                        ? $"порог горячей точки {Thresholds.GpuHotSpotWarning:0} °C"
                        : $"порог {Thresholds.GpuWarning:0} °C",
                });

            if (Find(gpu, SensorKind.Load, "GPU Core", "D3D 3D") is { } load)
                graphs.Add(new GraphPlan("Загрузка видеокарты", "GPU", new[] { new PlanSeries(load.Id, "Ядро", SensorKind.Load) }));

            var clocks = new List<PlanSeries>();
            if (Find(gpu, SensorKind.Clock, "GPU Core", "GPU Shader") is { } coreClock)
                clocks.Add(new PlanSeries(coreClock.Id, "Ядро", SensorKind.Clock));
            if (Find(gpu, SensorKind.Clock, "GPU Memory") is { } memoryClock)
                clocks.Add(new PlanSeries(memoryClock.Id, "Память", SensorKind.Clock));
            if (clocks.Count > 0)
                graphs.Add(new GraphPlan("Частота видеокарты", "GPU", clocks));

            if (Find(gpu, SensorKind.Power, "GPU Package", "GPU Power", "GPU PPT", "GPU Core") is { } power)
                graphs.Add(new GraphPlan("Мощность видеокарты", "GPU", new[] { new PlanSeries(power.Id, "Пакет", SensorKind.Power) }));
        }

        if (PhysicalMemory(snapshot) is { } memory && Find(memory, SensorKind.Data, "Memory Used") is { } used)
        {
            double? total = used.Value + Find(memory, SensorKind.Data, "Memory Available")?.Value;
            graphs.Add(new GraphPlan("Оперативная память", "RAM", new[] { new PlanSeries(used.Id, "Занято", SensorKind.Data) })
            {
                FixedMax = total is > 0 ? Math.Ceiling(total.Value) : null,
            });
        }

        if (gpu is not null && Find(gpu, SensorKind.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used") is { } vram)
        {
            float? total = Find(gpu, SensorKind.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total")?.Value;
            graphs.Add(new GraphPlan("Видеопамять", "GPU", new[] { new PlanSeries(vram.Id, "Занято", SensorKind.SmallData, ValueStyle.GigabytesFromMegabytes) })
            {
                FixedMax = total is > 0 ? Math.Ceiling(total.Value / 1024) : null,
            });
        }

        var disks = snapshot.Devices
            .Where(d => d.Kind == DeviceKind.Storage)
            .Select(d => (Device: d, Activity: Find(d, SensorKind.Load, "Total Activity")))
            .Where(x => x.Activity is not null)
            .Take(3)
            .Select(x => new PlanSeries(x.Activity!.Id, x.Device.Name.Trim(), SensorKind.Load))
            .ToList();
        if (disks.Count > 0)
            graphs.Add(new GraphPlan("Активность дисков", "SSD", disks));

        if (MainNetwork(snapshot) is { } network)
        {
            var series = new List<PlanSeries>();
            if (Find(network, SensorKind.Throughput, "Download Speed") is { } down)
                series.Add(new PlanSeries(down.Id, "Загрузка ↓", SensorKind.Throughput, ValueStyle.Megabits));
            if (Find(network, SensorKind.Throughput, "Upload Speed") is { } up)
                series.Add(new PlanSeries(up.Id, "Отдача ↑", SensorKind.Throughput, ValueStyle.Megabits));
            if (series.Count > 0)
                graphs.Add(new GraphPlan("Сеть · " + network.Name.Trim(), "NET", series));
        }

        return graphs;
    }

    // ---------- Вспомогательное ----------

    public static SensorReading? Find(DeviceSnapshot device, SensorKind kind, params string[] names)
    {
        foreach (string name in names)
        {
            foreach (SensorReading sensor in device.Sensors)
            {
                if (sensor.Kind == kind && string.Equals(sensor.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return sensor;
            }
        }

        return null;
    }

    private static string Name(DeviceSnapshot device, string sensorId) =>
        device.Sensors.FirstOrDefault(s => s.Id == sensorId)?.Name ?? "";

    private static IEnumerable<SensorReading> Plausible(DeviceSnapshot device, SensorKind kind) =>
        device.Sensors.Where(s => s.Kind == kind && s.Value is float v && KeyMetrics.IsPlausible(kind, v));

    /// <summary>
    /// Датчики «… #1», «… #2» одного вида (по ядрам или потокам) в порядке номеров.
    /// Без проверки значения: у спящего ядра частота на миг бывает пустой, а план не должен терять ядро.
    /// </summary>
    private static List<SensorReading> Numbered(DeviceSnapshot device, SensorKind kind, Func<SensorReading, bool>? filter = null) =>
        device.Sensors
            .Where(s => s.Kind == kind && SensorText.IndexOf(s.Name) is not null && (filter?.Invoke(s) ?? true))
            .Where(s => !s.Name.Contains("VID", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => SensorText.IndexOf(s.Name))
            .ToList();

    private static PlanItem Row(DeviceSnapshot device, SensorReading sensor, string? bar = null, ValueStyle style = ValueStyle.Default) =>
        new(PlanItemKind.Row, SensorText.Name(device.Kind, sensor.Kind, sensor.Name))
        {
            SensorId = sensor.Id,
            SensorKind = sensor.Kind,
            Style = style,
            BarSensorId = bar,
        };

    private static PlanItem Bars(string title, IReadOnlyList<SensorReading> sensors, SensorKind kind, double min, double max)
    {
        int first = SensorText.IndexOf(sensors[0].Name) ?? 1;
        int last = SensorText.IndexOf(sensors[^1].Name) ?? sensors.Count;
        return new PlanItem(PlanItemKind.Bars, $"{title} {first}–{last}")
        {
            SensorKind = kind,
            SensorIds = sensors.Select(s => s.Id).ToList(),
            BarsMin = min,
            BarsMax = max,
        };
    }

    private static double Traffic(DeviceSnapshot device) =>
        device.Sensors.Where(s => s.Kind == SensorKind.Data).Sum(s => (double)(s.Value ?? 0));
}
