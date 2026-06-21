using System.Reflection;
using System.Text.Json;
using DdoGearScanner.Model;

namespace DdoGearScanner.Vision;

/// <summary>One mod on a catalog item (DDOBuilder's split Stat/Value/BonusType model).</summary>
public sealed record CatalogMod(string Stat, double Value, string BonusType, string? Description);

/// <summary>A DDOBuilder item from the generated <c>items.json</c>. <see cref="Slots"/> are
/// our <see cref="EquipSlot"/> names (an item can fit more than one).</summary>
public sealed record CatalogItem(
    string Name,
    int MinLevel,
    IReadOnlyList<string> Slots,
    string? Type,
    IReadOnlyList<CatalogMod> Mods,
    IReadOnlyList<string> AugmentSlots,
    IReadOnlyList<string> Sets);

/// <summary>
/// The named-item catalog imported from DDOBuilder (<c>tools/DdoDataImporter</c> → <c>data/items.json</c>,
/// embedded). Loaded once and indexed by equipment slot so name matching only competes an item against
/// the gear that fits the slot it was captured in.
/// </summary>
public static class ItemCatalog
{
    private static readonly IReadOnlyList<CatalogItem> _all = Load();
    private static readonly IReadOnlyDictionary<EquipSlot, List<CatalogItem>> _bySlot = IndexBySlot(_all);

    public static IReadOnlyList<CatalogItem> All => _all;

    /// <summary>Items that can be equipped in <paramref name="slot"/> (empty if none / Unknown).</summary>
    public static IReadOnlyList<CatalogItem> ForSlot(EquipSlot slot)
        => _bySlot.TryGetValue(slot, out List<CatalogItem>? list) ? list : Array.Empty<CatalogItem>();

    private static IReadOnlyList<CatalogItem> Load()
    {
        try
        {
            Assembly asm = typeof(ItemCatalog).Assembly;
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("items.json", StringComparison.OrdinalIgnoreCase));
            if (name is null) return Array.Empty<CatalogItem>();
            using Stream? s = asm.GetManifestResourceStream(name);
            if (s is null) return Array.Empty<CatalogItem>();

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<CatalogItem>>(s, opts) ?? (IReadOnlyList<CatalogItem>)Array.Empty<CatalogItem>();
        }
        catch { return Array.Empty<CatalogItem>(); }   // never break the app over the data file
    }

    private static IReadOnlyDictionary<EquipSlot, List<CatalogItem>> IndexBySlot(IReadOnlyList<CatalogItem> items)
    {
        var map = new Dictionary<EquipSlot, List<CatalogItem>>();
        foreach (CatalogItem item in items)
            foreach (string token in item.Slots)
                if (Enum.TryParse(token, out EquipSlot slot) && slot != EquipSlot.Unknown)
                {
                    if (!map.TryGetValue(slot, out List<CatalogItem>? list)) map[slot] = list = new List<CatalogItem>();
                    list.Add(item);
                }
        return map;
    }
}
