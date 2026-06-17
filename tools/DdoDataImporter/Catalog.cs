namespace DdoGearScanner.DataImport;

/// <summary>One bonus to a stat, as imported from a DDOBuilder &lt;Buff&gt;.</summary>
public sealed record CatalogMod(
    string Stat,
    double Value,
    string BonusType,
    string? Description);

/// <summary>One item in the generated catalog. <see cref="Slots"/> are our EquipSlot enum names
/// (an item can fit more than one — a ring fits either finger, a one-hander either hand).</summary>
public sealed record CatalogItem(
    string Name,
    int MinLevel,
    IReadOnlyList<string> Slots,
    string? Type,
    IReadOnlyList<CatalogMod> Mods,
    IReadOnlyList<string> AugmentSlots,
    IReadOnlyList<string> Sets);

/// <summary>A bonus type and how it stacks with itself, from DDOBuilder's BonusTypes.xml.
/// Stacking is the game's authoritative rule ("Highest Only" / "Always" / "Stacking").</summary>
public sealed record BonusTypeDef(string Name, string Stacking);
