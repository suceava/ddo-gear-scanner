namespace DdoGearScanner.Vision;

/// <summary>
/// The known DDO bonus-type vocabulary. Most affix lines read like
/// "[Type] [Stat] +N" (e.g. "Insightful Constitution +3"); a leading word from this list
/// is the bonus type, and a line with no recognized prefix defaults to "Enhancement".
/// Having a fixed vocabulary is what makes local-OCR mod parsing tractable.
///
/// This list is the living reference for bonus types — keep it in sync with TOOLTIP_FORMAT.md.
/// </summary>
public static class BonusTypes
{
    public const string Default = "Enhancement";

    // Ordered longest-first isn't required here; matching is exact word(s) at line start.
    // Multi-word types (e.g. "Insightful Sheltering" is Stat=Sheltering Type=Insightful, but
    // "Quality" / "Insightful" are the type tokens) are handled by single-token prefix matching.
    public static readonly IReadOnlyList<string> All = new[]
    {
        "Enhancement",
        "Insightful",
        "Insight",        // some tooltips render "Insight" rather than "Insightful"
        "Quality",
        "Competence",
        "Exceptional",
        "Profane",
        "Sacred",
        "Artifact",
        "Festive",
        "Morale",
        "Luck",
        "Resistance",
        "Deflection",
        "Natural Armor",
        "Armor",
        "Shield",
        "Dodge",
        "Primal",
        "Equipment",
        "Alchemical",
        "Legendary",
        "Stacking",
    };

    private static readonly HashSet<string> Lookup =
        new(All, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string word) => Lookup.Contains(word.Trim());
}
