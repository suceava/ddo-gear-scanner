using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>Shared OCR preprocessing for the run-tracker regions. Scale is ADAPTIVE: small regions (the
/// tracker box) get a 2x upscale so tiny text reads; large regions (a full-window sweep for the entry
/// popup) are read at native resolution — upscaling a 4K frame to 7680px made OCR take ~6s and starved
/// the whole pipeline. Native 4K keeps the big popup/panel text readable while staying fast.</summary>
internal static class RunVision
{
    /// <summary>OCR with an adaptive scale: small crops upscale 2x (tiny text), big ones stay native
    /// (a 4K upscale takes ~6s and starves the pipeline). Used to LOCATE things cheaply on a big sweep.</summary>
    public static IReadOnlyList<OcrLine> Read(LocalOcr ocr, OpenCvMat bgr)
    {
        if (bgr.Empty()) return Array.Empty<OcrLine>();
        double scale = bgr.Width <= 900 ? 2.0 : bgr.Width <= 1500 ? 1.5 : 1.0;
        return ReadAt(ocr, bgr, scale);
    }

    /// <summary>OCR at a fixed scale. Used for the second pass: once a panel is located, its small crop is
    /// read enlarged (e.g. 3x) so small text — the quest level's digits — is crisp. Boxes come back in the
    /// scaled crop's coordinates, which is fine since the popup parser works in relative geometry.</summary>
    public static IReadOnlyList<OcrLine> ReadAt(LocalOcr ocr, OpenCvMat bgr, double scale)
    {
        if (bgr.Empty()) return Array.Empty<OcrLine>();

        OpenCvMat working = bgr;
        OpenCvMat? converted = null;
        if (bgr.Channels() == 4)
        {
            converted = new OpenCvMat();
            Cv2.CvtColor(bgr, converted, ColorConversionCodes.BGRA2BGR);
            working = converted;
        }

        IReadOnlyList<OcrLine> result;
        if (Math.Abs(scale - 1.0) < 0.01)
        {
            result = ocr.Recognize(working);
        }
        else
        {
            using OpenCvMat scaled = new();
            Cv2.Resize(working, scaled, new Size((int)(working.Width * scale), (int)(working.Height * scale)), 0, 0, InterpolationFlags.Cubic);
            result = ocr.Recognize(scaled);
        }
        converted?.Dispose();
        return result;
    }

    public static string JoinText(IReadOnlyList<OcrLine> lines)
        => string.Join("\n", lines.Select(l => l.Text));
}

/// <summary>
/// Reads DDO's adventure-entry popup (the dialog you get clicking a quest entrance) from a calibrated
/// screen region: OCRs the region and parses out the quest name + level via <see cref="RunTextParser"/>.
/// This is the authoritative quest-name source (the ornate tracker title OCRs badly).
/// </summary>
public sealed class EntryPopupReader
{
    private readonly LocalOcr _ocr;
    public EntryPopupReader(LocalOcr ocr) => _ocr = ocr;

    public bool IsAvailable => _ocr.IsAvailable;

    /// <summary>OCR the calibrated popup region and parse the entry (name + level), or null if the popup
    /// isn't up. The region IS the popup, so it's OCR'd directly and parsed in place. Scale by size: a
    /// tight box upscales (crisp level digits), a large/uncalibrated region downscales for speed.
    /// <paramref name="rawText"/> is always set (for the debug dump / overlay).</summary>
    public QuestEntry? Read(OpenCvMat regionBgr, out string rawText)
    {
        // Upscale a tight calibrated box hard — small text like a "11" level (two thin identical strokes)
        // needs the extra size or OCR merges the digits into "1".
        double scale = regionBgr.Width <= 1200 ? 4.0 : regionBgr.Width <= 2200 ? 1.5 : 2000.0 / regionBgr.Width;
        IReadOnlyList<OcrLine> lines = RunVision.ReadAt(_ocr, regionBgr, scale);
        rawText = RunVision.JoinText(lines);
        QuestEntry? entry = RunTextParser.ParseEntry(lines);
        if (entry is not null) DebugDump(regionBgr, rawText);
        return entry;
    }

    // Diagnostic: dump the region crop + its OCR so mis-reads (e.g. the level) are visible.
    private static void DebugDump(OpenCvMat region, string ocrText)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DdoGearScanner", "run-debug");
            System.IO.Directory.CreateDirectory(dir);
            Cv2.ImWrite(System.IO.Path.Combine(dir, "popup.png"), region);
            string log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddo-gear-scanner.log");
            System.IO.File.AppendAllText(log,
                $"{DateTime.Now:HH:mm:ss.fff} [popup] ocr: {ocrText.Replace("\n", " | ")}{Environment.NewLine}");
        }
        catch { }
    }
}

/// <summary>
/// OCRs the calibrated chat-log region and returns its lines (top→bottom). The pipeline compares
/// successive reads to detect newly-arrived lines (the chat is append-only, so new text shifts the
/// existing lines up) — that's how "Adventure Completed" is recognized as fresh rather than stale.
/// </summary>
public sealed class ChatLogReader
{
    private readonly LocalOcr _ocr;
    public ChatLogReader(LocalOcr ocr) => _ocr = ocr;

    public bool IsAvailable => _ocr.IsAvailable;

    /// <summary>The chat region's OCR'd lines, top to bottom (blank lines dropped).</summary>
    public IReadOnlyList<string> ReadLines(OpenCvMat regionBgr)
        => RunVision.Read(_ocr, regionBgr).Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
}

/// <summary>What the quest-tracker panel shows this tick: the quest title (or null when nothing is
/// tracked) and whether it reads "Status: Completed". The tracker is the authoritative signal for BOTH
/// entry (a title appears) and completion (the panel flips to Completed) — and it OCRs reliably because
/// it's the same panel throughout the run.</summary>
public readonly record struct TrackerStatus(string? Name, bool Completed);

/// <summary>
/// Reads the quest-tracker panel (top-right objectives box) from a screen-region crop: the tracked quest
/// name and whether the quest has completed ("Status: Completed").
/// </summary>
public sealed class QuestTrackerReader
{
    private readonly LocalOcr _ocr;
    public QuestTrackerReader(LocalOcr ocr) => _ocr = ocr;

    public bool IsAvailable => _ocr.IsAvailable;

    public TrackerStatus Read(OpenCvMat regionBgr)
    {
        IReadOnlyList<OcrLine> lines = RunVision.Read(_ocr, regionBgr);
        string? name = RunTextParser.CleanTrackerName(lines.Select(l => l.Text));
        bool completed = RunTextParser.IsTrackerCompleted(lines.Select(l => l.Text));
        return new TrackerStatus(name, completed);
    }
}
