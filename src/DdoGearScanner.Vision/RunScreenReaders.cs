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
    public static IReadOnlyList<OcrLine> Read(IOcrEngine ocr, OpenCvMat bgr)
    {
        if (bgr.Empty()) return Array.Empty<OcrLine>();
        double scale = bgr.Width <= 900 ? 2.0 : bgr.Width <= 1500 ? 1.5 : 1.0;
        return ReadAt(ocr, bgr, scale);
    }

    /// <summary>OCR at a fixed scale. Used for the second pass: once a panel is located, its small crop is
    /// read enlarged (e.g. 3x) so small text — the quest level's digits — is crisp. Boxes come back in the
    /// scaled crop's coordinates, which is fine since the popup parser works in relative geometry.</summary>
    public static IReadOnlyList<OcrLine> ReadAt(IOcrEngine ocr, OpenCvMat bgr, double scale)
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
    private readonly IOcrEngine _ocr;
    public EntryPopupReader(IOcrEngine ocr) => _ocr = ocr;

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
        if (entry is not null)
        {
            (string? diff, string dbg) = DetectDifficulty(regionBgr, lines, scale);
            if (diff is not null) entry = entry with { Difficulty = diff };
            DebugDump(regionBgr, rawText + $"  [difficulty={diff ?? "?"}] [white: {dbg}]");
        }
        return entry;
    }

    // Difficulty labels the popup can show (lowercase, letters-only for matching).
    private static readonly string[] DiffWords = { "casual", "normal", "hard", "elite", "reaper", "solo" };
    // Fixed left→right order of the heroic difficulty icons — lets us extrapolate every icon's X from any
    // two detected labels, so a merged/dropped label ("Casual Normal") doesn't lose those candidates.
    private static readonly string[] DiffOrder = { "casual", "normal", "hard", "elite", "reaper" };

    /// <summary>Which difficulty is SELECTED. Read by the LABEL, not the icon: the selected label goes
    /// bright WHITE while the others stay dim gray, regardless of the tier's theme colour. (The icon glow is
    /// colour-biased — silver Casual is brightest, red Reaper darkest — so comparing icon brightness just
    /// picks the lightest-coloured tier, not the selection.) Label X for every slot is extrapolated from the
    /// fixed difficulty order, so a tier is still a candidate when its label OCR merges/drops. Reaper is
    /// special: selecting it swaps the icon for a "N Skull" dropdown, so a "Skull" reading = Reaper N.</summary>
    private static (string? selected, string debug) DetectDifficulty(OpenCvMat regionBgr, IReadOnlyList<OcrLine> lines, double scale)
    {
        // Reaper: selecting it replaces the icon with a "N Skull" dropdown — the word "Skull" is itself the
        // tell (there is no ring to read). Capture the skull tier when shown.
        foreach (OcrLine l in lines)
            if (l.Text.Contains("Skull", StringComparison.OrdinalIgnoreCase))
            {
                System.Text.RegularExpressions.Match sm = System.Text.RegularExpressions.Regex.Match(l.Text, @"\d+");
                return (sm.Success ? $"Reaper {sm.Value}" : "Reaper", "skull");
            }

        var labels = new List<(string name, int cx, int top, int h)>();
        foreach (OcrLine l in lines)
        {
            string t = new string(l.Text.Where(char.IsLetter).ToArray()).ToLowerInvariant();
            foreach (string d in DiffWords)
                if (t == d)
                {
                    Rect b = l.Bbox;
                    labels.Add((d, (int)((b.X + b.Width / 2.0) / scale), (int)(b.Y / scale), Math.Max((int)(b.Height / scale), 8)));
                    break;
                }
        }
        if (labels.Count < 2) return (null, "few-labels");
        labels.Sort((a, b) => a.cx.CompareTo(b.cx));

        OpenCvMat bgr = regionBgr;
        OpenCvMat? conv = null;
        if (regionBgr.Channels() == 4) { conv = new OpenCvMat(); Cv2.CvtColor(regionBgr, conv, ColorConversionCodes.BGRA2BGR); bgr = conv; }
        Rect full = new(0, 0, bgr.Width, bgr.Height);

        int labelTop = labels.Min(x => x.top);
        int labelH = Math.Max(labels.Max(x => x.h), 8);

        // Extrapolate the LABEL X for ALL standard slots from the labels that DID read (map each detected
        // name to its fixed-order index, fit cx = x0 + slot*spacing) — so every difficulty is a candidate
        // even when its label OCR merged or dropped ("Casual Normal" as one word).
        var pts = labels.Select(l => (idx: Array.IndexOf(DiffOrder, l.name), l.cx)).Where(p => p.idx >= 0).OrderBy(p => p.idx).ToList();
        var cols = new List<(string name, int cx)>();
        double spacing;
        if (pts.Count >= 2 && pts[^1].idx != pts[0].idx)
        {
            spacing = (pts[^1].cx - pts[0].cx) / (double)(pts[^1].idx - pts[0].idx);
            double x0 = pts[0].cx - pts[0].idx * spacing;
            for (int s = 0; s < DiffOrder.Length; s++) cols.Add((DiffOrder[s], (int)Math.Round(x0 + s * spacing)));
        }
        else
        {
            spacing = (labels[^1].cx - labels[0].cx) / (double)Math.Max(1, labels.Count - 1);
            foreach (var l in labels) cols.Add((l.name, l.cx));
        }
        int colHalf = Math.Max(labelH, (int)(Math.Abs(spacing) * 0.40));

        string? best = null;
        double bestWhite = -1, second = -1;
        var sb = new System.Text.StringBuilder();
        foreach ((string name, int cx) in cols)
        {
            // Sample the LABEL text row and count bright-WHITE pixels. Selected label = white (passes),
            // unselected = gray (below threshold). Colour-independent, unlike the icon glow.
            Rect lab = new Rect(cx - colHalf, labelTop - labelH / 3, colHalf * 2, (int)(labelH * 1.7)) & full;
            double whiteFrac = 0;
            if (lab.Width > 2 && lab.Height > 2)
            {
                using OpenCvMat sub = new(bgr, lab);
                using OpenCvMat gray = new();
                Cv2.CvtColor(sub, gray, ColorConversionCodes.BGR2GRAY);
                using OpenCvMat mask = new();
                Cv2.Threshold(gray, mask, 200, 255, ThresholdTypes.Binary);   // bright WHITE label text only
                whiteFrac = (double)Cv2.CountNonZero(mask) / (sub.Rows * sub.Cols);
            }
            sb.Append($"{name[..Math.Min(4, name.Length)]}={whiteFrac:F3} ");
            if (whiteFrac > bestWhite) { second = bestWhite; bestWhite = whiteFrac; best = name; }
            else if (whiteFrac > second) second = whiteFrac;
        }
        conv?.Dispose();
        string? selected = best is not null && bestWhite > 0.015 && bestWhite > second * 1.5
            ? char.ToUpperInvariant(best[0]) + best[1..] : null;
        return (selected, sb.ToString().Trim() + $" lblY={labelTop}");
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
    private readonly IOcrEngine _ocr;
    public ChatLogReader(IOcrEngine ocr) => _ocr = ocr;

    public bool IsAvailable => _ocr.IsAvailable;

    /// <summary>The chat region's OCR'd lines, top to bottom (blank lines dropped).</summary>
    public IReadOnlyList<string> ReadLines(OpenCvMat regionBgr)
        => RunVision.Read(_ocr, regionBgr).Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
}

/// <summary>
/// Reads DDO's avatar area (calibrated region) — the character NAME above the health bar and the LEVEL
/// under the avatar — for stamping onto a run. Small region, so it's upscaled for crisp small text.
/// </summary>
public sealed class CharacterReader
{
    private readonly IOcrEngine _ocr;
    public CharacterReader(IOcrEngine ocr) => _ocr = ocr;

    public bool IsAvailable => _ocr.IsAvailable;

    public CharacterInfo Read(OpenCvMat regionBgr, out string rawText)
    {
        double scale = regionBgr.Width <= 900 ? 3.0 : regionBgr.Width <= 1600 ? 2.0 : 1.0;
        IReadOnlyList<OcrLine> lines = RunVision.ReadAt(_ocr, regionBgr, scale);
        rawText = RunVision.JoinText(lines);
        CharacterInfo info = RunTextParser.ParseCharacter(lines.Select(l => l.Text).ToList());

        // The level pip ("20") sits as small white text OVER the portrait image, so the plain OCR skips
        // it. If we didn't get a level, retry on a bright-text-only threshold that drops the portrait.
        if (info.Level is null)
        {
            int? lvl = ReadLevelThresholded(regionBgr);
            if (lvl is not null) info = info with { Level = lvl };
        }
        return info;
    }

    private int? ReadLevelThresholded(OpenCvMat regionBgr)
    {
        try
        {
            OpenCvMat src = regionBgr;
            using OpenCvMat? conv = regionBgr.Channels() == 4 ? new OpenCvMat() : null;
            if (conv is not null) { Cv2.CvtColor(regionBgr, conv, ColorConversionCodes.BGRA2BGR); src = conv; }
            using OpenCvMat gray = new();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            using OpenCvMat bin = new();
            Cv2.Threshold(gray, bin, 165, 255, ThresholdTypes.Binary);   // keep only bright text
            using OpenCvMat binBgr = new();
            Cv2.CvtColor(bin, binBgr, ColorConversionCodes.GRAY2BGR);
            var lines = RunVision.ReadAt(_ocr, binBgr, binBgr.Width <= 900 ? 3.0 : 2.0);
            return RunTextParser.ParseCharacter(lines.Select(l => l.Text).ToList()).Level;
        }
        catch { return null; }
    }
}

/// <summary>What the quest-tracker panel shows this tick: the quest title (or null when nothing is
/// tracked), whether it reads "Status: Completed", and ALL of the OCR'd lines. The header line is the
/// authoritative signal (a title = entry, "Status: Completed" = done), but callers also need the raw
/// lines: the header intermittently drops to an objective read, so "is this still my quest?" and the
/// wilderness "Slayer:" tell must be checked across every line, not just the one the cleaner picked.</summary>
public readonly record struct TrackerStatus(string? Name, bool Completed, IReadOnlyList<string> Lines);

/// <summary>
/// Reads the quest-tracker panel (top-right objectives box) from a screen-region crop: the tracked quest
/// name and whether the quest has completed ("Status: Completed").
/// </summary>
public sealed class QuestTrackerReader
{
    private readonly IOcrEngine _ocr;
    public QuestTrackerReader(IOcrEngine ocr) => _ocr = ocr;

    public bool IsAvailable => _ocr.IsAvailable;

    public TrackerStatus Read(OpenCvMat regionBgr)
    {
        IReadOnlyList<OcrLine> lines = RunVision.Read(_ocr, regionBgr);
        var texts = lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        string? name = RunTextParser.CleanTrackerName(texts);
        bool completed = RunTextParser.IsTrackerCompleted(texts);
        return new TrackerStatus(name, completed, texts);
    }
}
