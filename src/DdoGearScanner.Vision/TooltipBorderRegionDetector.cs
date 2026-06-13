using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>
/// Locates a DDO item tooltip among DDO's all-gold-bordered UI. Gold-border alone can't tell the
/// tooltip from the inventory/weapon panels, so the discriminator is the INTERIOR: a tooltip is a
/// dark panel of mostly-desaturated TEXT, whereas the inventory/character panels are full of
/// colorful (high-saturation) item icons. We threshold gold near the cursor, separate panels
/// (light dilation, with an absolute size cap so the big windows are rejected), and score each
/// candidate by dark + low-saturation interior, nearest the cursor.
///
/// Tunables are constructor args — iterate against the debug crop dumps. This is the brittle CV
/// piece the plan flags.
/// </summary>
public sealed class TooltipBorderRegionDetector : ITooltipRegionDetector
{
    private readonly int _searchRadius;
    private readonly Scalar _goldLow;
    private readonly Scalar _goldHigh;
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private readonly int _fallbackWidth;
    private readonly int _fallbackHeight;

    public TooltipBorderRegionDetector(
        int searchRadius = 750,
        int maxWidth = 700,
        int maxHeight = 1000,
        int fallbackWidth = 440,
        int fallbackHeight = 520)
    {
        _searchRadius = searchRadius;
        // OpenCV HSV: H 0-180. Gold/amber ~ H 14-40, saturated and bright.
        _goldLow = new Scalar(14, 80, 110);
        _goldHigh = new Scalar(40, 255, 255);
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
        _fallbackWidth = fallbackWidth;
        _fallbackHeight = fallbackHeight;
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
        using OpenCvMat bgr = EnsureBgr(roi, out OpenCvMat? bgrOwned);
        using OpenCvMat hsv = new();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        using OpenCvMat gold = new();
        Cv2.InRange(hsv, _goldLow, _goldHigh, gold);

        // Connect the ornate frame ring within a panel, but stay small so neighbouring panels
        // (inventory, weapon) don't merge into the tooltip.
        using OpenCvMat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(13, 13));
        using OpenCvMat merged = new();
        Cv2.MorphologyEx(gold, merged, MorphTypes.Close, kernel);

        Cv2.FindContours(merged, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        OpenCvMat[] hsvCh = hsv.Split();
        OpenCvMat satCh = hsvCh[1];
        OpenCvMat valCh = hsvCh[2];

        Point cursorInRoi = new(cx - x0, cy - y0);
        double bestScore = 0;
        Rect best = default;

        foreach (Point[] c in contours)
        {
            Rect r = Cv2.BoundingRect(c);

            // Absolute size sanity: large enough for a tooltip, but capped so the big inventory /
            // character windows can't win.
            if (r.Width < 150 || r.Height < 110) continue;
            if (r.Width > _maxWidth || r.Height > _maxHeight) continue;
            double aspect = r.Width / (double)r.Height;
            if (aspect < 0.3 || aspect > 3.5) continue;

            double darkFraction = DarkFraction(valCh, r);
            if (darkFraction < 0.30) continue;            // tooltip panel is mostly dark

            double meanSat = MeanValue(satCh, r);          // 0..255; text is desaturated, icons aren't
            double textScore = 1.0 - Math.Min(1.0, meanSat / 110.0);
            if (textScore <= 0) continue;                  // too colorful → an icon panel, skip

            bool contains = r.Contains(cursorInRoi);
            double dist = DistanceToRect(r, cursorInRoi);
            double proximity = contains ? 1.0 : Math.Max(0, 1.0 - dist / _searchRadius);

            double score = (0.25 + 0.45 * proximity) * (0.4 + 0.6 * darkFraction) * (0.3 + 0.7 * textScore);
            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }

        bgrOwned?.Dispose();
        foreach (OpenCvMat ch in hsvCh) ch.Dispose();

        if (bestScore > 0)
        {
            Rect frameRect = new(best.X + x0, best.Y + y0, best.Width, best.Height);
            frameRect = PadInward(frameRect, 6, frame);
            return new TooltipRegion(frameRect, Math.Min(1.0, bestScore));
        }

        return new TooltipRegion(AnchorFallback(cx, cy, frame), 0.15);
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

    private static double DarkFraction(OpenCvMat valCh, Rect r)
    {
        using OpenCvMat sub = new(valCh, r);
        using OpenCvMat darkMask = new();
        Cv2.Threshold(sub, darkMask, 85, 255, ThresholdTypes.BinaryInv);
        return Cv2.CountNonZero(darkMask) / (double)(r.Width * r.Height);
    }

    private static double MeanValue(OpenCvMat ch, Rect r)
    {
        using OpenCvMat sub = new(ch, r);
        return Cv2.Mean(sub).Val0;
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
