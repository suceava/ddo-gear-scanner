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
    private readonly FrameGrabber _grabber;
    private readonly ITooltipRegionDetector _detector;
    private readonly ITooltipReader _reader;
    private readonly CaptureStore _store;

    public event Action<CaptureOutcome>? Completed;

    // Motion-based "detection session": diff the streamed frames near the cursor and capture each
    // tooltip as it settles. Border/quality independent (works for normals too).
    private readonly TooltipChangeDetector _change = new();
    private volatile bool _sessionActive;
    public event Action<bool>? SessionChanged;

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
        if (region is null) return;
        Log($"CAPTURE region x={region.Value.X} y={region.Value.Y} w={region.Value.Width} h={region.Value.Height}");

        Rect b = ClampRect(region.Value, frame);
        if (b.Width < 8 || b.Height < 8) return;
        OpenCvMat crop = new OpenCvMat(frame, b).Clone();

        _ = Task.Run(async () =>
        {
            try
            {
                byte[] png = EncodePng(crop);
                DumpDebugCrop(png);
                TooltipReadResult read = await _reader.ReadAsync(crop).ConfigureAwait(false);
                GearItem? item = read.Item;
                bool ok = item is not null && (!string.IsNullOrWhiteSpace(item.Name) || item.Mods.Count > 0);
                if (ok) _store.Append(item!);
                Emit(new CaptureOutcome(ok,
                    ok ? $"Captured \"{item!.Name}\" ({item.Mods.Count} mods) — next piece" : "Tooltip seen but no text read.",
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

    public CapturePipeline(
        GameWindowTracker tracker,
        FrameGrabber grabber,
        ITooltipRegionDetector detector,
        ITooltipReader reader,
        CaptureStore store)
    {
        _tracker = tracker;
        _grabber = grabber;
        _detector = detector;
        _reader = reader;
        _store = store;
    }

    /// <summary>Fire-and-forget capture; result arrives via <see cref="Completed"/>.</summary>
    public void RequestCapture() => _ = Task.Run(RunOnceAsync);

    private async Task RunOnceAsync()
    {
        try
        {
            if (!_reader.IsAvailable)
            {
                Emit(Fail($"{_reader.BackendName} unavailable (no OCR language pack?)"));
                return;
            }

            GameWindowRect? rect = _tracker.CurrentRect;
            if (rect is null)
            {
                Emit(Fail("DDO window not found. Is the game running in windowed/borderless mode?"));
                return;
            }

            using OpenCvMat? frame = _grabber.GrabLatest();
            if (frame is null)
            {
                Emit(Fail("No captured frame yet. Give capture a moment after the game appears."));
                return;
            }

            (int curX, int curY) = GameWindowTracker.GetCursorScreenPosition();
            Point cursorInFrame = new(curX - rect.Value.Left, curY - rect.Value.Top);

            // Dump the full frame on every scan (even failures), with the cursor position encoded
            // in the filename, so detection can be tuned offline against ground truth.
            DumpDebugFrame(frame, cursorInFrame);

            TooltipRegion? region = _detector.Detect(frame, cursorInFrame);
            if (region is null)
            {
                Emit(Fail("Could not locate a tooltip near the cursor."));
                return;
            }

            Rect bounds = region.Value.Bounds;
            using OpenCvMat crop = new(frame, bounds);
            byte[] cropPng = EncodePng(crop);
            DumpDebugCrop(cropPng);

            TooltipReadResult read = await _reader.ReadAsync(crop).ConfigureAwait(false);
            GearItem? item = read.Item;

            if (item is not null && (!string.IsNullOrWhiteSpace(item.Name) || item.Mods.Count > 0))
            {
                _store.Append(item);
                Emit(new CaptureOutcome(
                    Success: true,
                    Message: $"Captured \"{item.Name}\" ({item.Mods.Count} mods)",
                    Item: item,
                    RawText: read.RawText,
                    ReadConfidence: read.Confidence,
                    RegionConfidence: region.Value.Confidence,
                    Backend: read.Backend,
                    CropPng: cropPng,
                    RegionX: bounds.X, RegionY: bounds.Y, RegionW: bounds.Width, RegionH: bounds.Height));
            }
            else
            {
                Emit(new CaptureOutcome(false, "Read produced no usable text — see raw output.",
                    item, read.RawText, read.Confidence, region.Value.Confidence, read.Backend, cropPng,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height));
            }
        }
        catch (Exception ex)
        {
            Emit(Fail($"Capture error: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private void Emit(CaptureOutcome outcome) => Completed?.Invoke(outcome);

    private static CaptureOutcome Fail(string message)
        => new(false, message, null, string.Empty, 0, 0, string.Empty, null);

    private static byte[] EncodePng(OpenCvMat crop)
    {
        Cv2.ImEncode(".png", crop, out byte[] bytes);
        return bytes;
    }

    private static void DumpDebugCrop(byte[] pngBytes)
    {
        if (!AppSettings.Instance.DebugDumpCrops) return;
        try
        {
            string dir = Path.Combine(AppSettings.AppDataDir, "debug-crops");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"crop-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            File.WriteAllBytes(path, pngBytes);
        }
        catch { /* debug only */ }
    }

    // Full frame alongside the crop, with the cursor position (frame px) encoded in the filename,
    // so detection geometry can be tuned offline against reality.
    private static void DumpDebugFrame(OpenCvMat frame, Point cursor)
    {
        if (!AppSettings.Instance.DebugDumpCrops) return;
        try
        {
            string dir = Path.Combine(AppSettings.AppDataDir, "debug-crops");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"frame-{DateTime.Now:yyyyMMdd-HHmmss-fff}-c{cursor.X}_{cursor.Y}.png");
            Cv2.ImWrite(path, frame);
        }
        catch { /* debug only */ }
    }
}
