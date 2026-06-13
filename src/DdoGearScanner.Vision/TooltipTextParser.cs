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

        // The equipped-comparison tooltip leads with a "CURRENTLY EQUIPPED" header — the real name
        // is below it. Skip such a header (OCR can mangle it, so match loosely). The name itself may
        // wrap across lines, so gather lines until the first real field/content line.
        int nameIdx = IsEquippedHeader(lines[0]) && lines.Count > 1 ? 1 : 0;
        int classifyFrom = nameIdx;
        string? itemTypeText = null;
        List<string> nameParts = new();
        while (classifyFrom < lines.Count && nameParts.Count < 3)
        {
            string l = lines[classifyFrom];
            if (IsContentLine(l)) break;
            // The type line (e.g. "Heavy Armor", "Tower Shield", "Bastard Sword (one-handed)") sits
            // right under the name and looks like a name continuation — stop there and capture it.
            if (nameParts.Count >= 1 && IsTypeLine(l)) { itemTypeText = l; classifyFrom++; break; }
            // A standalone quality word ("Normal", "Rare", …) under the name — skip it.
            if (nameParts.Count >= 1 && IsQualityLine(l)) { classifyFrom++; break; }
            nameParts.Add(l);
            classifyFrom++;
        }
        if (nameParts.Count == 0) { nameParts.Add(lines[nameIdx]); classifyFrom = nameIdx + 1; }
        string name = string.Join(" ", nameParts);
        int? minLevel = null;
        EquipSlot slot = EquipSlot.Unknown;
        string? binding = null;
        List<Mod> mods = new();
        List<AugmentSlot> augments = new();
        List<SetBonus> setBonuses = new();

        // Skip the name line; classify the rest.
        for (int i = classifyFrom; i < lines.Count; i++)
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

    private static bool IsEquippedHeader(string line)
    {
        string norm = new string(line.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return norm.Contains("current") || norm.Contains("equipp");
    }

    // The name ends (and content begins) at the first of these. Deliberately reliable markers, so
    // a multi-line name isn't truncated; the weapon/armor TYPE line is caught via "handed)" or the
    // Armor/Weapon/Damage prefixes.
    private static readonly string[] ContentMarkers =
    {
        "equips to", "bound to", "unbound", "proficiency", "accepts", "augment slot",
        "damage:", "critical", "base value", "durability",
    };

    private static bool IsContentLine(string line)
    {
        if (MinLevelRe().IsMatch(line)) return true;
        string l = line.ToLowerInvariant();
        foreach (string m in ContentMarkers) if (l.Contains(m)) return true;
        return false;
    }

    // Known DDO item-type lines (the line shown right under the name). A name CONTAINS type words
    // ("Ring of the Djinn") but the type LINE equals one of these (after dropping a trailing
    // "(one-handed)" etc.), so whole-line matching separates them.
    private static readonly HashSet<string> ItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // armor
        "cloth", "cloth armor", "light armor", "medium armor", "heavy armor", "docent", "outfit", "robe",
        // shields / off-hand
        "buckler", "small shield", "large shield", "tower shield", "orb",
        // weapons
        "dagger", "short sword", "long sword", "longsword", "rapier", "scimitar", "falchion", "khopesh",
        "bastard sword", "great sword", "greatsword", "handaxe", "throwing axe", "battle axe", "great axe",
        "greataxe", "dwarven war axe", "light mace", "heavy mace", "morningstar", "club", "light hammer",
        "war hammer", "maul", "quarterstaff", "kama", "kukri", "sickle", "light pick", "heavy pick",
        "handwraps", "throwing hammer", "throwing dagger", "dart", "shuriken", "light crossbow",
        "heavy crossbow", "repeating crossbow", "great crossbow", "longbow", "shortbow", "long bow", "short bow",
        // accessories (whole-line only)
        "ring", "necklace", "amulet", "goggles", "helmet", "cloak", "belt", "bracers", "gloves",
        "gauntlets", "boots", "trinket", "quiver", "arrows", "bolts",
    };

    private static readonly HashSet<string> Qualities = new(StringComparer.OrdinalIgnoreCase)
    {
        "normal", "common", "uncommon", "rare", "epic", "legendary", "mythic", "masterful",
    };

    private static bool IsTypeLine(string line)
    {
        string t = Regex.Replace(line, @"\s*\([^)]*\)\s*$", "").Trim();
        return ItemTypes.Contains(t);
    }

    private static bool IsQualityLine(string line) => Qualities.Contains(line.Trim());

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
