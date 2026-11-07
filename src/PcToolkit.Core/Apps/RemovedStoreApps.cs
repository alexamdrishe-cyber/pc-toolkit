using System.Text.Json;
using PcToolkit.Core.Logging;

namespace PcToolkit.Core.Apps;

/// <summary>Удалённое приложение Microsoft Store — чтобы его можно было вернуть.</summary>
public sealed class RemovedStoreApp
{
    public string Name { get; set; } = "";

    public string PackageName { get; set; } = "";

    public string PackageFullName { get; set; } = "";

    public string PackageFamilyName { get; set; } = "";

    public string InstallLocation { get; set; } = "";

    public DateTimeOffset RemovedAt { get; set; }

    public InstalledApp ToApp() => new()
    {
        Id = "store|" + PackageFullName,
        Kind = AppKind.Store,
        Name = Name,
        PackageName = PackageName,
        PackageFullName = PackageFullName,
        PackageFamilyName = PackageFamilyName,
        InstallLocation = InstallLocation,
    };
}

/// <summary>Список удалённых приложений Store: %LocalAppData%\PcToolkit\removed-store-apps.json.</summary>
public sealed class RemovedStoreApps
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly List<RemovedStoreApp> _items;

    private RemovedStoreApps(string path, List<RemovedStoreApp> items)
    {
        _path = path;
        _items = items;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcToolkit", "removed-store-apps.json");

    public IReadOnlyList<RemovedStoreApp> Items => _items;

    public static RemovedStoreApps Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return new RemovedStoreApps(path, JsonSerializer.Deserialize<List<RemovedStoreApp>>(File.ReadAllText(path)) ?? new());
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось прочитать список удалённых приложений.", ex);
        }

        return new RemovedStoreApps(path, new());
    }

    public void Add(InstalledApp app)
    {
        _items.RemoveAll(i => i.PackageFamilyName == app.PackageFamilyName);
        _items.Insert(0, new RemovedStoreApp
        {
            Name = app.Name,
            PackageName = app.PackageName,
            PackageFullName = app.PackageFullName,
            PackageFamilyName = app.PackageFamilyName,
            InstallLocation = app.InstallLocation,
            RemovedAt = DateTimeOffset.Now,
        });
        Save();
    }

    public void Remove(string packageFamilyName)
    {
        if (_items.RemoveAll(i => i.PackageFamilyName == packageFamilyName) > 0)
            Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_items, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить список удалённых приложений.", ex);
        }
    }
}
