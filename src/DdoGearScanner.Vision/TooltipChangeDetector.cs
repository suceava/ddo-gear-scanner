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
/// The baseline self-maintains: it refreshes whenever nothing is showing (tracking slow scene/UI
/// drift) and re-bases on a big whole-frame change (camera move/shake), so it doesn't go stale.
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
    private const double QuietFraction = 0.012;    // change below this → no tooltip; refresh the baseline

    private readonly int _maxCursorDist;
    private readonly int _minW, _minH, _maxW, _maxH;

    private OpenCvMat? _baseline;              // scaled gray, no-tooltip reference
    private int _stable;
    private Rect _candidate;
    private bool _hasCaptured;
    private Point _capturedCursor;
    private Point _prevCursor;
    private bool _havePrevCursor;

    public string LastChangeInfo { get; private set; } = "";

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

        // Camera moved / scene jumped → baseline is stale, re-base and bail.
        if (totalChanged > frameArea * BigChangeFraction)
        {
            _baseline.Dispose(); _baseline = small.Clone();
            _stable = 0; _candidate = default;
            LastChangeInfo = $"rebaseline(scene {totalChanged})";
            return null;
        }

        // Cursor moving between pieces → ignore (and don't touch the baseline).
        double moved = _havePrevCursor ? DistToPoint(_prevCursor, cursorInFrame) : 0;
        _prevCursor = cursorInFrame; _havePrevCursor = true;
        if (moved > MoveGate) { _stable = 0; _candidate = default; LastChangeInfo = $"moving({(int)moved})"; return null; }

        // Nothing showing → refresh the baseline (tracks slow drift) and wait.
        if (totalChanged < frameArea * QuietFraction)
        {
            _baseline.Dispose(); _baseline = small.Clone();
            _stable = 0; _candidate = default;
            LastChangeInfo = $"quiet({totalChanged}) baseline refreshed";
            return null;
        }

        // A tooltip is up. Its changed block nearest the cursor, full extent.
        Rect? near = NearestTooltipBlob(changed, cursorInFrame, out string dbg);
        if (near is not Rect r)
        {
            _stable = 0; _candidate = default;
            LastChangeInfo = $"change({totalChanged}) {dbg}";
            return null;
        }

        _candidate = r;
        _stable++;
        LastChangeInfo = $"blob {r.Width}x{r.Height} stable={_stable} cap={_hasCaptured}";

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
