using System.IO;
using DdoGearScanner.Capture;
using DdoGearScanner.Model;
using DdoGearScanner.Vision;
using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner;

/// <summary>The outcome of one capture, marshaled to the UI. Region* are the detected tooltip
/// bounds in frame (physical) pixels relative to the game client, for the overlay highlight.</summary>
public sealed record CaptureOutcome(
    bool Success,
    string Message,
    GearItem? Item,
    string RawText,
    double ReadConfidence,
    double RegionConfidence,
    string Backend,
    byte[]? CropPng,
    int RegionX = 0,
    int RegionY = 0,
    int RegionW = 0,
    int RegionH = 0);

/// <summary>
/// Orchestrates a single on-demand capture: grab the latest frame, map the cursor into frame
/// space, locate the tooltip box, OCR/parse it, persist it, and raise <see cref="Completed"/>.
/// Heavy work runs off the UI thread; the UI subscriber marshals back via Dispatcher.
/// </summary>
public sealed class CapturePipeline
{
    private readonly GameWindowTracker _tracker;
    private readonly ITooltipReader _reader;
    private readonly CaptureStore _store;

    public event Action<CaptureOutcome>? Completed;

    // Motion-based "detection session": diff the streamed frames near the cursor and capture each
    // tooltip as it settles. Border/quality independent (works for normals too).
    private readonly TooltipChangeDetector _change = new();
    private volatile bool _sessionActive;
    public event Action<bool>? SessionChanged;

    // Optional inventory slot detection (rag-doll anchor + calibrated slot offsets).
    private InventoryLocator? _invLocator;
    private SlotMap? _slotMap;
    public void SetInventory(InventoryLocator? locator, SlotMap? map) { _invLocator = locator; _slotMap = map; }
    private Point _invAnchor;     // rag-doll position, refreshed on clean frames; never goes stale-false
    private bool _invAnchorKnown; // have we located the rag-doll at least once this session (latches true)
    private int _invTick;
    private int _captureSeq;      // monotonically increasing detection id — outcomes from older detections are "stale"
    private int _readsInFlight;    // concurrent tooltip reads; capped so a burst can't queue up and lag everything
    private const int MaxReadsInFlight = 3;
    private int _invLocalMisses;  // consecutive local-window locate misses (every 3rd escalates to a full sweep)
    private double _invBestScore; // best (failed) template score — the "why isn't it matching" diagnostic
    private long _lastScoreLogTick;
    private Point _lastEmittedAnchor = new(-9999, -9999);
    private bool _lastEmittedActive;
    private bool _lastEmittedKnown;

    // Change-detection ROI derived from the calibration: tooltips can only appear near the inventory
    // (slot points + a tooltip-sized margin), so once the anchor is known the differ ignores the rest of
    // the frame — background motion (waterfalls!) can no longer churn the baseline or fragment the diff.
    // Quantized so anchor jitter doesn't move it; a real move (inventory dragged) resets the detector.
    private const int RoiMargin = 1200;
    private const int RoiQuantize = 128;
    private Rect? _roi;

    /// <summary>Re-emit the current overlay state (e.g. after the Show-slot-markers setting toggles).</summary>
    public void RefreshSlotOverlay() => EmitSlotOverlay(force: true);

    /// <summary>Fired the INSTANT a tooltip region is detected (before OCR/LLM) — drives the immediate
    /// capture highlight; the <see cref="CaptureOutcome"/> event later re-colors it with the result.</summary>
    public event Action<int, int, int, int>? RegionDetected;

    /// <summary>Fired as soon as the capture's crop exists (milliseconds after detection, before the
    /// read) — the loadout sheet shows the SHOT + a "processing" state immediately, so the read latency
    /// (1-2.5s with the LLM) is visible progress instead of dead air.</summary>
    public event Action<EquipSlot?, byte[]>? CaptureStarted;

    /// <summary>What the game overlay should show for gear capture: nothing (inactive), the calibrated
    /// slot markers (anchor known — points in frame pixels), or a "not located" hint (anchor unknown).</summary>
    public sealed record SlotOverlayState(bool SessionActive, bool AnchorKnown, bool Calibrated,
        IReadOnlyList<(string Label, int X, int Y)> Points, int Radius, double BestScore);
    public event Action<SlotOverlayState>? SlotOverlayChanged;

    private void EmitSlotOverlay(bool force = false)
    {
        if (!force && _lastEmittedActive == _sessionActive && _lastEmittedKnown == _invAnchorKnown
            && _lastEmittedAnchor == _invAnchor) return;
        _lastEmittedActive = _sessionActive; _lastEmittedKnown = _invAnchorKnown; _lastEmittedAnchor = _invAnchor;

        var points = new List<(string, int, int)>();
        if (_invAnchorKnown && _slotMap is { IsCalibrated: true } map)
            foreach ((EquipSlot slot, int[] off) in map.Offsets)
                if (off.Length >= 2)
                    points.Add((SlotInfo.Label(slot), _invAnchor.X + off[0], _invAnchor.Y + off[1]));
        SlotOverlayChanged?.Invoke(new SlotOverlayState(_sessionActive, _invAnchorKnown,
            _slotMap is { IsCalibrated: true }, points, _slotMap?.Radius ?? 26, _invBestScore));
    }

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ddo-gear-scanner.log");
    private static void Log(string m) { try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [session] {m}{Environment.NewLine}"); } catch { } }

    /// <summary>Recompute the change-detection ROI from the anchor + slot points + a tooltip-sized
    /// margin, quantized so anchor jitter can't move it. A REAL move (inventory dragged past the
    /// quantum) resets the detector — its baseline is a crop of the old view.</summary>
    private void UpdateRoi(OpenCvMat frame)
    {
        if (_slotMap is not { IsCalibrated: true } map || !_invAnchorKnown) return;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (int[] off in map.Offsets.Values)
        {
            if (off.Length < 2) continue;
            minX = Math.Min(minX, _invAnchor.X + off[0]); maxX = Math.Max(maxX, _invAnchor.X + off[0]);
            minY = Math.Min(minY, _invAnchor.Y + off[1]); maxY = Math.Max(maxY, _invAnchor.Y + off[1]);
        }
        if (minX == int.MaxValue) return;

        int x0 = Math.Max(0, (minX - RoiMargin) / RoiQuantize * RoiQuantize);
        int y0 = Math.Max(0, (minY - RoiMargin) / RoiQuantize * RoiQuantize);
        int x1 = Math.Min(frame.Width, (maxX + RoiMargin + RoiQuantize - 1) / RoiQuantize * RoiQuantize);
        int y1 = Math.Min(frame.Height, (maxY + RoiMargin + RoiQuantize - 1) / RoiQuantize * RoiQuantize);
        if (x1 - x0 < 200 || y1 - y0 < 200) return;   // degenerate — keep full-frame

        Rect next = new(x0, y0, x1 - x0, y1 - y0);
        if (_roi == next) return;
        _roi = next;
        _change.Reset();
        Log($"roi: change detection constrained to x={next.X} y={next.Y} w={next.Width} h={next.Height}");
    }
    private int _sessionFrameCount;

    public bool SessionActive => _sessionActive;

    public bool ToggleSession() { SetSession(!_sessionActive); return _sessionActive; }

    public void SetSession(bool active)
    {
        if (_sessionActive == active) return;
        _sessionActive = active;
        if (active) { _change.Reset(); _roi = null; }
        Log($"SetSession -> active={active}");
        SessionChanged?.Invoke(active);
        EmitSlotOverlay(force: true);
    }

    /// <summary>Feed every captured frame here (wired to CaptureCoordinator.FrameArrived). Only
    /// does work while a session is active. The frame is owned by the caller/event, so we clone the
    /// crop before reading off-thread.</summary>
    public void OnFrame(OpenCvMat frame)
    {
        if (!_sessionActive || frame.Empty()) return;
        GameWindowRect? rect = _tracker.CurrentRect;
        if (rect is null) return;
        (int cxs, int cys) = GameWindowTracker.GetCursorScreenPosition();
        Point cursor = new(cxs - rect.Value.Left, cys - rect.Value.Top);

        if (++_sessionFrameCount % 120 == 0) Log($"frame#{_sessionFrameCount} cursor=({cursor.X},{cursor.Y}) change={_change.LastChangeInfo}");

        // Constrain the differ to the calibration-derived ROI when we have one: tooltips can only appear
        // near the inventory, so a waterfall at the frame edge stops feeding the change detector.
        Rect? region;
        try
        {
            if (_roi is Rect roi)
            {
                using OpenCvMat sub = new(frame, roi);
                Point roiCursor = new(
                    Math.Clamp(cursor.X - roi.X, 0, roi.Width - 1),
                    Math.Clamp(cursor.Y - roi.Y, 0, roi.Height - 1));
                region = _change.OnFrame(sub, roiCursor);
                if (region is Rect r) region = new Rect(r.X + roi.X, r.Y + roi.Y, r.Width, r.Height);
            }
            else
            {
                region = _change.OnFrame(frame, cursor);
            }
        }
        catch (Exception ex) { Log($"OnFrame error {ex.GetType().Name}: {ex.Message}"); return; }

        // Refresh the rag-doll anchor. NOT gated on the differ being "clean" — that coupling
        // deadlocked: after the inventory window moves, the stale baseline keeps the differ in a
        // permanent not-clean state (too changed for quiet, too little for a re-base), so the locate
        // never ran again and the anchor froze at the old spot. A locate on a frame where a tooltip
        // covers the doll simply fails the 0.6 threshold and the old anchor is kept — harmless.
        // COST: once known, search only a small window around the last anchor (a full 4K sweep stalls
        // the frame thread ~100ms and made capture feel sluggish); full-sweep only on every 3rd miss,
        // which is what re-finds a far-dragged window or a fresh layout.
        if (_invLocator is not null && (!_invAnchorKnown ? ++_invTick % 3 == 0 : ++_invTick % 20 == 0))
        {
            Rect? searchIn = _invAnchorKnown
                ? new Rect(_invAnchor.X - 400, _invAnchor.Y - 400,
                           _invLocator.TemplateWidth + 800, _invLocator.TemplateHeight + 800)
                : null;
            Point? hit = _invLocator.Locate(frame, out double score, searchIn);
            if (hit is null && searchIn is not null && ++_invLocalMisses % 3 == 0)
                hit = _invLocator.Locate(frame, out score);   // occasional full sweep catches big jumps
            if (hit is not null) _invLocalMisses = 0;

            if (hit is Point pa)
            {
                bool moved = _invAnchorKnown && (Math.Abs(pa.X - _invAnchor.X) > 24 || Math.Abs(pa.Y - _invAnchor.Y) > 24);
                _invAnchor = pa;
                if (!_invAnchorKnown) { _invAnchorKnown = true; Log($"inv-check: ragdoll anchor @({pa.X},{pa.Y})"); }
                Rect? roiBefore = _roi;
                UpdateRoi(frame);
                if (moved && _roi == roiBefore)
                {
                    // Inventory moved but the quantized ROI didn't: the baseline still shows the window
                    // at its OLD position — a phantom blob that also wedges the "clean" state. Re-baseline.
                    _change.Reset();
                    Log($"inv-check: inventory moved -> anchor @({pa.X},{pa.Y}), detector re-baselined");
                }
            }
            else if (!_invAnchorKnown)
            {
                _invBestScore = Math.Max(_invBestScore, score);
                // The "why is nothing saving" diagnostic — a best score ~0.4 with the inventory open
                // means SCALE mismatch (DDO's UI scale is per-character); ~0.1 means it isn't visible.
                long now = Environment.TickCount64;
                if (now - _lastScoreLogTick > 3000)
                {
                    _lastScoreLogTick = now;
                    Log($"inv-check: ragdoll NOT found (best score {score:F2}, threshold 0.6)");
                }
            }
        }
        EmitSlotOverlay();

        // A capture only makes sense when the cursor is actually over the play area near the inventory:
        // with the mouse on another monitor the earlier ROI-clamped cursor let phantom blobs (e.g. the
        // just-moved window vs a stale baseline) fire junk captures at the ROI edge.
        if (region is not null
            && (cursor.X < 0 || cursor.Y < 0 || cursor.X >= frame.Width || cursor.Y >= frame.Height))
            region = null;

        if (region is null) return;

        Rect b = ClampRect(region.Value, frame);
        if (b.Width < 8 || b.Height < 8) return;

        // Inventory slot detection FIRST — before drawing anything. A tooltip over a BAG item / anything
        // that isn't a calibrated equipment slot must not even flash a border, since nothing will happen
        // (the border made it look like a capture was pending). Once the paper-doll is located, a capture
        // MUST land on a calibrated slot — else it's a bag item and we skip silently.
        EquipSlot? slot = null;
        if (_slotMap is { IsCalibrated: true })
        {
            if (!_invAnchorKnown) return;   // paper-doll not located yet — don't risk tagging a bag item
            slot = _slotMap.SlotAt(_invAnchor.X, _invAnchor.Y, cursor.X, cursor.Y);
            if (slot is null) return;       // not on an equipment slot (bag item) — no border, no capture
        }
        Log($"CAPTURE region x={b.X} y={b.Y} w={b.Width} h={b.Height} slot={slot?.ToString() ?? "(uncalibrated)"}");

        // Backlog guard: cap concurrent reads. Shimmering scenery (or fast hovering) can detect faster
        // than a ~2s LLM read completes; without a cap the reads QUEUE and every later tooltip's feedback
        // shows up seconds late (borders in the wrong place, stuck "processing"). Beyond the cap, drop.
        if (Interlocked.Increment(ref _readsInFlight) > MaxReadsInFlight)
        {
            Interlocked.Decrement(ref _readsInFlight);
            Log("capture dropped — read backlog");
            return;
        }

        // Instant border — now that we KNOW this is a real gear-slot capture.
        int seq = ++_captureSeq;
        RegionDetected?.Invoke(b.X, b.Y, b.Width, b.Height);

        // A user-locked slot is never overwritten by a re-capture (skip before the read work).
        if (slot is EquipSlot locked && _store.IsLocked(locked))
        {
            Interlocked.Decrement(ref _readsInFlight);
            Log($"slot {locked} is locked — skipping re-capture");
            Emit(new CaptureOutcome(false, $"{SlotInfo.Label(locked)} is locked — re-capture skipped",
                null, string.Empty, 0, 0, string.Empty, null));
            return;
        }

        OpenCvMat crop = new OpenCvMat(frame, b).Clone();

        _ = Task.Run(async () =>
        {
            try
            {
                byte[] png = EncodePng(crop);
                CaptureStarted?.Invoke(slot, png);   // sheet shows the shot + "processing" NOW
                string? label = StartDebugDump(png);
                TooltipReadResult read = await ReadWithOptionalDebug(crop, label).ConfigureAwait(false);
                GearItem? item = read.Item;
                if (slot is EquipSlot s && item is not null) item = item with { Slot = s };

                // Named-item match against the DDOBuilder catalog — but HOW MUCH to take from it depends
                // on who read the tooltip:
                //  - Local OCR: swap in the catalog's clean mods wholesale (the name reads far more
                //    reliably than a dozen mod lines; catalog data rescues garbage parses).
                //  - LLM (OpenRouter): the tooltip read is MORE accurate than the catalog — the catalog
                //    flattens names ("Efficient Metamagic - Extend II" → "Efficient Extend II",
                //    "Illusion Focus +2" → "Illusion +0") and can match the WRONG VARIANT of an item,
                //    clobbering real rolled values. Canonicalize the item NAME only; keep the LLM's mods.
                if (item is not null && !string.IsNullOrWhiteSpace(item.Name))
                {
                    ItemMatch? match = NamedItemMatcher.TryMatch(item.Name, item.Slot, item.MinimumLevel);
                    if (match is { HighConfidence: true })
                        item = read.Backend == "OpenRouter"
                            ? item with { Name = match.Item.Name, IsLikelyNamed = true, Matched = true }
                            : NamedItemMatcher.Apply(item, match.Item);
                }

                bool ok = item is not null && (!string.IsNullOrWhiteSpace(item.Name) || item.Mods.Count > 0);
                // Overwrite the slot in the loadout (re-capturing a slot updates it, not a new entry),
                // unless the user locked it (uncalibrated path can resolve the slot only here).
                if (ok && item!.Slot != EquipSlot.Unknown && !_store.IsLocked(item.Slot)) _store.SetSlot(item.Slot, item);
                // Only carry the region if this is STILL the newest detection — the slow (LLM) read means
                // the user may already be hovering the NEXT tooltip, and redrawing this one's box over it
                // looked like a broken highlight. Zeroed region = the overlay skips the redraw.
                bool stale = seq != _captureSeq;
                Emit(new CaptureOutcome(ok,
                    ok ? $"{(item!.Matched ? "Matched" : "Captured")} \"{item!.Name}\" → {SlotInfo.Label(item.Slot)}" : "Tooltip seen but no text read.",
                    item, read.RawText, read.Confidence, 1.0, read.Backend, png,
                    stale ? 0 : b.X, stale ? 0 : b.Y, stale ? 0 : b.Width, stale ? 0 : b.Height));
            }
            catch (Exception ex)
            {
                // An outcome must ALWAYS land — a swallowed exception here left the sheet stuck in
                // "processing" with no feedback.
                Log($"capture read error {ex.GetType().Name}: {ex.Message}");
                Emit(new CaptureOutcome(false, "Read failed — see log.", null, string.Empty, 0, 0, string.Empty, null));
            }
            finally { crop.Dispose(); Interlocked.Decrement(ref _readsInFlight); }
        });
    }

    private static Rect ClampRect(Rect r, OpenCvMat frame)
    {
        int x = Math.Clamp(r.X, 0, frame.Width - 1);
        int y = Math.Clamp(r.Y, 0, frame.Height - 1);
        int w = Math.Clamp(r.Width, 1, frame.Width - x);
        int h = Math.Clamp(r.Height, 1, frame.Height - y);
        return new Rect(x, y, w, h);
    }

    public CapturePipeline(GameWindowTracker tracker, ITooltipReader reader, CaptureStore store)
    {
        _tracker = tracker;
        _reader = reader;
        _store = store;
    }

    private void Emit(CaptureOutcome outcome) => Completed?.Invoke(outcome);

    private static byte[] EncodePng(OpenCvMat crop)
    {
        Cv2.ImEncode(".png", crop, out byte[] bytes);
        return bytes;
    }

    private static string DebugDir => DebugPaths.Gear;

    /// <summary>When debug dumping is on, write the raw crop as crop-&lt;label&gt;.png and return the
    /// shared label so the reader's diagnostic bundle (&lt;label&gt;-detect.png / -report.txt) lines
    /// up with it. Returns null when debug dumping is off.</summary>
    private static string? StartDebugDump(byte[] pngBytes)
    {
        if (!AppSettings.Instance.DebugMode || !AppSettings.Instance.DebugDumpGearCrops) return null;
        try
        {
            Directory.CreateDirectory(DebugDir);
            string label = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            File.WriteAllBytes(Path.Combine(DebugDir, $"crop-{label}.png"), pngBytes);
            return label;
        }
        catch { return null; }
    }

    /// <summary>Read the crop, additionally writing the full diagnostic bundle when a debug label is
    /// present and the reader supports it (the local OCR reader does).</summary>
    private Task<TooltipReadResult> ReadWithOptionalDebug(OpenCvMat crop, string? label)
        => label is not null && _reader is LocalOcrTooltipReader local
            ? local.ReadAsync(crop, DebugDir, label)
            : _reader.ReadAsync(crop);
}
