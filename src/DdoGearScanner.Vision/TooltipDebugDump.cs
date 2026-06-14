using System.Globalization;
using System.IO;
using System.Text;
using DdoGearScanner.Model;
using OpenCvSharp;

namespace DdoGearScanner.Vision;

/// <summary>
/// Writes a self-contained debug bundle for ONE capture so any mis-parse can be diagnosed from disk
/// alone — no live game needed. Mirrors pg-loot's visual-debug approach. Per capture it emits:
///   &lt;label&gt;-detect.png — the OCR'd image annotated with OCR line boxes (cyan), the bullet
///                            column (yellow), rejected gold blobs (red) and accepted ▶ (green,
///                            numbered), each accepted bullet tagged with the mod it produced;
///   &lt;label&gt;-report.txt — OCR lines w/ boxes, every bullet candidate and why it was kept/dropped,
///                            each ▶ block's rows + joined text + resulting mod or reject reason,
///                            and the final structured item.
/// </summary>
public static class TooltipDebugDump
{
    public static void Write(
        string dir, string label, Mat prepped,
        IReadOnlyList<OcrLine> lines, BulletDetector.Diagnostics bullets,
        IReadOnlyList<TooltipTextParser.ModBlock> blocks, GearItem? item, string backend)
    {
        try
        {
            Directory.CreateDirectory(dir);
            WriteImage(Path.Combine(dir, $"{label}-detect.png"), prepped, lines, bullets, blocks);
            File.WriteAllText(Path.Combine(dir, $"{label}-report.txt"),
                BuildReport(label, prepped, lines, bullets, blocks, item, backend));
        }
        catch { /* debug only — never break a capture */ }
    }

    private static void WriteImage(
        string path, Mat prepped, IReadOnlyList<OcrLine> lines,
        BulletDetector.Diagnostics bullets, IReadOnlyList<TooltipTextParser.ModBlock> blocks)
    {
        using Mat vis = prepped.Channels() == 4 ? Cvt(prepped) : prepped.Clone();

        foreach (OcrLine l in lines)
            Cv2.Rectangle(vis, l.Bbox, new Scalar(255, 255, 0), 1);   // OCR lines = cyan

        if (bullets.ColumnX >= 0)
            Cv2.Line(vis, new Point(bullets.ColumnX, 0), new Point(bullets.ColumnX, vis.Height),
                new Scalar(0, 255, 255), 1);                          // column = yellow

        foreach (BulletDetector.Candidate c in bullets.Candidates)
            if (!c.Accepted)
                Cv2.Rectangle(vis, c.Rect, new Scalar(0, 0, 255), 1); // rejected gold blob = red

        // map bulletY -> block (for the on-image tag)
        for (int i = 0; i < blocks.Count; i++)
        {
            TooltipTextParser.ModBlock b = blocks[i];
            int y = b.BulletY;
            Cv2.Line(vis, new Point(0, y), new Point(vis.Width, y), new Scalar(0, 200, 0), 1);
            string tag = b.Mod is { } m ? $"#{i} {m.Stat} +{Num(m.Value)} ({m.BonusType})" : $"#{i} REJECT";
            Cv2.PutText(vis, tag, new Point(Math.Max(0, bullets.ColumnX + 6), Math.Max(10, y - 2)),
                HersheyFonts.HersheySimplex, 0.4, new Scalar(0, 255, 0), 1, LineTypes.AntiAlias);
        }

        // accepted bullet glyphs on top (green box) so they read over everything
        foreach (BulletDetector.Candidate c in bullets.Candidates)
            if (c.Accepted)
                Cv2.Rectangle(vis, new Rect(c.Rect.X - 1, c.Rect.Y - 1, c.Rect.Width + 2, c.Rect.Height + 2),
                    new Scalar(0, 255, 0), 1);

        Cv2.ImWrite(path, vis);
    }

    private static string BuildReport(
        string label, Mat prepped, IReadOnlyList<OcrLine> lines,
        BulletDetector.Diagnostics bullets, IReadOnlyList<TooltipTextParser.ModBlock> blocks,
        GearItem? item, string backend)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== DDO tooltip debug: {label} ===");
        sb.AppendLine($"backend={backend}  prepped={prepped.Width}x{prepped.Height}");
        sb.AppendLine();

        sb.AppendLine("--- PARSED ITEM ---");
        if (item is null) sb.AppendLine("(null)");
        else
        {
            sb.AppendLine($"Name: {item.Name}");
            sb.AppendLine($"Type: {item.ItemTypeText}   ML: {item.MinimumLevel}   Slot: {item.Slot}   Binding: {item.Binding}");
            sb.AppendLine($"Mods ({item.Mods.Count}):");
            foreach (Mod m in item.Mods) sb.AppendLine($"    {m.Stat} +{Num(m.Value)} ({m.BonusType})");
            sb.AppendLine($"Augments ({item.Augments.Count}):");
            foreach (AugmentSlot a in item.Augments) sb.AppendLine($"    {a.Color} {(a.IsEmpty ? "Empty" : a.Filled)}");
            sb.AppendLine($"Set bonuses ({item.SetBonuses.Count}):");
            foreach (SetBonus set in item.SetBonuses) sb.AppendLine($"    {set.SetName}");
        }
        sb.AppendLine();

        sb.AppendLine("--- BULLET DETECTION ---");
        sb.AppendLine($"unit={bullets.Unit:F2}  columnX={bullets.ColumnX}  tol={bullets.Tol}  " +
                      $"accepted={bullets.BulletYs.Count}  candidates={bullets.Candidates.Count}");
        sb.AppendLine($"bulletYs=[{string.Join(", ", bullets.BulletYs)}]");
        // Only the triangle-passing / column / accepted candidates are interesting — a near-miss
        // (a real ▶ that scored verts=3 + size ok but fell off-column) shows here with col=n. The
        // hundreds of gold-text blobs that fail size/shape are summarised as a count.
        var interesting = bullets.Candidates
            .Where(c => (c.SizeOk && c.Verts == 3) || c.InColumn || c.Accepted)
            .OrderBy(c => c.Rect.Y).ToList();
        sb.AppendLine($"  interesting candidates ({interesting.Count} of {bullets.Candidates.Count}; " +
                      "rect | sizeOk verts leftFrac | inCol accepted):");
        foreach (BulletDetector.Candidate c in interesting)
            sb.AppendLine($"    [{c.Rect.X,4},{c.Rect.Y,4} {c.Rect.Width,3}x{c.Rect.Height,-3}] " +
                          $"size={(c.SizeOk ? "Y" : "n")} verts={c.Verts} left={c.LeftFrac:F2} | " +
                          $"col={(c.InColumn ? "Y" : "n")} acc={(c.Accepted ? "Y" : "n")}");
        sb.AppendLine();

        sb.AppendLine("--- MOD BLOCKS (bullet-segmented) ---");
        foreach (TooltipTextParser.ModBlock b in blocks)
        {
            sb.AppendLine($"  bulletY={b.BulletY}");
            foreach (string row in b.Rows) sb.AppendLine($"      row: {row}");
            sb.AppendLine($"      joined: {b.JoinedText}");
            sb.AppendLine(b.Mod is { } m
                ? $"      => MOD: {m.Stat} +{Num(m.Value)} ({m.BonusType})"
                : $"      => REJECT: {b.RejectReason}");
        }
        sb.AppendLine();

        sb.AppendLine("--- OCR LINES (text | bbox) ---");
        foreach (OcrLine l in lines)
            sb.AppendLine($"  [{l.Bbox.X,4},{l.Bbox.Y,4} {l.Bbox.Width,3}x{l.Bbox.Height,-3}]  {l.Text}");

        return sb.ToString();
    }

    private static Mat Cvt(Mat bgra)
    {
        Mat bgr = new();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
