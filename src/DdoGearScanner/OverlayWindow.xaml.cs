using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DdoGearScanner.Capture;

namespace DdoGearScanner;

/// <summary>
/// Transparent, click-through, topmost overlay that follows the DDO window and shows a brief
/// status toast after each capture. Same click-through technique as pg-loot-master's overlay
/// (WS_EX_TRANSPARENT | WS_EX_LAYERED). Reserved for richer on-game drawing in a later phase.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _highlightTimer;

    public OverlayWindow()
    {
        InitializeComponent();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBorder.Visibility = Visibility.Collapsed; };
        _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.0) };
        _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); RegionHighlight.Visibility = Visibility.Collapsed; };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TRANSPARENT | WS_EX_LAYERED));

        // React to debug-setting changes without any explicit wiring (pg-loot pattern).
        AppSettings.Instance.PropertyChanged += (_, _) => Dispatcher.Invoke(ApplyDebug);
        ApplyDebug();
    }

    public void AttachTracker(GameWindowTracker tracker)
    {
        tracker.GameWindowChanged += OnGameWindowChanged;
        tracker.GameWindowLost += OnGameWindowLost;
    }

    private void OnGameWindowChanged(IntPtr handle, GameWindowRect rect)
    {
        Dispatcher.Invoke(() =>
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Left = rect.Left / dpi.DpiScaleX;
            Top = rect.Top / dpi.DpiScaleY;
            Width = Math.Max(1, rect.Width / dpi.DpiScaleX);
            Height = Math.Max(1, rect.Height / dpi.DpiScaleY);
            Visibility = Visibility.Visible;
            ApplyDebug();   // the borders are ratio-based; refit when the window moves/resizes
        });
    }

    // --- Debug overlays, driven straight off AppSettings (react to changes, like pg-loot's overlay) ---

    /// <summary>Re-read the debug settings and show/position the region borders + chat panel accordingly.
    /// Called on any settings change and when the game window moves.</summary>
    private void ApplyDebug()
    {
        AppSettings s = AppSettings.Instance;
        bool borders = s.DebugMode && s.RunDebugOverlay;
        Place(DebugPopupBorder, DebugPopupLabel, s.CompletionX0, s.CompletionY0, s.CompletionX1, s.CompletionY1, borders);
        Place(DebugTrackerBorder, DebugTrackerLabel, s.TrackerX0, s.TrackerY0, s.TrackerX1, s.TrackerY1, borders);
        Place(DebugChatBorder, DebugChatLabel, s.ChatX0, s.ChatY0, s.ChatX1, s.ChatY1, borders);
    }

    private void Place(Rectangle border, TextBlock label, double x0, double y0, double x1, double y1, bool show)
    {
        Visibility v = show ? Visibility.Visible : Visibility.Collapsed;
        border.Visibility = v;
        label.Visibility = v;
        if (!show) return;
        double x = x0 * ActualWidth, y = y0 * ActualHeight;
        Canvas.SetLeft(border, x); Canvas.SetTop(border, y);
        border.Width = Math.Max(0, (x1 - x0) * ActualWidth);
        border.Height = Math.Max(0, (y1 - y0) * ActualHeight);
        Canvas.SetLeft(label, x + 3);
        Canvas.SetTop(label, Math.Max(0, y - 20));
    }


    private void OnGameWindowLost() => Dispatcher.Invoke(() => Visibility = Visibility.Collapsed);

    /// <summary>Draw a rectangle over the detected tooltip region. Bounds are in frame (physical)
    /// pixels relative to the game client; convert to overlay DIPs via the current DPI scale.</summary>
    public void ShowRegionHighlight(int x, int y, int w, int h, bool success)
    {
        if (w <= 0 || h <= 0) return;
        Dispatcher.Invoke(() =>
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Canvas.SetLeft(RegionHighlight, x / dpi.DpiScaleX);
            Canvas.SetTop(RegionHighlight, y / dpi.DpiScaleY);
            RegionHighlight.Width = w / dpi.DpiScaleX;
            RegionHighlight.Height = h / dpi.DpiScaleY;
            RegionHighlight.Stroke = new SolidColorBrush(success
                ? Color.FromRgb(0xE0, 0xA0, 0x30)   // gold = captured
                : Color.FromRgb(0xE0, 0x60, 0x40));  // red-ish = nothing usable
            RegionHighlight.Visibility = Visibility.Visible;
            _highlightTimer.Stop();
            _highlightTimer.Start();
        });
    }

    /// <summary>Show a toast. When <paramref name="sticky"/>, it stays up until the next toast
    /// (used for calibration prompts) instead of auto-hiding.</summary>
    public void ShowToast(string text, bool success, bool sticky = false)
    {
        Dispatcher.Invoke(() =>
        {
            ToastText.Text = text;
            ToastBorder.BorderBrush = new SolidColorBrush(success
                ? Color.FromRgb(0x35, 0xC2, 0x6B)
                : Color.FromRgb(0xE0, 0xA0, 0x30));
            ToastBorder.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            if (!sticky) _toastTimer.Start();
        });
    }
}
