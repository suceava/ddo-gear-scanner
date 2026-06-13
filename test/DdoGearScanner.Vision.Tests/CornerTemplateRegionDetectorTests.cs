using System.IO;
using DdoGearScanner.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace DdoGearScanner.Vision.Tests;

// Offline validation of the coil-corner detector against a real captured frame. Writes the
// detected crop + bounds next to the test binary so the result can be eyeballed/tuned.
public class CornerTemplateRegionDetectorTests
{
    private readonly ITestOutputHelper _out;
    public CornerTemplateRegionDetectorTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void FindsTooltipInSampleFrame()
    {
        string dir = AppContext.BaseDirectory;
        string framePath = Path.Combine(dir, "fixtures", "sample-frame.png");
        string tplPath = Path.Combine(dir, "fixtures", "tooltip-corner-right.png");
        Assert.True(File.Exists(framePath), $"missing {framePath}");
        Assert.True(File.Exists(tplPath), $"missing {tplPath}");

        using Mat frame = Cv2.ImRead(framePath, ImreadModes.Color);
        using Mat tpl = Cv2.ImRead(tplPath, ImreadModes.Color);
        CornerTemplateRegionDetector detector = new(new[] { (tpl, "gold") });

        // Cursor roughly where the hovered item was (right-ish of the inventory).
        TooltipRegion? region = detector.Detect(frame, new Point(frame.Width / 2, frame.Height / 2));

        Assert.NotNull(region);
        Rect b = region!.Value.Bounds;
        _out.WriteLine($"Detected bounds: x={b.X} y={b.Y} w={b.Width} h={b.Height} score={region.Value.Confidence:F3}");

        // Save the crop for visual confirmation.
        using Mat crop = new(frame, b);
        string outPath = Path.Combine(dir, "detected-tooltip.png");
        Cv2.ImWrite(outPath, crop);
        File.WriteAllText(Path.Combine(dir, "detected-bounds.txt"),
            $"{b.X},{b.Y},{b.Width},{b.Height},score={region.Value.Confidence:F3}");
        _out.WriteLine($"Wrote {outPath}");

        Assert.True(b.Width > 100 && b.Height > 100, "region too small");
    }
}
