using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DdoGearScanner;

/// <summary>
/// Persistent app settings (settings.json in %APPDATA%\DdoGearScanner). Adapted from
/// pg-loot-master's OverlaySettings (same singleton + INotifyPropertyChanged + swallow-on-error
/// pattern). Hotkey defaults to ScrollLock.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    // Win32 modifier flags: ALT=1, CONTROL=2, SHIFT=4, WIN=8.
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DdoGearScanner");
    private static readonly string SettingsPath = Path.Combine(Dir, "settings.json");

    public static AppSettings Instance { get; } = Load();

    private bool _suppressSave;

    // Global capture hotkey. Default = Insert. A normal key that fires WM_HOTKEY reliably (unlike
    // lock keys ScrollLock/Pause, which the driver handles specially) and that DDO doesn't bind.
    // Rebind via "Set hotkey".
    private uint _hotkeyModifiers = 0;
    public uint HotkeyModifiers { get => _hotkeyModifiers; set => Set(ref _hotkeyModifiers, value); }

    private uint _hotkeyVk = 0x2D; // VK_INSERT
    public uint HotkeyVk { get => _hotkeyVk; set => Set(ref _hotkeyVk, value); }

    // Dump each detected tooltip crop to %APPDATA%\DdoGearScanner\debug-crops for tuning.
    private bool _debugDumpCrops = true;
    public bool DebugDumpCrops { get => _debugDumpCrops; set => Set(ref _debugDumpCrops, value); }

    private double _windowLeft = 80;
    public double WindowLeft { get => _windowLeft; set => Set(ref _windowLeft, value); }

    private double _windowTop = 80;
    public double WindowTop { get => _windowTop; set => Set(ref _windowTop, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (!_suppressSave) Save();
    }

    private static AppSettings Load()
    {
        AppSettings s = new() { _suppressSave = true };
        try
        {
            if (File.Exists(SettingsPath))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null)
                {
                    s.HotkeyModifiers = loaded.HotkeyModifiers;
                    s.HotkeyVk = loaded.HotkeyVk;
                    s.DebugDumpCrops = loaded.DebugDumpCrops;
                    s.WindowLeft = loaded.WindowLeft;
                    s.WindowTop = loaded.WindowTop;
                }
            }
        }
        catch { /* defaults on any parse failure */ }
        s._suppressSave = false;
        return s;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* losing persistence beats crashing */ }
    }

    public static string AppDataDir => Dir;
}
