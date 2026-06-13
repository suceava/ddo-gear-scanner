using System.IO;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace DdoGearScanner.Vision.Tests;

// Dev diagnostic: auto-detect the equipment-slot squares in a native-scale inventory screenshot,
// printing each slot's rect (orig coords) and its offset from the rag-doll anchor (345,86). Used to
// build the slot-offset map.
public class SlotDetectDiagnostic
{
    private readonly ITestOutputHelper _out;
    public SlotDetectDiagnostic(ITestOutputHelper output) => _out = output;

    [Fact]
    public void DetectSlots()
    {
        string shot = @"C:\Users\Dan\Pictures\Screenshots\Screenshot 2026-06-12 231416.png";
        if (!File.Exists(shot)) { _out.WriteLine("no screenshot"); return; }
        using Mat img = Cv2.ImRead(shot, ImreadModes.Color);

        using Mat gray = new();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        using Mat edges = new();
        Cv2.Canny(gray, edges, 40.0, 120.0, 3, false);
        using Mat k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(edges, edges, k);

        Cv2.FindContours(edges, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        // rag-doll anchor top-left in this screenshot (where the template was cut).
        const int ragX = 345, ragY = 86;
        var rects = new List<Rect>();
        foreach (Point[] c in contours)
        {
            double peri = Cv2.ArcLength(c, true);
            Point[] approx = Cv2.ApproxPolyDP(c, 0.04 * peri, true);
            if (approx.Length != 4 || !Cv2.IsContourConvex(approx)) continue;
            Rect r = Cv2.BoundingRect(approx);
            if (r.Width < 26 || r.Width > 42 || r.Height < 26 || r.Height > 42) continue;
            if (Math.Abs(r.Width - r.Height) > 8) continue;
            if (r.X < 280) continue; // exclude the bag grid on the left; keep the paper-doll region
            rects.Add(r);
        }

        // De-dup overlapping detections, keep one per cluster.
        var kept = new List<Rect>();
        foreach (Rect r in rects.OrderBy(r => r.Y).ThenBy(r => r.X))
            if (!kept.Any(q => Math.Abs(q.X - r.X) < 16 && Math.Abs(q.Y - r.Y) < 16))
                kept.Add(r);

        _out.WriteLine($"detected {kept.Count} slot squares (x>280):");
        foreach (Rect r in kept.OrderBy(r => r.Y).ThenBy(r => r.X))
            _out.WriteLine($"  rect ({r.X},{r.Y},{r.Width},{r.Height})  offsetFromRagdoll=({r.X - ragX},{r.Y - ragY})  center=({r.X + r.Width / 2},{r.Y + r.Height / 2})");

        // Draw for spot-check (saved; viewing optional).
        using Mat vis = img.Clone();
        int i = 0;
        foreach (Rect r in kept) { Cv2.Rectangle(vis, r, Scalar.Lime, 1); Cv2.PutText(vis, (i++).ToString(), new Point(r.X, r.Y - 1), HersheyFonts.HersheySimplex, 0.3, Scalar.Yellow); }
        Cv2.ImWrite(@"C:\Users\Dan\AppData\Roaming\DdoGearScanner\debug-crops\_slots.png", vis);
    }
}
