using DdoGearScanner.Model;
using DdoGearScanner.Vision;
using Xunit;

namespace DdoGearScanner.Vision.Tests;

// Fixtures are plain text (what the OCR would yield) so the brittle local parser can be
// regression-tested without launching DDO. Add real captured OCR text here as it's gathered.
public class TooltipTextParserTests
{
    private const string NamedCloak =
        "Legendary Defender's Cloak\n" +
        "Minimum Level: 31\n" +
        "Cloak\n" +
        "Constitution +14\n" +
        "Insightful Healing Amplification +28\n" +
        "Green Augment Slot: Topaz of Power\n" +
        "Empty Colorless Augment Slot\n" +
        "Sentinel's Aspect Set\n" +
        "Bound to Character on Acquire";

    private const string CraftedRing =
        "+5 Ring of the Stalwart\n" +
        "Minimum Level: 28\n" +
        "Ring\n" +
        "Sheltering +20\n" +
        "Quality Constitution +2\n" +
        "Yellow Augment Slot\n" +
        "Unbound";

    [Fact]
    public void ParsesNameMinLevelAndSlot()
    {
        GearItem item = TooltipTextParser.ParseText(NamedCloak);
        Assert.Equal("Legendary Defender's Cloak", item.Name);
        Assert.Equal(31, item.MinimumLevel);
        Assert.Equal(EquipSlot.Cloak, item.Slot);
    }

    [Fact]
    public void ParsesModWithDefaultEnhancementType()
    {
        GearItem item = TooltipTextParser.ParseText(NamedCloak);
        Mod con = Assert.Single(item.Mods, m => m.Stat == "Constitution");
        Assert.Equal(14, con.Value);
        Assert.Equal("Enhancement", con.BonusType);
    }

    [Fact]
    public void ParsesModWithExplicitBonusType()
    {
        GearItem item = TooltipTextParser.ParseText(NamedCloak);
        Mod heal = Assert.Single(item.Mods, m => m.Stat == "Healing Amplification");
        Assert.Equal(28, heal.Value);
        Assert.Equal("Insightful", heal.BonusType);
    }

    [Fact]
    public void ParsesEmptyAugmentAsGap()
    {
        GearItem item = TooltipTextParser.ParseText(NamedCloak);
        AugmentSlot colorless = Assert.Single(item.Augments, a => a.Color == AugmentColor.Colorless);
        Assert.True(colorless.IsEmpty);
    }

    [Fact]
    public void ParsesFilledAugmentColor()
    {
        GearItem item = TooltipTextParser.ParseText(NamedCloak);
        AugmentSlot green = Assert.Single(item.Augments, a => a.Color == AugmentColor.Green);
        Assert.False(green.IsEmpty);
    }

    [Fact]
    public void DetectsSetBonusAndNamedFlag()
    {
        GearItem item = TooltipTextParser.ParseText(NamedCloak);
        Assert.Contains(item.SetBonuses, s => s.SetName.Contains("Aspect"));
        Assert.True(item.IsLikelyNamed);
        Assert.Equal("Bound to Character on Acquire", item.Binding);
    }

    [Fact]
    public void ParsesCraftedItemModsAndType()
    {
        GearItem item = TooltipTextParser.ParseText(CraftedRing);
        Assert.Equal(28, item.MinimumLevel);
        Assert.Equal(EquipSlot.Ring1, item.Slot);
        Mod quality = Assert.Single(item.Mods, m => m.Stat == "Constitution");
        Assert.Equal("Quality", quality.BonusType);
        Assert.Equal(2, quality.Value);
        Assert.Equal("Unbound", item.Binding);
    }

    [Fact]
    public void CraftedNamePrefixedWithPlusIsNotFlaggedNamed()
    {
        GearItem item = TooltipTextParser.ParseText(CraftedRing);
        Assert.False(item.IsLikelyNamed);
    }

    [Fact]
    public void NeverThrowsAndRetainsRawTextOnGarbage()
    {
        const string garbage = "··· ?? ###\n\n   \n@@@";
        GearItem item = TooltipTextParser.ParseText(garbage);
        Assert.Equal(garbage, item.RawOcrText);
    }

    [Fact]
    public void EmptyInputProducesEmptyItem()
    {
        GearItem item = TooltipTextParser.ParseText("");
        Assert.Equal("", item.Name);
        Assert.Empty(item.Mods);
    }

    [Fact]
    public void SkipsCurrentlyEquippedHeaderForName()
    {
        const string t =
            "CURRENTLY EQUIPPED\n" +
            "Morninglord's Khopesh\n" +
            "Khopesh (one-handed)\n" +
            "Equips to: Main Hand, Off Hand\n" +
            "Minimum Level: 30\n" +
            "Constitution +15";
        GearItem item = TooltipTextParser.ParseText(t);
        Assert.Equal("Morninglord's Khopesh", item.Name);
        Assert.Equal(30, item.MinimumLevel);
        Assert.Equal("Khopesh (one-handed)", item.ItemTypeText);
    }

    [Fact]
    public void TypeLineUnderNameIsNotPartOfTheName()
    {
        const string t =
            "CURRENTLY EQUIPPED\n" +
            "Epic Templar's Mail\n" +
            "Heavy Armor\n" +
            "Minimum Level: 23\n" +
            "Constitution +15";
        GearItem item = TooltipTextParser.ParseText(t);
        Assert.Equal("Epic Templar's Mail", item.Name);
        Assert.Equal("Heavy Armor", item.ItemTypeText);
        Assert.DoesNotContain("Heavy Armor", item.Mods.Select(m => m.Stat));
    }

    [Fact]
    public void MangledEquippedHeaderStrippedFromName()
    {
        // OCR mangles "CURRENTLY EQUIPPED"; the header line must drop out, not become the name.
        GearItem item = TooltipTextParser.ParseText(
            "CURRENTLY-EC)UIPPED\nVulkoorim Pendant\nEquips to: Neck\nMinimum Level: 9");
        Assert.Equal("Vulkoorim Pendant", item.Name);
    }

    [Fact]
    public void FooterAndFlavorNotCapturedAsMods()
    {
        // The line-based fallback (no ▶ bullets) must not scrape footer/flavor into mods.
        GearItem item = TooltipTextParser.ParseText(
            "Voice of the Master\nEquips to: Trinket\nMinimum Level: 5\n" +
            "Durability 100\nAdamantine Hardness 25\nBase Value 2020\n0.1 lbs\n" +
            "This trinket is a magical polyhedron with runes carved on each side.");
        Assert.DoesNotContain(item.Mods, m =>
            m.Stat.Contains("Durab") || m.Stat.Contains("Base Value") || m.Stat.Contains("lbs")
            || m.Stat.Contains("polyhedron") || m.Stat.Contains("Hardness"));
    }

    [Fact]
    public void SetBonusesHeaderIsNotASet()
    {
        GearItem item = TooltipTextParser.ParseText(
            "Some Item\nEquips to: Neck\nSet Bonuses:\nCrypt Raider Set (Heroic):");
        Assert.DoesNotContain(item.SetBonuses, s => s.SetName.Trim().TrimEnd(':').Equals("Set Bonuses"));
        Assert.Contains(item.SetBonuses, s => s.SetName.Contains("Crypt Raider"));
    }

    [Fact]
    public void MultiLineNameThenTypeLine()
    {
        const string t =
            "CURRENTLY EQUIPPED\n" +
            "+5 Reinforced Silver\n" +
            "Magecraft Buckler\n" +
            "Buckler\n" +
            "Minimum Level: 28\n" +
            "Sheltering +20";
        GearItem item = TooltipTextParser.ParseText(t);
        Assert.Equal("+5 Reinforced Silver Magecraft Buckler", item.Name);
        Assert.Equal("Buckler", item.ItemTypeText);
    }
}
