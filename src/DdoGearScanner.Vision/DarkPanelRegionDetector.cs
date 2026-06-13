using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>
/// Quality-independent tooltip locator. DDO recolors the tooltip BORDER (and its corner
/// ornaments) by item quality, and normal/random items have no ornament at all — so nothing on
/// the border is reliable. What every gear tooltip shares is a solid, near-black, low-saturation
/// rectangular INTERIOR holding the text. The inventory is full of colorful icons (not flat/dark)
/// and the dungeon scene is textured, so the largest flat-dark rectangle near the cursor is the
/// tooltip, normal or named.
///
/// Method: mask dark + desaturated pixels, morphologically close so the text-broken interior
/// becomes one blob, then pick the most rectangular, well-sized blob nearest the cursor. Tunables
/// are constructor args — iterate against the frame-*.png debug dumps.
/// </summary>
public sealed class DarkPanelRegionDetector : ITooltipRegionDetector
{
    private readonly int _searchRadius;
    private readonly int _maxValue;       // V below this = "dark"
    private readonly int _maxSat;         // S below this = "not a colorful icon/border"
    private readonly int _minW, _minH, _maxW, _maxH;
    private readonly double _minFill;     // contourArea / boundingRect area
    private readonly bool _useCursorProximity;

    public DarkPanelRegionDetector(
        int searchRadius = 900,
        int maxValue = 80,
        int maxSat = 90,
        int minW = 200, int minH = 140,
        int maxW = 900, int maxH = 1200,
        double minFill = 0.72,
        bool useCursorProximity = true)
    {
        _searchRadius = searchRadius;
        _maxValue = maxValue;
        _maxSat = maxSat;
        _minW = minW; _minH = minH; _maxW = maxW; _maxH = maxH;
        _minFill = minFill;
        _useCursorProximity = useCursorProximity;
    }

    public TooltipRegion? Detect(OpenCvMat frame, Point cursorInFrame)
    {
        if (frame.Empty()) return null;

        int cx = Math.Clamp(cursorInFrame.X, 0, frame.Width - 1);
        int cy = Math.Clamp(cursorInFrame.Y, 0, frame.Height - 1);
        int x0 = Math.Max(0, cx - _searchRadius);
        int y0 = Math.Max(0, cy - _searchRadius);
        int x1 = Math.Min(frame.Width, cx + _searchRadius);
        int y1 = Math.Min(frame.Height, cy + _searchRadius);
        Rect window = new(x0, y0, x1 - x0, y1 - y0);
        if (window.Width <= 0 || window.Height <= 0) return null;

        using OpenCvMat roi = new(frame, window);
        using OpenCvMat bgr = EnsureBgr(roi, out OpenCvMat? owned);
        using OpenCvMat hsv = new();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        owned?.Dispose();

        // Dark AND desaturated = the flat tooltip interior.
        using OpenCvMat mask = new();
        Cv2.InRange(hsv, new Scalar(0, 0, 0), new Scalar(180, _maxSat, _maxValue), mask);

        // Bridge the thin text gaps so the interior becomes one solid blob.
        using OpenCvMat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(21, 21));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        Point cursorInRoi = new(cx - x0, cy - y0);
        double bestScore = 0;
        Rect best = default;

        foreach (Point[] c in contours)
        {
            Rect r = Cv2.BoundingRect(c);
            if (r.Width < _minW || r.Height < _minH) continue;
            if (r.Width > _maxW || r.Height > _maxH) continue;

            double fill = Cv2.ContourArea(c) / Math.Max(1.0, r.Width * (double)r.Height);
            if (fill < _minFill) continue; // tooltip interior is a solid rectangle; scene/inventory aren't

            double proximity = 1.0;
            if (_useCursorProximity)
            {
                bool contains = r.Contains(cursorInRoi);
                double dist = DistanceToRect(r, cursorInRoi);
                proximity = contains ? 1.0 : Math.Max(0, 1.0 - dist / _searchRadius);
            }

            double sizeFactor = Math.Min(1.0, (r.Width * (double)r.Height) / (520.0 * 700.0));
            double score = fill * (0.3 + 0.7 * proximity) * (0.4 + 0.6 * sizeFactor);
            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }

        if (bestScore <= 0) return null;

        Rect bounds = Clamp(new Rect(best.X + x0, best.Y + y0, best.Width, best.Height), frame);
        return new TooltipRegion(bounds, Math.Min(1.0, bestScore));
    }

    /// <summary>Debug: the closed dark-desaturated mask over the search window (full-frame
    /// space, black outside the window). For offline tuning only.</summary>
    public OpenCvMat DebugMask(OpenCvMat frame, Point cursorInFrame)
    {
        OpenCvMat outMask = OpenCvMat.Zeros(frame.Rows, frame.Cols, MatType.CV_8UC1);
        int cx = Math.Clamp(cursorInFrame.X, 0, frame.Width - 1);
        int cy = Math.Clamp(cursorInFrame.Y, 0, frame.Height - 1);
        int x0 = Math.Max(0, cx - _searchRadius), y0 = Math.Max(0, cy - _searchRadius);
        int x1 = Math.Min(frame.Width, cx + _searchRadius), y1 = Math.Min(frame.Height, cy + _searchRadius);
        Rect window = new(x0, y0, x1 - x0, y1 - y0);
        using OpenCvMat roi = new(frame, window);
        using OpenCvMat bgr = EnsureBgr(roi, out OpenCvMat? owned);
        using OpenCvMat hsv = new();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        owned?.Dispose();
        using OpenCvMat mask = new();
        Cv2.InRange(hsv, new Scalar(0, 0, 0), new Scalar(180, _maxSat, _maxValue), mask);
        using OpenCvMat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(21, 21));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        using OpenCvMat dst = new(outMask, window);
        mask.CopyTo(dst);
        return outMask;
    }

    private static OpenCvMat EnsureBgr(OpenCvMat roi, out OpenCvMat? owned)
    {
        if (roi.Channels() == 4)
        {
            owned = new OpenCvMat();
            Cv2.CvtColor(roi, owned, ColorConversionCodes.BGRA2BGR);
            return owned;
        }
        owned = null;
        return roi;
    }

    private static double DistanceToRect(Rect r, Point p)
    {
        double dx = Math.Max(Math.Max(r.X - p.X, p.X - (r.X + r.Width)), 0);
        double dy = Math.Max(Math.Max(r.Y - p.Y, p.Y - (r.Y + r.Height)), 0);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Rect Clamp(Rect r, OpenCvMat frame)
    {
        int x = Math.Clamp(r.X, 0, frame.Width - 1);
        int y = Math.Clamp(r.Y, 0, frame.Height - 1);
        int w = Math.Clamp(r.Width, 1, frame.Width - x);
        int h = Math.Clamp(r.Height, 1, frame.Height - y);
        return new Rect(x, y, w, h);
    }
}
