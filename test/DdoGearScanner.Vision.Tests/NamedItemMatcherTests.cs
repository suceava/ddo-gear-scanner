using DdoGearScanner.Model;
using DdoGearScanner.Vision;
using Xunit;

namespace DdoGearScanner.Vision.Tests;

// Run against the real embedded catalog (data/items.json baked into the Vision assembly).
public class NamedItemMatcherTests
{
    private const string Gloves = "Legendary Admiral's Gloves";   // ML 31, Gloves slot

    [Fact]
    public void CatalogLoadsManyItems()
    {
        Assert.True(ItemCatalog.All.Count > 5000);
        Assert.NotEmpty(ItemCatalog.ForSlot(EquipSlot.Gloves));
    }

    [Fact]
    public void ExactNameMatchesHighConfidence()
    {
        ItemMatch? m = NamedItemMatcher.TryMatch(Gloves, EquipSlot.Gloves, 31);
        Assert.NotNull(m);
        Assert.Equal(Gloves, m!.Item.Name);
        Assert.True(m.HighConfidence);
    }

    [Fact]
    public void ToleratesOcrErrors()
    {
        // a couple of character misreads: l→i, dropped apostrophe
        ItemMatch? m = NamedItemMatcher.TryMatch("Legendary Admirai s Gloves", EquipSlot.Gloves, 31);
        Assert.NotNull(m);
        Assert.Equal(Gloves, m!.Item.Name);
        Assert.True(m.HighConfidence);
    }

    [Fact]
    public void WrongSlotDoesNotReturnTheGlovesItem()
    {
        // Searching the helmet slot must never surface a gloves item.
        ItemMatch? m = NamedItemMatcher.TryMatch(Gloves, EquipSlot.Helmet, 31);
        Assert.True(m is null || m.Item.Name != Gloves);
    }

    [Fact]
    public void GarbageNameIsNotHighConfidence()
    {
        ItemMatch? m = NamedItemMatcher.TryMatch("Xqzzy Blarg Nonsense 9000", EquipSlot.Gloves, 1);
        Assert.True(m is null || !m.HighConfidence);
    }

    [Fact]
    public void ApplyReplacesModsAndKeepsRawOcr()
    {
        GearItem ocr = GearItem.Empty("RAW OCR TEXT") with
        {
            Name = "wrong name",
            Slot = EquipSlot.Gloves,
            Mods = new[] { new Mod("garbage stat", 1, "Enhancement") },
        };
        ItemMatch? m = NamedItemMatcher.TryMatch(Gloves, EquipSlot.Gloves, 31);
        Assert.NotNull(m);

        GearItem applied = NamedItemMatcher.Apply(ocr, m!.Item);
        Assert.True(applied.Matched);
        Assert.Equal(Gloves, applied.Name);
        Assert.Equal(31, applied.MinimumLevel);
        Assert.Contains(applied.Mods, x => x.Stat == "Wizardry");          // catalog data, not OCR
        Assert.DoesNotContain(applied.Mods, x => x.Stat == "garbage stat");
        Assert.Equal("RAW OCR TEXT", applied.RawOcrText);                  // original kept
    }
}
