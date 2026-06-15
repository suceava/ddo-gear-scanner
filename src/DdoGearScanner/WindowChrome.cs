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
}
