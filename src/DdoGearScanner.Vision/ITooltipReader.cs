using DdoGearScanner.Model;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>Result of reading a tooltip crop: the structured item (null on total failure),
/// the raw text (OCR text or model output), a rough confidence, and which backend produced it.</summary>
public sealed record TooltipReadResult(GearItem? Item, string RawText, double Confidence, string Backend);

/// <summary>
/// The pluggable seam between "get pixels of a tooltip" and "structured gear data".
/// Phase 1 implementation is <see cref="LocalOcrTooltipReader"/> (Windows OCR + parser).
/// Phase 2 will add a Claude vision reader implementing this same interface — the
/// <see cref="GearItem"/> contract means swapping backends changes nothing downstream.
/// </summary>
public interface ITooltipReader
{
    string BackendName { get; }
    bool IsAvailable { get; }
    Task<TooltipReadResult> ReadAsync(OpenCvMat tooltipBgr, CancellationToken ct = default);
}
