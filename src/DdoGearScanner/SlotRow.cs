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
            _pending = false;   // a landed read (success or not) ends the "processing" state
            Raise();
        }
    }

    // True from the moment a tooltip is CAPTURED until its (possibly slow, LLM) read lands — the row
    // shows the shot was taken and is being processed, so the read latency doesn't feel like a miss.
    private bool _pending;
    public bool Pending
    {
        get => _pending;
        set { _pending = value; Raise(); }
    }

    private void Raise()
    {
        foreach (string p in new[] { nameof(Item), nameof(HasItem), nameof(Name), nameof(Sub), nameof(Badge), nameof(Pending) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public bool HasItem => _item is not null;

    public string Name => _pending ? "⏳ Processing…"
        : _item is null ? "— empty —"
        : (!string.IsNullOrWhiteSpace(_item.Name) ? _item.Name : "(unnamed)");

    public string Sub => _pending ? "captured — reading tooltip"
        : _item is null ? ""
        : $"ML {_item.MinimumLevel?.ToString() ?? "?"}  ·  {_item.Mods.Count} mods"
          + (_item.SetBonuses.Count > 0 ? "  ·  set" : "");

    /// <summary>Tiny status glyphs: 🔒 = locked (re-capture skips it), ✦ = catalog-matched, ✎ = hand-edited.</summary>
    public string Badge => _item is null ? ""
        : (_item.Locked ? "🔒 " : "") + (_item.Matched ? "✦ " : "") + (_item.Edited ? "✎" : "");

    public event PropertyChangedEventHandler? PropertyChanged;
}
