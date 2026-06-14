using DdoGearScanner.Vision;
using Xunit;

namespace DdoGearScanner.Vision.Tests;

public class GearPrioritiesTests
{
    [Fact]
    public void DataLoadsFromEmbeddedResource() => Assert.NotEmpty(GearPriorities.All);

    [Theory]
    [InlineData("Constitution", 'A')]
    [InlineData("Physical Sheltering", 'A')]
    [InlineData("Magical Sheltering", 'A')]
    [InlineData("Dodge", 'A')]
    [InlineData("Fortification", 'A')]
    [InlineData("Healing Amplification", 'A')]
    [InlineData("Spot", 'C')]
    [InlineData("Fire Resistance", 'C')]      // elemental, NOT the all-saves "Resistance" entry
    public void RanksKnownStats(string stat, char expected)
        => Assert.Equal(expected, GearPriorities.RankOf(stat));

    [Fact]
    public void LongestAliasWinsElementalVsAllSaves()
    {
        // "Resistance" alone = all-saves (A); "Fire Resistance" = elemental resistance (C).
        Assert.Equal('A', GearPriorities.RankOf("Resistance"));
        Assert.Equal("saves-all", GearPriorities.Lookup("Resistance")!.Id);
        Assert.Equal("elemental-resistance", GearPriorities.Lookup("Fire Resistance")!.Id);
    }

    [Fact]
    public void UnknownStatHasNoRank() => Assert.Null(GearPriorities.RankOf("Combat Brute"));

    [Fact]
    public void RankVariesByPlaystyle()
    {
        PriorityEntry speed = GearPriorities.Lookup("Striding")!;
        Assert.Equal("A", speed.Rank("melee"));
        Assert.Equal("B", speed.Rank("caster"));
    }
}
