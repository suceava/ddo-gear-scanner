using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// Calibrated equipment-slot layout: each slot's cursor offset from the rag-doll anchor (the
/// top-left of the located rag-doll). Built once by hovering each slot during calibration; persists
/// to %APPDATA%. At runtime: anchor + offset = the slot's screen point; the cursor within
/// <see cref="Radius"/> of it means the user is hovering that slot.
/// </summary>
public sealed class SlotMap
{
    private static readonly string Path_ = Path.Combine(AppSettings.AppDataDir, "slotmap.json");
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Slot → (dx,dy) cursor offset from the rag-doll anchor.</summary>
    public Dictionary<EquipSlot, int[]> Offsets { get; set; } = new();

    /// <summary>How close the cursor must be to a slot's calibrated point to count as hovering it.</summary>
    public int Radius { get; set; } = 26;

    [JsonIgnore]
    public bool IsCalibrated => Offsets.Count > 0;

    public void Set(EquipSlot slot, int dx, int dy) => Offsets[slot] = new[] { dx, dy };

    /// <summary>Which equipment slot the cursor is over, given the located rag-doll anchor; null if
    /// none. (cursor and anchor in the same pixel space.)</summary>
    public EquipSlot? SlotAt(int anchorX, int anchorY, int cursorX, int cursorY)
    {
        EquipSlot? best = null;
        double bestD = Radius;
        foreach ((EquipSlot slot, int[] off) in Offsets)
        {
            if (off.Length < 2) continue;
            double dx = cursorX - (anchorX + off[0]);
            double dy = cursorY - (anchorY + off[1]);
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= bestD) { bestD = d; best = slot; }
        }
        return best;
    }

    public static SlotMap Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                SlotMap? m = JsonSerializer.Deserialize<SlotMap>(File.ReadAllText(Path_), JsonOpts);
                if (m is not null) return m;
            }
        }
        catch { }
        return new SlotMap();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.AppDataDir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }
}
