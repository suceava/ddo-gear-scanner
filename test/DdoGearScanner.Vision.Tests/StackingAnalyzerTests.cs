using DdoGearScanner.Model;
using DdoGearScanner.Vision;
using Xunit;

namespace DdoGearScanner.Vision.Tests;

public class StackingAnalyzerTests
{
    private static GearItem Item(EquipSlot slot, params Mod[] mods) => new(
        Name: slot.ToString(), MinimumLevel: null, Slot: slot, ItemTypeText: null,
        Mods: mods, Augments: Array.Empty<AugmentSlot>(), SetBonuses: Array.Empty<SetBonus>(),
        Binding: null, IsLikelyNamed: false, RawOcrText: "", CapturedUtc: default);

    private static StackingMatrix Analyze(params (EquipSlot, GearItem)[] items)
        => StackingAnalyzer.Analyze(items.ToDictionary(x => x.Item1, x => x.Item2));

    [Fact]
    public void SameTypeOnTwoSlotsOverridesLower()
    {
        StackingMatrix m = Analyze(
            (EquipSlot.Necklace, Item(EquipSlot.Necklace, new Mod("Constitution", 6, "Enhancement"))),
            (EquipSlot.Ring1, Item(EquipSlot.Ring1, new Mod("Constitution", 10, "Enhancement"))));

        MatrixRow con = Assert.Single(m.Rows);
        Assert.True(con.HasOverride);
        Assert.Equal(10, con.Effective);                                   // only the higher counts
        Assert.True(con.Cells.Single(c => c.Value == 10).Counts);
        Assert.True(con.Cells.Single(c => c.Value == 6).Overridden);
    }

    [Fact]
    public void DifferentTypesAreSeparateRowsNotSummed()
    {
        // The matrix shows overlap per TYPE — different types are different rows (each counts), so you
        // can see where the Enhancement vs the Insightful Constitution come from, not a merged total.
        StackingMatrix m = Analyze(
            (EquipSlot.Necklace, Item(EquipSlot.Necklace, new Mod("Constitution", 6, "Enhancement"))),
            (EquipSlot.Trinket, Item(EquipSlot.Trinket, new Mod("Constitution", 3, "Insightful"))));

        Assert.Equal(2, m.Rows.Count);
        Assert.All(m.Rows, r => Assert.False(r.HasOverride));
        Assert.Equal(6, m.Rows.Single(r => r.BonusType == "Enhancement").Effective);
        Assert.Equal(3, m.Rows.Single(r => r.BonusType == "Insightful").Effective);
    }

    [Fact]
    public void SelfStackingTypeAddsEveryInstance()
    {
        StackingMatrix m = Analyze(
            (EquipSlot.Cloak, Item(EquipSlot.Cloak, new Mod("Melee Power", 5, "Artifact"))),
            (EquipSlot.Belt, Item(EquipSlot.Belt, new Mod("Melee Power", 5, "Artifact"))));

        MatrixRow mp = Assert.Single(m.Rows);
        Assert.False(mp.HasOverride);                                      // artifact stacks with itself
        Assert.Equal(10, mp.Effective);
    }

    [Fact]
    public void ValuelessNamedEffectsListedSeparately()
    {
        StackingMatrix m = Analyze(
            (EquipSlot.Ring1, Item(EquipSlot.Ring1, new Mod("Trap the Soul Guard", 0, "Enhancement"))));

        Assert.Empty(m.Rows);
        ItemLocalEffect local = Assert.Single(m.ItemLocal);
        Assert.Equal("Trap the Soul Guard", local.Stat);
    }

    [Fact]
    public void WeaponLocalEnchantmentIsNotInTheMatrix()
    {
        // A weapon's own enchantment ("+1[W] with this weapon") only affects that weapon — it must
        // not appear as a cross-slot stat, even though it has a value.
        StackingMatrix m = Analyze(
            (EquipSlot.MainHand, Item(EquipSlot.MainHand,
                new Mod("Combat Brute", 1, "Enhancement", false, "+1[W] with this weapon."))),
            (EquipSlot.Necklace, Item(EquipSlot.Necklace, new Mod("Constitution", 10, "Enhancement"))));

        Assert.Single(m.Rows);                                  // only Constitution
        Assert.Equal("Constitution", m.Rows[0].Stat);
        Assert.Contains(m.ItemLocal, e => e.Stat == "Combat Brute");
    }

    [Fact]
    public void PlaystyleChangesPriorityRank()
    {
        var loadout = new Dictionary<EquipSlot, GearItem>
        {
            [EquipSlot.Cloak] = Item(EquipSlot.Cloak, new Mod("Melee Power", 10, "Artifact")),
        };
        // Melee Power is A-priority for melee, but not prioritized for a caster.
        Assert.Equal('A', StackingAnalyzer.Analyze(loadout, "melee").Rows[0].Priority);
        Assert.Null(StackingAnalyzer.Analyze(loadout, "caster").Rows[0].Priority);
    }

    [Fact]
    public void RowsAreGroupedByCategoryAbilitiesFirst()
    {
        StackingMatrix m = Analyze(
            (EquipSlot.Goggles, Item(EquipSlot.Goggles, new Mod("Spot", 13, "Competence"))),
            (EquipSlot.Necklace, Item(EquipSlot.Necklace, new Mod("Constitution", 10, "Enhancement"))),
            (EquipSlot.Armor, Item(EquipSlot.Armor, new Mod("Physical Sheltering", 27, "Enhancement"))));

        Assert.Equal(StatCategory.Ability, m.Rows[0].Category);     // Constitution first
        Assert.Equal("Constitution", m.Rows[0].Stat);
        Assert.Equal(StatCategory.Skill, m.Rows[^1].Category);      // Spot last
    }
}
