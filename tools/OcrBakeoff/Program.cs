using DdoGearScanner.Vision;
using OpenCvSharp;

// OCR bake-off: run each available engine over sample crops (the ornate in-dungeon quest-title being the
// hard case) and print what each reads, so we can pick the engine that actually reads DDO's text.
//
// Usage: OcrBakeoff <folder-of-pngs>   (defaults to the scratchpad bakeoff dir)

string dir = args.Length > 0 ? args[0]
    : @"C:\Users\Dan\AppData\Local\Temp\claude\c--code\bcc6d78e-4963-4450-9dc2-7d5aab413fed\scratchpad\bakeoff";

string[] images = Directory.GetFiles(dir, "*.png").OrderBy(f => f).ToArray();
Console.WriteLine($"Samples in {dir}: {images.Length}\n");

// Engines under test. Windows OCR won the 2026-07 bake-off (Tesseract misread real gameplay badly;
// PaddleOCR fails on upscaled crops) — add candidates here (any IOcrEngine) to re-run a comparison.
var engines = new List<IOcrEngine> { new LocalOcr() };

foreach (string img in images)
{
    Console.WriteLine($"=== {Path.GetFileName(img)} ===");
    using Mat bgr = Cv2.ImRead(img, ImreadModes.Color);
    if (bgr.Empty()) { Console.WriteLine("  (couldn't load)\n"); continue; }

    foreach (IOcrEngine engine in engines)
    {
        foreach (double scale in new[] { 1.0, 3.0 })
        {
            using Mat scaled = scale == 1.0 ? bgr.Clone()
                : bgr.Resize(new Size(bgr.Width * scale, bgr.Height * scale), 0, 0, InterpolationFlags.Cubic);
            string text;
            try { text = string.Join(" | ", engine.Recognize(scaled).Select(l => l.Text)); }
            catch (Exception ex) { text = $"<error: {ex.Message.Split('\n')[0]}>"; }
            Console.WriteLine($"  {engine.Name,-10} x{scale}: {text}");
        }
    }
    Console.WriteLine();
}
