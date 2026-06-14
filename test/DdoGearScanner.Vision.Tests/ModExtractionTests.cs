using DdoGearScanner.Model;
using DdoGearScanner.Vision;
using Xunit;

namespace DdoGearScanner.Vision.Tests;

// Tests for the ▶-bullet-segmented mod extraction. Strings are taken verbatim from real DDO OCR
// captures (loadout.json) so these are regression fixtures against actual tooltip text.
public class ModExtractionTests
{
    [Theory]
    // Split model (matches DDOBuilderV2 <Buff>): Stat name | Value | BonusType, value out of the name.
    [InlineData("Insightful Strength +2: Passive: +2 Insight bonus to Strength", "Strength", 2, "Insightful")]
    [InlineData("Melee Alacrity +6%: +6 Enhancement bonus to melee attack speed.", "Melee Alacrity", 6, "Enhancement")]
    [InlineData("Incite +75%: +75% Enhancement bonus to Increased threat generated from melee damage.", "Incite", 75, "Enhancement")]
    [InlineData("Intimidate +16: +16 Competence bonus to Intimidate.", "Intimidate", 16, "Competence")]
    [InlineData("Insightful Physical Sheltering +9: Passive: +9 Insight bonus to Physical Resistance Rating", "Physical Sheltering", 9, "Insightful")]
    [InlineData("Quality Curse Resistance +1: +1 Quality bonus to saves versus curses.", "Curse Resistance", 1, "Quality")]
    [InlineData("Deadly +5: +5 Competence bonus to Weapon Damage.", "Deadly", 5, "Competence")]
    [InlineData("Constitution +6: Passive: +6 Enhancement bonus to Constitution", "Constitution", 6, "Enhancement")]
    [InlineData("Mythic Neck Boost +1: +1 Mythic bonus to the target's Physical and Magical Resistance Ratings", "Neck Boost", 1, "Mythic")]
    [InlineData("Protection +6: +6 Deflection bonus to AC.", "Protection", 6, "Deflection")]
    [InlineData("Natural Armor Bonus +8: This item is padded with leather and other natural ingredients, and provides a 8 Natural Armor bonus to AC", "Natural Armor", 8, "Natural Armor")]
    public void ExtractsModFromBlock(string block, string stat, int value, string type)
    {
        Mod? mod = TooltipTextParser.ExtractModFromBlock(block);
        Assert.NotNull(mod);
        Assert.Equal(stat, mod!.Stat);
        Assert.Equal(value, mod.Value);
        Assert.Equal(type, mod.BonusType);
    }

    [Fact]
    public void ValuelessNamedEffectKeptWithZeroValue()
    {
        Mod? mod = TooltipTextParser.ExtractModFromBlock("Increased Weapon Die: +(W) damage.");
        Assert.NotNull(mod);
        Assert.Equal("Increased Weapon Die", mod!.Stat);
        Assert.Equal(0, mod.Value);
    }

    [Fact]
    public void DescriptionFragmentIsNotAMod()
    {
        // A stray/false bullet that captures a wrapped description line must not yield a mod.
        Assert.Null(TooltipTextParser.ExtractModFromBlock("considered cold iron."));
        Assert.Null(TooltipTextParser.ExtractModFromBlock("leather and other natural ingredients"));
    }

    [Fact]
    public void DescriptionRestatedValueIsNotDoubleCounted()
    {
        // The value appears twice (head + description); exactly one mod, value once.
        Mod? mod = TooltipTextParser.ExtractModFromBlock(
            "Magical Sheltering +25: Passive: +25 Enhancement bonus to Magical Resistance Rating");
        Assert.NotNull(mod);
        Assert.Equal("Magical Sheltering", mod!.Stat);
        Assert.Equal(25, mod.Value);
        Assert.Equal("Enhancement", mod.BonusType);
    }

    [Fact]
    public void SegmentsRowsByBulletAndExcludesHeaderAndFooter()
    {
        var rows = new List<(string Text, int X, int YCenter, int Height)>
        {
            ("Soul-Stealing Ring", 10, 10, 12),                                              // header
            ("Minimum Level: 30", 10, 30, 12),                                               // header
            ("Magical Sheltering +25: Passive: +25 Enhancement bonus to", 10, 60, 12),       // ▶ block 0
            ("Magical Resistance Rating", 10, 74, 12),                                        //   wrap
            ("Electric Resistance +38: Passive: +38 Enhancement bonus to", 10, 95, 12),       // ▶ block 1
            ("Resist Electric.", 10, 109, 12),                                               //   wrap
            ("Augments:", 10, 130, 12),                                                       // footer
            ("Blue Augment Slot: Empty", 10, 145, 12),                                        // footer
        };
        var bullets = new List<int> { 60, 95 };

        List<Mod> mods = TooltipTextParser.SegmentMods(rows, bullets);

        Assert.Equal(2, mods.Count);
        Assert.Equal("Magical Sheltering", mods[0].Stat);
        Assert.Equal(25, mods[0].Value);
        Assert.Equal("Electric Resistance", mods[1].Stat);
        Assert.Equal(38, mods[1].Value);
    }
}
