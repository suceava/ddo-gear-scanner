using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>
/// Phase-1 tooltip locator. DDO gear tooltips render as a dark, near-rectangular box next to
/// the cursor. We threshold the dark background within a window around the cursor, find the
/// largest rectangular contour, and return its bounding box. If nothing convincing is found we
/// fall back to a fixed-size box anchored near the cursor so OCR still gets something.
///
/// Tunables (dark threshold, search radius) are exposed so they can be wired to settings and
/// eyeballed against debug crop dumps. This is the second-most-brittle piece after parsing.
/// </summary>
public sealed class DarkBoxRegionDetector : ITooltipRegionDetector
{
    private readonly int _searchRadius;
    private readonly int _darkThreshold;
    private readonly int _fallbackWidth;
    private readonly int _fallbackHeight;

    public DarkBoxRegionDetector(
        int searchRadius = 700,
        int darkThreshold = 70,
        int fallbackWidth = 460,
        int fallbackHeight = 560)
    {
        _searchRadius = searchRadius;
        _darkThreshold = darkThreshold;
        _fallbackWidth = fallbackWidth;
        _fallbackHeight = fallbackHeight;
    }

    public TooltipRegion? Detect(OpenCvMat frame, Point cursorInFrame)
    {
        if (frame.Empty()) return null;

        // Clamp cursor to the frame.
        int cx = Math.Clamp(cursorInFrame.X, 0, frame.Width - 1);
        int cy = Math.Clamp(cursorInFrame.Y, 0, frame.Height - 1);

        // Search window around the cursor, clamped to the frame.
        int x0 = Math.Max(0, cx - _searchRadius);
        int y0 = Math.Max(0, cy - _searchRadius);
        int x1 = Math.Min(frame.Width, cx + _searchRadius);
        int y1 = Math.Min(frame.Height, cy + _searchRadius);
        Rect window = new(x0, y0, x1 - x0, y1 - y0);
        if (window.Width <= 0 || window.Height <= 0) return null;

        using OpenCvMat roi = new(frame, window);
        using OpenCvMat gray = new();
        Cv2.CvtColor(roi, gray, roi.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);

        using OpenCvMat mask = new();
        Cv2.Threshold(gray, mask, _darkThreshold, 255, ThresholdTypes.BinaryInv);

        using OpenCvMat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        int minArea = 120 * 100; // ignore tiny dark patches
        double bestScore = 0;
        Rect best = default;

        Point cursorInRoi = new(cx - x0, cy - y0);

        foreach (Point[] c in contours)
        {
            Rect r = Cv2.BoundingRect(c);
            if (r.Width * r.Height < minArea) continue;
            if (r.Width < 120 || r.Height < 80) continue;

            // Rectangularity: how much of the bounding box the contour actually fills.
            double contourArea = Cv2.ContourArea(c);
            double rectangularity = contourArea / Math.Max(1, r.Width * r.Height);
            if (rectangularity < 0.6) continue;

            // Prefer boxes that contain the cursor, then ones near it.
            bool containsCursor = r.Contains(cursorInRoi);
            double dist = DistanceToRect(r, cursorInRoi);
            double proximity = containsCursor ? 1.0 : Math.Max(0, 1.0 - dist / _searchRadius);

            double sizeFactor = Math.Min(1.0, (r.Width * r.Height) / (double)(_fallbackWidth * _fallbackHeight));
            double score = rectangularity * (0.5 + 0.5 * proximity) * (0.4 + 0.6 * sizeFactor);

            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }

        if (bestScore > 0)
        {
            // Translate to frame coords and pad inward to drop the border before OCR.
            Rect frameRect = new(best.X + x0, best.Y + y0, best.Width, best.Height);
            frameRect = PadInward(frameRect, 4, frame);
            return new TooltipRegion(frameRect, Math.Min(1.0, bestScore));
        }

        // Fallback: fixed-size box anchored near the cursor (tooltips usually render below/right).
        Rect fallback = AnchorFallback(cx, cy, frame);
        return new TooltipRegion(fallback, 0.2);
    }

    private Rect AnchorFallback(int cx, int cy, OpenCvMat frame)
    {
        int left = cx + 12;
        int top = cy + 12;
        if (left + _fallbackWidth > frame.Width) left = Math.Max(0, cx - _fallbackWidth - 12);
        if (top + _fallbackHeight > frame.Height) top = Math.Max(0, frame.Height - _fallbackHeight);
        int w = Math.Min(_fallbackWidth, frame.Width - left);
        int h = Math.Min(_fallbackHeight, frame.Height - top);
        return new Rect(left, top, w, h);
    }

    private static Rect PadInward(Rect r, int pad, OpenCvMat frame)
    {
        int x = Math.Min(r.X + pad, frame.Width - 1);
        int y = Math.Min(r.Y + pad, frame.Height - 1);
        int w = Math.Max(1, r.Width - 2 * pad);
        int h = Math.Max(1, r.Height - 2 * pad);
        if (x + w > frame.Width) w = frame.Width - x;
        if (y + h > frame.Height) h = frame.Height - y;
        return new Rect(x, y, w, h);
    }

    private static double DistanceToRect(Rect r, Point p)
    {
        double dx = Math.Max(Math.Max(r.X - p.X, p.X - (r.X + r.Width)), 0);
        double dy = Math.Max(Math.Max(r.Y - p.Y, p.Y - (r.Y + r.Height)), 0);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
