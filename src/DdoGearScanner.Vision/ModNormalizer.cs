using DdoGearScanner.Model;

namespace DdoGearScanner.Vision;

/// <summary>
/// Splits a bonus-type word baked into a mod's STAT into the separate BonusType field, so captured mods
/// match the catalog's split model (Stat + BonusType). Tooltips read "Insightful Strength +2"; local OCR
/// already splits the prefix, but the LLM path (and older stored data) returns the whole phrase as the
/// stat ("Insightful Strength"). Left unsplit, the same affix groups differently in the two apps'
/// stacking matrices (web reads split catalog data; the scanner read the combined capture).
///
/// Safe rule: split only when (a) the leading word is a known <see cref="BonusTypes"/> and (b) the
/// remainder is a real <see cref="ItemCatalog"/> stat. Requiring the remainder to exist in the catalog
/// both prevents bad splits ("Insight Bonus to Armor Class" stays whole) and self-consistently mirrors
/// the catalog's own (not-always-split) representation — e.g. if the catalog keeps a phrase combined, we
/// leave it combined too.
/// </summary>
public static class ModNormalizer
{
    // Longest-first so "Insightful" wins over "Insight" for "Insightful Strength" (the trailing-space
    // guard already prevents "Insight" from matching, but ordering makes the intent explicit).
    private static readonly IReadOnlyList<string> Prefixes =
        BonusTypes.All.OrderByDescending(t => t.Length).ToList();

    /// <summary>Return the mod with any leading bonus-type word moved from Stat to BonusType; unchanged
    /// if the stat is already a catalog stat or has no splittable prefix.</summary>
    public static Mod Normalize(Mod mod)
    {
        string s = mod.Stat.Trim();
        if (s.Length == 0 || ItemCatalog.IsKnownStat(s)) return mod;   // already canonical / unknown-but-not-prefixed

        foreach (string type in Prefixes)
        {
            if (s.Length <= type.Length + 1) continue;
            if (!s.StartsWith(type + " ", StringComparison.OrdinalIgnoreCase)) continue;
            string remainder = s[(type.Length + 1)..].Trim();
            if (ItemCatalog.IsKnownStat(remainder))
                return mod with { Stat = remainder, BonusType = BonusTypes.Canonical(type) };
        }
        return mod;
    }

    /// <summary>Normalize every mod in a list (returns a new list; identity-stable if nothing changed).</summary>
    public static List<Mod> NormalizeAll(IEnumerable<Mod> mods) => mods.Select(Normalize).ToList();
}
