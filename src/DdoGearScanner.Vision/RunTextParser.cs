using System.Globalization;
using System.Text.RegularExpressions;
using OpenCvSharp;

namespace DdoGearScanner.Vision;

/// <summary>The clean-font datapoints from DDO's adventure-entry popup (shown before entering a quest).
/// The quest NAME here reads reliably, unlike the ornate quest-tracker title — so this is the
/// authoritative name source. Captured before entry and stamped onto the run when it starts.</summary>
public sealed record QuestEntry(string Name, int? QuestLevel, string? Difficulty = null);

/// <summary>Character datapoints OCR'd from the avatar region: the name (shown above the health bar) and
/// the level (shown under the avatar). Both best-effort.</summary>
public sealed record CharacterInfo(string? Name, int? Level);

/// <summary>
/// Pure parsing for the run tracker: OCR text/boxes → the quest-entry popup's name+level, the quest
/// tracker's name, and its completion state. Free of OpenCV capture so it can be unit-tested against
/// fixtures. Geometry-based (uses OCR box positions) so it reads only the calibrated panels.
/// </summary>
public static class RunTextParser
{
    // Ordered most-specific first so "Reaper"/"Elite" win over a stray "Normal" elsewhere in the text.
    private static readonly string[] DifficultyWords = { "Reaper", "Elite", "Hard", "Normal", "Casual", "Solo" };

    /// <summary>
    /// Extract the quest from DDO's adventure-entry popup using the OCR word POSITIONS — not a flattened
    /// text soup. The popup is located by its landmarks ("Select Difficulty" heading + the "Enter"/
    /// "Cancel" buttons), which give its on-screen rectangle. Only text INSIDE that rectangle is
    /// considered, so chat, the avatar/health bars, and the quest's flavor/region text (all elsewhere on
    /// screen) are excluded by geometry rather than guessed away. The quest name is the name-like line
    /// sitting directly above the difficulty heading, within the panel's horizontal span. Level is
    /// optional + OCR-tolerant ("10" often reads as "IO").
    /// </summary>
    public static QuestEntry? ParseEntry(IReadOnlyList<OcrLine>? lines)
    {
        if (lines is null || lines.Count == 0) return null;

        // Landmarks. The heading anchors the top of the difficulty section; Enter/Cancel bound the panel
        // width along the bottom.
        OcrLine? heading = lines.FirstOrDefault(l => l.Text.Contains("Difficult", StringComparison.OrdinalIgnoreCase));
        OcrLine? enter = lines.FirstOrDefault(l => Regex.IsMatch(l.Text, @"\bEnter\b", RegexOptions.IgnoreCase));
        OcrLine? cancel = lines.FirstOrDefault(l => Regex.IsMatch(l.Text, @"\bCancel\b", RegexOptions.IgnoreCase));
        if (heading is null || (enter is null && cancel is null &&
            !heading.Text.Contains("Select Difficult", StringComparison.OrdinalIgnoreCase)))
            return null;   // not the entry popup

        Rect h = heading.Bbox;
        // The region is the calibrated popup, so everything in it is popup content — the quest name is
        // simply the name-like line directly ABOVE the "Select Difficulty" heading (that Y split separates
        // the name/level from the difficulty icons + Enter/Cancel below). No X-band needed.
        bool AboveHeading(Rect b) => b.Bottom <= h.Top + 4;   // small slack for OCR box jitter

        OcrLine? best = null;
        for (int i = 0; i < lines.Count; i++)
        {
            OcrLine line = lines[i];
            if (!AboveHeading(line.Bbox)) continue;
            if (Regex.IsMatch(line.Text, @"\d\s*/")) continue;                                         // HP/SP fragment itself
            if (i + 1 < lines.Count && Regex.IsMatch(lines[i + 1].Text.Trim(), @"^\d+\s*/")) continue;  // char name (HP on next line)
            if (IsEntryNoise(line.Text)) continue;
            string cand = CleanName(line.Text);
            if (!IsNameLike(cand) || LooksLikeObjective(cand)) continue;
            if (cand.Count(char.IsLetter) < 4 || StopWords.Contains(cand.Trim())) continue;
            if (best is null || line.Bbox.Bottom > best.Bbox.Bottom) best = line;   // closest above heading
        }
        if (best is null) return null;

        // Optional level from a line above the heading (avoids the character's "Level 15" readout below).
        int? questLevel = null;
        foreach (OcrLine line in lines)
        {
            if (!AboveHeading(line.Bbox)) continue;
            Match m = Regex.Match(line.Text, @"Level\s*[:.]\s*([0-9IlOo]{1,3})", RegexOptions.IgnoreCase);
            if (m.Success && TryOcrLevel(m.Groups[1].Value, out int lv)) { questLevel = lv; break; }
        }

        return new QuestEntry(CleanName(best.Text), questLevel);
    }

    /// <summary>Parse the avatar region: character NAME (the name-like line, above the health bar) and
    /// LEVEL (a "Level N" or a bare 1–2 digit number under the avatar). HP/SP fractions ("500/500") and
    /// pure numbers are skipped for the name. Player names are user-chosen, so we do NOT apply the quest
    /// i↔l correction to them — just tidy whitespace. First-draft; tune against a real avatar crop.</summary>
    public static CharacterInfo ParseCharacter(IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0) return new CharacterInfo(null, null);
        string? name = null;
        int? level = null;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            Match lm = Regex.Match(line, @"level\s*[:.]?\s*([0-9IlOo]{1,2})\b", RegexOptions.IgnoreCase);
            if (lm.Success && TryOcrLevel(lm.Groups[1].Value, out int lv)) level ??= lv;
            else if (level is null && Regex.IsMatch(line, @"^[0-9IlOo]{1,2}$") && TryOcrLevel(line, out int lv2)) level = lv2;

            if (name is null && !Regex.IsMatch(line, @"\d\s*/\s*\d"))   // not an HP/SP fraction
            {
                string cand = Regex.Replace(line.Replace('_', ' '), @"\s+", " ").Trim(' ', '-', ':', '.', ',');
                if (cand.Length >= 2 && !Regex.IsMatch(cand, @"^\d+$")
                    && cand.Count(char.IsLetter) >= 2 && cand.Count(char.IsLetter) >= cand.Length / 2)
                    name = cand;
            }
        }
        return new CharacterInfo(name, level);
    }

    // Lone articles/prepositions that show up as OCR fragments (esp. from chat) — never a quest name.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "of", "and", "to", "in", "on", "or", "for", "with", "by", "house", "road" };

    private static bool TryOcrLevel(string token, out int level)
    {
        string norm = token.Replace('I', '1').Replace('l', '1').Replace('O', '0').Replace('o', '0');
        return int.TryParse(norm, out level) && level is >= 1 and <= 40;
    }

    // Popup labels, difficulty/button words, and chat that show up in the full-window sweep but aren't
    // the quest name.
    private static bool IsEntryNoise(string line)
    {
        string t = line.Trim().ToLowerInvariant();
        if (t.Length == 0 || t.StartsWith("(")) return true;                 // blank / chat channel
        if (t is "enter" or "cancel" or "solo" or "normal" or "hard" or "elite" or "reaper" or "heroic" or "legendary") return true;
        if (Regex.IsMatch(t, @"^level\s*[:.]")) return true;                 // "Level: 2"
        return t.Contains("difficult") || t.Contains("duration") || t.Contains("ration")   // ration = "mratlon" garble
            || t.Contains("grouping") || t.Contains("dungeons completed") || t.Contains("quest bestowed")
            || t.Contains("you may") || t.Contains("you cannot") || t.Contains("reenter")
            || t.Contains("channel") || t.Contains("joining");   // chat: "Joining channel: The Mists…"
    }

    /// <summary>True if the chat log shows "Adventure Completed" — DDO's definitive, clean-font, persistent
    /// completion message (unlike the quest-tracker's ornate title / brief "Status: Completed" flash).</summary>
    public static bool IsAdventureCompleted(string? chatText)
        => !string.IsNullOrWhiteSpace(chatText) && Regex.IsMatch(chatText, @"adventure\s+complete", RegexOptions.IgnoreCase);

    /// <summary>Best-effort quest XP from the chat's completion lines — "You receive N XP / experience".
    /// Takes the LAST such value (the quest total shows after the per-objective ones). Editable downstream;
    /// DDO XP in chat isn't authoritative (optionals/over-level muddy it).</summary>
    public static int? ExtractChatXp(IEnumerable<string> lines)
    {
        int? xp = null;
        foreach (string line in lines)
        {
            Match m = Regex.Match(line, @"receive[^\d]{0,15}([\d,]+)\s*(?:xp|experience)", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                xp = v;
        }
        return xp;
    }

    /// <summary>True if the quest-tracker panel shows the quest finished — its "Status: Completed" line.
    /// The OCR frequently DROPS the "Status:" and reads just "Completed", so we match any line whose only
    /// significant word (ignoring "status") starts with "complet". That fires on a standalone "Completed"
    /// yet still rejects an OBJECTIVE line like "Slay the Demon Razagnol (Completed)" — which carries other
    /// words — so an objective ticking complete can't be mistaken for the quest finishing.</summary>
    public static bool IsTrackerCompleted(IEnumerable<string>? lines)
    {
        if (lines is null) return false;
        foreach (string line in lines)
        {
            string cleaned = Regex.Replace(line.ToLowerInvariant(), @"[^a-z ]+", " ");
            var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                               .Where(w => w.Length > 1 && w != "status")
                               .ToList();
            if (words.Count == 1 && words[0].StartsWith("complet", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Verbs that begin an objective line ("Talk to…", "Defeat the…") but essentially never begin a DDO
    // quest title (which are noun phrases). Used to skip objective lines when picking the title.
    private static readonly Regex ObjectiveVerb = new(
        @"^(talk|speak|defeat|escort|rescue|disable|retrieve|deliver|investigate|activate|sabotage|eliminate|assassinate)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Clean a candidate quest name off the quest-tracker panel: the title sits above the
    /// objectives, so take the first name-like line that ISN'T an objective. Skipping objective lines
    /// stops a zone-load flicker (title not yet drawn, objective is) from being logged as a bogus run.
    /// Returns null if nothing qualifies.</summary>
    public static string? CleanTrackerName(IEnumerable<string> lines)
    {
        foreach (string raw in lines)
        {
            string name = CleanName(raw);
            if (IsNameLike(name) && !LooksLikeObjective(name)) return name;
        }
        return null;
    }

    // An objective line, not a title: starts lowercase (a title is capitalized — a lowercase start is an
    // OCR fragment like "to captain in the"), carries a "(0/3)" progress counter, or opens with an
    // objective verb.
    private static bool LooksLikeObjective(string s)
    {
        if (s.Length == 0) return true;
        if (char.IsLower(s[0])) return true;
        if (Regex.IsMatch(s, @"\d+\s*/\s*\d+")) return true;
        return ObjectiveVerb.IsMatch(s);
    }

    private static string CleanName(string s)
    {
        s = s.Trim();
        // Windows OCR renders the gap in DDO's ornate gold title font as an underscore ("The_Harbor"),
        // so restore it to a space before anything else.
        s = s.Replace('_', ' ');
        // Drop a leading "Quest:"/"Quest -" the pattern may have left, collapse whitespace, strip
        // trailing punctuation and a trailing difficulty word ("The Pit Elite" -> "The Pit").
        s = Regex.Replace(s, @"^\s*quest\s*[:\-]\s*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = s.Trim('-', ':', '.', '!', ',', '"', '\'', '(', ')', ' ');
        foreach (string d in DifficultyWords)
            s = Regex.Replace(s, $@"\s+{d}$", "", RegexOptions.IgnoreCase);
        return FixMisreadI(s).Trim();
    }

    private const string Vowels = "aeiouAEIOU";
    private static bool IsVowel(char c) => Vowels.IndexOf(c) >= 0;

    /// <summary>Windows OCR reads DDO's ornate title-font 'i' as a lowercase 'l' ("High"→"Hlgh",
    /// "Riddle"→"Rlddle"). Correct an 'l' to 'i' ONLY where it can't be a real 'l': an onset 'l' (no vowel
    /// yet in the word) that isn't sitting before a vowel — such a cluster is unpronounceable, so it's a
    /// misread 'i'. This leaves genuine post-vowel 'l's alone ("World", "Hall", "Slave").</summary>
    private static string FixMisreadI(string s)
    {
        char[] a = s.ToCharArray();
        bool vowelSeen = false;   // has the current word had a vowel yet?
        bool wordStart = true;
        for (int i = 0; i < a.Length; i++)
        {
            char c = a[i];
            if (!char.IsLetter(c)) { vowelSeen = false; wordStart = true; continue; }

            if (c == 'l' && !vowelSeen)
            {
                char next = i + 1 < a.Length ? a[i + 1] : ' ';
                bool nextVowel = char.IsLetter(next) && IsVowel(next);
                if (!nextVowel)                       // onset 'l', not before a vowel ⇒ misread 'i'
                {
                    a[i] = wordStart ? 'I' : 'i';
                    vowelSeen = true;
                    wordStart = false;
                    continue;
                }
            }

            if (IsVowel(c)) vowelSeen = true;
            wordStart = false;
        }
        return new string(a);
    }

    // A plausible quest name: has letters, a couple of them, and isn't just an objective/number line.
    private static bool IsNameLike(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length is < 3 or > 60) return false;
        int letters = s.Count(char.IsLetter);
        if (letters < 3) return false;
        // Objective lines are often "(0/3)", percentages, or timers — reject mostly-non-letter text.
        return letters >= s.Length / 2;
    }
}
