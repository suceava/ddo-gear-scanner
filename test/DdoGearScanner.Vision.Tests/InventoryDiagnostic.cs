using System.IO;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace DdoGearScanner.Vision.Tests;

// Dev diagnostic: does the rag-doll template (cut from a native-scale inventory screenshot) match
// the live capture frames? Validates scale + template quality before building the slot map.
public class InventoryDiagnostic
{
    private readonly ITestOutputHelper _out;
    public InventoryDiagnostic(ITestOutputHelper output) => _out = output;

    [Fact]
    public void MatchRagdollInFrames()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DdoGearScanner", "debug-crops");
        string tplPath = Path.Combine(dir, "_ragdoll_tpl.png");
        if (!File.Exists(tplPath)) { _out.WriteLine("no template"); return; }
        using Mat tpl = Cv2.ImRead(tplPath, ImreadModes.Color);

        string outDir = Path.Combine(dir, "_inv");
        Directory.CreateDirectory(outDir);

        foreach (string f in Directory.GetFiles(dir, "frame-*.png"))
        {
            using Mat frame = Cv2.ImRead(f, ImreadModes.Color);
            if (frame.Empty() || frame.Width < tpl.Width || frame.Height < tpl.Height) continue;
            using Mat result = new();
            Cv2.MatchTemplate(frame, tpl, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
            _out.WriteLine($"{Path.GetFileNameWithoutExtension(f)}: score={maxVal:F3} at ({maxLoc.X},{maxLoc.Y})");

            if (maxVal > 0.5)
            {
                int x = Math.Clamp(maxLoc.X - 90, 0, frame.Width - 1);
                int y = Math.Clamp(maxLoc.Y - 70, 0, frame.Height - 1);
                int w = Math.Min(330, frame.Width - x);
                int h = Math.Min(360, frame.Height - y);
                using Mat ctx = new(frame, new Rect(x, y, w, h));
                Cv2.ImWrite(Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(f)}_match.png"), ctx);
            }
        }
    }
}
