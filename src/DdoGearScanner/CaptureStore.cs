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

    /// <summary>Set (overwrite) the item in a slot.</summary>
    public void SetSlot(EquipSlot slot, GearItem item)
    {
        Loadout[slot] = item;
        Save();
    }

    public GearItem? Get(EquipSlot slot) => Loadout.TryGetValue(slot, out GearItem? i) ? i : null;

    /// <summary>True if the slot holds a user-locked item (re-capture must not overwrite it).</summary>
    public bool IsLocked(EquipSlot slot) => Get(slot)?.Locked == true;

    public void Remove(EquipSlot slot)
    {
        if (Loadout.Remove(slot)) Save();
    }

    public void Clear()
    {
        Loadout.Clear();
        Save();
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
