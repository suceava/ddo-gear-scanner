using System.Text;
using System.Text.RegularExpressions;
using DdoGearScanner.Model;

namespace DdoGearScanner.Vision;

/// <summary>The best catalog match for a captured name. <see cref="HighConfidence"/> is the bar for
/// auto-applying the catalog's clean mods over the OCR'd ones.</summary>
public sealed record ItemMatch(CatalogItem Item, double Score, bool HighConfidence);

/// <summary>
/// Matches an OCR'd item NAME against the DDOBuilder <see cref="ItemCatalog"/>. The name is far easier
/// to read correctly than a dozen mod lines, so for named items a confident name match lets us swap in
/// the catalog's clean Stat/Value/BonusType data and skip OCR mod-parsing entirely. Random/crafted
/// items won't match (no catalog entry) and keep their OCR'd mods.
///
/// Matching only competes a name against items that fit the captured slot, with min level as a
/// tiebreaker, then a normalized edit-distance similarity with a confidence threshold + a margin over
/// the runner-up (so an ambiguous near-tie is never auto-applied).
/// </summary>
public static class NamedItemMatcher
{
    public const double HighThreshold = 0.86;          // slot known
    public const double UnknownSlotThreshold = 0.93;   // no slot to narrow on → demand a closer match
    public const double Margin = 0.04;                 // best must beat runner-up by this

    public static ItemMatch? TryMatch(string? ocrName, EquipSlot slot, int? minLevel)
    {
        if (string.IsNullOrWhiteSpace(ocrName)) return null;
        string target = Normalize(ocrName);
        if (target.Length < 4) return null;            // too short to match reliably

        IReadOnlyList<CatalogItem> candidates = slot != EquipSlot.Unknown ? ItemCatalog.ForSlot(slot) : ItemCatalog.All;
        if (candidates.Count == 0) return null;

        CatalogItem? best = null;
        double bestScore = 0;
        string bestBase = "";
        // Runner-up for the confidence margin: the best DIFFERENTLY-named candidate. Level variants of
        // one item ("Whisperchain (Level 16/17/18…)") share a base name and are disambiguated by ML —
        // they're the same item, not competing alternatives, so a sibling must never sink confidence.
        double runnerUpScore = 0;
        foreach (CatalogItem c in candidates)
        {
            string cBase = Normalize(StripLevel(c.Name));
            double score = Similarity(target, cBase);
            // Min level agreeing nudges toward the right variant — decisive among same-name variants,
            // a gentle closeness tiebreaker otherwise; never a hard filter (ML is often misread, and many
            // items share a name across heroic/legendary at different MLs).
            if (minLevel is int ml && c.MinLevel > 0)
                score += ml == c.MinLevel ? 0.03 : 0.015 / (1 + Math.Abs(ml - c.MinLevel));

            if (score > bestScore)
            {
                // the outgoing best becomes a margin runner-up only if it names a DIFFERENT item
                if (best is not null && bestBase != cBase && bestScore > runnerUpScore) runnerUpScore = bestScore;
                best = c; bestScore = score; bestBase = cBase;
            }
            else if (cBase != bestBase && score > runnerUpScore)
            {
                runnerUpScore = score;
            }
        }
        if (best is null) return null;

        double threshold = slot != EquipSlot.Unknown ? HighThreshold : UnknownSlotThreshold;
        bool high = bestScore >= threshold && bestScore - runnerUpScore >= Margin;
        return new ItemMatch(best, Math.Min(bestScore, 1.0), high);
    }

    /// <summary>Replace a capture's parsed fields with the matched catalog item's clean data, keeping
    /// the captured slot and the original RawOcrText. Marks <see cref="GearItem.Matched"/>.</summary>
    public static GearItem Apply(GearItem ocr, CatalogItem match) => ocr with
    {
        Name = match.Name,
        MinimumLevel = match.MinLevel > 0 ? match.MinLevel : ocr.MinimumLevel,
        ItemTypeText = match.Type ?? ocr.ItemTypeText,
        Mods = match.Mods.Select(m => new Mod(m.Stat, m.Value, m.BonusType, false, m.Description)).ToList(),
        Augments = match.AugmentSlots.Select(a => new AugmentSlot(ParseColor(a), null, true)).ToList(),
        SetBonuses = match.Sets.Select(s => new SetBonus(s)).ToList(),
        IsLikelyNamed = true,
        Matched = true,
    };

    private static AugmentColor ParseColor(string token)
        => Enum.TryParse(token, ignoreCase: true, out AugmentColor color) ? color : AugmentColor.Unknown;

    private static readonly Regex LevelSuffix =
        new(@"\s*\(Level \d+\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Drop a trailing "(Level N)". DDOBuilder names each ML variant of a scaling item
    /// "Whisperchain (Level 18)", but the game (and OCR) shows just "Whisperchain" — so compare on the
    /// base name and let min level pick the variant.</summary>
    internal static string StripLevel(string s) => LevelSuffix.Replace(s, "");

    /// <summary>Lowercase, strip non-alphanumerics to spaces, collapse runs. Makes OCR punctuation
    /// noise ("Admiral's"/"Admirals", stray dots) irrelevant to the comparison.</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastSpace = false;
        foreach (char ch in s)
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); lastSpace = false; }
            else if (!lastSpace) { sb.Append(' '); lastSpace = true; }
        }
        return sb.ToString().Trim();
    }

    /// <summary>1.0 = identical, 0 = nothing alike — normalized Levenshtein over the longer string.</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        int max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        return 1.0 - (double)Levenshtein(a, b) / max;
    }

    private static int Levenshtein(string a, string b)
    {
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
