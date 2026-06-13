using System.Text.RegularExpressions;
using DdoGearScanner.Model;

namespace DdoGearScanner.Vision;

/// <summary>
/// Turns OCR'd tooltip lines into a structured <see cref="GearItem"/>.
///
/// This is the deliberately-brittle local-OCR path: line-by-line regex/keyword
/// classification. It exists to prove the capture->parse->store pipeline and to populate
/// a usable local catalog; the Phase-2 Claude vision reader is expected to supersede it for
/// hard tooltips. It NEVER throws and ALWAYS retains the raw text, so a bad parse degrades to
/// "name + raw text" rather than data loss.
///
/// Operates on plain strings (not OCR bounding boxes) so it can be unit-tested against saved
/// text fixtures without launching the game. The format reference lives in TOOLTIP_FORMAT.md.
/// </summary>
public static partial class TooltipTextParser
{
    public static GearItem Parse(IReadOnlyList<OcrLine> ocrLines)
        => ParseLines(ocrLines.Select(l => l.Text).ToList());

    public static GearItem ParseText(string rawText)
        => ParseLines(rawText.Replace("\r", "").Split('\n'));

    public static GearItem ParseLines(IReadOnlyList<string> rawLines)
    {
        string raw = string.Join("\n", rawLines);

        List<string> lines = rawLines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0) return GearItem.Empty(raw);

        string name = lines[0];
        int? minLevel = null;
        EquipSlot slot = EquipSlot.Unknown;
        string? itemTypeText = null;
        string? binding = null;
        List<Mod> mods = new();
        List<AugmentSlot> augments = new();
        List<SetBonus> setBonuses = new();

        // Skip the name line; classify the rest.
        for (int i = 1; i < lines.Count; i++)
        {
            string line = lines[i];

            if (minLevel is null && TryParseMinLevel(line, out int ml)) { minLevel = ml; continue; }

            if (TryParseAugment(line, out AugmentSlot? aug) && aug is not null) { augments.Add(aug); continue; }

            if (TryParseBinding(line, out string? bind)) { binding = bind; continue; }

            if (itemTypeText is null && TryParseItemType(line, out string? typeText, out EquipSlot mappedSlot))
            {
                itemTypeText = typeText;
                if (mappedSlot != EquipSlot.Unknown) slot = mappedSlot;
                continue;
            }

            if (TryParseSetBonus(line, out SetBonus? set) && set is not null) { setBonuses.Add(set); continue; }

            if (TryParseMod(line, out Mod? mod) && mod is not null) { mods.Add(mod); continue; }
        }

        bool likelyNamed = setBonuses.Count > 0
            || (CountWords(name) >= 2 && char.IsLetter(name[0]) && !LooksCrafted(name));

        return new GearItem(
            Name: name,
            MinimumLevel: minLevel,
            Slot: slot,
            ItemTypeText: itemTypeText,
            Mods: mods,
            Augments: augments,
            SetBonuses: setBonuses,
            Binding: binding,
            IsLikelyNamed: likelyNamed,
            RawOcrText: raw,
            CapturedUtc: DateTime.UtcNow);
    }

    // ---- line classifiers ----

    private static bool TryParseMinLevel(string line, out int level)
    {
        level = 0;
        Match m = MinLevelRe().Match(line);
        if (!m.Success) return false;
        return int.TryParse(m.Groups[1].Value, out level);
    }

    private static bool TryParseAugment(string line, out AugmentSlot? aug)
    {
        aug = null;
        Match m = AugmentRe().Match(line);
        if (!m.Success) return false;

        bool empty = m.Groups[1].Success || line.Contains("Empty", StringComparison.OrdinalIgnoreCase);
        AugmentColor color = ParseColor(m.Groups[2].Value);

        string? filled = null;
        if (!empty)
        {
            int colon = line.IndexOf(':');
            if (colon >= 0 && colon < line.Length - 1)
            {
                string after = line[(colon + 1)..].Trim();
                if (after.Length > 0) filled = after;
            }
        }
        aug = new AugmentSlot(color, filled, empty);
        return true;
    }

    private static bool TryParseBinding(string line, out string? binding)
    {
        binding = null;
        if (line.Contains("Bound to", StringComparison.OrdinalIgnoreCase)
            || line.Equals("Unbound", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Bound to Account", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Bound to Character", StringComparison.OrdinalIgnoreCase))
        {
            binding = line;
            return true;
        }
        return false;
    }

    private static bool TryParseItemType(string line, out string? typeText, out EquipSlot slot)
    {
        typeText = null;
        slot = EquipSlot.Unknown;

        // "Armor: Medium", "Weapon: Longsword", "Accessory", or a bare slot keyword.
        foreach ((string kw, EquipSlot s) in SlotKeywords)
        {
            if (Regex.IsMatch(line, $@"\b{Regex.Escape(kw)}\b", RegexOptions.IgnoreCase))
            {
                typeText = line;
                slot = s;
                return true;
            }
        }

        if (line.StartsWith("Armor:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Weapon:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Shield:", StringComparison.OrdinalIgnoreCase))
        {
            typeText = line;
            if (line.StartsWith("Armor:", StringComparison.OrdinalIgnoreCase)) slot = EquipSlot.Armor;
            else if (line.StartsWith("Shield:", StringComparison.OrdinalIgnoreCase)) slot = EquipSlot.OffHand;
            else slot = EquipSlot.MainHand;
            return true;
        }

        return false;
    }

    private static bool TryParseSetBonus(string line, out SetBonus? set)
    {
        set = null;
        // DDO set lines usually contain the word "Set" and often "Bonus". Avoid swallowing
        // ordinary affixes: require the word "Set" as a standalone token.
        if (!Regex.IsMatch(line, @"\bSet\b")) return false;
        // Skip if it's clearly a numeric mod that merely mentions 'set'.
        set = new SetBonus(line);
        return true;
    }

    private static bool TryParseMod(string line, out Mod? mod)
    {
        mod = null;

        Match valueMatch = ValueRe().Match(line);
        if (valueMatch.Success)
        {
            string numText = valueMatch.Groups[1].Value
                .Replace(",", "").Replace("O", "0").Replace("o", "0");
            if (!double.TryParse(numText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value))
                return false;

            // Remove the value token from the line, then peel a known bonus-type prefix.
            string remainder = line.Remove(valueMatch.Index, valueMatch.Length).Trim();
            (string bonusType, string stat) = SplitTypeAndStat(remainder);
            stat = CleanStat(stat);
            if (stat.Length == 0) stat = remainder.Trim();
            if (stat.Length == 0) return false;

            mod = new Mod(stat, value, bonusType);
            return true;
        }

        // Valueless named effect (e.g. "True Seeing", "Feather Falling"). Capture as a 0-value
        // mod so the catalog keeps it. Filter obvious OCR garbage.
        if (LooksLikeNamedEffect(line))
        {
            mod = new Mod(line, 0, BonusTypes.Default);
            return true;
        }
        return false;
    }

    // ---- helpers ----

    private static (string bonusType, string stat) SplitTypeAndStat(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && BonusTypes.IsKnown(words[0]))
        {
            string stat = string.Join(' ', words.Skip(1));
            return (NormalizeType(words[0]), stat);
        }
        return (BonusTypes.Default, text);
    }

    private static string NormalizeType(string t)
        => t.Equals("Insight", StringComparison.OrdinalIgnoreCase) ? "Insightful" : t.Trim();

    private static string CleanStat(string stat)
        => stat.Trim().Trim('+', '-', ':', '.', ',').Trim();

    private static AugmentColor ParseColor(string word) => word.Trim().ToLowerInvariant() switch
    {
        "colorless" => AugmentColor.Colorless,
        "blue" => AugmentColor.Blue,
        "yellow" => AugmentColor.Yellow,
        "red" => AugmentColor.Red,
        "orange" => AugmentColor.Orange,
        "purple" => AugmentColor.Purple,
        "green" => AugmentColor.Green,
        _ => AugmentColor.Unknown,
    };

    private static bool LooksLikeNamedEffect(string line)
    {
        // Mostly-alpha, a couple words, reasonable length — filters OCR fragments.
        if (line.Length < 3 || line.Length > 48) return false;
        int letters = line.Count(char.IsLetter);
        if (letters < line.Length * 0.6) return false;
        return char.IsUpper(line[0]);
    }

    private static bool LooksCrafted(string name)
        => name.StartsWith("+", StringComparison.Ordinal)
           || Regex.IsMatch(name, @"\+\d+\s", RegexOptions.IgnoreCase);

    private static int CountWords(string s)
        => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static readonly (string kw, EquipSlot slot)[] SlotKeywords =
    {
        ("Helmet", EquipSlot.Helmet), ("Helm", EquipSlot.Helmet),
        ("Goggles", EquipSlot.Goggles),
        ("Necklace", EquipSlot.Necklace), ("Amulet", EquipSlot.Necklace),
        ("Cloak", EquipSlot.Cloak), ("Cape", EquipSlot.Cloak),
        ("Belt", EquipSlot.Belt),
        ("Ring", EquipSlot.Ring1),
        ("Bracers", EquipSlot.Bracers), ("Bracelet", EquipSlot.Bracers),
        ("Gauntlets", EquipSlot.Gloves), ("Gloves", EquipSlot.Gloves),
        ("Boots", EquipSlot.Boots),
        ("Trinket", EquipSlot.Trinket),
        ("Quiver", EquipSlot.Quiver),
    };

    [GeneratedRegex(@"Min(?:imum)?\.?\s*Level[:\s]*([0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MinLevelRe();

    [GeneratedRegex(@"(?:(Empty)\s+)?(Colorless|Blue|Yellow|Red|Orange|Purple|Green)\s+Augment\s+Slot", RegexOptions.IgnoreCase)]
    private static partial Regex AugmentRe();

    // First +N / -N / N value token in the line (allow comma thousands, optional %).
    [GeneratedRegex(@"([+\-]?\d[\d,]*(?:\.\d+)?)%?")]
    private static partial Regex ValueRe();
}
