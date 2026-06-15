using System.IO;
using System.Reflection;
using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>
/// Locates DDO's inventory paper-doll by template-matching the generic rag-doll figure (a fixed
/// gray humanoid in a maroon oval — identical for every character/race, confirmed). Returns the
/// rag-doll's top-left position in frame pixels, which is a stable anchor: every equipment slot
/// sits at a fixed offset from it, so the calibrated slot map follows the window when it's dragged.
/// Returns null when the inventory is closed / not visible.
/// </summary>
public sealed class InventoryLocator
{
    private readonly OpenCvMat _ragdoll; // BGR template
    private readonly double _threshold;

    public InventoryLocator(OpenCvMat ragdollBgr, double threshold = 0.6)
    {
        _ragdoll = ragdollBgr.Clone();
        _threshold = threshold;
    }

    public static InventoryLocator? TryLoad(string path, double threshold = 0.6)
    {
        if (!File.Exists(path)) return null;
        using OpenCvMat tpl = Cv2.ImRead(path, ImreadModes.Color);
        if (tpl.Empty()) return null;
        return new InventoryLocator(tpl, threshold);
    }

    /// <summary>Load the rag-doll template embedded in this assembly, so the published single-file exe
    /// is self-contained (no loose Inventory\ragdoll.png next to it).</summary>
    public static InventoryLocator? TryLoadEmbedded(double threshold = 0.6)
    {
        try
        {
            Assembly asm = typeof(InventoryLocator).Assembly;
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("ragdoll.png", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using Stream? s = asm.GetManifestResourceStream(name);
            if (s is null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            using OpenCvMat tpl = Cv2.ImDecode(ms.ToArray(), ImreadModes.Color);
            return tpl.Empty() ? null : new InventoryLocator(tpl, threshold);
        }
        catch { return null; }
    }

    public int TemplateWidth => _ragdoll.Width;
    public int TemplateHeight => _ragdoll.Height;

    /// <summary>Rag-doll top-left in frame pixels, or null if not confidently found.</summary>
    public Point? Locate(OpenCvMat frame)
    {
        if (frame.Empty() || frame.Width < _ragdoll.Width || frame.Height < _ragdoll.Height) return null;

        using OpenCvMat bgr = EnsureBgr(frame, out OpenCvMat? owned);
        using OpenCvMat result = new();
        Cv2.MatchTemplate(bgr, _ragdoll, result, TemplateMatchModes.CCoeffNormed);
        owned?.Dispose();
        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
        return maxVal >= _threshold ? maxLoc : null;
    }

    private static OpenCvMat EnsureBgr(OpenCvMat roi, out OpenCvMat? owned)
    {
        if (roi.Channels() == 4)
        {
            owned = new OpenCvMat();
            Cv2.CvtColor(roi, owned, ColorConversionCodes.BGRA2BGR);
            return owned;
        }
        owned = null;
        return roi;
    }
}
