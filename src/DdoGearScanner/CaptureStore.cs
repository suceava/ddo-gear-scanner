using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// The active character's equipped loadout: one item per equipment slot. Re-capturing a slot
/// overwrites it. Gear is stored PER CHARACTER in %APPDATA%\DdoGearScanner\loadout-&lt;id&gt;.json;
/// <see cref="SwitchTo"/> swaps the active character (called when the user changes the selection).
/// </summary>
public sealed class CaptureStore
{
    private static readonly string Dir = AppSettings.AppDataDir;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string CharacterId { get; private set; } = "";
    public Dictionary<EquipSlot, GearItem> Loadout { get; private set; } = new();

    /// <summary>Fires when the active character's gear CHANGES (set/remove/clear) — not on a plain switch/load.
    /// Drives the cloud loadout push (LoadoutSyncService).</summary>
    public event Action? Changed;

    private string StorePath => Path.Combine(Dir, $"loadout-{CharacterId}.json");

    /// <summary>Load the given character's loadout, replacing the current one.</summary>
    public void SwitchTo(string characterId)
    {
        CharacterId = characterId;
        Loadout = new();
        try
        {
            if (File.Exists(StorePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<EquipSlot, GearItem>>(File.ReadAllText(StorePath), JsonOpts);
                if (loaded is not null) Loadout = loaded;
            }
        }
        catch { /* start empty rather than crash on a corrupt file */ }
    }

    /// <summary>Read any character's saved loadout WITHOUT changing the active one (for pushing every
    /// character's loadout to the cloud). Empty dict if none / unreadable.</summary>
    public static Dictionary<EquipSlot, GearItem> ReadLoadout(string characterId)
    {
        var result = new Dictionary<EquipSlot, GearItem>();
        if (string.IsNullOrEmpty(characterId)) return result;
        try
        {
            string path = Path.Combine(Dir, $"loadout-{characterId}.json");
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<EquipSlot, GearItem>>(File.ReadAllText(path), JsonOpts);
                if (loaded is not null) result = loaded;
            }
        }
        catch { /* unreadable → empty */ }
        return result;
    }

    /// <summary>Set (overwrite) the item in a slot.</summary>
    public void SetSlot(EquipSlot slot, GearItem item)
    {
        Loadout[slot] = item;
        Save();
        Changed?.Invoke();
    }

    public GearItem? Get(EquipSlot slot) => Loadout.TryGetValue(slot, out GearItem? i) ? i : null;

    /// <summary>True if the slot holds a user-locked item (re-capture must not overwrite it).</summary>
    public bool IsLocked(EquipSlot slot) => Get(slot)?.Locked == true;

    public void Remove(EquipSlot slot)
    {
        if (Loadout.Remove(slot))
        {
            Save();
            Changed?.Invoke();
        }
    }

    public void Clear()
    {
        Loadout.Clear();
        Save();
        Changed?.Invoke();
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(CharacterId)) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Loadout, JsonOpts));
        }
        catch { /* losing one save beats crashing */ }
    }
}
