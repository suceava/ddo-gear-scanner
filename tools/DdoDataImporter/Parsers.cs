using System.Text.RegularExpressions;
using System.Xml.Linq;
using DdoGearScanner.Model;

namespace DdoGearScanner.DataImport;

/// <summary>Parses DDOBuilder's XML data files into our catalog records. Pure: takes file paths,
/// returns records. The brittle bit is <see cref="StatName"/> — DDOBuilder stores a buff's target in
/// a child literally named &lt;Item&gt; (e.g. "Haggle"); when that's absent or "All" (procs like
/// Vorpal) we humanize the effect &lt;Type&gt; instead.</summary>
public static class Parsers
{
    /// <summary>Read every *.item file under <paramref name="itemsDir"/> into catalog items.
    /// Skips purely cosmetic items (those whose only equipment slots are Cosmetic*); they grant
    /// nothing and aren't worn in a tracked gear slot, so they'd only pollute name matching. Items
    /// with a missing/empty slot tag (a DDOBuilder data gap on real gear) are kept.</summary>
    public static List<CatalogItem> ParseItems(string itemsDir, out List<string> warnings, out int cosmeticSkipped)
    {
        warnings = new List<string>();
        cosmeticSkipped = 0;
        var items = new List<CatalogItem>();
        foreach (string file in Directory.EnumerateFiles(itemsDir, "*.item", SearchOption.AllDirectories))
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (Exception ex) { warnings.Add($"parse failed: {Path.GetFileName(file)} — {ex.Message}"); continue; }

            foreach (XElement item in doc.Descendants("Item"))
            {
                string? name = (string?)item.Element("Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (IsCosmeticOnly(item)) { cosmeticSkipped++; continue; }
                items.Add(ParseItem(item));
            }
        }
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsCosmeticOnly(XElement item)
    {
        var slots = item.Element("EquipmentSlot")?.Elements().ToList();
        return slots is { Count: > 0 }
            && slots.All(e => e.Name.LocalName.StartsWith("Cosmetic", StringComparison.Ordinal));
    }

    private static CatalogItem ParseItem(XElement item)
    {
        string name = ((string?)item.Element("Name"))?.Trim() ?? "";
        var slots = (item.Element("EquipmentSlot")?.Elements() ?? Enumerable.Empty<XElement>())
            .SelectMany(e => SlotMapper.Map(e.Name.LocalName))
            .Distinct()
            .Select(s => s.ToString())
            .ToList();

        string? type = (string?)item.Element("Weapon") ?? (string?)item.Element("Armor");

        var mods = item.Elements("Buff")
            .Select(ParseMod)
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

        var augments = item.Elements("ItemAugment")
            .Select(a => (string?)a.Element("Type"))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();

        var sets = item.Elements("SetBonus")
            .Select(s => s.Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        return new CatalogItem(
            Name: name,
            MinLevel: (int?)item.Element("MinLevel") ?? 0,
            Slots: slots,
            Type: type,
            Mods: mods,
            AugmentSlots: augments,
            Sets: sets);
    }

    private static CatalogMod? ParseMod(XElement buff)
    {
        string? itemTarget = (string?)buff.Element("Item");
        string? effectType = (string?)buff.Element("Type");
        string stat = (!string.IsNullOrWhiteSpace(itemTarget) && itemTarget != "All")
            ? itemTarget!.Trim()
            : StatName(effectType);
        if (string.IsNullOrWhiteSpace(stat)) return null;

        double value = (double?)buff.Element("Value1") ?? 0;
        string bonusType = ((string?)buff.Element("BonusType"))?.Trim() ?? "Enhancement";
        string? desc = ((string?)buff.Element("Description1"))?.Trim();
        if (!string.IsNullOrEmpty(desc) && string.Equals(desc, itemTarget, StringComparison.Ordinal)) desc = null;

        return new CatalogMod(stat, value, bonusType, string.IsNullOrWhiteSpace(desc) ? null : desc);
    }

    /// <summary>Humanize a DDOBuilder effect type: split camelCase and drop trailing Number/Bonus
    /// (WizardryNumber → "Wizardry", WillSave → "Will Save", Vorpal → "Vorpal").</summary>
    public static string StatName(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "";
        string s = Regex.Replace(type, "([a-z0-9])([A-Z])", "$1 $2");
        s = Regex.Replace(s, @"\s*(Number|Bonus)$", "");
        return s.Trim();
    }

    /// <summary>Parse DDOBuilder's BonusTypes.xml (name + self-stacking rule).</summary>
    public static List<BonusTypeDef> ParseBonusTypes(string bonusTypesXml)
    {
        var doc = XDocument.Load(bonusTypesXml);
        return doc.Descendants("Bonus")
            .Select(b => new BonusTypeDef(
                ((string?)b.Element("Name"))?.Trim() ?? "",
                ((string?)b.Element("Stacking"))?.Trim() ?? "Highest Only"))
            .Where(b => b.Name.Length > 0)
            .GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)   // dedupe (their file repeats a few)
            .Select(g => g.First())
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
