using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>
/// Motion-based tooltip detector for a "detection session", using a BASELINE reference rather than
/// frame-to-frame diffing. The baseline is the background with no tooltip showing; each frame is
/// diffed against it, so a tooltip shows up as a change for the whole time it's displayed (giving a
/// full, stable box), and when it vanishes the frame returns to the baseline (no diff → no false
/// capture of the revealed inventory). The cursor only DISAMBIGUATES which changed block is the
/// tooltip; the full extent is captured. Border/quality independent — works for normals too.
///
/// The baseline self-maintains as a running background model (<see cref="UpdateBaseline"/>): only
/// UNCHANGED pixels track the live frame, while the changed region (a tooltip) is left as clean
/// background — so a tooltip is never baked in and the area under it still matches once it vanishes. A
/// big whole-frame change (camera) hard-rebases; a lingering phantom hard-rebases after StuckLimit frames.
///
/// Stateful: call <see cref="OnFrame"/> per frame; returns a full-frame Rect once per tooltip, when
/// it has been stable under a settled cursor. <see cref="Reset"/> when starting a session.
/// </summary>
public sealed class TooltipChangeDetector
{
    private const double Scale = 0.5;
    private const int DiffThreshold = 26;
    private const int SettleFrames = 2;        // frames the tooltip must be stably shown before capture
    private const int MoveGate = 10;           // px/frame above which the cursor is "moving" — ignore
    private const int MinMoveDist = 35;        // px the cursor must move (new piece) before re-capturing
    private const double BigChangeFraction = 0.40; // whole-frame change this large → re-baseline (camera moved)
    private const double QuietFraction = 0.012;    // change below this → no tooltip showing
    private const int StuckLimit = 30;             // frames of persistent change with no valid tooltip
                                                   // blob → hard-rebaseline (heals a baked-in phantom)

    private readonly int _maxCursorDist;
    private readonly int _minW, _minH, _maxW, _maxH;

    private OpenCvMat? _baseline;              // scaled gray, no-tooltip reference
    private int _stable;
    private Rect _candidate;
    private bool _hasCaptured;
    private Point _capturedCursor;
    private Point _prevCursor;
    private bool _havePrevCursor;
    private int _prevTotalChanged;   // last frame's change count, to tell when a tooltip stops drawing
    private int _noBlobFrames;       // consecutive frames of change with no valid tooltip blob (phantom)

    public string LastChangeInfo { get; private set; } = "";

    /// <summary>True when the last frame had no tooltip up (≈ the baseline) — a good moment to
    /// locate the rag-doll, which a tooltip would otherwise cover.</summary>
    public bool LastFrameClean { get; private set; }

    public TooltipChangeDetector(int maxCursorDist = 380, int minW = 180, int minH = 100, int maxW = 1100, int maxH = 1450)
    {
        _maxCursorDist = maxCursorDist;
        _minW = minW; _minH = minH; _maxW = maxW; _maxH = maxH;
    }

    public void Reset()
    {
        _baseline?.Dispose(); _baseline = null;
        _stable = 0; _candidate = default;
        _hasCaptured = false; _capturedCursor = default;
        _havePrevCursor = false; _prevCursor = default;
        _prevTotalChanged = 0; _noBlobFrames = 0;
    }

    public Rect? OnFrame(OpenCvMat frameBgrOrBgra, Point cursorInFrame)
    {
        if (frameBgrOrBgra.Empty()) return null;

        using OpenCvMat gray = new();
        Cv2.CvtColor(frameBgrOrBgra, gray,
            frameBgrOrBgra.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        using OpenCvMat small = new();
        Cv2.Resize(gray, small, new Size((int)(gray.Width * Scale), (int)(gray.Height * Scale)));

        if (_baseline is null || _baseline.Size() != small.Size())
        {
            _baseline?.Dispose(); _baseline = small.Clone();
            LastChangeInfo = "baseline set";
            return null;
        }

        using OpenCvMat diff = new();
        Cv2.Absdiff(small, _baseline, diff);
        using OpenCvMat changed = new();
        Cv2.Threshold(diff, changed, DiffThreshold, 255, ThresholdTypes.Binary);
        using OpenCvMat k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 5));
        Cv2.MorphologyEx(changed, changed, MorphTypes.Close, k);

        double frameArea = small.Width * (double)small.Height;
        int totalChanged = Cv2.CountNonZero(changed);
        LastFrameClean = totalChanged < frameArea * QuietFraction;
        // How much the change grew/shrank since last frame — large = the tooltip is still drawing
        // in (fade/render), ~0 = fully drawn and steady.
        int changeDelta = Math.Abs(totalChanged - _prevTotalChanged);
        _prevTotalChanged = totalChanged;

        // Camera moved / scene jumped → the whole baseline is meaningless; hard re-base and bail.
        if (totalChanged > frameArea * BigChangeFraction)
        {
            _baseline.Dispose(); _baseline = small.Clone();
            _stable = 0; _candidate = default;
            LastChangeInfo = $"rebaseline(scene {totalChanged})";
            return null;
        }

        // Maintain the background model WITHOUT ever baking the tooltip in, and keeping the tooltip
        // region as CLEAN background so that when the tooltip vanishes the revealed area matches the
        // baseline (no diff → no false capture of the inventory underneath). Only UNCHANGED pixels are
        // copied from the current frame (tracking slow scene/UI drift); changed pixels — the tooltip —
        // are left untouched. The old code wholesale-cloned the frame on refresh, which baked tooltips
        // into the background; an earlier leak-blend did the opposite, corrupting the tooltip region so
        // the revealed inventory then false-fired.
        UpdateBaseline(small, changed);

        // Cursor moving between pieces → ignore (don't try to settle on a smearing tooltip).
        double moved = _havePrevCursor ? DistToPoint(_prevCursor, cursorInFrame) : 0;
        _prevCursor = cursorInFrame; _havePrevCursor = true;
        if (moved > MoveGate) { _stable = 0; _candidate = default; LastChangeInfo = $"moving({(int)moved})"; return null; }

        // Nothing showing → no tooltip to settle on (the background model already tracked this frame).
        if (totalChanged < frameArea * QuietFraction)
        {
            _stable = 0; _candidate = default; _noBlobFrames = 0;
            LastChangeInfo = $"quiet({totalChanged})";
            return null;
        }

        // A tooltip is up. Its changed block nearest the cursor, full extent.
        Rect? near = NearestTooltipBlob(changed, cursorInFrame, out string dbg);
        if (near is not Rect r)
        {
            _stable = 0; _candidate = default;
            // Persistent change with no tooltip-shaped blob near the cursor is a phantom (e.g. the
            // baseline was cloned while a tooltip was up during a camera move / at session start). Heal
            // it by hard-rebaselining after it lingers — this never fires while a real tooltip is found.
            if (++_noBlobFrames > StuckLimit)
            {
                _baseline.Dispose(); _baseline = small.Clone();
                _stable = 0; _candidate = default; _noBlobFrames = 0;
                LastChangeInfo = $"stuck → rebaseline({totalChanged})";
                return null;
            }
            LastChangeInfo = $"change({totalChanged}) {dbg}";
            return null;
        }
        _noBlobFrames = 0;

        _candidate = r;
        // Only count it as "settled" once the tooltip has stopped changing (finished drawing in).
        // While it's still rendering, changeDelta is large → reset the counter.
        if (changeDelta > frameArea * 0.004) _stable = 0; else _stable++;
        LastChangeInfo = $"blob {r.Width}x{r.Height} stable={_stable} d={changeDelta} cap={_hasCaptured}";

        if (_stable >= SettleFrames)
        {
            // De-dupe: same piece still under a near-stationary cursor → don't re-capture. Moving to
            // a new gear slot moves the cursor, which re-enables capture.
            if (_hasCaptured && DistToPoint(_capturedCursor, cursorInFrame) < MinMoveDist) return null;
            _hasCaptured = true; _capturedCursor = cursorInFrame;
            Rect result = _candidate;
            if (result.Width >= _minW && result.Height >= _minH) return result;
        }
        return null;
    }

    /// <summary>Background update: copy only the UNCHANGED pixels from the current frame into the
    /// baseline. This tracks slow scene/UI drift while deliberately leaving the changed region (the
    /// tooltip) as clean background, so the tooltip is never baked in AND the area it covers still
    /// matches the baseline once it vanishes (no false capture of the revealed inventory).</summary>
    private void UpdateBaseline(OpenCvMat small, OpenCvMat changed)
    {
        if (_baseline is null) return;
        using OpenCvMat unchanged = new();
        Cv2.BitwiseNot(changed, unchanged);
        small.CopyTo(_baseline, unchanged);
    }

    private Rect? NearestTooltipBlob(OpenCvMat changedScaled, Point cursorFull, out string dbg)
    {
        Cv2.FindContours(changedScaled, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        Rect bestScaled = default; Rect best = default;
        double bestDist = double.MaxValue;
        int sized = 0;
        foreach (Point[] c in contours)
        {
            Rect rs = Cv2.BoundingRect(c);
            Rect r = new((int)(rs.X / Scale), (int)(rs.Y / Scale), (int)(rs.Width / Scale), (int)(rs.Height / Scale));
            if (r.Width < _minW || r.Height < _minH || r.Width > _maxW || r.Height > _maxH) continue;
            sized++;
            double d = DistToRect(r, cursorFull);
            if (d <= _maxCursorDist && d < bestDist) { bestDist = d; best = r; bestScaled = rs; }
        }
        if (bestDist >= double.MaxValue) { dbg = $"blobs={contours.Length} sized={sized} none"; return null; }

        // Trim top/bottom to the CONTIGUOUS changed band so disconnected UI above/below is dropped.
        (int topS, int botS) = ContiguousBand(changedScaled, bestScaled);
        dbg = $"blobs={contours.Length} sized={sized} HIT@{(int)bestDist}";
        return new Rect(best.X, (int)(topS / Scale), best.Width, (int)((botS - topS) / Scale));
    }

    private static (int top, int bottom) ContiguousBand(OpenCvMat changed, Rect blobScaled)
    {
        int xL = Math.Clamp(blobScaled.X, 0, changed.Cols - 1);
        int xR = Math.Clamp(blobScaled.X + blobScaled.Width, xL + 1, changed.Cols);
        int yTop = Math.Clamp(blobScaled.Y, 0, changed.Rows - 1);
        int yBot = Math.Clamp(blobScaled.Y + blobScaled.Height, yTop + 1, changed.Rows);
        int span = xR - xL;
        double rowThresh = Math.Max(2, span * 0.10);
        const int gapLimit = 8;

        int RowCount(int y) { using OpenCvMat row = new(changed, new Rect(xL, y, span, 1)); return Cv2.CountNonZero(row); }

        int seed = yTop, bestCnt = -1;
        for (int y = yTop; y < yBot; y++) { int c = RowCount(y); if (c > bestCnt) { bestCnt = c; seed = y; } }

        int top = seed, bottom = seed, gap = 0;
        for (int y = seed; y >= 0; y--) { if (RowCount(y) >= rowThresh) { top = y; gap = 0; } else if (++gap > gapLimit) break; }
        gap = 0;
        for (int y = seed; y < changed.Rows; y++) { if (RowCount(y) >= rowThresh) { bottom = y; gap = 0; } else if (++gap > gapLimit) break; }
        return (top, bottom);
    }

    private static double DistToPoint(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistToRect(Rect r, Point p)
    {
        double dx = Math.Max(Math.Max(r.X - p.X, p.X - (r.X + r.Width)), 0);
        double dy = Math.Max(Math.Max(r.Y - p.Y, p.Y - (r.Y + r.Height)), 0);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
