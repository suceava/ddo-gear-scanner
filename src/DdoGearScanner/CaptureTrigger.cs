using System.Windows;

namespace DdoGearScanner;

/// <summary>A request to capture+parse the tooltip currently under the cursor.</summary>
public readonly record struct CaptureRequest(DateTime RequestedUtc);

/// <summary>
/// Abstraction over "something asked for a capture". Phase 1 = <see cref="HotkeyTrigger"/>.
/// A future auto-detect trigger (watch frames for a tooltip appearing) implements the same
/// interface and can run alongside the hotkey — the pipeline subscribes to any number of them.
/// </summary>
public interface ICaptureTrigger : IDisposable
{
    event Action<CaptureRequest>? Triggered;
    void Start();
    void Stop();
}

/// <summary>
/// Global-hotkey trigger backed by a low-level keyboard hook (<see cref="LowLevelKeyHook"/>) so it
/// fires even while DDO owns the foreground — RegisterHotKey gets suppressed over games. Rebinding
/// just changes the watched key; the hook never "fails to register" against other apps.
/// </summary>
public sealed class HotkeyTrigger : ICaptureTrigger
{
    private readonly LowLevelKeyHook _hook;

    public event Action<CaptureRequest>? Triggered;

    public HotkeyTrigger(Window owner, uint modifiers, uint vk)
    {
        _hook = new LowLevelKeyHook(modifiers, vk);
        _hook.Pressed += () => Triggered?.Invoke(new CaptureRequest(DateTime.UtcNow));
        ActiveModifiers = modifiers;
        ActiveVk = vk;
    }

    /// <summary>True if the keyboard hook installed.</summary>
    public bool Registered { get; private set; }

    public uint ActiveModifiers { get; private set; }
    public uint ActiveVk { get; private set; }

    public void Start() => Registered = _hook.Install();

    /// <summary>Change the watched key. Always succeeds (no registration conflicts).</summary>
    public bool Rebind(uint modifiers, uint vk)
    {
        _hook.TargetModifiers = modifiers;
        _hook.TargetVk = vk;
        ActiveModifiers = modifiers;
        ActiveVk = vk;
        Registered = true;
        return true;
    }

    public void Stop() => _hook.Dispose();

    public void Dispose() => _hook.Dispose();
}
