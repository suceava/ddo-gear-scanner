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

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ddo-gear-scanner.log");
    private static void Log(string m) { try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [session] {m}{Environment.NewLine}"); } catch { } }
    private int _sessionFrameCount;

    public bool SessionActive => _sessionActive;

    public bool ToggleSession() { SetSession(!_sessionActive); return _sessionActive; }

    public void SetSession(bool active)
    {
        if (_sessionActive == active) return;
        _sessionActive = active;
        if (active) _change.Reset();
        Log($"SetSession -> active={active}");
        SessionChanged?.Invoke(active);
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

        Rect? region;
        try { region = _change.OnFrame(frame, cursor); }
        catch (Exception ex) { Log($"OnFrame error {ex.GetType().Name}: {ex.Message}"); return; }

        // Cache the rag-doll anchor from CLEAN (no-tooltip) frames — at capture time a tooltip
        // covers it. Throttled so the template match stays cheap.
        // Refresh the rag-doll anchor from CLEAN frames. We only ever UPDATE it on a hit (latching
        // _invAnchorKnown true) — a single missed locate must NOT disable gating, or bag-item
        // tooltips leak through to the parser's slot guess. If the inventory moves, the anchor
        // self-corrects on the next clean frame.
        // Locate eagerly on EVERY clean frame until we have the anchor (so the no-capture window at
        // session start is tiny), then throttle to keep the frame thread cheap.
        if (_invLocator is not null && _change.LastFrameClean && (!_invAnchorKnown || ++_invTick % 20 == 0))
        {
            if (_invLocator.Locate(frame) is Point pa)
            {
                _invAnchor = pa;
                if (!_invAnchorKnown) { _invAnchorKnown = true; Log($"inv-check: ragdoll anchor @({pa.X},{pa.Y})"); }
            }
        }

        if (region is null) return;
        Log($"CAPTURE region x={region.Value.X} y={region.Value.Y} w={region.Value.Width} h={region.Value.Height}");

        Rect b = ClampRect(region.Value, frame);
        if (b.Width < 8 || b.Height < 8) return;

        // Inventory slot: if calibrated and the paper-doll is open (from the cached clean-frame
        // locate), find which equipment slot the cursor is over. On a slot → tag it. Inventory open
        // but cursor NOT on a slot (a bag item) → skip the capture (only equipped gear).
        // Once we've found the paper-doll, captures MUST land on a calibrated slot — otherwise it's a
        // bag item (or off the doll) and we skip it. We never fall back to the parser's slot guess
        // while calibrated, which is what was tagging bag items onto equipped slots.
        EquipSlot? slot = null;
        if (_slotMap is { IsCalibrated: true })
        {
            // Calibrated => the slot MUST come from inventory detection, never the parser's guess.
            // If we haven't located the paper-doll yet, skip rather than risk tagging a bag item.
            if (!_invAnchorKnown) { Log("slot-detect: no inventory anchor yet — skipping"); return; }
            slot = _slotMap.SlotAt(_invAnchor.X, _invAnchor.Y, cursor.X, cursor.Y);
            Log($"slot-detect: anchor=({_invAnchor.X},{_invAnchor.Y}) cursor=({cursor.X},{cursor.Y}) -> {slot?.ToString() ?? "null (gated, skipping)"}");
            if (slot is null) return;
        }

        OpenCvMat crop = new OpenCvMat(frame, b).Clone();

        _ = Task.Run(async () =>
        {
            try
            {
                byte[] png = EncodePng(crop);
                string? label = StartDebugDump(png);
                TooltipReadResult read = await ReadWithOptionalDebug(crop, label).ConfigureAwait(false);
                GearItem? item = read.Item;
                if (slot is EquipSlot s && item is not null) item = item with { Slot = s };
                bool ok = item is not null && (!string.IsNullOrWhiteSpace(item.Name) || item.Mods.Count > 0);
                // Overwrite the slot in the loadout (re-capturing a slot updates it, not a new entry).
                if (ok && item!.Slot != EquipSlot.Unknown) _store.SetSlot(item.Slot, item);
                Emit(new CaptureOutcome(ok,
                    ok ? $"Captured \"{item!.Name}\" → {SlotInfo.Label(item.Slot)}" : "Tooltip seen but no text read.",
                    item, read.RawText, read.Confidence, 1.0, read.Backend, png, b.X, b.Y, b.Width, b.Height));
            }
            finally { crop.Dispose(); }
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

    private static string DebugDir => Path.Combine(AppSettings.AppDataDir, "debug-crops");

    /// <summary>When debug dumping is on, write the raw crop as crop-&lt;label&gt;.png and return the
    /// shared label so the reader's diagnostic bundle (&lt;label&gt;-detect.png / -report.txt) lines
    /// up with it. Returns null when debug dumping is off.</summary>
    private static string? StartDebugDump(byte[] pngBytes)
    {
        if (!AppSettings.Instance.DebugDumpCrops) return null;
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
