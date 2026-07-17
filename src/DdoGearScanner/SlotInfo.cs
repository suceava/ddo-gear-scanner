using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>Display order and in-game ("Equips to:") labels for the equipment slots.</summary>
public static class SlotInfo
{
    /// <summary>Loadout-sheet order — follows the in-game paper-doll clockwise (Eyes → Head → Neck →
    /// Trinket → Cloak → …), so the sheet reads in the same order you hover the slots.</summary>
    public static readonly EquipSlot[] DisplayOrder =
    {
        EquipSlot.Goggles, EquipSlot.Helmet, EquipSlot.Necklace, EquipSlot.Trinket, EquipSlot.Cloak,
        EquipSlot.Armor, EquipSlot.Bracers, EquipSlot.Gloves, EquipSlot.Belt, EquipSlot.Boots,
        EquipSlot.Ring1, EquipSlot.Ring2, EquipSlot.MainHand, EquipSlot.OffHand, EquipSlot.Quiver,
    };

    public static string Label(EquipSlot s) => s switch
    {
        EquipSlot.Helmet => "Head",
        EquipSlot.Goggles => "Eyes",
        EquipSlot.Necklace => "Neck",
        EquipSlot.Cloak => "Cloak",
        EquipSlot.Belt => "Waist",
        EquipSlot.Ring1 => "Ring 1",
        EquipSlot.Ring2 => "Ring 2",
        EquipSlot.Bracers => "Wrists",
        EquipSlot.Gloves => "Hands",
        EquipSlot.Boots => "Feet",
        EquipSlot.Armor => "Armor",
        EquipSlot.Trinket => "Trinket",
        EquipSlot.MainHand => "Main Hand",
        EquipSlot.OffHand => "Off Hand",
        EquipSlot.Quiver => "Quiver",
        _ => s.ToString(),
    };
}
