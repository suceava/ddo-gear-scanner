using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DdoGearScanner;

/// <summary>Switches a window's OS title bar to dark mode (Win10 1809+/Win11), so the white title
/// bar with faded text matches the dark app theme.</summary>
internal static class WindowChrome
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;       // current
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE = 19;   // Win10 1809..1909

    public static void UseDarkTitleBar(Window window)
    {
        static void Apply(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE, ref on, sizeof(int));
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) Apply(handle);
        else window.SourceInitialized += (_, _) => Apply(new WindowInteropHelper(window).Handle);
    }

    // Window placement persistence via the Win32 WINDOWPLACEMENT API. WPF's Left/Top are DIPs and
    // restoring them by hand is fragile across multiple monitors / mixed DPI (the window ends up
    // off-screen, so WPF cascades it to a "random" default spot). SetWindowPlacement takes physical
    // workspace pixels, is DPI- and multi-monitor-correct, and the OS clamps a saved position onto a
    // visible monitor automatically (so an unplugged monitor no longer strands the window).

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMAXIMIZED = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    // Windows currently mid-restore: persistence is paused for them so the DPI dance below can't
    // overwrite the good saved bounds with mangled intermediate ones.
    private static readonly HashSet<Window> Restoring = new();

    /// <summary>Restore a window's saved placement (position + size + maximized). NaN / non-positive
    /// values mean "nothing saved" → keep the XAML default. The OS clamps the saved rect onto a
    /// currently-visible monitor, so a stale position can never strand the window off-screen.
    /// MIXED-DPI monitors need a second pass: the placement is applied at SourceInitialized while the
    /// window still sits on the monitor it was created on — moving it to a monitor with a different DPI
    /// makes WPF "rescale" the freshly-restored size by the DPI ratio (observed: restored 1917x1340
    /// shrank by exactly 1.5x crossing a 150%→100% boundary). Re-applying the SAME physical rect after
    /// first render lands exactly, because the window is already ON the target monitor by then.</summary>
    public static void ApplyBounds(Window w, double left, double top, double width, double height, bool maximized)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsNaN(width) || double.IsNaN(height)
            || width < 100 || height < 100)
            return;

        void Apply()
        {
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            WINDOWPLACEMENT wp = default;
            wp.length = Marshal.SizeOf<WINDOWPLACEMENT>();
            wp.flags = 0;
            // ALWAYS restore the normal rect first — never maximize in this pass. Maximizing while the
            // window still sits on its creation monitor maximizes THERE (the classic "opens maximized on
            // the wrong monitor" bug); the normal rect is what moves it to the right monitor.
            wp.showCmd = SW_SHOWNORMAL;
            wp.ptMinPosition = new POINT(-1, -1);
            wp.ptMaxPosition = new POINT(-1, -1);
            wp.rcNormalPosition = new RECT { Left = (int)left, Top = (int)top, Right = (int)(left + width), Bottom = (int)(top + height) };
            SetWindowPlacement(hwnd, ref wp);
        }

        Restoring.Add(w);
        w.Closed += (_, _) => Restoring.Remove(w);   // never-strand cleanup if the window dies mid-restore

        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply();
        else w.SourceInitialized += (_, _) => Apply();

        // Second pass after first render: same physical rect, but no monitor/DPI transition this time —
        // then, once the window is provably on the right monitor, maximize if that's how it was left.
        // Persistence resumes only when the dispatcher goes IDLE: WPF keeps nudging the window (DPI /
        // layout adjustments) for several ticks after ContentRendered, and persisting any of those
        // transitional rects made the saved bounds decay a little on every launch.
        w.ContentRendered += (_, _) =>
        {
            Apply();
            if (maximized) w.WindowState = WindowState.Maximized;
            w.Dispatcher.BeginInvoke(new Action(() => Restoring.Remove(w)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
    }

    /// <summary>Persist a window's placement continuously (on move / resize / maximize) while it's
    /// alive — done live rather than at Close because the handle/placement are gone once a window
    /// starts closing. Stores the NORMAL (un-maximized) rect so a maximized window still restores to a
    /// sensible size. <paramref name="save"/> receives (left, top, width, height, maximized) in pixels.</summary>
    public static void PersistBounds(Window w, Action<double, double, double, double, bool> save)
    {
        void Save()
        {
            if (Restoring.Contains(w)) return;   // mid-restore bounds are transient/DPI-mangled — never persist them
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            WINDOWPLACEMENT wp = default;
            wp.length = Marshal.SizeOf<WINDOWPLACEMENT>();
            if (!GetWindowPlacement(hwnd, ref wp)) return;
            RECT r = wp.rcNormalPosition;
            save(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, wp.showCmd == SW_SHOWMAXIMIZED);
        }
        w.LocationChanged += (_, _) => Save();
        w.SizeChanged += (_, _) => Save();
        w.StateChanged += (_, _) => Save();
    }
}
