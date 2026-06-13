using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>A located tooltip box within a frame, plus a rough confidence.</summary>
public readonly record struct TooltipRegion(Rect Bounds, double Confidence);

/// <summary>
/// Finds the gear tooltip box inside a captured frame, anchored on the cursor. Replaceable:
/// Phase 1 uses <see cref="DarkBoxRegionDetector"/> (dark-background contour near the cursor);
/// a future Claude path may skip precise detection and hand a looser cursor-region crop instead.
/// </summary>
public interface ITooltipRegionDetector
{
    /// <param name="frame">Full game-client frame (BGRA or BGR).</param>
    /// <param name="cursorInFrame">Cursor position in frame pixels.</param>
    TooltipRegion? Detect(OpenCvMat frame, Point cursorInFrame);
}
