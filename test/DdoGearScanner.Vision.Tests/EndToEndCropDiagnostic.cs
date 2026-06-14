using System.IO;
using DdoGearScanner.Model;
using DdoGearScanner.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace DdoGearScanner.Vision.Tests;

// Dev diagnostic: run the REAL reader (preprocess -> bullet detect -> Windows OCR -> bullet-
// segmented parse) on a saved crop and print the structured result. Machine-specific (needs the
// crop + a Windows OCR pack); skips if absent.
public class EndToEndCropDiagnostic
{
    private readonly ITestOutputHelper _out;
    public EndToEndCropDiagnostic(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ReparseAllSavedCrops()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DdoGearScanner", "debug-crops");
        if (!Directory.Exists(dir)) { _out.WriteLine("no debug-crops dir"); return; }
        var ocr = new LocalOcr();
        if (!ocr.IsAvailable) { _out.WriteLine("no OCR engine"); return; }
        var reader = new LocalOcrTooltipReader(ocr);

        foreach (string crop in Directory.GetFiles(dir, "crop-*.png").OrderBy(f => f))
        {
            using Mat mat = Cv2.ImRead(crop, ImreadModes.Color);
            GearItem? item = reader.ReadAsync(mat).GetAwaiter().GetResult().Item;
            _out.WriteLine($"### {Path.GetFileName(crop)}  ::  {item?.Name}");
            foreach (Mod m in item?.Mods ?? new List<Mod>())
            {
                _out.WriteLine($"     {m.Stat}   [{m.BonusType}={m.ValueText}]");
                if (m.Description is not null) _out.WriteLine($"         desc: {m.Description}");
            }
            foreach (AugmentSlot a in item?.Augments ?? new List<AugmentSlot>())
                _out.WriteLine($"     AUG: {a.Color} {(a.IsEmpty ? "(empty)" : a.Filled)}");
        }
    }

    [Fact]
    public void ParseCrop()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DdoGearScanner", "debug-crops");
        string crop = Path.Combine(dir, "crop-20260613-003129-139.png");
        if (!File.Exists(crop)) { _out.WriteLine($"missing {crop}"); return; }

        var ocr = new LocalOcr();
        if (!ocr.IsAvailable) { _out.WriteLine("no OCR engine"); return; }
        var reader = new LocalOcrTooltipReader(ocr);

        using Mat mat = Cv2.ImRead(crop, ImreadModes.Color);
        TooltipReadResult result = reader.ReadAsync(mat, dir, "_e2e").GetAwaiter().GetResult();
        _out.WriteLine($"wrote bundle: {Path.Combine(dir, "_e2e-detect.png")} + _e2e-report.txt");
        GearItem? item = result.Item;
        if (item is null) { _out.WriteLine("null item"); return; }

        _out.WriteLine($"NAME: {item.Name}");
        _out.WriteLine($"TYPE: {item.ItemTypeText}   ML: {item.MinimumLevel}");
        _out.WriteLine($"MODS ({item.Mods.Count}):");
        foreach (Mod m in item.Mods)
            _out.WriteLine($"  {m.Stat} +{m.Value} ({m.BonusType})");
        _out.WriteLine($"AUGMENTS: {item.Augments.Count}   SETS: {item.SetBonuses.Count}");
    }
}
