using DdoGearScanner.Model;

namespace DdoGearScanner.Vision;

/// <summary>One item's contribution to a stat: which slot, how much, what bonus type, and whether it
/// actually COUNTS (a same-type non-stacking bonus elsewhere with a higher value overrides it).</summary>
public sealed record MatrixCell(
    EquipSlot Slot, double Value, string BonusType, bool IsPercent, bool Counts)
{
    public bool Overridden => !Counts;
}

/// <summary>One (stat, bonus type) stack across the whole loadout — e.g. "Constitution · Enhancement".
/// Keyed by BOTH stat and type so overlaps within a type are visible (two Competence bonuses to the
/// same stat land in the same row, and the loser is struck out). <see cref="Effective"/> is the
/// max for a non-stacking type, the sum for a self-stacking type.</summary>
public sealed record MatrixRow(
    string Stat, string BonusType, StatCategory Category, char? Priority, bool IsPercent, double Effective,
    bool HasOverride, IReadOnlyList<MatrixCell> Cells);

/// <summary>A weapon/armor enchantment or proc that only affects its own item — shown under the item,
/// not in the cross-slot stacking grid.</summary>
public sealed record ItemLocalEffect(
    EquipSlot Slot, string Stat, double Value, bool IsPercent, string BonusType, string? Description);

/// <summary>The stacking "puzzle": character-wide stats (rows, grouped by category) × the slots that
/// contribute, with overlap/override resolved by the bonus-type rules; plus the item-local effects.</summary>
public sealed record StackingMatrix(
    IReadOnlyList<EquipSlot> Slots, IReadOnlyList<MatrixRow> Rows, IReadOnlyList<ItemLocalEffect> ItemLocal);

/// <summary>
/// Builds the stacking matrix from a captured loadout. Mods are first split by
/// <see cref="StatCatalog"/> into character-wide (cross-slot stacking) and item-local (per-item).
/// For each character-wide stat the contributions are grouped by bonus type and resolved per
/// <see cref="BonusTypes.StacksWithSelf"/>: self-stacking types add every instance; the rest keep
/// only the highest (others flagged overridden = wasted). Rows are grouped/sorted by category.
/// Pure and unit-tested — no UI, no game.
/// </summary>
public static class StackingAnalyzer
{
    public static StackingMatrix Analyze(
        IReadOnlyDictionary<EquipSlot, GearItem> loadout, string? playstyleKey = null)
    {
        var all = new List<(EquipSlot Slot, Mod Mod)>();
        foreach ((EquipSlot slot, GearItem item) in loadout)
            foreach (Mod mod in item.Mods)
                all.Add((slot, mod));

        // Item-local: weapon/armor enchants, procs, and valueless named effects.
        var itemLocal = all
            .Where(e => e.Mod.Value == 0 || StatCatalog.IsItemLocal(e.Mod))
            .Select(e => new ItemLocalEffect(e.Slot, e.Mod.Stat, e.Mod.Value, e.Mod.IsPercent, e.Mod.BonusType, e.Mod.Description))
            .OrderBy(x => (int)x.Slot).ThenBy(x => x.Stat, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var character = all.Where(e => e.Mod.Value != 0 && !StatCatalog.IsItemLocal(e.Mod)).ToList();
        IReadOnlyList<EquipSlot> slots = character.Select(e => e.Slot).Distinct().OrderBy(s => (int)s).ToList();

        // One row per (stat, bonus type) so overlaps WITHIN a type are visible. Within a row: a
        // self-stacking type counts every cell (Effective = sum); otherwise only the highest counts
        // and the rest are struck out (Effective = max).
        var rows = new List<MatrixRow>();
        foreach (var grp in character.GroupBy(
                     e => (Stat: e.Mod.Stat.Trim(), Type: e.Mod.BonusType.Trim())))
        {
            var items = grp.ToList();
            bool stacks = BonusTypes.StacksWithSelf(grp.Key.Type);
            double max = items.Max(e => e.Mod.Value);

            var cells = items
                .Select(e => new MatrixCell(e.Slot, e.Mod.Value, e.Mod.BonusType, e.Mod.IsPercent,
                    Counts: stacks || e.Mod.Value >= max))
                .OrderBy(c => (int)c.Slot)
                .ToList();
            // For a non-stacking type with ties at the max, keep only ONE as counting.
            if (!stacks)
            {
                bool kept = false;
                cells = cells.Select(c =>
                {
                    if (!c.Counts) return c;
                    if (kept) return c with { Counts = false };
                    kept = true;
                    return c;
                }).ToList();
            }

            double effective = cells.Where(c => c.Counts).Sum(c => c.Value);
            bool hasOverride = cells.Any(c => c.Overridden);
            bool isPercent = cells.Count(c => c.IsPercent) * 2 >= cells.Count;
            string stat = grp.Key.Stat;
            rows.Add(new MatrixRow(stat, grp.Key.Type, StatCatalog.Categorize(stat),
                GearPriorities.RankOf(stat, playstyleKey), isPercent, effective, hasOverride, cells));
        }

        // Group by PRIORITY tier (Strimtom A>B>C, unranked last); within a tier cluster by category
        // (abilities in canonical order), then stat name, then bonus type.
        rows = rows
            .OrderBy(r => PriorityOrder(r.Priority))
            .ThenBy(r => (int)r.Category)
            .ThenBy(r => StatCatalog.OrderInCategory(r.Stat))
            .ThenBy(r => r.Stat, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.BonusType, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StackingMatrix(slots, rows, itemLocal);
    }

    // A=0, B=1, C=2, unranked last.
    private static int PriorityOrder(char? rank) => rank switch { 'A' => 0, 'B' => 1, 'C' => 2, _ => 3 };
}
