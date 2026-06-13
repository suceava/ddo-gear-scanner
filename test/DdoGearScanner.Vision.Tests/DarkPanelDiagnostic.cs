using System.IO;
using DdoGearScanner.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace DdoGearScanner.Vision.Tests;

// Dev-only diagnostic: runs DarkPanelRegionDetector over the real frame-*.png dumps in the user's
// AppData and writes the detected crops to a _detect subfolder for visual tuning. No-ops on
// machines without that folder, so it never fails CI.
public class DarkPanelDiagnostic
{
    private readonly ITestOutputHelper _out;
    public DarkPanelDiagnostic(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RunCornerDetectorOnFrames()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DdoGearScanner", "debug-crops");
        if (!Directory.Exists(dir)) { _out.WriteLine("no folder"); return; }
        string tplDir = @"C:\code\ddo-gear-scanner\assets\templates";
        var det = CornerTemplateRegionDetector.TryLoadAll(tplDir, threshold: 0.40);
        if (det is null) { _out.WriteLine("no templates"); return; }
        string outDir = Path.Combine(dir, "_corner");
        Directory.CreateDirectory(outDir);

        foreach (string f in Directory.GetFiles(dir, "frame-*-c*.png"))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"-c(\d+)_(\d+)$");
            if (!m.Success) continue;
            using Mat frame = Cv2.ImRead(f, ImreadModes.Color);
            if (frame.Empty()) continue;
            var region = det.Detect(frame, new Point(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
            if (region is null) { _out.WriteLine($"{name}: NO MATCH"); continue; }
            var b = region.Value.Bounds;
            _out.WriteLine($"{name}: x={b.X} y={b.Y} w={b.Width} h={b.Height} score={region.Value.Confidence:F3}");
            using Mat crop = new(frame, b);
            Cv2.ImWrite(Path.Combine(outDir, $"{name}.png"), crop);
        }
    }

    [Fact]
    public void DumpMetalMaskNearCursor()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DdoGearScanner", "debug-crops");
        if (!Directory.Exists(dir)) { _out.WriteLine("no folder"); return; }
        string outDir = Path.Combine(dir, "_detect");
        Directory.CreateDirectory(outDir);

        foreach (string f in Directory.GetFiles(dir, "frame-*-c*.png"))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"-c(\d+)_(\d+)$");
            if (!m.Success) continue;
            int cxv = int.Parse(m.Groups[1].Value), cyv = int.Parse(m.Groups[2].Value);
            using Mat frame = Cv2.ImRead(f, ImreadModes.Color);
            if (frame.Empty()) continue;

            int r = 760;
            int x0 = Math.Max(0, cxv - r), y0 = Math.Max(0, cyv - r);
            int x1 = Math.Min(frame.Width, cxv + r), y1 = Math.Min(frame.Height, cyv + r);
            using Mat roi = new(frame, new Rect(x0, y0, x1 - x0, y1 - y0));
            using Mat hsv = new();
            Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
            using Mat mask = new();
            Cv2.InRange(hsv, new Scalar(0, 60, 70), new Scalar(180, 255, 255), mask);
            // Save the masked color ROI so I can see the border outline + read corner offsets
            // (offsets are relative to x0,y0 = cursor-r).
            using Mat vis = new();
            Cv2.CvtColor(mask, vis, ColorConversionCodes.GRAY2BGR);
            Cv2.ImWrite(Path.Combine(outDir, $"{name}_metal_x{x0}_y{y0}.png"), vis);
            _out.WriteLine($"{name}: roi origin ({x0},{y0})");
        }
    }

    [Fact]
    public void RunOnCapturedFrames()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DdoGearScanner", "debug-crops");
        if (!Directory.Exists(dir)) { _out.WriteLine($"no folder {dir}"); return; }

        string[] frames = Directory.GetFiles(dir, "frame-*.png");
        if (frames.Length == 0) { _out.WriteLine("no frames"); return; }

        string outDir = Path.Combine(dir, "_detect");
        Directory.CreateDirectory(outDir);

        // Use the cursor encoded in the filename (frame-...-c<X>_<Y>.png) when present.
        var withCursor = new DarkPanelRegionDetector();
        var noCursor = new DarkPanelRegionDetector(searchRadius: 2200, useCursorProximity: false);

        foreach (string f in frames)
        {
            using Mat frame = Cv2.ImRead(f, ImreadModes.Color);
            if (frame.Empty()) continue;
            string name = Path.GetFileNameWithoutExtension(f);

            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(name, @"-c(\d+)_(\d+)$");
            TooltipRegion? region = m.Success
                ? withCursor.Detect(frame, new Point(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)))
                : noCursor.Detect(frame, new Point(frame.Width / 2, frame.Height / 2));

            // Dump the dark mask (downscaled) for tuning.
            if (m.Success)
            {
                using Mat mask = withCursor.DebugMask(frame, new Point(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
                using Mat small = new();
                Cv2.Resize(mask, small, new Size(mask.Width / 3, mask.Height / 3));
                Cv2.ImWrite(Path.Combine(outDir, $"{name}_mask.png"), small);
            }

            if (region is null) { _out.WriteLine($"{name}: NO REGION"); continue; }

            Rect b = region.Value.Bounds;
            _out.WriteLine($"{name}: x={b.X} y={b.Y} w={b.Width} h={b.Height} score={region.Value.Confidence:F3}");
            using Mat crop = new(frame, b);
            Cv2.ImWrite(Path.Combine(outDir, $"{name}.png"), crop);
        }
    }
}
