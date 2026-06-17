using DdoGearScanner.Model;

namespace DdoGearScanner.DataImport;

/// <summary>Maps a DDOBuilder &lt;EquipmentSlot&gt; token to our <see cref="EquipSlot"/> enum. Most
/// tokens match our enum names verbatim; the exceptions are the weapons (Weapon1/2 → Main/Off hand)
/// and rings (one "Ring" token means either finger). Cosmetic and internal slots are dropped.</summary>
public static class SlotMapper
{
    public static IReadOnlyList<EquipSlot> Map(string token) => token switch
    {
        "Weapon1" => new[] { EquipSlot.MainHand },
        "Weapon2" => new[] { EquipSlot.OffHand },
        "Ring" => new[] { EquipSlot.Ring1, EquipSlot.Ring2 },
        "Arrows" => new[] { EquipSlot.Quiver },   // ammunition — we have no separate ammo slot
        _ when token.StartsWith("Cosmetic", StringComparison.Ordinal) => Array.Empty<EquipSlot>(),
        _ when Enum.TryParse(token, out EquipSlot s) && s != EquipSlot.Unknown => new[] { s },
        _ => Array.Empty<EquipSlot>(),            // internal-only slots (ArmorCloth, FindItems, …)
    };
}
