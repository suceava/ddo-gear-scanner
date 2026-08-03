using DdoGearScanner.Model;
using DdoGearScanner.Vision;
using Xunit;

namespace DdoGearScanner.Vision.Tests;

// Runs against the real embedded catalog (its stat vocabulary is the split authority).
public class ModNormalizerTests
{
    [Theory]
    [InlineData("Insightful Strength", "Strength", "Insightful")]
    [InlineData("Exceptional Constitution", "Constitution", "Exceptional")]
    [InlineData("Insightful Physical Sheltering", "Physical Sheltering", "Insightful")]
    public void SplitsBonusTypePrefixWhenRemainderIsACatalogStat(string stat, string expectStat, string expectType)
    {
        Mod m = ModNormalizer.Normalize(new Mod(stat, 2, "Enhancement"));
        Assert.Equal(expectStat, m.Stat);
        Assert.Equal(expectType, m.BonusType);
    }

    [Fact]
    public void LeavesAlreadySplitModUnchanged()
    {
        Mod m = ModNormalizer.Normalize(new Mod("Strength", 8, "Enhancement"));
        Assert.Equal("Strength", m.Stat);
        Assert.Equal("Enhancement", m.BonusType);
    }

    [Theory]
    [InlineData("Insight Bonus to Armor Class")]  // remainder "Bonus to Armor Class" isn't a catalog stat
    [InlineData("Enhancement Bonus")]              // remainder "Bonus" isn't a catalog stat
    public void LeavesNonSplittableStatsWhole(string stat)
    {
        Mod m = ModNormalizer.Normalize(new Mod(stat, 5, "Enhancement"));
        Assert.Equal(stat, m.Stat);
    }
}
