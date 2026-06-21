using System.ComponentModel;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>One equipment-slot row in the loadout sheet. Holds the item filling the slot (or null).</summary>
public sealed class SlotRow : INotifyPropertyChanged
{
    public EquipSlot Slot { get; }
    public string Label { get; }

    public SlotRow(EquipSlot slot)
    {
        Slot = slot;
        Label = SlotInfo.Label(slot);
    }

    private GearItem? _item;
    public GearItem? Item
    {
        get => _item;
        set
        {
            _item = value;
            foreach (string p in new[] { nameof(Item), nameof(HasItem), nameof(Name), nameof(Sub), nameof(Badge) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }
    }

    public bool HasItem => _item is not null;

    public string Name => _item is null ? "— empty —"
        : (!string.IsNullOrWhiteSpace(_item.Name) ? _item.Name : "(unnamed)");

    public string Sub => _item is null ? ""
        : $"ML {_item.MinimumLevel?.ToString() ?? "?"}  ·  {_item.Mods.Count} mods"
          + (_item.SetBonuses.Count > 0 ? "  ·  set" : "");

    /// <summary>Tiny status glyphs: 🔒 = locked (re-capture skips it), ✦ = catalog-matched, ✎ = hand-edited.</summary>
    public string Badge => _item is null ? ""
        : (_item.Locked ? "🔒 " : "") + (_item.Matched ? "✦ " : "") + (_item.Edited ? "✎" : "");

    public event PropertyChangedEventHandler? PropertyChanged;
}
