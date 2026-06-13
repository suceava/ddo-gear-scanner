using System.Runtime.InteropServices;

namespace DdoGearScanner;

/// <summary>
/// Global hotkey via a low-level keyboard hook (WH_KEYBOARD_LL). Unlike RegisterHotKey — which
/// Windows suppresses while a game owns the foreground (borderless/fullscreen) — a low-level hook
/// intercepts keys at the input layer and fires even over the game (provided this process runs at
/// the game's integrity level, i.e. elevated). The hook is installed on the calling (UI) thread,
/// which pumps messages.
/// </summary>
public sealed class LowLevelKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddo-gear-scanner.log");
    private static void Log(string m) { try { System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [llhook] {m}{Environment.NewLine}"); } catch { } }

    private readonly HookProc _proc;   // kept alive to avoid GC of the callback
    private IntPtr _hook;

    public uint TargetModifiers { get; set; }
    public uint TargetVk { get; set; }
    public event Action? Pressed;

    public LowLevelKeyHook(uint modifiers, uint vk)
    {
        TargetModifiers = modifiers;
        TargetVk = vk;
        _proc = Callback;
    }

    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        int err = _hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        Log($"Install -> {(_hook != IntPtr.Zero ? "OK" : $"FAIL err={err}")} target mod=0x{TargetModifiers:X} vk=0x{TargetVk:X}");
        return _hook != IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field
            if ((uint)vk == TargetVk && ModifiersMatch())
            {
                Log($"key 0x{vk:X} matched -> firing");
                try { Pressed?.Invoke(); } catch { }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool ModifiersMatch()
    {
        bool ctrl = Down(0x11), shift = Down(0x10), alt = Down(0x12);
        bool win = Down(0x5B) || Down(0x5C);
        bool needCtrl = (TargetModifiers & 0x0002) != 0;
        bool needShift = (TargetModifiers & 0x0004) != 0;
        bool needAlt = (TargetModifiers & 0x0001) != 0;
        bool needWin = (TargetModifiers & 0x0008) != 0;
        return ctrl == needCtrl && shift == needShift && alt == needAlt && win == needWin;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { try { UnhookWindowsHookEx(_hook); } catch { } _hook = IntPtr.Zero; }
    }
}
