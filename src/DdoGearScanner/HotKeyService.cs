using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DdoGearScanner;

/// <summary>
/// Registers a Windows global hotkey (works regardless of which app has focus) and raises
/// <see cref="Pressed"/> when it fires. pg-loot-master had no hotkey — this is new. Bind it to
/// a Window's handle; the WM_HOTKEY message is caught via an HwndSource hook.
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xA1FE;
    private const uint MOD_NOREPEAT = 0x4000; // don't refire while held

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddo-gear-scanner.log");
    private static void Log(string m) { try { System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [hotkey] {m}{Environment.NewLine}"); } catch { } }

    private readonly Window _owner;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;

    public event Action? Pressed;

    public HotKeyService(Window owner) => _owner = owner;

    /// <summary>Register the given modifiers + virtual key. Safe to call after the owner window
    /// has a handle (we ensure one). Re-registering replaces the previous binding.</summary>
    public bool Register(uint modifiers, uint vk)
    {
        EnsureHook();
        if (_hwnd == IntPtr.Zero) return false;

        if (_registered) { UnregisterHotKey(_hwnd, HotkeyId); _registered = false; }
        _registered = RegisterHotKey(_hwnd, HotkeyId, modifiers | MOD_NOREPEAT, vk);
        int err = _registered ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        Log($"RegisterHotKey mod=0x{modifiers:X} vk=0x{vk:X} hwnd=0x{_hwnd.ToInt64():X} -> {(_registered ? "OK" : $"FAIL err={err}")}");
        return _registered;
    }

    private void EnsureHook()
    {
        if (_source is not null) return;
        _hwnd = new WindowInteropHelper(_owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Log("WM_HOTKEY received");
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try { if (_registered && _hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyId); } catch { }
        try { _source?.RemoveHook(WndProc); } catch { }
        _registered = false;
    }
}
