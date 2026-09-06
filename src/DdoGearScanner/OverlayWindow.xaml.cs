using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DdoGearScanner.Capture;
using DdoGearScanner.Model;
using DdoGearScanner.Vision;

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
    private const int WS_EX_NOACTIVATE = 0x08000000;   // clicks work WITHOUT stealing focus from the game

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private const int VK_LBUTTON = 0x01;
    private struct POINT { public int X; public int Y; }

    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _highlightTimer;
    private readonly DispatcherTimer _runHudTimer;
    private readonly DispatcherTimer _hitTestTimer;   // makes ONLY the HUD box catch clicks (see UpdateClickThrough)

    private IntPtr _hwnd;
    private long _baseExStyle;        // TRANSPARENT|LAYERED|NOACTIVATE, minus TRANSPARENT which we toggle
    private bool _clickThrough = true;
    private bool _draggingHud;
    private POINT _dragAnchorCursor;  // physical cursor pos at drag start
    private double _dragStartLeft, _dragStartTop;   // Canvas.Left/Top at drag start
    private const double HudEdgeGap = 16;           // keep the box this far off the window edges

    /// <summary>Raised when the HUD's Pause/Resume button is clicked; App maps it to the pipeline.</summary>
    public event Action? PauseResumeRequested;

    // Mini run readout state: mirrors the run tracker's current/last run. The PIPELINE owns how long a completed
    // run stays up (kept while you're in the quest, then a grace period after you leave), so the HUD just follows
    // `_current` — it shows whatever the pipeline hands it and hides when the pipeline clears it.
    private RunRecord? _hudRun;
    // A quest-entry popup seen but not yet entered — previewed in the HUD ("ready", no live timer) so the readout
    // appears the moment the app detects the quest, not only once the run is in progress. A live run outranks it.
    private QuestEntry? _hudEntry;

    public OverlayWindow()
    {
        InitializeComponent();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBorder.Visibility = Visibility.Collapsed; };
        _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); RegionHighlight.Visibility = Visibility.Collapsed; };
        // Ticks the live timer (and expires the post-completion linger) for the mini run readout.
        _runHudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        _runHudTimer.Tick += (_, _) => RefreshRunHud();
        _runHudTimer.Start();
        // Fast poll: flip the overlay click-through ON/OFF by whether the cursor is over the HUD box, so ONLY the
        // HUD is interactive while everything else always passes through to the game.
        _hitTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _hitTestTimer.Tick += (_, _) => UpdateClickThrough();
        _hitTestTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        // NOACTIVATE so clicking the HUD button/dragging never steals focus from the game. TRANSPARENT is the bit
        // we toggle per-cursor-position (UpdateClickThrough); the rest is the constant base.
        _baseExStyle = ex | WS_EX_LAYERED | WS_EX_NOACTIVATE;
        _clickThrough = true;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)(_baseExStyle | WS_EX_TRANSPARENT));

        // React to setting changes without any explicit wiring (pg-loot pattern): debug region borders + the
        // run-HUD show/hide toggle both apply live.
        AppSettings.Instance.PropertyChanged += (_, _) => Dispatcher.Invoke(() => { ApplyDebug(); RefreshRunHud(); });
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
            PositionRunHud();   // keep the HUD at its saved ratio position after a move/resize
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

    // ---- gear-capture slot markers (feedback that calibration and reality line up) ----

    private readonly List<UIElement> _slotShapes = new();
    private System.Windows.Threading.DispatcherTimer? _slotFadeTimer;
    private static readonly Brush SlotStroke = new SolidColorBrush(Color.FromArgb(0xE6, 0xE6, 0xC6, 0x6A));
    private static readonly Brush SlotFill = new SolidColorBrush(Color.FromArgb(0x28, 0xE6, 0xC6, 0x6A));

    /// <summary>Draw a circle (+ tiny label) at every calibrated slot point — the user can SEE where
    /// the app expects the inventory slots and drag the window until they line up. Points are frame
    /// (physical) pixels; <paramref name="radius"/> is the slot hover tolerance. Markers FADE after a
    /// few seconds (they'd otherwise obscure the very tooltips being captured) and re-appear whenever
    /// this is called again — i.e. at session start and every time the inventory window moves.</summary>
    public void ShowSlotMarkers(IReadOnlyList<(string Label, int X, int Y)> points, int radius)
    {
        Dispatcher.Invoke(() =>
        {
            ClearSlotShapes();
            SlotHintBorder.Visibility = Visibility.Collapsed;
            if (_slotFadeTimer is null)
            {
                _slotFadeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _slotFadeTimer.Tick += (_, _) => { _slotFadeTimer.Stop(); ClearSlotShapes(); };
            }
            _slotFadeTimer.Stop();
            _slotFadeTimer.Start();
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            foreach ((string label, int x, int y) in points)
            {
                double d = radius * 2 / dpi.DpiScaleX;
                var circle = new System.Windows.Shapes.Ellipse
                {
                    Width = d, Height = d, Stroke = SlotStroke, StrokeThickness = 2, Fill = SlotFill,
                };
                Canvas.SetLeft(circle, x / dpi.DpiScaleX - d / 2);
                Canvas.SetTop(circle, y / dpi.DpiScaleY - d / 2);
                HighlightCanvas.Children.Add(circle);
                _slotShapes.Add(circle);

                var text = new TextBlock
                {
                    Text = label, FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
                    FontWeight = FontWeights.SemiBold, Foreground = SlotStroke,
                };
                Canvas.SetLeft(text, x / dpi.DpiScaleX - d / 2);
                Canvas.SetTop(text, y / dpi.DpiScaleY + d / 2 + 1);
                HighlightCanvas.Children.Add(text);
                _slotShapes.Add(text);
            }
        });
    }

    /// <summary>Show the "capture is on but the inventory isn't located" message — the previously
    /// SILENT failure (moved inventory / per-character UI scale) made visible on the game.</summary>
    public void ShowSlotHint(string text)
    {
        Dispatcher.Invoke(() =>
        {
            ClearSlotShapes();
            SlotHintText.Text = text;
            SlotHintBorder.Visibility = Visibility.Visible;
        });
    }

    public void HideSlotMarkers()
    {
        Dispatcher.Invoke(() =>
        {
            ClearSlotShapes();
            SlotHintBorder.Visibility = Visibility.Collapsed;
        });
    }

    private void ClearSlotShapes()
    {
        foreach (UIElement s in _slotShapes) HighlightCanvas.Children.Remove(s);
        _slotShapes.Clear();
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

    // ---- mini run readout (bottom-right): timer while running, XP at completion ----

    /// <summary>Feed the mini run readout from the run tracker's <c>CurrentChanged</c>. Shows a one-row HUD in
    /// the game's bottom-right WHILE a run is in progress; on completion it shows the result + XP. How long a
    /// completed run stays up is owned by the pipeline (kept while you're in the quest, then a grace period after
    /// you leave), so this just mirrors the run it's handed and hides when the pipeline clears it (null).</summary>
    public void SetCurrentRun(RunRecord? run) => Dispatcher.Invoke(() =>
    {
        _hudRun = run;   // the pipeline keeps a completed run set through its in-quest + post-leave window
        RefreshRunHud();
    });

    /// <summary>Feed the mini readout the quest-entry popup (the run tracker's <c>EntryHeld</c>) so it previews
    /// the quest the moment the popup is detected — before the run starts. A live run (SetCurrentRun) outranks
    /// it; passing null clears the preview (popup cancelled/consumed).</summary>
    public void SetPendingEntry(QuestEntry? entry) => Dispatcher.Invoke(() =>
    {
        _hudEntry = entry;
        RefreshRunHud();
    });

    private void RefreshRunHud()
    {
        if (!AppSettings.Instance.ShowRunHud)
        {
            RunHudBorder.Visibility = Visibility.Collapsed;
            return;
        }
        if (_hudRun is { } r) { ShowRunHud(r); return; }      // live/finished run wins
        if (_hudEntry is { } e) { ShowEntryHud(e); return; }  // else preview the detected popup
        RunHudBorder.Visibility = Visibility.Collapsed;
    }

    // --- Click-through toggle: make ONLY the HUD box interactive (everything else passes to the game) ---
    private void UpdateClickThrough()
    {
        if (_draggingHud) { DragTick(); SetClickThrough(false); return; }   // cursor-driven drag owns this tick
        bool want = false;
        if (RunHudBorder.Visibility == Visibility.Visible && RunHudBorder.ActualWidth > 0 && GetCursorPos(out POINT p))
        {
            Point tl = RunHudBorder.PointToScreen(new Point(0, 0));
            Point br = RunHudBorder.PointToScreen(new Point(RunHudBorder.ActualWidth, RunHudBorder.ActualHeight));
            want = p.X >= tl.X && p.X < br.X && p.Y >= tl.Y && p.Y < br.Y;
        }
        SetClickThrough(!want);
    }

    private void SetClickThrough(bool on)
    {
        if (on == _clickThrough || _hwnd == IntPtr.Zero) return;
        _clickThrough = on;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)(on ? _baseExStyle | WS_EX_TRANSPARENT : _baseExStyle));
    }

    // --- Drag the HUD within the game window; persist as a 0..1 ratio ---
    // Drag START only comes from WPF (the box is interactive under the cursor here); MOVE + RELEASE are then
    // driven by the cursor poll (GetCursorPos + async button state), which is reliable on this layered/no-activate
    // overlay where captured MouseMove/Up can be flaky.
    private void RunHud_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsWithin(e.OriginalSource as DependencyObject, HudPauseButton)) return;   // let the button click through
        if (!GetCursorPos(out _dragAnchorCursor)) return;
        _dragStartLeft = double.IsNaN(Canvas.GetLeft(RunHudBorder)) ? 0 : Canvas.GetLeft(RunHudBorder);
        _dragStartTop = double.IsNaN(Canvas.GetTop(RunHudBorder)) ? 0 : Canvas.GetTop(RunHudBorder);
        _draggingHud = true;
        e.Handled = true;
    }

    // Called from the hit-test timer while dragging: follow the cursor; end + persist on left-button release.
    private void DragTick()
    {
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) { _draggingHud = false; return; }   // button up → done
        if (!GetCursorPos(out POINT c)) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double dx = (c.X - _dragAnchorCursor.X) / dpi.DpiScaleX;
        double dy = (c.Y - _dragAnchorCursor.Y) / dpi.DpiScaleY;
        double bw = RunHudBorder.ActualWidth, bh = RunHudBorder.ActualHeight;
        double left = Clamp(_dragStartLeft + dx, ActualWidth, bw);
        double top = Clamp(_dragStartTop + dy, ActualHeight, bh);
        Canvas.SetLeft(RunHudBorder, left);
        Canvas.SetTop(RunHudBorder, top);
        // Persist the box's CENTER as a 0..1 ratio (saved every move so the 1s re-position can't snap it back).
        // Center-anchoring lets the box grow/shrink around its spot without shoving content off an edge.
        AppSettings.Instance.RunHudPosX = Math.Clamp((left + bw / 2) / Math.Max(1, ActualWidth), 0, 1);
        AppSettings.Instance.RunHudPosY = Math.Clamp((top + bh / 2) / Math.Max(1, ActualHeight), 0, 1);
    }

    // Clamp a coordinate so the box stays fully in-window with an edge gap (falls back to the gap when the box is
    // somehow larger than the space).
    private static double Clamp(double v, double windowLen, double boxLen)
    {
        double max = windowLen - boxLen - HudEdgeGap;
        return max <= HudEdgeGap ? HudEdgeGap : Math.Max(HudEdgeGap, Math.Min(v, max));
    }

    // Position the HUD on the canvas: from the persisted CENTER ratio once dragged, else default bottom-right.
    // Uses the box's DESIRED width (Canvas measures unconstrained) and clamps with an edge gap, so the box grows
    // and shrinks around its spot and never clips. Re-run on size change (SizeChanged) so it stays fluid.
    private void PositionRunHud()
    {
        if (_draggingHud) return;
        double bw = RunHudBorder.ActualWidth, bh = RunHudBorder.ActualHeight;
        if (bw <= 0 || ActualWidth <= 0) return;
        AppSettings s = AppSettings.Instance;
        double left, top;
        if (s.RunHudPosX < 0 || s.RunHudPosY < 0)   // never dragged → default bottom-right
        {
            left = ActualWidth - bw - HudEdgeGap;
            top = ActualHeight - bh - HudEdgeGap;
        }
        else
        {
            left = s.RunHudPosX * ActualWidth - bw / 2;
            top = s.RunHudPosY * ActualHeight - bh / 2;
        }
        left = Clamp(left, ActualWidth, bw);
        top = Clamp(top, ActualHeight, bh);
        if (Canvas.GetLeft(RunHudBorder) != left) Canvas.SetLeft(RunHudBorder, left);
        if (Canvas.GetTop(RunHudBorder) != top) Canvas.SetTop(RunHudBorder, top);
    }

    // Content changed the box size (XP appears on completion, longer name, etc.) — re-apply the center-ratio
    // position so it grows/shrinks around its spot and stays fully in-window instead of clipping at an edge.
    private void RunHud_SizeChanged(object sender, SizeChangedEventArgs e) => PositionRunHud();

    private void HudPause_Click(object sender, RoutedEventArgs e) => PauseResumeRequested?.Invoke();

    // Pause/Play glyphs drawn as vector shapes (dark, to contrast the light default button) — no icon-font risk.
    private static readonly Brush HudIconBrush = FrozenBrush(0xE6, 0xC6, 0x6A);   // gold — visible on the dark HUD
    // Both icons live in a FIXED 12x12 host so swapping pause<->play never changes the button (and box) width — a
    // width change is what made the whole HUD jump on click.
    private static UIElement PauseIcon()
    {
        var host = new Grid { Width = 12, Height = 12 };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new Rectangle { Width = 3.5, Height = 12, Fill = HudIconBrush, Margin = new Thickness(0, 0, 3, 0) });
        sp.Children.Add(new Rectangle { Width = 3.5, Height = 12, Fill = HudIconBrush });
        host.Children.Add(sp);
        return host;
    }
    private static UIElement PlayIcon()
    {
        var host = new Grid { Width = 12, Height = 12 };
        host.Children.Add(new System.Windows.Shapes.Path
        {
            Fill = HudIconBrush,
            Data = Geometry.Parse("M1,0 L12,6 L1,12 Z"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return host;
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    // Live/finished run: dot + name + difficulty + running timer (+ XP at completion).
    private void ShowRunHud(RunRecord r)
    {
        bool done = r.Completed, paused = r.Paused;
        bool warn = r.XpMissing; // completed but XP never read — the time-sensitive miss (chat scrolls away)
        RunHudDot.Fill = warn ? HudWarn : done ? HudGreen : paused ? HudAmber : HudLive;
        // Paused reads at a glance — WITHOUT any size change (constant thickness + fixed-size icon → no jump on
        // click): a strong ORANGE border + orange timer + amber-tinted fill, plus the amber dot and ▶ button.
        RunHudBorder.BorderBrush = warn ? HudWarn : done ? HudGreen : paused ? HudPausedAccent : HudGold;
        RunHudBorder.Background = paused ? HudBgPaused : HudBgDefault;
        RunHudName.Text = string.IsNullOrWhiteSpace(r.DungeonName) ? "(unnamed quest)" : r.DungeonName;
        // No "· paused" text — the amber dot + the ▶ button already signal paused, and a changing suffix would
        // grow the box and push the button off-screen.
        RunHudDiff.Text = string.IsNullOrWhiteSpace(r.Difficulty) ? "" : "· " + r.Difficulty;
        RunHudTimer.Text = FmtElapsed(r.Elapsed(DateTime.UtcNow));
        RunHudTimer.Foreground = paused ? HudPausedAccent : HudTimer;
        if (warn)
        {
            RunHudXp.Text = "⚠ XP not read — check chat";
            RunHudXp.Foreground = HudWarn;
            RunHudXp.Visibility = Visibility.Visible;
        }
        else if (done && r.Xp is { } xp)
        {
            RunHudXp.Text = $"+{xp:N0} XP";
            RunHudXp.Foreground = HudGreen;
            RunHudXp.Visibility = Visibility.Visible;
        }
        else RunHudXp.Visibility = Visibility.Collapsed;
        // Pause/Resume only for a LIVE run (running or paused) — not a finished/lingering card.
        HudPauseButton.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        HudPauseButton.Content = paused ? PlayIcon() : PauseIcon();   // drawn shapes — no icon-font dependency
        HudPauseButton.ToolTip = paused ? "Resume run" : "Pause run";
        RunHudBorder.Visibility = Visibility.Visible;
        PositionRunHud();
    }

    // Quest-entry popup detected (not yet entered): preview the quest that's about to start — name + difficulty +
    // level, an amber "ready" dot, and "ready" where the live timer will appear. Hands off to ShowRunHud on start.
    private void ShowEntryHud(QuestEntry e)
    {
        RunHudDot.Fill = HudAmber;
        RunHudBorder.BorderBrush = HudGold;
        RunHudName.Text = string.IsNullOrWhiteSpace(e.Name) ? "(quest)" : e.Name;
        string diff = string.IsNullOrWhiteSpace(e.Difficulty) ? "" : e.Difficulty!;
        string lvl = e.QuestLevel is { } l ? $"L{l}" : "";
        string tail = diff;
        if (lvl.Length > 0) tail = tail.Length > 0 ? $"{tail} · {lvl}" : lvl;
        RunHudDiff.Text = tail.Length > 0 ? "· " + tail : "";
        RunHudTimer.Text = "ready";
        RunHudXp.Visibility = Visibility.Collapsed;
        HudPauseButton.Visibility = Visibility.Collapsed;   // nothing to pause until the run starts
        RunHudBorder.Visibility = Visibility.Visible;
        PositionRunHud();
    }

    private static string FmtElapsed(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static readonly Brush HudGreen = FrozenBrush(0x8F, 0xCF, 0x8A);  // completed
    private static readonly Brush HudAmber = FrozenBrush(0xE8, 0xB3, 0x4A);  // paused
    private static readonly Brush HudGold = FrozenBrush(0xE0, 0xA0, 0x30);   // in-progress border
    private static readonly Brush HudLive = FrozenBrush(0x35, 0xC2, 0x6B);   // in-progress dot
    private static readonly Brush HudWarn = FrozenBrush(0xE8, 0x7A, 0x3A);   // completed but XP not read
    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
    private static Brush FrozenArgb(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
    private static readonly Brush HudBgDefault = FrozenArgb(0xE0, 0x10, 0x14, 0x18);   // matches the XAML default
    private static readonly Brush HudBgPaused = FrozenArgb(0xE6, 0x3A, 0x28, 0x0C);    // clearly amber-tinted dark
    private static readonly Brush HudPausedAccent = FrozenBrush(0xF2, 0x8A, 0x2A);     // strong orange — paused border+timer
    private static readonly Brush HudTimer = FrozenBrush(0xE6, 0xC6, 0x6A);            // gold — running timer
}
