using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DdoGearScanner.Capture;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// Main window: the equipped LOADOUT sheet (one row per equipment slot, filled as you capture;
/// re-capturing a slot overwrites it) on the left, and the selected item's detail + tooltip image
/// on the right. Normal (not click-through) window — the click-through overlay is separate.
/// </summary>
public partial class CaptureListWindow : Window
{
    private readonly CaptureStore _store;
    private readonly CharacterStore _charStore;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<SlotRow> _rows;
    private bool _switchingCharacter;
    // Captured tooltip images for THIS session, keyed by slot (slots loaded from disk have none).
    private readonly Dictionary<EquipSlot, BitmapImage> _crops = new();

    /// <summary>Raised when the user presses a new hotkey combo. Returns true if it registered.</summary>
    public event Func<uint, uint, bool>? RebindRequested;

    /// <summary>Raised when the user clicks the Detection toggle button.</summary>
    public event Action? DetectionToggleRequested;

    /// <summary>Raised when the user clicks the Calibrate slots button.</summary>
    public event Action? CalibrateRequested;

    private bool _bindingHotkey;

    public CaptureListWindow(CaptureStore store, CharacterStore charStore, AppSettings settings, bool ocrAvailable)
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        _store = store;
        _charStore = charStore;
        _settings = settings;

        WindowChrome.ApplyBounds(this, settings.WindowLeft, settings.WindowTop,
            settings.WindowWidth, settings.WindowHeight, settings.WindowMaximized);

        _rows = new ObservableCollection<SlotRow>(SlotInfo.DisplayOrder.Select(s => new SlotRow(s)));
        SlotSheet.ItemsSource = _rows;
        RefreshLoadout();
        PopulateCharacters();

        if (!ocrAvailable)
            StatusText.Text = "⚠ Windows OCR engine unavailable — install an OCR language pack (Settings → Language).";

        WindowChrome.PersistBounds(this, (l, t, w, h, m) =>
        {
            _settings.WindowLeft = l; _settings.WindowTop = t;
            _settings.WindowWidth = w; _settings.WindowHeight = h; _settings.WindowMaximized = m;
        });
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void SetHotkeyStatus(bool registered, uint modifiers, uint vk)
    {
        string combo = DescribeHotkey(modifiers, vk);
        HotkeyMenuItem.Content = $"Set Detection Hotkey ({combo})";
        if (!registered)
            StatusText.Text = $"⚠ Hotkey {combo} is taken by another app — pick another in ☰ Menu → Set Detection Hotkey.";
    }

    public void NoteHotkeyHealed(uint modifiers, uint vk)
        => StatusText.Text = $"Saved hotkey was taken by another app — reverted to {DescribeHotkey(modifiers, vk)}. Rebind via ☰ Menu → Set Detection Hotkey.";

    public void OnSessionChanged(bool active) => Dispatcher.Invoke(() =>
    {
        DetectionButton.Content = active ? "Toggle Detection (On)" : "Toggle Detection (Off)";
        StatusText.Text = active
            ? "● Detection ON — hover each gear piece in DDO; each tooltip is captured automatically."
            : "Detection paused. Press the hotkey (or ☰ Menu → Toggle Detection) to resume.";
    });

    private void Menu_Click(object sender, RoutedEventArgs e) => MenuPopup.IsOpen = true;

    private void DetectionToggle_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        DetectionToggleRequested?.Invoke();
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        CalibrateRequested?.Invoke();
    }

    public void SetStatusText(string text) => Dispatcher.Invoke(() => StatusText.Text = text);

    public void OnCaptureCompleted(CaptureOutcome outcome)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = outcome.Message;
            if (outcome.Item is not GearItem item) return;

            BitmapImage? crop = outcome.CropPng is { Length: > 0 } png ? BitmapFromBytes(png) : null;

            // A successful capture with a known slot fills/overwrites that slot row and selects it.
            if (outcome.Success && item.Slot != EquipSlot.Unknown
                && _rows.FirstOrDefault(r => r.Slot == item.Slot) is SlotRow row)
            {
                if (crop is not null) _crops[item.Slot] = crop;
                row.Item = item;
                SlotSheet.SelectedItem = row;   // drives ShowItem via SelectionChanged
                SlotSheet.ScrollIntoView(row);
            }
            else
            {
                // No calibrated slot (or failed read) — just show it in the detail panel.
                ShowItem(item, crop);
            }
        });
    }

    private void SlotSheet_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SlotSheet.SelectedItem is SlotRow row)
            ShowItem(row.Item, _crops.GetValueOrDefault(row.Slot));
    }

    private void ShowItem(GearItem? item, BitmapImage? crop)
    {
        // Action bar: a slot must be selected to add; an item must exist to edit/lock.
        bool slotSelected = SlotSheet.SelectedItem is SlotRow;
        EditButton.Visibility = item is not null ? Visibility.Visible : Visibility.Collapsed;
        LockButton.Visibility = item is not null ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Visibility = (item is null && slotSelected) ? Visibility.Visible : Visibility.Collapsed;
        if (item is not null)
        {
            LockButton.IsChecked = item.Locked;
            LockButton.Content = item.Locked ? "🔒 Locked" : "🔓 Lock";
        }

        if (item is null)
        {
            LastName.Text = "— empty —";
            LastMeta.Text = ""; LastReadInfo.Text = ""; LastBinding.Text = ""; RawText.Text = "";
            ModsList.ItemsSource = null; AugList.ItemsSource = null; SetList.ItemsSource = null;
            CropImage.Source = null;
            return;
        }

        LastName.Text = string.IsNullOrWhiteSpace(item.Name) ? "(no name read)" : item.Name;
        LastMeta.Text = $"ML {item.MinimumLevel?.ToString() ?? "?"}  ·  {item.Slot}  ·  {item.ItemTypeText ?? "type ?"}";
        LastReadInfo.Text = (item.IsLikelyNamed ? "likely NAMED" : "random/crafted")
                            + $"  ·  {item.Mods.Count} mods  ·  {item.CapturedUtc.ToLocalTime():HH:mm:ss}";
        ModsList.ItemsSource = item.Mods;
        AugList.ItemsSource = item.Augments;
        SetList.ItemsSource = item.SetBonuses;
        LastBinding.Text = item.Binding ?? "";
        RawText.Text = item.RawOcrText;
        CropImage.Source = crop;
    }


    private void SetHotkey_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        _bindingHotkey = true;
        BindingPrompt.Visibility = Visibility.Visible;
        // The popup orphans keyboard focus when it closes; pull it back to the window so the next
        // key press reaches OnPreviewKeyDown. Deferred so it runs after the popup actually closes.
        Dispatcher.BeginInvoke(new Action(() => { Activate(); Keyboard.Focus(this); }),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_bindingHotkey) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            _bindingHotkey = false;
            BindingPrompt.Visibility = Visibility.Collapsed;
            e.Handled = true;
            StatusText.Text = "Hotkey unchanged.";
            return;
        }

        // Wait for a non-modifier key so we capture the full combo.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        uint mod = 0;
        ModifierKeys m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Alt)) mod |= 0x0001;
        if (m.HasFlag(ModifierKeys.Control)) mod |= 0x0002;
        if (m.HasFlag(ModifierKeys.Shift)) mod |= 0x0004;
        if (m.HasFlag(ModifierKeys.Windows)) mod |= 0x0008;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        _bindingHotkey = false;
        BindingPrompt.Visibility = Visibility.Collapsed;
        e.Handled = true;

        bool ok = RebindRequested?.Invoke(mod, vk) ?? false;
        SetHotkeyStatus(ok, mod, vk);
        if (ok)
            StatusText.Text = $"Hotkey set to {DescribeHotkey(mod, vk)}.";
        else
            StatusText.Text = $"{DescribeHotkey(mod, vk)} is taken by another app — pick a different combo via ☰ Menu → Set Detection Hotkey.";
    }

    private void ClearList_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        if (MessageBox.Show($"Clear {_charStore.Active.Name}'s whole loadout?", "DDO Gear Scanner",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _store.Clear();
        foreach (SlotRow row in _rows) row.Item = null;
        _crops.Clear();
        ShowItem(null, null);
        StatusText.Text = "Loadout cleared.";
    }

    private MatrixWindow? _matrixWindow;

    private void Matrix_Click(object sender, RoutedEventArgs e)
    {
        var matrix = Vision.StackingAnalyzer.Analyze(_store.Loadout, _charStore.Active.PlaystyleKey);
        if (_matrixWindow is not null)
        {
            _matrixWindow.Update(matrix);              // reuse the open one, refreshed
            _matrixWindow.Activate();
            return;
        }
        _matrixWindow = new MatrixWindow(matrix) { Owner = this };
        _matrixWindow.Closed += (_, _) => _matrixWindow = null;
        _matrixWindow.Show();
    }

    // ---- item editing ----

    private void EditItem_Click(object sender, RoutedEventArgs e)
    {
        if (SlotSheet.SelectedItem is SlotRow row && row.Item is not null) OpenEditor(row.Item, row.Slot);
    }

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        if (SlotSheet.SelectedItem is SlotRow row) OpenEditor(null, row.Slot);
    }

    private void OpenEditor(GearItem? existing, EquipSlot slot)
    {
        var dlg = new ItemEditWindow(existing, slot) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        EquipSlot target = dlg.TargetSlot;
        if (dlg.Result is null)
        {
            _store.Remove(slot);                                   // Delete
            StatusText.Text = $"Removed item from {SlotInfo.Label(slot)}.";
        }
        else
        {
            if (existing is not null && slot != target) _store.Remove(slot); // moved to another slot
            _store.SetSlot(target, dlg.Result);
            StatusText.Text = $"Saved \"{dlg.Result.Name}\" → {SlotInfo.Label(target)}.";
        }

        // The edited slot no longer matches its capture crop; drop it so we don't show a stale image.
        _crops.Remove(slot);
        if (target != slot) _crops.Remove(target);
        AfterLoadoutChange(dlg.Result is null ? slot : target);
    }

    /// <summary>Quick lock toggle from the detail panel — no editor needed.</summary>
    private void LockItem_Click(object sender, RoutedEventArgs e)
    {
        if (SlotSheet.SelectedItem is not SlotRow row || row.Item is null) return;
        bool locked = LockButton.IsChecked == true;
        GearItem updated = row.Item with { Locked = locked };
        _store.SetSlot(row.Slot, updated);
        row.Item = updated;                                        // refreshes the badge
        LockButton.Content = locked ? "🔒 Locked" : "🔓 Lock";
        StatusText.Text = locked
            ? $"{SlotInfo.Label(row.Slot)} locked — a re-capture won't overwrite it."
            : $"{SlotInfo.Label(row.Slot)} unlocked.";
    }

    private void AfterLoadoutChange(EquipSlot focus)
    {
        RefreshLoadout();
        SlotRow? row = _rows.FirstOrDefault(r => r.Slot == focus);
        SlotSheet.SelectedItem = row;
        ShowItem(row?.Item, row is null ? null : _crops.GetValueOrDefault(row.Slot));
        if (_matrixWindow is not null)
            _matrixWindow.Update(Vision.StackingAnalyzer.Analyze(_store.Loadout, _charStore.Active.PlaystyleKey));
    }

    // ---- characters ----

    private void PopulateCharacters()
    {
        _switchingCharacter = true;
        CharacterSelector.ItemsSource = _charStore.Profiles.ToList();
        CharacterSelector.SelectedItem = _charStore.Profiles.FirstOrDefault(p => p.Id == _charStore.ActiveId);
        _switchingCharacter = false;
    }

    private void CharacterSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingCharacter || CharacterSelector.SelectedItem is not CharacterProfile p) return;
        if (p.Id == _charStore.ActiveId) return;
        SwitchToCharacter(p.Id);
    }

    private void SwitchToCharacter(string id)
    {
        _charStore.SetActive(id);
        _store.SwitchTo(id);
        RefreshLoadout();
        _crops.Clear();
        ShowItem(null, null);
        StatusText.Text = $"Switched to {_charStore.Active.Name}.";
    }

    private void RefreshLoadout()
    {
        foreach (SlotRow row in _rows) row.Item = _store.Get(row.Slot);
    }

    private void NewCharacter_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CharacterEditWindow(null, canDelete: false) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        CharacterProfile added = _charStore.Add(dlg.Result.Name, dlg.Result.Playstyle, dlg.Result.Classes, dlg.Result.Level);
        _store.SwitchTo(added.Id);
        RefreshLoadout();
        _crops.Clear();
        ShowItem(null, null);
        PopulateCharacters();
        StatusText.Text = $"Created {added.Name}.";
    }

    private void EditCharacter_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CharacterEditWindow(_charStore.Active, canDelete: _charStore.Profiles.Count > 1) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.DeleteRequested)
        {
            string newActive = _charStore.Remove(_charStore.ActiveId);
            SwitchToCharacter(newActive);
        }
        else if (dlg.Result is not null)
        {
            _charStore.Update(dlg.Result);
        }
        PopulateCharacters();
    }

    private static string DescribeHotkey(uint modifiers, uint vk)
    {
        StringBuilder sb = new();
        if ((modifiers & 0x0002) != 0) sb.Append("Ctrl+");
        if ((modifiers & 0x0001) != 0) sb.Append("Alt+");
        if ((modifiers & 0x0004) != 0) sb.Append("Shift+");
        if ((modifiers & 0x0008) != 0) sb.Append("Win+");
        Key key = KeyInterop.KeyFromVirtualKey((int)vk);
        sb.Append(key);
        return sb.ToString();
    }

    private static BitmapImage BitmapFromBytes(byte[] bytes)
    {
        BitmapImage bmp = new();
        using MemoryStream ms = new(bytes);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
