using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// Persists the running list of scanned gear items as JSON in %APPDATA%\DdoGearScanner.
/// Adapted from pg-loot-master's GameHistoryStore (same Load/Append/Save + swallow-on-error
/// pattern). This is the "local list of items + mods" the desktop tool produces in v1.
/// </summary>
public sealed class CaptureStore
{
    private static readonly string Dir = AppSettings.AppDataDir;
    private static readonly string StorePath = Path.Combine(Dir, "captures.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public List<GearItem> Items { get; private set; } = new();

    public static CaptureStore Load()
    {
        CaptureStore store = new();
        try
        {
            if (File.Exists(StorePath))
            {
                List<GearItem>? loaded = JsonSerializer.Deserialize<List<GearItem>>(File.ReadAllText(StorePath), JsonOpts);
                if (loaded is not null) store.Items = loaded;
            }
        }
        catch { /* start empty rather than crash on a corrupt file */ }
        return store;
    }

    public void Append(GearItem item)
    {
        Items.Add(item);
        Save();
    }

    public void Remove(GearItem item)
    {
        if (Items.Remove(item)) Save();
    }

    public void Clear()
    {
        Items.Clear();
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Items, JsonOpts));
        }
        catch { /* losing one save beats crashing */ }
    }
}
