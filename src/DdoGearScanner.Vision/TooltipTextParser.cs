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

    /// <summary>
    /// Bullet-aware parse. <paramref name="bulletYCenters"/> are the Y-centres of the gold ▶
    /// mod-bullets (from <see cref="BulletDetector"/>), in the SAME pixel space as the OCR line
    /// boxes. When present, mods are segmented by bullet (one mod per ▶ block) instead of guessed
    /// line-by-line — this is what removes run-together and double-counting. Everything else
    /// (name, ML, augments, set bonuses, binding) still comes from the line-based parse.
    /// </summary>
    public static GearItem Parse(IReadOnlyList<OcrLine> ocrLines, IReadOnlyList<int> bulletYCenters)
        => Parse(ocrLines, bulletYCenters, out _);

    public static GearItem Parse(
        IReadOnlyList<OcrLine> ocrLines, IReadOnlyList<int> bulletYCenters, out List<ModBlock> blockTrace)
    {
        blockTrace = new List<ModBlock>();
        GearItem baseItem = ParseLines(ocrLines.Select(l => l.Text).ToList());
        if (bulletYCenters is null || bulletYCenters.Count == 0) return baseItem;

        var rows = ocrLines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => (Text: l.Text.Trim(), X: l.Bbox.X, YCenter: l.Bbox.Y + l.Bbox.Height / 2, Height: l.Bbox.Height))
            .ToList();

        List<Mod> mods = SegmentMods(rows, bulletYCenters, out blockTrace);
        if (mods.Count == 0) return baseItem;
        mods = QualifyBaseEnhancement(mods, baseItem.ItemTypeText, baseItem.Slot);
        return baseItem with { Mods = mods };
    }

    // The base item affix "+N Enhancement Bonus" parses to Stat "Enhancement Bonus" — which on its
    // own doesn't say what it boosts. Qualify it by item kind: armor/shields enhance AC, weapons
    // enhance attack & damage (DDOBuilderV2 splits these into ArmorEnchantment / WeaponEnchantment).
    private static List<Mod> QualifyBaseEnhancement(List<Mod> mods, string? itemTypeText, EquipSlot slot)
    {
        string? target = EnhancementTarget(itemTypeText, slot);
        if (target is null) return mods;
        return mods
            .Select(m => m.Stat.Equals("Enhancement Bonus", StringComparison.OrdinalIgnoreCase)
                ? m with { Stat = $"Enhancement Bonus ({target})" }
                : m)
            .ToList();
    }

    private static string? EnhancementTarget(string? itemTypeText, EquipSlot slot)
    {
        string t = (itemTypeText ?? string.Empty).ToLowerInvariant();
        if (t.Contains("armor") || t.Contains("shield") || t is "docent" or "robe" or "outfit" or "cloth" or "buckler")
            return "Armor";
        if (IsWeaponTypeText(t)) return "Attack & Damage";
        // Fall back to the equipment slot when the type line was unreadable.
        if (slot == EquipSlot.Armor) return "Armor";
        if (slot == EquipSlot.MainHand) return "Attack & Damage";
        return null;
    }

    private static bool IsWeaponTypeText(string t) =>
        t.Length > 0 && (t.Contains("sword") || t.Contains("axe") || t.Contains("mace") || t.Contains("bow")
            || t.Contains("crossbow") || t.Contains("dagger") || t.Contains("rapier") || t.Contains("scimitar")
            || t.Contains("falchion") || t.Contains("khopesh") || t.Contains("hammer") || t.Contains("maul")
            || t.Contains("club") || t.Contains("staff") || t.Contains("kama") || t.Contains("kukri")
            || t.Contains("sickle") || t.Contains("pick") || t.Contains("handwraps") || t.Contains("dart")
            || t.Contains("shuriken") || t.Contains("handaxe") || t.Contains("morningstar"));

    public static GearItem ParseText(string rawText)
        => ParseLines(rawText.Replace("\r", "").Split('\n'));

    // ---- bullet-segmented mod extraction (pure; unit-tested without images) ----

    /// <summary>
    /// Groups OCR rows into one block per ▶ bullet and extracts a single mod from each. A row
    /// belongs to the nearest bullet at/above it; rows above the first bullet are the header and
    /// rows at/after the first footer marker (Augments / Set Bonuses / Base Value / …) are the
    /// footer — both are excluded so only real affix blocks become mods.
    /// </summary>
    /// <summary>One ▶ block and what it produced — for the debug dump. <see cref="RejectReason"/>
    /// is non-null exactly when <see cref="Mod"/> is null.</summary>
    public sealed record ModBlock(int BulletY, IReadOnlyList<string> Rows, string JoinedText, Mod? Mod, string? RejectReason);

    // Reading order for a block's rows: cluster into visual lines (Y within lineGap of the line's
    // first row), then order each line left-to-right by X, lines top-to-bottom.
    private static List<string> OrderRowsReading(List<(string Text, int X, int Y)> rows, int lineGap)
    {
        var outRows = new List<string>();
        var byY = rows.OrderBy(r => r.Y).ToList();
        int i = 0;
        while (i < byY.Count)
        {
            int lineY = byY[i].Y;
            var line = new List<(string Text, int X, int Y)>();
            while (i < byY.Count && byY[i].Y - lineY <= lineGap) { line.Add(byY[i]); i++; }
            outRows.AddRange(line.OrderBy(r => r.X).Select(r => r.Text));
        }
        return outRows;
    }

    public static List<Mod> SegmentMods(
        IReadOnlyList<(string Text, int X, int YCenter, int Height)> rows, IReadOnlyList<int> bulletYCenters)
        => SegmentMods(rows, bulletYCenters, out _);

    public static List<Mod> SegmentMods(
        IReadOnlyList<(string Text, int X, int YCenter, int Height)> rows, IReadOnlyList<int> bulletYCenters,
        out List<ModBlock> blockTrace)
    {
        var mods = new List<Mod>();
        blockTrace = new List<ModBlock>();
        if (bulletYCenters.Count == 0 || rows.Count == 0) return mods;

        List<int> bullets = bulletYCenters.OrderBy(y => y).ToList();
        int firstB = bullets[0];
        int lineH = Median(rows.Select(r => r.Height).ToList());
        int tol = Math.Max(4, (int)(lineH * 0.6));

        var ordered = rows.OrderBy(r => r.YCenter).ToList();
        int footerY = int.MaxValue;
        foreach (var r in ordered)
            if (r.YCenter >= firstB - tol && IsFooterMarker(r.Text)) { footerY = r.YCenter; break; }

        var blocks = new List<(string Text, int X, int Y)>[bullets.Count];
        for (int i = 0; i < blocks.Length; i++) blocks[i] = new List<(string, int, int)>();
        foreach (var r in ordered)
        {
            if (r.YCenter < firstB - tol || r.YCenter >= footerY) continue;
            int k = -1;
            for (int i = 0; i < bullets.Count; i++)
            {
                if (bullets[i] <= r.YCenter + tol) k = i; else break;
            }
            if (k >= 0) blocks[k].Add((r.Text, r.X, r.YCenter));
        }

        // A mod renders as "Name: description" where the description starts on the SAME line as the
        // name and wraps to the left margin below. OCR splits the name and the start-of-description
        // into separate runs at nearly the same Y (centres differ a couple px from height rounding).
        // Cluster rows into visual lines (Y within half a line-height) and read each line
        // left-to-right so the name leads — otherwise the description run sorts first and the name is
        // lost ("Passive +25" not "Magical Sheltering +25"). Hard Y-buckets fail here because a 3px
        // centre gap can straddle a bucket boundary, so cluster by proximity instead.
        int lineGap = Math.Max(2, (int)(lineH * 0.5));
        for (int i = 0; i < blocks.Length; i++)
        {
            List<string> sorted = OrderRowsReading(blocks[i], lineGap);
            string joined = string.Join(" ", sorted);
            if (sorted.Count == 0)
            {
                blockTrace.Add(new ModBlock(bullets[i], sorted, joined, null, "no OCR rows assigned to this bullet"));
                continue;
            }
            Mod? mod = ExtractModFromBlock(joined, out string? reason);
            blockTrace.Add(new ModBlock(bullets[i], sorted, joined, mod, reason));
            if (mod is not null) mods.Add(mod);
        }
        return mods;
    }

    /// <summary>
    /// Extracts one mod from a single ▶ block. The affix name + value sit at the head (before the
    /// first colon); the description follows and carries the bonus type ("+N &lt;Type&gt; bonus").
    /// Taking exactly one value per block is what prevents the description's restated value from
    /// being double-counted.
    /// </summary>
    public static Mod? ExtractModFromBlock(string blockText) => ExtractModFromBlock(blockText, out _);

    public static Mod? ExtractModFromBlock(string blockText, out string? rejectReason)
    {
        rejectReason = null;
        string text = (blockText ?? string.Empty).Trim();
        // Drop a leading OCR'd bullet artifact (>, •, *, etc.) but keep a real +/- sign.
        int s = 0;
        while (s < text.Length && !char.IsLetterOrDigit(text[s]) && text[s] != '+' && text[s] != '-') s++;
        text = text[s..].Trim();
        if (text.Length == 0) { rejectReason = "empty after trimming"; return null; }

        int colon = text.IndexOf(':');
        string head = colon >= 0 ? text[..colon] : text;
        string desc = colon >= 0 ? text[(colon + 1)..] : string.Empty;

        // Split into Stat / Value / BonusType — matching the DDOBuilderV2 + web-planner data model
        // (<Buff> with separate Type / Value1 / BonusType; valueless named effects carry no value).
        // The value is taken from the affix head ("Protection +6"); if the head has none, from the
        // description ("...+6 Deflection bonus"). Taking it once avoids double-counting.
        Match vm = SignedValueRe().Match(head);
        bool valFromHead = vm.Success;
        if (!vm.Success) vm = SignedValueRe().Match(desc);
        double value = 0;
        bool isPercent = false;
        if (vm.Success)
        {
            string num = vm.Groups[1].Value.Replace(",", "").Replace("O", "0").Replace("o", "0");
            double.TryParse(num, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
            isPercent = vm.Value.Contains('%');   // the matched token kept its trailing % if any
        }

        // Name = the head with the value token removed, e.g. "Insightful Physical Sheltering +9"
        // -> "Insightful Physical Sheltering".
        string name = valFromHead ? head.Remove(vm.Index, vm.Length) : head;
        name = name.Trim().Trim('+', '-', ':', '.', ',', ' ').Trim();

        // BonusType: stated in the description ("+N <Type> bonus") wins; else a leading type word in
        // the name; else Enhancement.
        string? type = BonusTypeFromDescription(text);
        (string leadType, string statAfterType) = SplitTypeAndStat(name);
        type ??= leadType;

        // Stat = the name with the leading type word and a trailing " Bonus" removed
        // ("Natural Armor Bonus" -> "Natural Armor"). But if that empties it, keep the full name so
        // the base "+N Enhancement Bonus" affix stays "Enhancement Bonus" instead of collapsing to
        // the bare type word "Enhancement" (it's later qualified to (Armor)/(Attack & Damage)).
        string stat = CleanStat(Regex.Replace(statAfterType, @"\s+Bonus$", "", RegexOptions.IgnoreCase).Trim());
        if (stat.Length == 0 || stat.Equals("Bonus", StringComparison.OrdinalIgnoreCase))
            stat = CleanStat(name);
        if (stat.Length == 0) { rejectReason = "no stat name found"; return null; }
        if (value == 0 && !LooksLikeNamedEffect(stat))
        {
            rejectReason = $"value 0 and \"{stat}\" doesn't look like a named effect (likely a description fragment)";
            return null;
        }

        string? description = string.IsNullOrWhiteSpace(desc) ? null : desc.Trim();
        return new Mod(stat, value, NormalizeType(type), isPercent, description);
    }

    // The bonus type stated in the description, e.g. "+13 Competence bonus to …" => "Competence".
    // Allows a two-word type ("Natural Armor") and is validated against the known vocabulary.
    private static string? BonusTypeFromDescription(string text)
    {
        foreach (Match m in BonusTypeDescRe().Matches(text))
        {
            string cap = m.Groups[1].Value.Trim();
            if (BonusTypes.IsKnown(cap)) return cap;
            string first = cap.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (BonusTypes.IsKnown(first)) return first;
        }
        return null;
    }

    // The end-of-mods boundary. Must be SPECIFIC: a bare word like "durability" or "hardness" also
    // appears in mod flavor text ("immune to durability damage"), and matching it there truncates the
    // whole mod list. So anchor to the real footer: section headers, or the bottom STAT lines where a
    // number follows the keyword.
    private static bool IsFooterMarker(string line)
    {
        string l = line.ToLowerInvariant();
        if (l.Contains("augment slot") || l.StartsWith("augments")) return true;
        if (l.Contains("set bonus") || l.Contains("pieces equipped")) return true;
        if (l.Contains("base value")) return true;
        if (Regex.IsMatch(l, @"durability[^a-z]*\d")) return true;   // "Durability: 360", not "durability damage"
        if (Regex.IsMatch(l, @"hardness[^a-z]*\d")) return true;     // "Hardness: 26"
        if (Regex.IsMatch(l, @"^\s*[\d.,]+\s*lbs")) return true;     // weight line
        return false;
    }

    private static int Median(List<int> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

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
                if (after.Length > 0 && !after.Equals("Empty", StringComparison.OrdinalIgnoreCase)) filled = after;
            }
        }
        // OCR often splits the "Empty"/contents onto a separate run (right column), so when no fill
        // name is on this line, treat the slot as empty (the common upgrade-gap case) rather than
        // "filled with nothing".
        if (!empty && string.IsNullOrEmpty(filled)) empty = true;
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
        => t.Trim().Equals("Insight", StringComparison.OrdinalIgnoreCase)
            ? "Insightful"
            : BonusTypes.Canonical(t);

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
        "moon" => AugmentColor.Moon,
        "sun" => AugmentColor.Sun,
        "globe" => AugmentColor.Globe,
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

    // Tolerant: OCR mangles "Augment" (e.g. "Augnnent") and sometimes drops "Slot", so match the
    // colour + an "Aug…" token with optional "Slot". Colours include the newer Moon/Sun/Globe slots.
    [GeneratedRegex(@"(?:(Empty)\s+)?(Colorless|Colourless|Blue|Yellow|Red|Orange|Purple|Green|Moon|Sun|Globe)\s+Aug\w{2,}(?:\s+Slot)?", RegexOptions.IgnoreCase)]
    private static partial Regex AugmentRe();

    // First +N / -N / N value token in the line (allow comma thousands, optional %).
    [GeneratedRegex(@"([+\-]?\d[\d,]*(?:\.\d+)?)%?")]
    private static partial Regex ValueRe();

    // A SIGNED value token ("+18", "-20", "+6"); used for per-block extraction where an unsigned
    // number ("provides a 8") must NOT be mistaken for the affix value.
    [GeneratedRegex(@"([+\-]\d[\d,]*(?:\.\d+)?)%?")]
    private static partial Regex SignedValueRe();

    // "<value> <Type[ Type2]> bonus" inside a description — the stated bonus type.
    [GeneratedRegex(@"[+\-]?\d[\d,]*%?\s+([A-Za-z][A-Za-z]+(?:\s[A-Z][a-z]+)?)\s+[Bb]onus")]
    private static partial Regex BonusTypeDescRe();
}
