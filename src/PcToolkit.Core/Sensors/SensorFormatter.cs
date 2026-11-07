namespace PcToolkit.Core.Sensors;

/// <summary>Подписи, единицы измерения и порядок отображения датчиков.</summary>
public static class SensorFormatter
{
    public static string Unit(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => "%",
        SensorKind.Clock => "МГц",
        SensorKind.Power => "Вт",
        SensorKind.Voltage => "В",
        SensorKind.Current => "А",
        SensorKind.Fan => "об/мин",
        SensorKind.Data => "ГБ",
        SensorKind.SmallData => "МБ",
        SensorKind.Throughput => "Б/с",
        SensorKind.Frequency => "Гц",
        SensorKind.Flow => "л/ч",
        SensorKind.Energy => "мВт·ч",
        SensorKind.Noise => "дБА",
        SensorKind.Timing => "нс",
        SensorKind.Conductivity => "мкСм/см",
        SensorKind.TimeSpan => "с",
        _ => "",
    };

    public static string Format(SensorKind kind, float? value)
    {
        if (value is not float v)
            return "—";

        return kind switch
        {
            SensorKind.Temperature => $"{v:0.0} °C",
            SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => $"{v:0.0} %",
            SensorKind.Clock => $"{v:0} МГц",
            SensorKind.Power => $"{v:0.0} Вт",
            SensorKind.Voltage => $"{v:0.000} В",
            SensorKind.Current => $"{v:0.00} А",
            SensorKind.Fan => $"{v:0} об/мин",
            SensorKind.Data => $"{v:0.0} ГБ",
            SensorKind.SmallData => $"{v:0} МБ",
            SensorKind.Throughput => FormatThroughput(v),
            SensorKind.Frequency => $"{v:0} Гц",
            SensorKind.TimeSpan => FormatDuration(v),
            SensorKind.Factor => $"{v:0.###}",
            _ => $"{v:0.##} {Unit(kind)}".TrimEnd(),
        };
    }

    public static string GroupTitle(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "Температуры",
        SensorKind.Load => "Нагрузка",
        SensorKind.Clock => "Частоты",
        SensorKind.Power => "Мощность",
        SensorKind.Voltage => "Напряжения",
        SensorKind.Current => "Ток",
        SensorKind.Fan => "Вентиляторы",
        SensorKind.Control => "Управление вентиляторами",
        SensorKind.Level => "Уровни",
        SensorKind.Data or SensorKind.SmallData => "Объём данных",
        SensorKind.Throughput => "Скорость обмена",
        SensorKind.Frequency => "Частота",
        SensorKind.Flow => "Поток жидкости",
        SensorKind.Factor => "Коэффициенты",
        SensorKind.TimeSpan => "Время",
        SensorKind.Timing => "Тайминги",
        SensorKind.Energy => "Энергия",
        SensorKind.Noise => "Шум",
        SensorKind.Conductivity => "Проводимость",
        SensorKind.Humidity => "Влажность",
        _ => "Прочее",
    };

    public static int GroupOrder(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => 0,
        SensorKind.Load => 1,
        SensorKind.Clock => 2,
        SensorKind.Power => 3,
        SensorKind.Fan => 4,
        SensorKind.Control => 5,
        SensorKind.Voltage => 6,
        SensorKind.Current => 7,
        SensorKind.Data or SensorKind.SmallData => 8,
        SensorKind.Throughput => 9,
        _ => 20,
    };

    public static string DeviceTitle(DeviceKind kind) => kind switch
    {
        DeviceKind.Cpu => "Процессор",
        DeviceKind.GpuNvidia => "Видеокарта NVIDIA",
        DeviceKind.GpuAmd => "Видеокарта AMD",
        DeviceKind.GpuIntel => "Графика Intel",
        DeviceKind.Memory => "Память",
        DeviceKind.Motherboard => "Материнская плата",
        DeviceKind.Storage => "Накопитель",
        DeviceKind.Network => "Сетевой адаптер",
        DeviceKind.Cooler => "Система охлаждения",
        DeviceKind.Psu => "Блок питания",
        DeviceKind.Battery => "Батарея",
        _ => "Устройство",
    };

    /// <summary>Короткая метка для карточек: CPU, GPU, RAM и т. д.</summary>
    public static string DeviceTag(DeviceKind kind) => kind switch
    {
        DeviceKind.Cpu => "CPU",
        DeviceKind.GpuNvidia or DeviceKind.GpuAmd or DeviceKind.GpuIntel => "GPU",
        DeviceKind.Memory => "RAM",
        DeviceKind.Motherboard => "MB",
        DeviceKind.Storage => "DISK",
        DeviceKind.Network => "NET",
        DeviceKind.Cooler => "FAN",
        DeviceKind.Psu => "PSU",
        DeviceKind.Battery => "BAT",
        _ => "DEV",
    };

    public static int DeviceOrder(DeviceKind kind) => kind switch
    {
        DeviceKind.Cpu => 0,
        DeviceKind.GpuNvidia or DeviceKind.GpuAmd or DeviceKind.GpuIntel => 1,
        DeviceKind.Memory => 2,
        DeviceKind.Motherboard => 3,
        DeviceKind.Cooler => 4,
        DeviceKind.Storage => 5,
        DeviceKind.Psu => 6,
        DeviceKind.Battery => 7,
        DeviceKind.Network => 8,
        _ => 9,
    };

    private static string FormatThroughput(float bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
            return $"{bytesPerSecond / (1024 * 1024):0.0} МБ/с";

        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:0.0} КБ/с";

        return $"{bytesPerSecond:0} Б/с";
    }

    private static string FormatDuration(float seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
    }
}
