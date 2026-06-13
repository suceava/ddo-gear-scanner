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
        "Green Augment Slot\n" +
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
}
