using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// The character's equipped loadout: one item per equipment slot. Re-capturing a slot overwrites
/// it (it's not a new entry). Persists to %APPDATA%\DdoGearScanner\loadout.json.
/// </summary>
public sealed class CaptureStore
{
    private static readonly string Dir = AppSettings.AppDataDir;
    private static readonly string StorePath = Path.Combine(Dir, "loadout.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Dictionary<EquipSlot, GearItem> Loadout { get; private set; } = new();

    public static CaptureStore Load()
    {
        CaptureStore store = new();
        try
        {
            if (File.Exists(StorePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<EquipSlot, GearItem>>(File.ReadAllText(StorePath), JsonOpts);
                if (loaded is not null) store.Loadout = loaded;
            }
        }
        catch { /* start empty rather than crash on a corrupt file */ }
        return store;
    }

    /// <summary>Set (overwrite) the item in a slot.</summary>
    public void SetSlot(EquipSlot slot, GearItem item)
    {
        Loadout[slot] = item;
        Save();
    }

    public GearItem? Get(EquipSlot slot) => Loadout.TryGetValue(slot, out GearItem? i) ? i : null;

    public void Clear()
    {
        Loadout.Clear();
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Loadout, JsonOpts));
        }
        catch { /* losing one save beats crashing */ }
    }
}
