using System.IO;
using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>
/// Locates a DDO item tooltip by its corner ornaments. DDO recolors/reshapes the border per item
/// quality (gold coil, silver scroll, silver rope, …) and silver borders are the same bright gray
/// as the stone scenery — so a color/brightness mask alone can't find them. Instead we match the
/// ornament's GRAYSCALE TEXTURE within its exact SILHOUETTE (matchTemplate with a mask): the stone
/// won't match the embossed pattern, only the real ornament does. Color-independent.
///
/// All FOUR corners are matched (the top-right template, mirrored horizontally for top-left and
/// vertically for the bottom corners), so the box — including the dynamic bottom — comes straight
/// from the corner positions. One template PNG per quality in the Templates folder; best wins.
/// </summary>
public sealed class CornerTemplateRegionDetector : ITooltipRegionDetector
{
    private const int InnerInset = 12;     // crop inward of the corners to the text interior
    private const int CornerYTol = 60;     // top (and bottom) corners must align in y
    private const int CornerXTol = 60;     // left (and right) corners must align in x

    private sealed class Tpl
    {
        public required OpenCvMat GTR, GTL, GBR, GBL;   // grayscale corner images
        public required OpenCvMat MTR, MTL, MBR, MBL;   // silhouette masks
        public int W, H;
        public required string Name;
    }

    private readonly List<Tpl> _templates = new();
    private readonly double _threshold;       // min corner score (1 - normalized SQDIFF)
    private readonly int _searchRadius;

    public CornerTemplateRegionDetector(IEnumerable<(OpenCvMat bgr, string name)> rightTemplates,
        double threshold = 0.45, int searchRadius = 1300)
    {
        _threshold = threshold;
        _searchRadius = searchRadius;
        foreach ((OpenCvMat bgr, string name) in rightTemplates)
        {
            OpenCvMat gray = new();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            OpenCvMat mask = Silhouette(bgr);

            Tpl t = new()
            {
                GTR = gray, GTL = Flip(gray, FlipMode.Y), GBR = Flip(gray, FlipMode.X), GBL = Flip(gray, FlipMode.XY),
                MTR = mask, MTL = Flip(mask, FlipMode.Y), MBR = Flip(mask, FlipMode.X), MBL = Flip(mask, FlipMode.XY),
                W = gray.Width, H = gray.Height, Name = name,
            };
            _templates.Add(t);
        }
    }

    public static CornerTemplateRegionDetector? TryLoadAll(string dir, double threshold = 0.45)
    {
        if (!Directory.Exists(dir)) return null;
        List<(OpenCvMat, string)> tpls = new();
        foreach (string path in Directory.GetFiles(dir, "*.png"))
        {
            OpenCvMat m = Cv2.ImRead(path, ImreadModes.Color);
            if (!m.Empty()) tpls.Add((m, Path.GetFileNameWithoutExtension(path)));
        }
        if (tpls.Count == 0) return null;
        return new CornerTemplateRegionDetector(tpls, threshold);
    }

    public TooltipRegion? Detect(OpenCvMat frame, Point cursorInFrame)
    {
        if (frame.Empty() || _templates.Count == 0) return null;

        int cx = Math.Clamp(cursorInFrame.X, 0, frame.Width - 1);
        int cy = Math.Clamp(cursorInFrame.Y, 0, frame.Height - 1);
        int x0 = Math.Max(0, cx - _searchRadius), y0 = Math.Max(0, cy - _searchRadius);
        int x1 = Math.Min(frame.Width, cx + _searchRadius), y1 = Math.Min(frame.Height, cy + _searchRadius);
        Rect window = new(x0, y0, x1 - x0, y1 - y0);

        using OpenCvMat roi = new(frame, window);
        using OpenCvMat gray = EnsureGray(roi);

        double bestScore = 0;
        Rect bestRect = default;
        foreach (Tpl t in _templates)
        {
            if (window.Width < t.W || window.Height < t.H) continue;

            (Point tr, double sTR) = MatchMasked(gray, t.GTR, t.MTR);
            (Point tl, double sTL) = MatchMasked(gray, t.GTL, t.MTL);
            // Top corners are mandatory and must align horizontally.
            if (Math.Min(sTR, sTL) < _threshold) continue;
            if (Math.Abs(tr.Y - tl.Y) > CornerYTol) continue;
            if (tl.X >= tr.X) continue;

            (Point br, double sBR) = MatchMasked(gray, t.GBR, t.MBR);
            (Point bl, double sBL) = MatchMasked(gray, t.GBL, t.MBL);
            bool haveBottom = Math.Min(sBR, sBL) >= _threshold
                              && Math.Abs(br.Y - bl.Y) <= CornerYTol
                              && br.Y > tr.Y + 60;

            int left = Math.Min(tl.X, haveBottom ? bl.X : tl.X) + InnerInset;
            int right = Math.Max(tr.X, haveBottom ? br.X : tr.X) + t.W - InnerInset;
            int top = Math.Min(tr.Y, tl.Y) + InnerInset;
            int bottom = haveBottom
                ? Math.Max(br.Y, bl.Y) + t.H - InnerInset
                : Math.Min(tr.Y, tl.Y) + t.H + 600; // fallback if bottom corners not found

            if (right - left < 80 || bottom - top < 60) continue;

            double score = (Math.Min(sTR, sTL) + (haveBottom ? Math.Min(sBR, sBL) : 0)) / (haveBottom ? 2.0 : 1.0);
            if (score > bestScore)
            {
                bestScore = score;
                bestRect = new Rect(window.X + left, window.Y + top, right - left, bottom - top);
            }
        }

        if (bestScore <= 0) return null;
        return new TooltipRegion(Clamp(bestRect, frame), Math.Min(1.0, bestScore));
    }

    // Masked grayscale match. SQDIFF_NORMED: lowest is best, ~[0,1]; return (loc, 1-min) so higher
    // is better. The mask limits comparison to the ornament's pixels.
    private static (Point loc, double score) MatchMasked(OpenCvMat grayImg, OpenCvMat grayTpl, OpenCvMat mask)
    {
        using OpenCvMat result = new();
        Cv2.MatchTemplate(grayImg, grayTpl, result, TemplateMatchModes.SqDiffNormed, mask);
        Cv2.PatchNaNs(result, 1.0);
        Cv2.MinMaxLoc(result, out double minVal, out _, out Point minLoc, out _);
        return (minLoc, 1.0 - minVal);
    }

    // Ornament silhouette from the clean template: saturated (colored borders) OR mid-bright,
    // not-near-white (silver borders), excluding the dark interior and pale screenshot background.
    private static OpenCvMat Silhouette(OpenCvMat bgr)
    {
        using OpenCvMat hsv = new();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using OpenCvMat colored = new();
        Cv2.InRange(hsv, new Scalar(0, 70, 70), new Scalar(180, 255, 255), colored);
        using OpenCvMat silver = new();
        Cv2.InRange(hsv, new Scalar(0, 0, 110), new Scalar(180, 95, 205), silver);
        OpenCvMat mask = new();
        Cv2.BitwiseOr(colored, silver, mask);
        using OpenCvMat k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, k);
        return mask;
    }

    private static OpenCvMat Flip(OpenCvMat src, FlipMode mode)
    {
        OpenCvMat dst = new();
        Cv2.Flip(src, dst, mode);
        return dst;
    }

    private static OpenCvMat EnsureGray(OpenCvMat roi)
    {
        OpenCvMat gray = new();
        Cv2.CvtColor(roi, gray, roi.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return gray;
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
