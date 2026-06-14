using System.IO;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace DdoGearScanner.Vision.Tests;

// Dev diagnostic: detect the orange ▶ mod-bullets in a tooltip crop. They are NOT just "gold blobs"
// (DDO highlights description keywords in gold too) — they're small right-pointing TRIANGLES that
// line up in a vertical column just right of the left border and left of all text. Filter on
// column position + small size + triangle shape, then keep the dominant column.
public class BulletDetectDiagnostic
{
    private readonly ITestOutputHelper _out;
    public BulletDetectDiagnostic(ITestOutputHelper output) => _out = output;

    [Fact]
    public void DetectBullets()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DdoGearScanner", "debug-crops");
        string crop = Path.Combine(dir, "crop-20260613-003129-139.png");
        if (!File.Exists(crop)) { _out.WriteLine($"missing {crop}"); return; }
        using Mat img = Cv2.ImRead(crop, ImreadModes.Color);
        int scale = 2;
        Cv2.Resize(img, img, new Size(img.Width * scale, img.Height * scale));

        using Mat hsv = new();
        Cv2.CvtColor(img, hsv, ColorConversionCodes.BGR2HSV);
        using Mat gold = new();
        Cv2.InRange(hsv, new Scalar(15, 90, 110), new Scalar(40, 255, 255), gold);

        Cv2.FindContours(gold, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        // Candidate ▶: small, in the left strip (right of border, left of text), shaped like a
        // right-pointing triangle (3 vertices, apex on the right, base edge on the left).
        // Bullet = small gold right-pointing TRIANGLE (3 vertices, mass left-of-center) in the left
        // third of the tooltip. Collect candidates, then keep only the dominant x-column (the bullets
        // share an x; gold keyword-text fragments scatter). Column is found dynamically => crop-agnostic.
        int searchMax = img.Width / 3;
        var cands = new List<Rect>();
        foreach (Point[] c in contours)
        {
            Rect r = Cv2.BoundingRect(c);
            if (r.X + r.Width / 2 > searchMax) continue;
            if (r.Width < 2 * scale || r.Width > 10 * scale) continue;
            if (r.Height < 3 * scale || r.Height > 12 * scale) continue;
            double peri = Cv2.ArcLength(c, true);
            Point[] approx = Cv2.ApproxPolyDP(c, 0.12 * peri, true);
            if (approx.Length != 3) continue;                 // filled triangle
            Moments m = Cv2.Moments(c);
            double mcx = m.M00 > 0 ? m.M10 / m.M00 : r.X + r.Width / 2.0;
            if ((mcx - r.X) / Math.Max(1, r.Width) > 0.40) continue;   // left-heavy (base left, apex right)
            cands.Add(r);
        }

        // Dominant column: bin candidate centers, pick the fullest bin (±3px), keep its members.
        var bullets = new List<Rect>();
        if (cands.Count > 0)
        {
            int tol = 1 * scale;
            int bestX = 0, bestN = 0;
            foreach (Rect a in cands)
            {
                int ax = a.X + a.Width / 2;
                int n = cands.Count(b => Math.Abs((b.X + b.Width / 2) - ax) <= tol);
                if (n > bestN) { bestN = n; bestX = ax; }
            }
            bullets = cands.Where(b => Math.Abs((b.X + b.Width / 2) - bestX) <= tol)
                           .OrderBy(b => b.Y).ToList();
        }

        _out.WriteLine($"img {img.Width}x{img.Height} scale{scale}  candidates={cands.Count} bullets={bullets.Count}");
        foreach (Rect b in bullets) _out.WriteLine($"  bullet y={b.Y / scale} x={b.X / scale} {b.Width / scale}x{b.Height / scale}");

        using Mat vis = img.Clone();
        foreach (Rect b in cands) Cv2.Rectangle(vis, b, new Scalar(0, 0, 255), 1);        // all triangle candidates = red
        foreach (Rect b in bullets) Cv2.Rectangle(vis, new Rect(b.X - 2, b.Y - 2, b.Width + 4, b.Height + 4), Scalar.Lime, 1);
        Cv2.ImWrite(Path.Combine(dir, "_bullets.png"), vis);
    }
}
