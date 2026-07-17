using DdoGearScanner.Model;
using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>
/// Phase-1 reader: upscales the tooltip crop, runs Windows OCR, and parses the lines with
/// <see cref="TooltipTextParser"/>. Light preprocessing (3x cubic upscale) measurably helps
/// Windows.Media.Ocr on small game text. Confidence is a crude proxy from how much structure
/// we recovered, so the App can decide whether to (later) escalate to the Claude reader.
/// </summary>
public sealed class LocalOcrTooltipReader : ITooltipReader
{
    private readonly IOcrEngine _ocr;

    public LocalOcrTooltipReader(IOcrEngine ocr) => _ocr = ocr;

    public string BackendName => "Local OCR";
    public bool IsAvailable => _ocr.IsAvailable;

    public Task<TooltipReadResult> ReadAsync(OpenCvMat tooltipBgr, CancellationToken ct = default)
        => ReadAsync(tooltipBgr, null, null, ct);

    /// <summary>
    /// As <see cref="ReadAsync(OpenCvMat, CancellationToken)"/>, but when <paramref name="debugDir"/>
    /// and <paramref name="label"/> are set, also writes a full diagnostic bundle
    /// (<see cref="TooltipDebugDump"/>) so any mis-parse is reproducible from disk.
    /// </summary>
    public Task<TooltipReadResult> ReadAsync(
        OpenCvMat tooltipBgr, string? debugDir, string? label, CancellationToken ct = default)
    {
        if (tooltipBgr.Empty())
            return Task.FromResult(new TooltipReadResult(null, string.Empty, 0, BackendName));

        using OpenCvMat prepped = Preprocess(tooltipBgr);
        IReadOnlyList<OcrLine> lines = _ocr.Recognize(prepped);
        // The gold ▶ mod-bullets delimit mods reliably where text can't. Detected on the SAME
        // preprocessed image so the bullet Y-centres line up with the OCR line boxes.
        IReadOnlyList<int> bullets = BulletDetector.DetectYCenters(prepped, out BulletDetector.Diagnostics bdiag);
        GearItem item = TooltipTextParser.Parse(lines, bullets, out List<TooltipTextParser.ModBlock> blocks);

        if (!string.IsNullOrEmpty(debugDir) && !string.IsNullOrEmpty(label))
            TooltipDebugDump.Write(debugDir!, label!, prepped, lines, bdiag, blocks, item, BackendName);

        double confidence = ScoreConfidence(item, lines.Count);
        return Task.FromResult(new TooltipReadResult(item, item.RawOcrText, confidence, BackendName));
    }

    private static OpenCvMat Preprocess(OpenCvMat bgr)
    {
        // Ensure 3-channel BGR (capture frames are BGRA), then upscale for small text.
        OpenCvMat working = bgr;
        OpenCvMat? converted = null;
        if (bgr.Channels() == 4)
        {
            converted = new OpenCvMat();
            Cv2.CvtColor(bgr, converted, ColorConversionCodes.BGRA2BGR);
            working = converted;
        }

        OpenCvMat scaled = new();
        Cv2.Resize(working, scaled, new Size(working.Width * 3, working.Height * 3), 0, 0, InterpolationFlags.Cubic);
        converted?.Dispose();
        return scaled;
    }

    private static double ScoreConfidence(GearItem item, int lineCount)
    {
        if (lineCount == 0) return 0;
        double score = 0;
        if (!string.IsNullOrWhiteSpace(item.Name)) score += 0.3;
        if (item.MinimumLevel is not null) score += 0.2;
        if (item.Mods.Count > 0) score += 0.4;
        if (item.Augments.Count > 0 || item.SetBonuses.Count > 0) score += 0.1;
        return Math.Min(1.0, score);
    }
}
