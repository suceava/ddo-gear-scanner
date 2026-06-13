using System.Diagnostics;
using System.IO;
using System.Text;

namespace DdoGearScanner.Capture;

internal static class DebugLog
{
    private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddo-gear-scanner.log");
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        lock (Sync)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}

public readonly record struct GameWindowRect(int Left, int Top, int Width, int Height);

/// <summary>A visible top-level window, surfaced by the "Detect game window" diagnostic so
/// we can confirm DDO's real process name / class / title at runtime.</summary>
public readonly record struct WindowInfo(IntPtr Handle, uint ProcessId, string ProcessName, string ClassName, string Title);

// Adapted from pg-loot-master's GameWindowTracker. The match target changed from Project
// Gorgon (Unity) to DDO. DDO's exact process name + window class are UNCONFIRMED across the
// standalone vs Steam builds, so matching is deliberately loose: prefer a known process name,
// else fall back to any visible top-level window whose title contains the DDO needle. The
// window class is logged, not hard-required. Use EnumerateCandidateWindows() to lock the real
// values from a running client.
public sealed class GameWindowTracker : IDisposable
{
    // Historically the DDO client is dndclient64.exe (64-bit) / dndclient.exe (32-bit). The
    // Steam wrapper may differ — hence the title fallback below.
    private static readonly string[] ProcessNames = { "dndclient64", "dndclient" };
    private const string TitleNeedle = "Dungeons & Dragons Online";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _lock = new();
    private Timer? _timer;
    private IntPtr _trackedHandle = IntPtr.Zero;
    private GameWindowRect? _lastRect;
    private bool _disposed;

    public event Action<IntPtr, GameWindowRect>? GameWindowChanged;
    public event Action? GameWindowLost;

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DebugLog.Write("Tracker.Start");
            _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
        }
    }

    private int _pollCount;

    private void Poll()
    {
        IntPtr handle;
        GameWindowRect? rect;
        Exception? error = null;
        try
        {
            handle = FindGameWindow();
            rect = handle == IntPtr.Zero ? null : TryGetClientRect(handle);
        }
        catch (Exception ex)
        {
            handle = IntPtr.Zero;
            rect = null;
            error = ex;
        }

        int n = Interlocked.Increment(ref _pollCount);
        if (error is not null)
        {
            DebugLog.Write($"Poll #{n} ERROR: {error.GetType().Name}: {error.Message}");
        }
        else if (n <= 5 || n % 40 == 0)
        {
            DebugLog.Write($"Poll #{n} handle=0x{handle.ToInt64():X} rect={rect?.ToString() ?? "null"}");
        }

        Action? toRaise = null;
        Action<IntPtr, GameWindowRect>? toRaiseChanged = null;
        IntPtr handleArg = IntPtr.Zero;
        GameWindowRect? rectArg = null;

        lock (_lock)
        {
            if (_disposed) return;

            if (rect is null)
            {
                if (_trackedHandle != IntPtr.Zero)
                {
                    _trackedHandle = IntPtr.Zero;
                    _lastRect = null;
                    toRaise = GameWindowLost;
                }
            }
            else
            {
                bool handleChanged = handle != _trackedHandle;
                bool rectChanged = _lastRect != rect;

                if (handleChanged || rectChanged)
                {
                    _trackedHandle = handle;
                    _lastRect = rect;
                    toRaiseChanged = GameWindowChanged;
                    handleArg = handle;
                    rectArg = rect;
                }
            }
        }

        if (toRaise is not null)
        {
            DebugLog.Write("Firing GameWindowLost");
            toRaise.Invoke();
        }
        if (toRaiseChanged is not null && rectArg.HasValue)
        {
            DebugLog.Write($"Firing GameWindowChanged handle=0x{handleArg.ToInt64():X} {rectArg.Value}");
            toRaiseChanged.Invoke(handleArg, rectArg.Value);
        }
    }

    /// <summary>Current tracked client rect in screen pixels, or null when the game window
    /// isn't found. Used by the capture pipeline to map cursor -> frame coordinates.</summary>
    public GameWindowRect? CurrentRect
    {
        get { lock (_lock) { return _lastRect; } }
    }

    /// <summary>Cursor position in physical screen pixels (matches the captured frame's pixel
    /// space when the process is per-monitor DPI aware).</summary>
    public static (int X, int Y) GetCursorScreenPosition()
    {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT p)) return (p.X, p.Y);
        return (0, 0);
    }

    private static IntPtr FindGameWindow()
    {
        List<WindowInfo> windows = EnumerateCandidateWindows();

        // 1) Prefer a window owned by a known DDO client process.
        foreach (WindowInfo w in windows)
        {
            foreach (string name in ProcessNames)
            {
                if (w.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return w.Handle;
            }
        }

        // 2) Fall back to any visible top-level window whose title looks like DDO.
        foreach (WindowInfo w in windows)
        {
            if (w.Title.Contains(TitleNeedle, StringComparison.OrdinalIgnoreCase))
                return w.Handle;
        }

        return IntPtr.Zero;
    }

    /// <summary>Enumerate visible top-level windows that have a title. Surfaced for the
    /// "Detect game window" diagnostic so the real DDO process/class/title can be confirmed.</summary>
    public static List<WindowInfo> EnumerateCandidateWindows()
    {
        List<WindowInfo> results = new();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            int len = NativeMethods.GetWindowTextLength(hWnd);
            if (len <= 0) return true;

            StringBuilder title = new(len + 1);
            NativeMethods.GetWindowText(hWnd, title, title.Capacity);

            StringBuilder cls = new(256);
            NativeMethods.GetClassName(hWnd, cls, cls.Capacity);

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            string procName = SafeProcessName(pid);

            results.Add(new WindowInfo(hWnd, pid, procName, cls.ToString(), title.ToString()));
            return true;
        }, IntPtr.Zero);
        return results;
    }

    private static string SafeProcessName(uint pid)
    {
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static GameWindowRect? TryGetClientRect(IntPtr hWnd)
    {
        // Use the window's rendered bounds (DWMWA_EXTENDED_FRAME_BOUNDS) — these match what
        // Windows.Graphics.Capture frames (full window incl. title bar, no shadow). The client
        // rect excludes the title bar, which would offset the overlay highlight vs the frame.
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        if (NativeMethods.DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT b, 16) == 0 && b.Width > 0 && b.Height > 0)
            return new GameWindowRect(b.Left, b.Top, b.Width, b.Height);

        if (NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT w))
            return new GameWindowRect(w.Left, w.Top, w.Width, w.Height);
        return null;
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
    }
}
