using System.Text.RegularExpressions;
using DdoGearScanner.Model;

namespace DdoGearScanner.Vision;

/// <summary>Broad grouping of a character stat, used to organize the stacking matrix. Order matters
/// (it's the display order): abilities first, then defenses/offense, skills, then everything else.</summary>
public enum StatCategory { Ability, SavingThrow, Defense, Offense, Skill, Other }

/// <summary>
/// Classifies a parsed mod as a CHARACTER-WIDE bonus (participates in cross-slot stacking — goes in
/// the matrix) vs an ITEM-LOCAL effect (a weapon/armor enchantment or on-hit proc that only affects
/// the item it's on — listed under the item, never overridden across slots). Also buckets a
/// character-wide stat into a <see cref="StatCategory"/> for grouping/sorting.
///
/// This is the one piece of curated game knowledge: a catalog of stat names + a few description
/// markers — NOT a per-item hand-flagging. It's a starter set modeled on DDOBuilderV2's stat list;
/// extend it as new stats show up (unknown stats fall to item-local, which never invents an
/// override). See GAME_RULES.md.
/// </summary>
public static partial class StatCatalog
{
    /// <summary>True if the mod only affects its own item (weapon enchant / proc / guard) and must be
    /// kept out of the cross-slot stacking math.</summary>
    public static bool IsItemLocal(Mod mod)
    {
        string s = mod.Stat;
        if (NamedItemEffects.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase))) return true;
        // The base weapon/armor enhancement is item-local (it only buffs this item).
        if (s.StartsWith("Enhancement Bonus", StringComparison.OrdinalIgnoreCase)) return true;

        string d = mod.Description ?? string.Empty;
        return ItemLocalMarker().IsMatch(s) || ItemLocalMarker().IsMatch(d);
    }

    /// <summary>Category for a character-wide stat (used only for stats that aren't item-local).</summary>
    public static StatCategory Categorize(string stat)
    {
        string s = stat.Trim();
        if (Abilities.Contains(s)) return StatCategory.Ability;
        if (Skills.Contains(s)) return StatCategory.Skill;
        if (ContainsAny(s, SaveWords)) return StatCategory.SavingThrow;
        if (ContainsAny(s, DefenseWords)) return StatCategory.Defense;
        if (ContainsAny(s, OffenseWords)) return StatCategory.Offense;
        return StatCategory.Other;
    }

    /// <summary>Sub-order within a category (abilities in the canonical STR…CHA order; otherwise 99 so
    /// the caller falls back to value/name).</summary>
    public static int OrderInCategory(string stat)
    {
        int i = AbilityOrder.IndexOf(stat.Trim());
        return i >= 0 ? i : 99;
    }

    public static string CategoryLabel(StatCategory c) => c switch
    {
        StatCategory.Ability => "Ability Scores",
        StatCategory.SavingThrow => "Saving Throws",
        StatCategory.Defense => "Defense",
        StatCategory.Offense => "Offense",
        StatCategory.Skill => "Skills",
        _ => "Other",
    };

    private static bool ContainsAny(string s, string[] words)
        => words.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static readonly List<string> AbilityOrder = new()
        { "Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma" };
    private static readonly HashSet<string> Abilities = new(AbilityOrder, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Skills = new(StringComparer.OrdinalIgnoreCase)
    {
        "Balance", "Bluff", "Concentration", "Diplomacy", "Disable Device", "Haggle", "Heal", "Hide",
        "Intimidate", "Jump", "Listen", "Move Silently", "Open Lock", "Perform", "Repair", "Search",
        "Spellcraft", "Spot", "Swim", "Tumble", "Use Magic Device",
    };

    private static readonly string[] SaveWords = { "Save", "Fortitude", "Reflex", "Will", "versus", "Resistance Rating to" };

    private static readonly string[] DefenseWords =
    {
        "Sheltering", "Fortification", "Armor Class", " AC", "Dodge", "Resistance Rating", "Spell Resistance",
        "False Life", "Hit Points", "Healing Amplification", "Resistance", "Absorption", "Natural Armor",
        "Deflection", "Block", "Mitigation", "Guard Bonus",
    };

    private static readonly string[] OffenseWords =
    {
        "Melee Power", "Ranged Power", "Spell Power", "Universal", "Potency", "Devotion", "Nullification",
        "Magnetism", "Glaciation", "Combustion", "Corrosion", "Resonance", "Radiance", "Impulse", "Spell Lore",
        " Lore", "Attack", "Damage", "Doublestrike", "Doubleshot", "Deadly", "Accuracy", "Seeker", "Sneak Attack",
        "Imbue", "Critical", "Alacrity", "Power", "Spell Focus", "Caster Level", "Threat",
    };

    // Named effects / weapon-or-armor properties that are inherently item-local.
    private static readonly string[] NamedItemEffects =
    {
        "Increased Weapon Die", "Combat Brute", "Arborea", "Overbalance", "Extraordinary Virtue",
        "Increased Critical", "Weapon Die", "Gird Against", "Manslayer", "Dusk Surge", "Bane",
        "Surge Guard", "Soul Guard", "Undead Guard", "Nearly Finished", "Masterful Craftsmanship",
        "Feather Falling", "Jet Propulsion", "Freedom of Movement",
    };

    // Description/stat phrases that mean "this affects only the item it's on".
    [GeneratedRegex(@"\[W\]|\(W\)|this weapon|your weapon|weapons you wield|weapons and shields|on vorpal|on hit|on (a )?critical|when the wearer|attacked in melee|considered (cold iron|silver|byeshk|adamantine)|\bguard\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ItemLocalMarker();
}
