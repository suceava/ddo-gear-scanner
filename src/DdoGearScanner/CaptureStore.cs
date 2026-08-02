using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DdoGearScanner.Model;
using DdoGearScanner.Vision;

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
            if (ReMatchUnmatched(Loadout)) Save();
        }
        catch { /* start empty rather than crash on a corrupt file */ }
    }

    /// <summary>Re-run the named-item matcher over any UNMATCHED items in a freshly-loaded loadout and
    /// upgrade the ones that now match high-confidence. Self-heals stored loadouts after a catalog update
    /// or a matcher improvement (e.g. the level-scaling items that used to miss) so the corrected catalog
    /// id gets pushed without the user re-hovering every slot. Conservative: only the name / min level /
    /// Matched flag change — captured mods are left untouched (the planner uses catalog mods for a matched
    /// item anyway). Returns true if anything changed.</summary>
    private static bool ReMatchUnmatched(Dictionary<EquipSlot, GearItem> loadout)
    {
        bool changed = false;
        foreach (EquipSlot slot in loadout.Keys.ToList())
        {
            GearItem item = loadout[slot];
            if (item.Matched || string.IsNullOrWhiteSpace(item.Name)) continue;
            ItemMatch? match = NamedItemMatcher.TryMatch(item.Name, slot, item.MinimumLevel);
            if (match is { HighConfidence: true })
            {
                loadout[slot] = item with
                {
                    Name = match.Item.Name,
                    MinimumLevel = match.Item.MinLevel > 0 ? match.Item.MinLevel : item.MinimumLevel,
                    IsLikelyNamed = true,
                    Matched = true,
                };
                changed = true;
            }
        }
        return changed;
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
                if (ReMatchUnmatched(result))
                    File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOpts));
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
