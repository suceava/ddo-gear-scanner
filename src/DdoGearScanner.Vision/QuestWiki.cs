using System.Text.RegularExpressions;

namespace DdoGearScanner.Vision;

/// <summary>Builds a DDO wiki URL for a quest from its (possibly OCR'd) name. The wiki is a MediaWiki
/// instance where page titles are the quest name with spaces as underscores, so we "slugify and go" —
/// the wiki normalizes/redirects minor mismatches, and the quest page embeds the dungeon map. No quest
/// lookup table yet; if slug misses become common, a name→slug table is the follow-up.</summary>
public static class QuestWiki
{
    public const string Base = "https://ddowiki.com/page/";

    // Small words the wiki leaves lowercase mid-title ("Ruins of Threnal"). First word is always capped.
    private static readonly HashSet<string> MinorWords = new(StringComparer.OrdinalIgnoreCase)
        { "of", "the", "and", "in", "to", "a", "an", "on", "at", "for", "from", "with", "by", "or" };

    /// <summary>Quest name → MediaWiki page slug: normalize casing, whitespace → underscore, then
    /// URL-encode (apostrophes become %27, etc. — the wiki won't accept a raw apostrophe in the path).</summary>
    public static string Slug(string name)
    {
        string s = Regex.Replace(NormalizeCasing(name).Trim(), @"\s+", "_");
        return Uri.EscapeDataString(s);   // '_' is unreserved (kept); "'" → %27, and so on
    }

    public static string Url(string name) => Base + Slug(name);

    // The quest tracker renders titles in ALL CAPS, but wiki pages are Title Case ("The Smuggler's
    // Warehouse"), so title-case an all-caps read. Mixed-case reads are left as-is.
    private static string NormalizeCasing(string name)
    {
        if (name.Any(char.IsLower)) return name;   // already has case → trust it
        string[] words = Regex.Split(name.Trim(), @"\s+");
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i].ToLowerInvariant();
            words[i] = i > 0 && MinorWords.Contains(w) ? w : Capitalize(w);
        }
        return string.Join(" ", words);
    }

    private static string Capitalize(string w) => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..];
}
