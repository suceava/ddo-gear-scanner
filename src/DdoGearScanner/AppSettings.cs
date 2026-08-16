using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    private static readonly string Dir = ResolveDataDir();
    private static readonly string SettingsPath = Path.Combine(Dir, "settings.json");

    // Data folder = %APPDATA%\DdoCompanion\ (matches the product/exe name). One-time migration: the app
    // used to store under %APPDATA%\DdoGearScanner\, so if the new folder doesn't exist yet but the old
    // one does, RENAME it over — carrying existing scans / loadouts / runs / settings with zero loss. On
    // any failure we fall back to the legacy folder so data is never stranded.
    private static string ResolveDataDir()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "DdoCompanion");
        string legacy = Path.Combine(appData, "DdoGearScanner");
        try
        {
            if (Directory.Exists(dir)) return dir;                 // already migrated (or fresh new-name install)
            if (Directory.Exists(legacy)) Directory.Move(legacy, dir);
            return dir;
        }
        catch
        {
            return Directory.Exists(legacy) ? legacy : dir;        // move failed → keep using existing data
        }
    }

    // AllowNamedFloatingPointLiterals so the NaN "unset" sentinels for window bounds serialize as
    // "NaN" instead of throwing (which silently broke ALL settings persistence).
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppSettings Instance { get; } = Load();

    private bool _suppressSave;

    // Global capture hotkey. Default = Insert. A normal key that fires WM_HOTKEY reliably (unlike
    // lock keys ScrollLock/Pause, which the driver handles specially) and that DDO doesn't bind.
    // Rebind via "Set hotkey".
    private uint _hotkeyModifiers = 0;
    public uint HotkeyModifiers { get => _hotkeyModifiers; set => Set(ref _hotkeyModifiers, value); }

    private uint _hotkeyVk = 0x2D; // VK_INSERT
    public uint HotkeyVk { get => _hotkeyVk; set => Set(ref _hotkeyVk, value); }

    // Per-feature disk dumps for tuning, each OFF by default and gated under DebugMode (dev
    // instrumentation). Gear → debug\gear\ (grows unbounded — one file per capture); Run → debug\run\
    // (4 fixed files, overwritten each dump). Kept SEPARATE so neither feature's toggle touches the other.
    private bool _debugDumpGearCrops;
    public bool DebugDumpGearCrops { get => _debugDumpGearCrops; set => Set(ref _debugDumpGearCrops, value); }

    private bool _debugDumpRunRegions;
    public bool DebugDumpRunRegions { get => _debugDumpRunRegions; set => Set(ref _debugDumpRunRegions, value); }

    // Main window bounds (NaN width/height => use the XAML default on first run).
    private double _windowLeft = 80;
    public double WindowLeft { get => _windowLeft; set => Set(ref _windowLeft, value); }
    private double _windowTop = 80;
    public double WindowTop { get => _windowTop; set => Set(ref _windowTop, value); }
    private double _windowWidth = double.NaN;
    public double WindowWidth { get => _windowWidth; set => Set(ref _windowWidth, value); }
    private double _windowHeight = double.NaN;
    public double WindowHeight { get => _windowHeight; set => Set(ref _windowHeight, value); }
    private bool _windowMaximized;
    public bool WindowMaximized { get => _windowMaximized; set => Set(ref _windowMaximized, value); }

    // Which shell page was active at last exit ("Home" | "Gear" | "Run"); restored on next launch.
    private string _activePage = "Home";
    public string ActivePage { get => _activePage; set => Set(ref _activePage, value); }

    // Run tracker: automatically open the quest's DDO wiki page in the browser when a run starts. Off by default.
    private bool _autoOpenWiki;
    public bool AutoOpenWiki { get => _autoOpenWiki; set => Set(ref _autoOpenWiki, value); }

    // Run tracker: show the mini in-game run readout (bottom-right overlay) — live timer while a quest runs, XP
    // at completion. On by default; only visible while a run is active.
    private bool _showRunHud = true;
    public bool ShowRunHud { get => _showRunHud; set => Set(ref _showRunHud, value); }

    // Sticky solo/group default: party is manual (never OCR'd), so each auto-captured run is stamped with the
    // LAST party you set, and editing a run's party updates this. Defaults to "solo"; rides forward until you
    // flip it. Only ever "solo" or "group" — legacy (pre-feature) runs stay null, which this never overwrites.
    private string _lastParty = "solo";
    public string LastParty { get => _lastParty; set => Set(ref _lastParty, value); }

    // Gear capture: draw the calibrated slot markers on the game while a detection session is active,
    // and an explicit "inventory not located" hint when the paper-doll can't be found — so a moved
    // inventory window / different UI scale fails VISIBLY instead of silently skipping every capture.
    private bool _showSlotMarkers = true;
    public bool ShowSlotMarkers { get => _showSlotMarkers; set => Set(ref _showSlotMarkers, value); }

    // ---- AI reading (OpenRouter) — USER settings, app-wide. When enabled, EVENT reads (gear tooltip
    // captures, the quest-entry popup, the character avatar) go through an LLM vision model and override
    // the local-OCR result when they land; the 3/sec tracker+chat polling always stays on local OCR
    // (cost/latency). Key is stored plaintext by explicit user choice. Off by default.
    private bool _llmEnabled;
    public bool LlmEnabled { get => _llmEnabled; set => Set(ref _llmEnabled, value); }

    private string _openRouterApiKey = string.Empty;
    public string OpenRouterApiKey { get => _openRouterApiKey; set => Set(ref _openRouterApiKey, value); }

    // OpenRouter model id. Default = a cheap, fast vision model; user-editable free text.
    private string _openRouterModel = "google/gemini-2.5-flash";
    public string OpenRouterModel { get => _openRouterModel; set => Set(ref _openRouterModel, value); }

    // ---- Cloud sync (DDO Companion account) ----
    // Per-user API key for the account. Provisioned automatically by "Sign in with Google" (the web-brokered
    // device link) or pasted manually. When set, runs/loadouts are pushed to the account. Held in memory
    // (bound to the Advanced paste box + read by the sync client) but PERSISTED ENCRYPTED via Windows DPAPI
    // (see SyncApiKeyProtected) — it's auto-provisioned now, so plaintext-on-disk is no longer acceptable.
    private string _syncApiKey = string.Empty;
    [JsonIgnore]
    public string SyncApiKey
    {
        get => _syncApiKey;
        set
        {
            if (_syncApiKey == value) return;
            _syncApiKey = value;
            _syncApiKeyProtected = Protect(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SyncApiKey)));
            if (!_suppressSave) Save();
        }
    }

    // The DPAPI-encrypted (base64) form of SyncApiKey — this is what's written to settings.json. Not bound;
    // kept in sync by the SyncApiKey setter and decrypted back on load. CurrentUser scope: only this Windows
    // account on this machine can read it.
    private string? _syncApiKeyProtected;
    public string? SyncApiKeyProtected { get => _syncApiKeyProtected; set => _syncApiKeyProtected = value; }

    // Base URL of the run-tracker API. Overridable without a rebuild (dev/staging); default is prod.
    private string _syncApiBase = "https://ddo-api.gnarlybits.com";
    public string SyncApiBase { get => _syncApiBase; set => Set(ref _syncApiBase, value); }

    // Base URL of the web app (hosts /link-desktop for "Sign in with Google"). Separate from the API host.
    private string _syncWebBase = "https://ddo.gnarlybits.com";
    public string SyncWebBase { get => _syncWebBase; set => Set(ref _syncWebBase, value); }

    private static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    private static string? Unprotect(string? blob)
    {
        if (string.IsNullOrEmpty(blob)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    // Stacking-matrix window bounds (NaN => center on first open).
    private double _matrixLeft = double.NaN;
    public double MatrixLeft { get => _matrixLeft; set => Set(ref _matrixLeft, value); }
    private double _matrixTop = double.NaN;
    public double MatrixTop { get => _matrixTop; set => Set(ref _matrixTop, value); }
    private double _matrixWidth = double.NaN;
    public double MatrixWidth { get => _matrixWidth; set => Set(ref _matrixWidth, value); }
    private double _matrixHeight = double.NaN;
    public double MatrixHeight { get => _matrixHeight; set => Set(ref _matrixHeight, value); }
    private bool _matrixMaximized;
    public bool MatrixMaximized { get => _matrixMaximized; set => Set(ref _matrixMaximized, value); }

    // Run-tracker window bounds (NaN => center on first open).
    private double _runLeft = double.NaN;
    public double RunLeft { get => _runLeft; set => Set(ref _runLeft, value); }
    private double _runTop = double.NaN;
    public double RunTop { get => _runTop; set => Set(ref _runTop, value); }
    private double _runWidth = double.NaN;
    public double RunWidth { get => _runWidth; set => Set(ref _runWidth, value); }
    private double _runHeight = double.NaN;
    public double RunHeight { get => _runHeight; set => Set(ref _runHeight, value); }
    private bool _runMaximized;
    public bool RunMaximized { get => _runMaximized; set => Set(ref _runMaximized, value); }

    // Whether the dungeon-run tracker is watching the capture stream.
    private bool _runTrackingEnabled = true;
    public bool RunTrackingEnabled { get => _runTrackingEnabled; set => Set(ref _runTrackingEnabled, value); }

    // ---- Debug ----
    // Master switch. When off, every debug feature below is inert regardless of its own flag.
    private bool _debugMode;
    public bool DebugMode { get => _debugMode; set => Set(ref _debugMode, value); }

    // Draw the calibrated run-tracker region borders on the game overlay.
    private bool _runDebugOverlay;
    public bool RunDebugOverlay { get => _runDebugOverlay; set => Set(ref _runDebugOverlay, value); }

    // Show a live panel of the OCR'd chat text (with newly-detected lines highlighted).
    private bool _debugShowChatText;
    public bool DebugShowChatText { get => _debugShowChatText; set => Set(ref _debugShowChatText, value); }

    // Debug Diagnostics window bounds (NaN => center on first open).
    private double _debugLeft = double.NaN;
    public double DebugLeft { get => _debugLeft; set => Set(ref _debugLeft, value); }
    private double _debugTop = double.NaN;
    public double DebugTop { get => _debugTop; set => Set(ref _debugTop, value); }
    private double _debugWidth = double.NaN;
    public double DebugWidth { get => _debugWidth; set => Set(ref _debugWidth, value); }
    private double _debugHeight = double.NaN;
    public double DebugHeight { get => _debugHeight; set => Set(ref _debugHeight, value); }
    private bool _debugMaximized;
    public bool DebugMaximized { get => _debugMaximized; set => Set(ref _debugMaximized, value); }

    // OCR regions for run tracking, as fractions of the game window (x0,y0,x1,y1). Defaults: the
    // quest-tracker box (top-right) and the end-of-quest reward panel (center). Exposed here so they can
    // be field-tuned against real captures without a rebuild.
    private double _trackerX0 = 0.76, _trackerY0 = 0.10, _trackerX1 = 1.00, _trackerY1 = 0.46;
    public double TrackerX0 { get => _trackerX0; set => Set(ref _trackerX0, value); }
    public double TrackerY0 { get => _trackerY0; set => Set(ref _trackerY0, value); }
    public double TrackerX1 { get => _trackerX1; set => Set(ref _trackerX1, value); }
    public double TrackerY1 { get => _trackerY1; set => Set(ref _trackerY1, value); }

    private double _completionX0 = 0.18, _completionY0 = 0.16, _completionX1 = 0.82, _completionY1 = 0.84;
    public double CompletionX0 { get => _completionX0; set => Set(ref _completionX0, value); }
    public double CompletionY0 { get => _completionY0; set => Set(ref _completionY0, value); }
    public double CompletionX1 { get => _completionX1; set => Set(ref _completionX1, value); }
    public double CompletionY1 { get => _completionY1; set => Set(ref _completionY1, value); }

    // Chat-log region — watched for "Adventure Completed" (and XP). Default = bottom-left; calibrate to
    // the BOTTOM few lines of your chat so only recent messages are read.
    private double _chatX0 = 0.0, _chatY0 = 0.74, _chatX1 = 0.34, _chatY1 = 0.99;
    public double ChatX0 { get => _chatX0; set => Set(ref _chatX0, value); }
    public double ChatY0 { get => _chatY0; set => Set(ref _chatY0, value); }
    public double ChatX1 { get => _chatX1; set => Set(ref _chatX1, value); }
    public double ChatY1 { get => _chatY1; set => Set(ref _chatY1, value); }

    // Avatar region — character NAME (above the health bar) + LEVEL (under the avatar). Default = top-left.
    private double _characterX0 = 0.0, _characterY0 = 0.0, _characterX1 = 0.13, _characterY1 = 0.15;
    public double CharacterX0 { get => _characterX0; set => Set(ref _characterX0, value); }
    public double CharacterY0 { get => _characterY0; set => Set(ref _characterY0, value); }
    public double CharacterX1 { get => _characterX1; set => Set(ref _characterX1, value); }
    public double CharacterY1 { get => _characterY1; set => Set(ref _characterY1, value); }

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
        bool migratedSyncKey = false;
        try
        {
            if (File.Exists(SettingsPath))
            {
                string rawJson = File.ReadAllText(SettingsPath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(rawJson, JsonOpts);
                if (loaded is not null)
                {
                    s.HotkeyModifiers = loaded.HotkeyModifiers;
                    s.HotkeyVk = loaded.HotkeyVk;
                    s.DebugDumpGearCrops = loaded.DebugDumpGearCrops;
                    s.DebugDumpRunRegions = loaded.DebugDumpRunRegions;
                    s.WindowLeft = loaded.WindowLeft;
                    s.WindowTop = loaded.WindowTop;
                    s.WindowWidth = loaded.WindowWidth;
                    s.WindowHeight = loaded.WindowHeight;
                    s.WindowMaximized = loaded.WindowMaximized;
                    s.ActivePage = loaded.ActivePage ?? "Home";
                    s.AutoOpenWiki = loaded.AutoOpenWiki;
                    s.ShowSlotMarkers = loaded.ShowSlotMarkers;
                    s.LlmEnabled = loaded.LlmEnabled;
                    s.OpenRouterApiKey = loaded.OpenRouterApiKey ?? string.Empty;
                    s.OpenRouterModel = string.IsNullOrWhiteSpace(loaded.OpenRouterModel) ? s.OpenRouterModel : loaded.OpenRouterModel;
                    // Sync key: prefer the encrypted blob; fall back to (and migrate) any legacy plaintext.
                    s._syncApiKeyProtected = loaded.SyncApiKeyProtected;
                    string? syncKey = Unprotect(loaded.SyncApiKeyProtected);
                    if (string.IsNullOrEmpty(syncKey))
                    {
                        try
                        {
                            using JsonDocument doc = JsonDocument.Parse(rawJson);
                            if (doc.RootElement.TryGetProperty(nameof(SyncApiKey), out JsonElement v) && v.ValueKind == JsonValueKind.String)
                                syncKey = v.GetString();
                        }
                        catch { /* no legacy plaintext */ }
                        if (!string.IsNullOrEmpty(syncKey)) { s._syncApiKeyProtected = Protect(syncKey); migratedSyncKey = true; }
                    }
                    s._syncApiKey = syncKey ?? string.Empty; // set backing field directly (no re-encrypt/notify during load)
                    s.SyncApiBase = string.IsNullOrWhiteSpace(loaded.SyncApiBase) ? s.SyncApiBase : loaded.SyncApiBase;
                    s.SyncWebBase = string.IsNullOrWhiteSpace(loaded.SyncWebBase) ? s.SyncWebBase : loaded.SyncWebBase;
                    s.MatrixLeft = loaded.MatrixLeft;
                    s.MatrixTop = loaded.MatrixTop;
                    s.MatrixWidth = loaded.MatrixWidth;
                    s.MatrixHeight = loaded.MatrixHeight;
                    s.MatrixMaximized = loaded.MatrixMaximized;
                    s.RunLeft = loaded.RunLeft;
                    s.RunTop = loaded.RunTop;
                    s.RunWidth = loaded.RunWidth;
                    s.RunHeight = loaded.RunHeight;
                    s.RunMaximized = loaded.RunMaximized;
                    s.RunTrackingEnabled = loaded.RunTrackingEnabled;
                    s.DebugMode = loaded.DebugMode;
                    s.RunDebugOverlay = loaded.RunDebugOverlay;
                    s.DebugShowChatText = loaded.DebugShowChatText;
                    s.DebugLeft = loaded.DebugLeft;
                    s.DebugTop = loaded.DebugTop;
                    s.DebugWidth = loaded.DebugWidth;
                    s.DebugHeight = loaded.DebugHeight;
                    s.DebugMaximized = loaded.DebugMaximized;
                    s.TrackerX0 = loaded.TrackerX0;
                    s.TrackerY0 = loaded.TrackerY0;
                    s.TrackerX1 = loaded.TrackerX1;
                    s.TrackerY1 = loaded.TrackerY1;
                    s.CompletionX0 = loaded.CompletionX0;
                    s.CompletionY0 = loaded.CompletionY0;
                    s.CompletionX1 = loaded.CompletionX1;
                    s.CompletionY1 = loaded.CompletionY1;
                    s.ChatX0 = loaded.ChatX0;
                    s.ChatY0 = loaded.ChatY0;
                    s.ChatX1 = loaded.ChatX1;
                    s.ChatY1 = loaded.ChatY1;
                    s.CharacterX0 = loaded.CharacterX0;
                    s.CharacterY0 = loaded.CharacterY0;
                    s.CharacterX1 = loaded.CharacterX1;
                    s.CharacterY1 = loaded.CharacterY1;
                }
            }
        }
        catch { /* defaults on any parse failure */ }
        s._suppressSave = false;
        // A migrated legacy plaintext key: rewrite settings.json now so the encrypted blob replaces it.
        if (migratedSyncKey) s.Save();
        return s;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* losing persistence beats crashing */ }
    }

    public static string AppDataDir => Dir;
}
