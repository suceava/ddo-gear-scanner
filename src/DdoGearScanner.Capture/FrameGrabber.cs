using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner.Capture;

/// <summary>
/// Keeps a clone of the most recent captured frame so the capture pipeline can grab a
/// single shot on demand (when the hotkey fires) instead of processing every frame like
/// pg-loot's per-frame loop. Subscribe <see cref="OnFrame"/> to
/// <see cref="CaptureCoordinator.FrameArrived"/>.
/// </summary>
public sealed class FrameGrabber : IDisposable
{
    private readonly object _lock = new();
    private OpenCvMat? _latest;
    private bool _disposed;

    /// <summary>Wire this to CaptureCoordinator.FrameArrived. The incoming Mat is owned by the
    /// caller and disposed after the event returns, so we clone what we keep.</summary>
    public void OnFrame(OpenCvMat frame)
    {
        if (frame.Empty()) return;
        OpenCvMat clone = frame.Clone();
        lock (_lock)
        {
            if (_disposed) { clone.Dispose(); return; }
            _latest?.Dispose();
            _latest = clone;
        }
    }

    /// <summary>Returns a clone of the latest frame (caller owns it), or null if none yet.</summary>
    public OpenCvMat? GrabLatest()
    {
        lock (_lock)
        {
            return _latest is null || _latest.Empty() ? null : _latest.Clone();
        }
    }

    public bool HasFrame
    {
        get { lock (_lock) { return _latest is not null && !_latest.Empty(); } }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _latest?.Dispose();
            _latest = null;
        }
    }
}
