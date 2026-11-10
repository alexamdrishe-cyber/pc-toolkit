using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Diagnostics;

/// <summary>Разбор малого дампа синего экрана: код, параметры и драйвер-виновник.</summary>
public sealed record KernelDumpInfo(
    string File,
    DateTime Time,
    uint BugCheckCode,
    ulong[] Parameters,
    string? Culprit,
    string CulpritReason,
    bool CulpritIsWindows,
    List<string> StackDrivers);

/// <summary>
/// Читает малые дампы (C:\Windows\Minidump\*.dmp, формат PAGEDU64 «triage»): заголовок с кодом ошибки и параметрами,
/// список загруженных драйверов и кусок стека потока, на котором случился сбой. Виновник — драйвер, в который попадает
/// адрес из параметров ошибки, или первый сторонний драйвер на стеке (так же рассуждают WhoCrashed и !analyze).
/// Только чтение; папка Minidump доступна администраторам — читает служба или приложение от администратора.
/// </summary>
public static class KernelDumpReader
{
    private const ulong KernelSpace = 0xFFFF800000000000;
    private const int TriageOffset = 0x2000;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");

    /// <summary>Последние дампы (новые сверху). Нет доступа или папки — пустой список.</summary>
    public static List<KernelDumpInfo> ReadAll(string? folder = null, int max = 15)
    {
        folder ??= Folder;
        var result = new List<KernelDumpInfo>();
        try
        {
            if (!Directory.Exists(folder))
                return result;
            foreach (FileInfo file in new DirectoryInfo(folder).GetFiles("*.dmp").OrderByDescending(f => f.LastWriteTime).Take(max))
            {
                try
                {
                    if (file.Length is > 16 * 1024 * 1024 or < TriageOffset + 0x100)
                        continue;
                    if (Parse(File.ReadAllBytes(file.FullName), file.FullName, file.LastWriteTime) is { } info)
                        result.Add(info);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"Не удалось прочитать дамп {file.Name}.", ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn("Нет доступа к папке дампов.", ex);
        }

        return result;
    }

    /// <summary>Разбор дампа из памяти. null — не малый дамп 64-битной Windows.</summary>
    public static KernelDumpInfo? Parse(byte[] data, string file, DateTime fileTime)
    {
        if (data.Length < TriageOffset + 0x80 || Encoding.ASCII.GetString(data, 0, 8) != "PAGEDU64")
            return null;

        uint code = U32(data, 0x38);
        ulong[] parameters = { U64(data, 0x40), U64(data, 0x48), U64(data, 0x50), U64(data, 0x58) };
        ulong systemTime = U64(data, 0xFA8);
        DateTime time = systemTime is > 0 and < 0x7FFFFFFFFFFFFFFF ? SafeFileTime(systemTime) ?? fileTime : fileTime;

        List<Module> modules = Drivers(data);
        var stack = new List<string>();
        int stackOffset = (int)U32(data, TriageOffset + 0x28);
        int stackSize = (int)U32(data, TriageOffset + 0x2C);
        if (modules.Count > 0 && stackOffset > 0 && stackSize > 0 && (long)stackOffset + stackSize <= data.Length)
        {
            for (int i = stackOffset; i + 8 <= stackOffset + stackSize; i += 8)
            {
                if (ModuleAt(modules, U64(data, i)) is { } module && !stack.Contains(module.Name, StringComparer.OrdinalIgnoreCase))
                    stack.Add(module.Name);
            }
        }

        (string? culprit, string reason, bool windows) = FindCulprit(data, modules, parameters, stack);
        return new KernelDumpInfo(file, time, code, parameters, culprit, reason, windows, stack.Take(12).ToList());
    }

    private static (string? Name, string Reason, bool Windows) FindCulprit(byte[] data, List<Module> modules, ulong[] parameters, List<string> stack)
    {
        // 1. Windows сама назвала драйвер (BrokenDriverOffset).
        int broken = (int)U32(data, TriageOffset + 0x40);
        if (broken > 0 && DumpString(data, broken) is { Length: > 3 } named && LooksLikeModule(named))
            return (named, "Windows указала этот драйвер при сбое", IsWindowsDriver(named));

        // 2. Адрес из параметров ошибки внутри драйвера.
        var fromParameters = parameters.Select(p => ModuleAt(modules, p)).OfType<Module>().Select(m => m.Name).Distinct().ToList();
        string? thirdParty = fromParameters.FirstOrDefault(n => !IsWindowsDriver(n));
        if (thirdParty is not null)
            return (thirdParty, "ошибка произошла внутри этого драйвера", false);

        // 3. Первый сторонний драйвер на стеке.
        string? onStack = stack.FirstOrDefault(n => !IsWindowsDriver(n));
        if (onStack is not null)
            return (onStack, "этот драйвер работал в момент сбоя", false);

        // 4. Только части Windows — виноват, скорее всего, не она (память, железо или другой драйвер).
        string? windows = fromParameters.FirstOrDefault() ?? stack.FirstOrDefault(n => !IsKernelCore(n));
        return windows is not null
            ? (windows, "сбой внутри части Windows — обычно виноваты память, железо или другой драйвер", true)
            : (null, "", false);
    }

    private sealed record Module(string Name, ulong Base, uint Size);

    /// <summary>Список драйверов из triage-дампа. Размер записи и смещение адреса проверяются на правдоподобие.</summary>
    private static List<Module> Drivers(byte[] data)
    {
        int listOffset = (int)U32(data, TriageOffset + 0x30);
        int count = (int)U32(data, TriageOffset + 0x34);
        if (listOffset <= 0 || count is <= 0 or > 1024 || listOffset >= data.Length)
            return new List<Module>();

        foreach (int stride in new[] { 0xA8, 0xA0, 0xB0, 0x98, 0xB8 })
        {
            foreach (int baseAt in new[] { 0x38, 0x30, 0x40 })
            {
                var modules = new List<Module>(count);
                for (int i = 0; i < count; i++)
                {
                    int entry = listOffset + i * stride;
                    if (entry + Math.Max(stride, baseAt + 16) > data.Length)
                        break;
                    string? name = DumpString(data, (int)U32(data, entry));
                    ulong start = U64(data, entry + baseAt);
                    uint size = U32(data, entry + baseAt + 0x10);
                    if (name is null || !LooksLikeModule(name) || start < KernelSpace || size is 0 or > 0x40000000)
                        continue;
                    modules.Add(new Module(name, start, size));
                }

                if (modules.Count >= count * 0.9)
                    return modules.OrderBy(m => m.Base).ToList();
            }
        }

        return new List<Module>();
    }

    private static Module? ModuleAt(List<Module> modules, ulong address)
    {
        if (address < KernelSpace)
            return null;
        foreach (Module module in modules)
        {
            if (address >= module.Base && address < module.Base + module.Size)
                return module;
        }

        return null;
    }

    /// <summary>DUMP_STRING: длина в символах (4 байта), затем UTF-16.</summary>
    private static string? DumpString(byte[] data, int offset)
    {
        if (offset <= 0 || offset + 4 > data.Length)
            return null;
        int length = (int)U32(data, offset);
        if (length is <= 0 or > 260 || offset + 4 + length * 2 > data.Length)
            return null;
        return Encoding.Unicode.GetString(data, offset + 4, length * 2);
    }

    private static bool LooksLikeModule(string name) =>
        name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, bool> WindowsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Драйвер Microsoft (по файлу на этом компьютере), ядро и HAL — части Windows.</summary>
    public static bool IsWindowsDriver(string name)
    {
        if (IsKernelCore(name))
            return true;
        lock (WindowsCache)
        {
            if (WindowsCache.TryGetValue(name, out bool cached))
                return cached;
        }

        bool windows = false;
        string system = Environment.SystemDirectory;
        foreach (string path in new[] { Path.Combine(system, "drivers", name), Path.Combine(system, name) })
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                string company = FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
                windows = company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // неизвестно — считаем сторонним
            }
        }

        lock (WindowsCache)
            WindowsCache[name] = windows;
        return windows;
    }

    private static bool IsKernelCore(string name) =>
        name.Equals("ntoskrnl.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("ntkrnlmp.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("hal.dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>Что это за драйвер — для известных.</summary>
    public static string Describe(string name) => Path.GetFileNameWithoutExtension(name).ToLowerInvariant() switch
    {
        "nvlddmkm" => "драйвер видеокарты NVIDIA",
        "atikmdag" or "atikmpag" or "amdkmdag" or "amdkmpfd" or "amdfendr" => "драйвер видеокарты AMD",
        "igdkmd64" or "igdkmdn64" or "igdkmdnd64" => "драйвер видеокарты Intel",
        "dxgkrnl" or "dxgmms2" or "dxgmms1" or "watchdog" => "графическая подсистема Windows — обычно виноват драйвер видеокарты",
        "ntoskrnl" or "ntkrnlmp" => "ядро Windows — само оно редко виновато: чаще память, разгон или другой драйвер",
        "hal" => "слой оборудования Windows — чаще виновато железо",
        "amdppm" or "intelppm" => "управление питанием процессора — часто нестабильный разгон или андервольт",
        "tcpip" or "netio" or "ndis" => "сеть Windows — чаще виноват драйвер сетевой карты или Wi-Fi",
        "ntfs" or "fltmgr" => "файловая система — проверьте диск и антивирус",
        "storport" or "stornvme" or "storahci" or "iastora" or "iastorac" or "iastorav" => "драйвер дисков",
        "usbxhci" or "ucx01000" or "usbhub3" => "USB",
        "portcls" or "ks" or "hdaudbus" => "звук",
        "rt640x64" or "rt68cx21x64" or "rtcx21x64" or "rtux64w10" => "сетевая карта Realtek",
        "e1d68x64" or "e1i68x64" or "e2f68" => "сетевая карта Intel",
        "netwtw10" or "netwtw08" or "netwtw06" or "netwtw04" => "Wi-Fi Intel",
        "rtwlane" or "rtwlane02" or "rtwlanu" => "Wi-Fi Realtek",
        "vgk" => "античит Riot Vanguard",
        "easyanticheat" or "easyanticheat_eos" => "античит Easy Anti-Cheat",
        "bedaisy" => "античит BattlEye",
        "aswsp" or "aswsnx" or "aswvmm" or "aswarpot" => "антивирус Avast",
        "klif" or "kl1" or "klhk" => "антивирус Kaspersky",
        "pawnio" => "драйвер датчиков PawnIO",
        "winring0x64" or "winring0" => "драйвер доступа к железу WinRing0 (программы мониторинга и RGB)",
        _ => "",
    };

    private static DateTime? SafeFileTime(ulong value)
    {
        try
        {
            return DateTime.FromFileTimeUtc((long)value).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static uint U32(byte[] data, int offset) =>
        offset >= 0 && offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) : 0;

    private static ulong U64(byte[] data, int offset) =>
        offset >= 0 && offset + 8 <= data.Length ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset)) : 0;
}
