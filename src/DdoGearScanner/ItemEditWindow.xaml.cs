using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DdoGearScanner.Model;
using DdoGearScanner.Vision;

namespace DdoGearScanner;

/// <summary>
/// Editor for every field of a <see cref="GearItem"/> — the manual fix for OCR misreads (and a way
/// to add an item that was never scanned). Build a brand-new immutable GearItem on Save; the caller
/// persists it via <see cref="CaptureStore"/>. <see cref="ShowDialog"/> returns true on Save/Delete;
/// inspect <see cref="Result"/> (the new item, null if deleted) and <see cref="TargetSlot"/>.
/// </summary>
public partial class ItemEditWindow : Window
{
    // Editable row view-models. Plain mutable types: TwoWay bindings write into them and we read on
    // Save; ObservableCollection drives add/remove in the lists.
    public sealed class ModRow
    {
        public string Stat { get; set; } = "";
        public string Value { get; set; } = "";
        public bool IsPercent { get; set; }
        public string BonusType { get; set; } = "Enhancement";
        public string Description { get; set; } = "";
    }

    public sealed class AugRow
    {
        public AugmentColor Color { get; set; } = AugmentColor.Colorless;
        public bool IsEmpty { get; set; } = true;
        public string Filled { get; set; } = "";
    }

    public sealed class SetRow
    {
        public string Name { get; set; } = "";
    }

    public sealed record SlotOption(EquipSlot Slot, string Label);

    public ObservableCollection<ModRow> Mods { get; } = new();
    public ObservableCollection<AugRow> Augments { get; } = new();
    public ObservableCollection<SetRow> Sets { get; } = new();

    /// <summary>Bonus-type dropdown source: the known vocabulary, plus any value already on the item
    /// (so an OCR'd or unusual type still shows rather than blanking the combo).</summary>
    public IReadOnlyList<string> BonusTypeOptions { get; }
    public IReadOnlyList<AugmentColor> ColorOptions { get; } =
        Enum.GetValues(typeof(AugmentColor)).Cast<AugmentColor>().ToArray();

    private readonly EquipSlot _originalSlot;
    private readonly GearItem? _original;
    private bool _locked;

    /// <summary>The new item to persist (null when the user chose Delete).</summary>
    public GearItem? Result { get; private set; }
    /// <summary>Where the item should be stored — may differ from the original slot if the user
    /// reassigned it. Also the slot to clear on Delete.</summary>
    public EquipSlot TargetSlot { get; private set; }

    public ItemEditWindow(GearItem? item, EquipSlot slot)
    {
        _original = item;
        _originalSlot = slot;
        TargetSlot = slot;

        BonusTypeOptions = BuildBonusTypeOptions(item);
        InitializeComponent();

        var slotOptions = SlotInfo.DisplayOrder.Select(s => new SlotOption(s, SlotInfo.Label(s))).ToList();
        SlotCombo.ItemsSource = slotOptions;
        SlotCombo.SelectedItem = slotOptions.FirstOrDefault(o => o.Slot == slot) ?? slotOptions[0];

        ModsList.ItemsSource = Mods;
        AugList.ItemsSource = Augments;
        SetList.ItemsSource = Sets;

        if (item is null)
        {
            Title = "Add Item";
            Heading.Text = "Add Item";
            DeleteButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "Edit Item";
            Heading.Text = "Edit Item";
            DeleteButton.Visibility = Visibility.Visible;
            LoadFrom(item);
        }

        SetLocked(item?.Locked ?? false);
    }

    private static IReadOnlyList<string> BuildBonusTypeOptions(GearItem? item)
    {
        // Curated user-facing list, plus any type already on this item that isn't in it (so an
        // unusual/OCR'd value still shows). Sorted alphabetically — a predictable scan order.
        var list = new List<string>(BonusTypes.UserSelectable);
        if (item is not null)
            foreach (Mod m in item.Mods)
                if (!string.IsNullOrWhiteSpace(m.BonusType)
                    && !list.Contains(m.BonusType.Trim(), StringComparer.OrdinalIgnoreCase))
                    list.Add(m.BonusType.Trim());
        return list.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void LoadFrom(GearItem item)
    {
        NameBox.Text = item.Name;
        TypeBox.Text = item.ItemTypeText ?? "";
        MlBox.Text = item.MinimumLevel?.ToString(CultureInfo.InvariantCulture) ?? "";

        foreach (Mod m in item.Mods)
            Mods.Add(new ModRow
            {
                Stat = m.Stat,
                Value = m.Value.ToString("0.##", CultureInfo.InvariantCulture),
                IsPercent = m.IsPercent,
                BonusType = m.BonusType,
                Description = m.Description ?? "",
            });

        foreach (AugmentSlot a in item.Augments)
            Augments.Add(new AugRow { Color = a.Color, IsEmpty = a.IsEmpty, Filled = a.Filled ?? "" });

        foreach (SetBonus s in item.SetBonuses)
            Sets.Add(new SetRow { Name = s.SetName });
    }

    private void SetLocked(bool locked)
    {
        _locked = locked;
        LockToggle.IsChecked = locked;
        LockToggle.Content = locked ? "🔒 Locked" : "🔓 Unlocked";
    }

    private void LockToggle_Click(object sender, RoutedEventArgs e) => SetLocked(LockToggle.IsChecked == true);

    private void AddMod_Click(object sender, RoutedEventArgs e) => Mods.Add(new ModRow());
    private void AddAug_Click(object sender, RoutedEventArgs e) => Augments.Add(new AugRow());
    private void AddSet_Click(object sender, RoutedEventArgs e) => Sets.Add(new SetRow());

    private void RemoveMod_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is ModRow r) Mods.Remove(r); }
    private void RemoveAug_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is AugRow r) Augments.Remove(r); }
    private void RemoveSet_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is SetRow r) Sets.Remove(r); }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        EquipSlot slot = (SlotCombo.SelectedItem as SlotOption)?.Slot ?? _originalSlot;

        int? ml = null;
        if (int.TryParse(MlBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mlv)) ml = mlv;

        var mods = new List<Mod>();
        foreach (ModRow r in Mods)
        {
            string stat = (r.Stat ?? "").Trim();
            if (stat.Length == 0) continue; // drop blank rows
            double val = ParseValue(r.Value);
            string type = string.IsNullOrWhiteSpace(r.BonusType) ? "Enhancement" : r.BonusType.Trim();
            string? desc = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
            mods.Add(new Mod(stat, val, type, r.IsPercent, desc));
        }

        var augs = new List<AugmentSlot>();
        foreach (AugRow r in Augments)
        {
            string? filled = string.IsNullOrWhiteSpace(r.Filled) ? null : r.Filled.Trim();
            augs.Add(new AugmentSlot(r.Color, r.IsEmpty ? null : filled, r.IsEmpty));
        }

        var sets = new List<SetBonus>();
        foreach (SetRow r in Sets)
        {
            string name = (r.Name ?? "").Trim();
            if (name.Length > 0) sets.Add(new SetBonus(name));
        }

        string itemName = (NameBox.Text ?? "").Trim();
        string? type2 = string.IsNullOrWhiteSpace(TypeBox.Text) ? null : TypeBox.Text.Trim();

        GearItem result = (_original ?? GearItem.Empty("")) with
        {
            Name = itemName,
            MinimumLevel = ml,
            Slot = slot,
            ItemTypeText = type2,
            Mods = mods,
            Augments = augs,
            SetBonuses = sets,
            IsLikelyNamed = itemName.Length > 0,
            Locked = _locked,
            Edited = true,
        };

        Result = result;
        TargetSlot = slot;
        DialogResult = true;
    }

    private static double ParseValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        string t = text.Trim().Replace("+", "").Replace("%", "").Replace(",", "");
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Remove this item from {SlotInfo.Label(_originalSlot)}?",
                "Delete item", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Result = null;
        TargetSlot = _originalSlot;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
