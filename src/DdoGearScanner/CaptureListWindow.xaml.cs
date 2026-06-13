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
/// Main interactive window: shows the last captured item's parsed fields + crop + raw OCR text,
/// and a running list of everything scanned this/previous sessions. Normal (not click-through)
/// window — the click-through overlay is separate.
/// </summary>
public partial class CaptureListWindow : Window
{
    private readonly CaptureStore _store;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<GearItem> _items;
    // Captured tooltip images for THIS session, keyed by item (items loaded from disk have none).
    private readonly Dictionary<GearItem, BitmapImage> _crops = new();

    /// <summary>Raised when the "Scan now" button is clicked (App wires this to the pipeline).</summary>
    public event Action? ScanRequested;

    /// <summary>Raised when the user presses a new hotkey combo. Returns true if it registered.</summary>
    public event Func<uint, uint, bool>? RebindRequested;

    /// <summary>Raised when the user clicks the Detection toggle button.</summary>
    public event Action? DetectionToggleRequested;

    private bool _bindingHotkey;

    public CaptureListWindow(CaptureStore store, AppSettings settings, bool ocrAvailable)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;

        Left = settings.WindowLeft;
        Top = settings.WindowTop;

        _items = new ObservableCollection<GearItem>(store.Items.AsEnumerable().Reverse());
        ItemsList.ItemsSource = _items;

        if (!ocrAvailable)
            StatusText.Text = "⚠ Windows OCR engine unavailable — install an OCR language pack (Settings → Language).";

        LocationChanged += (_, _) => { _settings.WindowLeft = Left; _settings.WindowTop = Top; };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void SetHotkeyStatus(bool registered, uint modifiers, uint vk)
    {
        string combo = DescribeHotkey(modifiers, vk);
        HotkeyHint.Text = registered
            ? $"Hotkey: {combo}"
            : $"⚠ Hotkey {combo} is taken by another app — use \"Scan now\".";
    }

    public void NoteHotkeyHealed(uint modifiers, uint vk)
        => StatusText.Text = $"Saved hotkey was taken by another app — reverted to {DescribeHotkey(modifiers, vk)}. Rebind via \"Set hotkey\".";

    public void OnSessionChanged(bool active) => Dispatcher.Invoke(() =>
    {
        DetectionButton.Content = active ? "Detection: ON" : "Detection: OFF";
        StatusText.Text = active
            ? "● Detection ON — hover each gear piece in DDO; each tooltip is captured automatically."
            : "Detection paused. Click \"Detection: OFF\" to resume.";
    });

    private void DetectionToggle_Click(object sender, RoutedEventArgs e) => DetectionToggleRequested?.Invoke();

    public void OnCaptureCompleted(CaptureOutcome outcome)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = outcome.Message;
            if (outcome.Item is not GearItem item) return;

            BitmapImage? crop = outcome.CropPng is { Length: > 0 } png ? BitmapFromBytes(png) : null;

            if (outcome.Success)
            {
                if (crop is not null) _crops[item] = crop;
                _items.Insert(0, item);
                ItemsList.SelectedItem = item;   // drives ShowItem via SelectionChanged
                ItemsList.ScrollIntoView(item);
            }
            else
            {
                // Not added to the list, but still show it so the user can see what was read.
                ShowItem(item, crop);
            }
        });
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is GearItem item)
            ShowItem(item, _crops.GetValueOrDefault(item));
    }

    private void ShowItem(GearItem item, BitmapImage? crop)
    {
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

    private void ScanNow_Click(object sender, RoutedEventArgs e) => ScanRequested?.Invoke();

    private void SetHotkey_Click(object sender, RoutedEventArgs e)
    {
        _bindingHotkey = true;
        StatusText.Text = "Press your desired hotkey now (e.g. ScrollLock, or Ctrl+Shift+…). Esc to cancel.";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_bindingHotkey) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            _bindingHotkey = false;
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
        e.Handled = true;

        bool ok = RebindRequested?.Invoke(mod, vk) ?? false;
        SetHotkeyStatus(ok, mod, vk);
        if (ok)
            StatusText.Text = $"Hotkey set to {DescribeHotkey(mod, vk)}.";
        else
            StatusText.Text = $"{DescribeHotkey(mod, vk)} is taken by another app — press a different combo via \"Set hotkey\".";
    }

    private void ClearList_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Clear all captured items?", "DDO Gear Scanner",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _store.Clear();
        _items.Clear();
        StatusText.Text = "Cleared.";
    }

    private void DetectGameWindow_Click(object sender, RoutedEventArgs e)
    {
        List<WindowInfo> windows = GameWindowTracker.EnumerateCandidateWindows();
        StringBuilder sb = new();
        sb.AppendLine("Visible top-level windows (process / class / title):");
        sb.AppendLine();

        bool foundDdo = false;
        foreach (WindowInfo w in windows
                     .OrderByDescending(w => LooksLikeDdo(w))
                     .ThenBy(w => w.ProcessName))
        {
            bool ddo = LooksLikeDdo(w);
            if (ddo) foundDdo = true;
            sb.AppendLine($"{(ddo ? "➤ " : "  ")}{w.ProcessName,-18}  [{w.ClassName}]  {w.Title}");
        }

        StatusText.Text = foundDdo
            ? "DDO window detected (marked ➤ below). Update GameWindowTracker constants if needed."
            : "No obvious DDO window found. Is the client running in windowed/borderless mode?";
        RawText.Text = sb.ToString();
    }

    private static bool LooksLikeDdo(WindowInfo w)
        => w.ProcessName.Contains("dndclient", StringComparison.OrdinalIgnoreCase)
           || w.Title.Contains("Dungeons & Dragons Online", StringComparison.OrdinalIgnoreCase);

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
