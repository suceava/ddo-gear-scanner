using System.Text;
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

        CatalogItem? best = null, second = null;
        double bestScore = 0, secondScore = 0;
        foreach (CatalogItem c in candidates)
        {
            double score = Similarity(target, Normalize(c.Name));
            // Min level agreeing nudges a near-tie toward the right item; never a hard filter (ML is
            // often misread, and many items share a name across heroic/legendary at different MLs).
            if (minLevel is int ml && c.MinLevel > 0 && ml == c.MinLevel) score += 0.03;

            if (score > bestScore) { second = best; secondScore = bestScore; best = c; bestScore = score; }
            else if (score > secondScore) { second = c; secondScore = score; }
        }
        if (best is null) return null;

        double threshold = slot != EquipSlot.Unknown ? HighThreshold : UnknownSlotThreshold;
        bool high = bestScore >= threshold && (second is null || bestScore - secondScore >= Margin);
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
