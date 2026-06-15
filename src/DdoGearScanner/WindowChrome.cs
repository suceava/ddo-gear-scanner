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

    /// <summary>Restore a window's saved size + position (NaN values keep the XAML default), skipping
    /// an off-screen position (e.g. a monitor that's since been unplugged), then maximized state.</summary>
    public static void ApplyBounds(Window w, double left, double top, double width, double height, bool maximized)
    {
        if (width > 100) w.Width = width;          // NaN comparisons are false => keep default
        if (height > 100) w.Height = height;
        if (!double.IsNaN(left) && !double.IsNaN(top) && OnScreen(left, top, w.Width, w.Height))
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = left;
            w.Top = top;
        }
        if (maximized) w.WindowState = WindowState.Maximized;
    }

    /// <summary>Persist a window's bounds continuously (on move / resize / maximize) while it's alive.
    /// Done live rather than at Close because RestoreBounds is empty once a window starts closing —
    /// which silently lost the bounds of secondary windows during app shutdown. Uses RestoreBounds so
    /// a maximized window still saves its underlying normal size. <paramref name="save"/> writes
    /// (left, top, width, height, maximized).</summary>
    public static void PersistBounds(Window w, Action<double, double, double, double, bool> save)
    {
        void Save()
        {
            System.Windows.Rect b = w.RestoreBounds;
            if (!b.IsEmpty) save(b.Left, b.Top, b.Width, b.Height, w.WindowState == WindowState.Maximized);
        }
        w.LocationChanged += (_, _) => Save();
        w.SizeChanged += (_, _) => Save();
        w.StateChanged += (_, _) => Save();
    }

    private static bool OnScreen(double left, double top, double width, double height)
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        double w = double.IsNaN(width) ? 400 : width;
        // require a chunk of the title bar to land inside the virtual desktop
        return left + w > vl + 40 && left < vl + vw - 40 && top >= vt - 2 && top < vt + vh - 24;
    }
}
