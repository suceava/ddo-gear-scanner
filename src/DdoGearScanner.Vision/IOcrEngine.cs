using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Vision;

/// <summary>
/// A pluggable OCR backend: turn a BGR image crop into recognized lines (text + bounding box). Every OCR
/// operation in the app goes through this seam so a different engine can be swapped in. A 2026-07 bake-off
/// (Windows vs Tesseract vs PaddleOCR on real gameplay) settled on Windows OCR — the others were removed
/// (Tesseract misread badly; Paddle chokes on our upscaled crops). The seam stays because the planned
/// OpenRouter LLM reader plugs in here as just another engine.
/// Implementations must be thread-safe for concurrent <see cref="Recognize"/> calls or serialize internally
/// (the gear + run pipelines both OCR off the shared frame stream).
/// </summary>
public interface IOcrEngine
{
    /// <summary>Short identifier for logging (e.g. "Windows").</summary>
    string Name { get; }

    /// <summary>False when the engine can't run (missing language pack / model / native libs). Callers
    /// degrade gracefully — a miss costs an edit, not a crash.</summary>
    bool IsAvailable { get; }

    /// <summary>Recognize text in a BGR (or BGRA) crop. Preprocessing (scaling, thresholding) is the
    /// caller's job; the engine just recognizes. Returns lines top-to-bottom; empty on a blank/failed read.</summary>
    IReadOnlyList<OcrLine> Recognize(OpenCvMat bgrCrop);
}
