namespace DdoGearScanner.Model;

/// <summary>
/// DDO equipment slots. <see cref="Unknown"/> is used when the tooltip didn't reveal
/// a slot we could map (the user can re-file manually later).
/// </summary>
public enum EquipSlot
{
    Unknown,
    Helmet,
    Goggles,
    Necklace,
    Cloak,
    Belt,
    Ring1,
    Ring2,
    Bracers,
    Gloves,
    Boots,
    Armor,
    Trinket,
    MainHand,
    OffHand,
    Quiver,
}

/// <summary>DDO augment-slot colors.</summary>
public enum AugmentColor
{
    Unknown,
    Colorless,
    Blue,
    Yellow,
    Red,
    Orange,
    Purple,
    Green,
    Moon,
    Sun,
    Globe,
}

/// <summary>
/// A single bonus on an item, e.g. ("Constitution", 15, "Insightful").
/// <paramref name="BonusType"/> defaults to "Enhancement" when the tooltip line carries
/// no explicit type prefix — that's the DDO convention. The bonus type is what makes the
/// web planner's stacking math work (same type doesn't stack, highest wins), so it is a
/// first-class field even though it is the hardest thing to OCR reliably.
/// <paramref name="IsPercent"/> distinguishes a percent mod ("Fortification +117%") from a flat one
/// ("Constitution +10") — the "+N" reads the same number either way, so the unit must be stored.
/// <paramref name="Description"/> is the mod's prose (the text after the affix name) when present —
/// retained because for named effects (e.g. "Gird Against Demons") the description IS the effect.
/// </summary>
public sealed record Mod(
    string Stat, double Value, string BonusType = "Enhancement", bool IsPercent = false, string? Description = null)
{
    /// <summary>Value formatted for display, with a trailing % for percent mods.</summary>
    public string ValueText => Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                               + (IsPercent ? "%" : string.Empty);
}

/// <summary>An augment slot on the item. <see cref="IsEmpty"/> slots are upgrade gaps.</summary>
public sealed record AugmentSlot(AugmentColor Color, string? Filled, bool IsEmpty);

/// <summary>A set-bonus reference parsed off the tooltip. Piece counts are frequently not
/// readable from a single item's tooltip, so they're optional.</summary>
public sealed record SetBonus(string SetName, string? GrantedText = null);

/// <summary>
/// A scanned piece of gear. Handles BOTH named items (recognizable name, the chase items)
/// and random / Cannith-crafted items (no useful name — the value is in <see cref="Mods"/>).
/// <see cref="RawOcrText"/> is always retained so nothing is lost when parsing is imperfect.
/// </summary>
public sealed record GearItem(
    string Name,
    int? MinimumLevel,
    EquipSlot Slot,
    string? ItemTypeText,
    IReadOnlyList<Mod> Mods,
    IReadOnlyList<AugmentSlot> Augments,
    IReadOnlyList<SetBonus> SetBonuses,
    string? Binding,
    bool IsLikelyNamed,
    string RawOcrText,
    DateTime CapturedUtc,
    // User-driven: Locked items are never overwritten by a re-capture (the user toggles this; it is
    // never set automatically). Edited marks an item that was hand-corrected in the item editor.
    bool Locked = false,
    bool Edited = false,
    // Set when a capture's name matched a DDOBuilder catalog item with high confidence and its mods
    // were replaced with the catalog's clean data (RawOcrText is still kept).
    bool Matched = false)
{
    public static GearItem Empty(string rawOcrText) => new(
        Name: string.Empty,
        MinimumLevel: null,
        Slot: EquipSlot.Unknown,
        ItemTypeText: null,
        Mods: Array.Empty<Mod>(),
        Augments: Array.Empty<AugmentSlot>(),
        SetBonuses: Array.Empty<SetBonus>(),
        Binding: null,
        IsLikelyNamed: false,
        RawOcrText: rawOcrText,
        CapturedUtc: DateTime.UtcNow);
}
