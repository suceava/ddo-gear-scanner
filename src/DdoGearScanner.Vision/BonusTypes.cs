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
    // The DDO bonus-type vocabulary. The canonical set is ddowiki Category:Bonus types (see
    // GAME_RULES.md, which also records each type's STACKING behavior — the planner's math depends
    // on it). Deflection / Natural Armor / Dodge are AC bonus types catalogued separately on the
    // wiki but still appear on items, so they're kept here.
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
        "Mythic",
        "Luck",
        "Resistance",
        "Primal",
        "Equipment",
        "Alchemical",
        "Legendary",
        "Stacking",
        "Circumstance",
        "Determination",
        "Divine",
        "Epic",
        "Feat",
        "Fortune",
        "Guild",
        "Implement",
        "Inherent",
        "Music",
        "Psionic",
        "Rage",
        "Reaper",
        "Size",
        "Style",
        // AC bonus types (catalogued under Armor Class on the wiki, but they tag items):
        "Deflection",
        "Natural Armor",
        "Armor",
        "Shield",
        "Dodge",
    };

    /// <summary>Curated, deduplicated list for the item-editor dropdown — the general bonus types a
    /// user would assign to a stat mod. Excludes parser-only OCR aliases (e.g. "Insight" for
    /// "Insightful"), the rarely-on-gear category entries, and the AC-only bonus types (Armor,
    /// Shield, Dodge, Deflection, Natural Armor) which only make sense on Armor Class — they'd be
    /// nonsense on, say, a Constitution mod. (Any type already on an item that isn't here is still
    /// shown — the editor unions it in.)</summary>
    public static readonly IReadOnlyList<string> UserSelectable = new[]
    {
        "Enhancement",
        "Insightful",
        "Quality",
        "Exceptional",
        "Profane",
        "Sacred",
        "Competence",
        "Artifact",
        "Festive",
        "Morale",
        "Luck",
        "Resistance",
        "Primal",
        "Alchemical",
        "Equipment",
        "Inherent",
        "Mythic",
        "Reaper",
        "Legendary",
        "Stacking",
    };

    private static readonly HashSet<string> Lookup =
        new(All, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string word) => Lookup.Contains(word.Trim());

    // Types that stack with THEMSELVES (every instance adds) — see GAME_RULES.md. Everything else is
    // "same type doesn't stack, highest applies". Mythic stacks per gear slot, so across a loadout it
    // effectively adds; Stacking/Untyped always add.
    private static readonly HashSet<string> SelfStacking = new(StringComparer.OrdinalIgnoreCase)
    {
        "Artifact", "Primal", "Circumstance", "Feat", "Reaper", "Epic", "Mythic", "Stacking", "Untyped",
    };

    /// <summary>True if multiple bonuses of this type all add together; false if only the highest of
    /// the type applies (the default). The basis for the loadout overlap/override matrix.</summary>
    public static bool StacksWithSelf(string type) => SelfStacking.Contains(type.Trim());

    /// <summary>Returns the canonically-cased vocabulary entry (e.g. "resistance" -> "Resistance"),
    /// or the trimmed input unchanged if it isn't a known type.</summary>
    public static string Canonical(string word)
    {
        string w = word.Trim();
        foreach (string t in All)
            if (t.Equals(w, StringComparison.OrdinalIgnoreCase)) return t;
        return w;
    }
}
