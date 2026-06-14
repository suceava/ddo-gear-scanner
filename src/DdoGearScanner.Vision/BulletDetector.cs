using OpenCvSharp;

namespace DdoGearScanner.Vision;

/// <summary>
/// Finds DDO's gold ▶ mod-bullets in a tooltip crop. Each affix line is prefixed with a small,
/// filled, right-pointing gold triangle; descriptions are NOT. The triangle is therefore the only
/// reliable mod delimiter — the glyph does not survive OCR (it's dropped), and the text itself is
/// ambiguous because descriptions contain colon phrases ("Passive:", "Weapons and Shields:").
///
/// Detection is purely geometric so it adapts to any crop:
///  1. mask the gold core (high R/G, low B),
///  2. keep small contours that approximate to a 3-vertex, left-of-center (right-pointing) triangle,
///  3. keep only the dominant x-column (bullets share an x; gold keyword-text fragments scatter),
///  4. drop size-outliers within that column.
/// Returns the Y-centres of the bullets, in the SAME pixel space as the Mat passed in (so callers
/// must run OCR and detection on the same image), top-to-bottom.
/// </summary>
public static class BulletDetector
{
    /// <summary>One gold blob considered as a possible ▶, with WHY it was kept or dropped — so a
    /// missed/extra mod can be diagnosed from the debug dump alone.</summary>
    public sealed record Candidate(Rect Rect, bool SizeOk, int Verts, double LeftFrac, bool InColumn, bool Accepted);

    /// <summary>Full record of a bullet-detection pass for the debug bundle.</summary>
    public sealed class Diagnostics
    {
        public double Unit;
        public int ColumnX = -1;
        public int Tol;
        public List<Candidate> Candidates = new();
        public List<int> BulletYs = new();
    }

    public static IReadOnlyList<int> DetectYCenters(Mat bgr) => DetectYCenters(bgr, out _);

    public static IReadOnlyList<int> DetectYCenters(Mat bgr, out Diagnostics diag)
    {
        diag = new Diagnostics();
        if (bgr.Empty()) return Array.Empty<int>();

        // Normalise to a known scale so the pixel-size thresholds below are stable regardless of how
        // much the caller upscaled. We work at ~2x the raw tooltip; the reader upscales 3x, so allow
        // any input and derive a unit from the image height instead of hard-coding.
        Mat? tmp = null;
        Mat src = bgr;
        if (bgr.Channels() == 4) { tmp = new Mat(); Cv2.CvtColor(bgr, tmp, ColorConversionCodes.BGRA2BGR); src = tmp; }
        using Mat hsv = new();
        Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);
        tmp?.Dispose();
        using Mat gold = new();
        Cv2.InRange(hsv, new Scalar(15, 90, 110), new Scalar(40, 255, 255), gold);

        Cv2.FindContours(gold, out Point[][] contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        // Glyph size scales with the text; a tooltip line is ~3% of crop height. The gold core of the
        // ▶ is roughly a third of a line tall. Bound generously.
        double unit = Math.Max(2.0, bgr.Height / 700.0);   // ≈ scale factor vs the raw crop
        diag.Unit = unit;
        int wMin = (int)(2 * unit), wMax = (int)(11 * unit);
        int hMin = (int)(3 * unit), hMax = (int)(13 * unit);
        int searchMax = bgr.Width / 3;                     // bullets live in the left third

        // Shape-pass: every gold blob in the left third becomes a Candidate (so rejects are visible
        // in the dump). Triangle = small, 3-vertex, mass left-of-center (points right).
        var shaped = new List<(Rect r, bool tri)>();
        foreach (Point[] c in contours)
        {
            Rect r = Cv2.BoundingRect(c);
            if (r.X + r.Width / 2 > searchMax) continue;
            bool sizeOk = r.Width >= wMin && r.Width <= wMax && r.Height >= hMin && r.Height <= hMax;
            double peri = Cv2.ArcLength(c, true);
            Point[] approx = peri > 0 ? Cv2.ApproxPolyDP(c, 0.12 * peri, true) : Array.Empty<Point>();
            Moments m = Cv2.Moments(c);
            double mcx = m.M00 > 0 ? m.M10 / m.M00 : r.X + r.Width / 2.0;
            double leftFrac = (mcx - r.X) / Math.Max(1, r.Width);
            bool tri = sizeOk && approx.Length == 3 && leftFrac <= 0.42;
            shaped.Add((r, tri));
            diag.Candidates.Add(new Candidate(r, sizeOk, approx.Length, leftFrac, InColumn: false, Accepted: false));
        }
        var cands = shaped.Where(s => s.tri).Select(s => s.r).ToList();
        if (cands.Count == 0) return Array.Empty<int>();

        // Dominant column: the bullets all share an x; stray gold-text triangles sit a few px off.
        int tol = Math.Max(1, (int)Math.Round(unit));
        diag.Tol = tol;
        int bestCx = 0, bestN = 0;
        foreach (Rect a in cands)
        {
            int ax = a.X + a.Width / 2;
            int n = cands.Count(b => Math.Abs((b.X + b.Width / 2) - ax) <= tol);
            if (n > bestN) { bestN = n; bestCx = ax; }
        }
        diag.ColumnX = bestCx;
        var column = cands.Where(b => Math.Abs((b.X + b.Width / 2) - bestCx) <= tol).ToList();

        // Within the column, drop blobs whose height is far from the median (gold text that happened
        // to land in the column is usually taller/blockier than the uniform glyph cores).
        var heights = column.Select(b => b.Height).OrderBy(h => h).ToList();
        int medianH = heights[heights.Count / 2];
        var accepted = column.Where(b => Math.Abs(b.Height - medianH) <= 2 * unit).ToList();
        var acceptedSet = accepted.ToHashSet();

        // Annotate diagnostics: mark which candidates landed in the column / were accepted.
        for (int i = 0; i < diag.Candidates.Count; i++)
        {
            Candidate c = diag.Candidates[i];
            bool inCol = c.Verts == 3 && Math.Abs((c.Rect.X + c.Rect.Width / 2) - bestCx) <= tol && c.SizeOk && c.LeftFrac <= 0.42;
            bool acc = inCol && acceptedSet.Contains(c.Rect);
            diag.Candidates[i] = c with { InColumn = inCol, Accepted = acc };
        }

        var bullets = accepted.Select(b => b.Y + b.Height / 2).OrderBy(y => y).ToList();

        // Collapse near-duplicate detections (a glyph can split into two contours) within one line.
        var merged = new List<int>();
        int minGap = (int)(6 * unit);
        foreach (int y in bullets)
            if (merged.Count == 0 || y - merged[^1] > minGap) merged.Add(y);
            else merged[^1] = (merged[^1] + y) / 2;

        diag.BulletYs = merged;
        return merged;
    }
}
