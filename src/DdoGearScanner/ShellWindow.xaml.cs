using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// The "DDO Companion" main window: a global header (product mark + active-character selector + a
/// global menu) and a left nav rail that swaps the active feature <b>page</b> (Home, Loadout,
/// Runs) in a content host. The click-through overlay, calibration, and debug windows remain
/// separate floating windows launched by <see cref="App"/> / the pages — only the two data views are
/// embedded here. Character selection is global (both pages read the active character).
/// </summary>
public partial class ShellWindow : Window
{
    private readonly CharacterStore _charStore;
    private readonly RunTrackerPipeline _runPipeline;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _charChipTimer;
    private (string Name, int? Level)? _pendingAdd;   // detected char with no profile, ready for the Add button
    private static readonly Brush CharMatched = Frozen(0x8F, 0xCF, 0x8A);   // green — detected name has a profile
    private static readonly Brush CharUnknown = Frozen(0xE8, 0xB3, 0x4A);   // amber — detected, no profile yet

    /// <summary>The Loadout page — exposed so <see cref="App"/> can route gear-pipeline events to it.</summary>
    public GearLoadoutView Gear { get; }

    /// <summary>The Runs page — exposed so <see cref="App"/> can wire its calibrate action.</summary>
    public RunTrackerView Run { get; }

    private readonly HomeView _home;

    public ShellWindow(CaptureStore captureStore, CharacterStore charStore, RunStore runStore,
        RunTrackerPipeline runPipeline, AppSettings settings, bool ocrAvailable, DdoGearScanner.Capture.GameWindowTracker? tracker = null)
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        _charStore = charStore;
        _runPipeline = runPipeline;
        _settings = settings;

        WindowChrome.ApplyBounds(this, settings.WindowLeft, settings.WindowTop,
            settings.WindowWidth, settings.WindowHeight, settings.WindowMaximized);
        WindowChrome.PersistBounds(this, (l, t, w, h, m) =>
        {
            _settings.WindowLeft = l; _settings.WindowTop = t;
            _settings.WindowWidth = w; _settings.WindowHeight = h; _settings.WindowMaximized = m;
        });

        Gear = new GearLoadoutView(captureStore, charStore, settings, ocrAvailable);
        Run = new RunTrackerView(runStore, charStore, runPipeline, settings, tracker);
        _home = new HomeView();
        _home.NavigateGear += ShowGear;
        _home.NavigateRun += ShowRun;

        RestoreActivePage();

        // Header character chip: poll the pipeline's detected character (~1s; detection is infrequent) and
        // reflect it. DISPLAY only — never changes the Gear-active character.
        _charChipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _charChipTimer.Tick += (_, _) => UpdateCharChip();
        _charChipTimer.Start();
        UpdateCharChip();

        _ = RefreshAccountAsync();

        // Footer: always show the current build; check GitHub for a newer release in the background.
        VersionText.Text = $"v{UpdateChecker.CurrentDisplay}";
        _ = CheckForUpdateAsync();
    }

    // ---- update check (footer) ----

    private string? _updateUrl;

    private async Task CheckForUpdateAsync()
    {
        UpdateInfo? info = await UpdateChecker.CheckAsync();
        if (info is not { UpdateAvailable: true }) return; // offline / up to date → footer just shows the version
        _updateUrl = info.Url;
        UpdateText.Text = info.Major
            ? $"Major update: v{info.Latest} — may have breaking changes"
            : $"Update available: v{info.Latest}";
        UpdateText.Foreground = info.Major ? UpdateWarn : (Brush)FindResource("GoldBright");
        UpdateText.ToolTip = "Open the latest release on GitHub";
        UpdateText.Visibility = Visibility.Visible;
    }

    private static readonly Brush UpdateWarn = Frozen(0xE8, 0x7A, 0x3A); // orange — major bump, possible breaking changes

    private void Update_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_updateUrl)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _updateUrl, UseShellExecute = true }); }
        catch { /* no browser / bad url shouldn't crash the app */ }
    }

    // ---- account menu ----

    private RunSyncClient NewSyncClient() => new(
        () => string.IsNullOrWhiteSpace(_settings.SyncApiKey)
            ? null
            : new SyncConfig(_settings.SyncApiKey.Trim(), _settings.SyncApiBase.Trim()),
        _ => "n/a");

    private async Task RefreshAccountAsync()
    {
        AccountInfo? acc = await NewSyncClient().AccountAsync();
        string? who = acc?.Email ?? acc?.Name;
        AccountLabel.Text = string.IsNullOrWhiteSpace(who) ? "Account" : ShortName(who!);
        AccountEmail.Text = string.IsNullOrWhiteSpace(who) ? "Signed in" : $"Signed in as {who}";
        AccountInitial.Text = string.IsNullOrWhiteSpace(who) ? "" : who!.Trim()[..1].ToUpperInvariant();
        AccountAvatar.Fill = (Brush)FindResource("BgInput"); // initial-circle fallback until the picture loads
        if (!string.IsNullOrWhiteSpace(acc?.AvatarUrl)) TryLoadAvatar(acc!.AvatarUrl!);
    }

    // Load the Google profile picture into the avatar circle; keep the initial fallback if it fails.
    private void TryLoadAvatar(string url)
    {
        try
        {
            BitmapImage bmp = new();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            void Apply() { AccountAvatar.Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill }; AccountInitial.Text = ""; }
            if (bmp.IsDownloading) bmp.DownloadCompleted += (_, _) => Apply();
            else Apply();
        }
        catch { /* keep the initial-circle fallback */ }
    }

    // Header shows the local part of the email (or the full name); the dropdown shows the full identity.
    private static string ShortName(string who)
    {
        int at = who.IndexOf('@');
        return at > 0 ? who[..at] : who;
    }

    private void Account_Click(object sender, RoutedEventArgs e) => AccountPopup.IsOpen = true;

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        AccountPopup.IsOpen = false;
        _settings.SyncApiKey = string.Empty; // disconnect — clears the stored (encrypted) key
        // Web-centric: the app requires sign-in to run, so re-gate immediately. Re-sign-in continues; Quit exits.
        LoginWindow login = new() { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (login.ShowDialog() == true) _ = RefreshAccountAsync();
        else Application.Current.Shutdown();
    }

    // ---- header character chip ----

    private void UpdateCharChip()
    {
        // Display-only: the top-bar character is the ACTIVE RUN's character — a single stable name stamped at
        // quest start. It is NEVER the raw per-frame avatar OCR (which flickers good/garbage every tick) and
        // never a stand-in like the active Gear char. So it's the real character, or nothing — never a second,
        // different name that reads as broken. No run → hide the chip entirely.
        RunRecord? run = _runPipeline.Current;
        string? name = run?.CharacterName;
        int? level = run?.CharacterLevel;

        if (string.IsNullOrWhiteSpace(name))
        {
            CharChip.Visibility = Visibility.Collapsed;
            _pendingAdd = null;
            return;
        }

        CharChip.Visibility = Visibility.Visible;
        CharName.Text = name!;
        CharLevel.Text = level is int lv ? $"Lv {lv}" : "";

        bool hasProfile = _charStore.Profiles.Any(p => NameEq(p.Name, name));
        if (hasProfile)
        {
            CharDot.Fill = CharMatched;                     // known + saved
            CharAddButton.Visibility = Visibility.Collapsed;
            _pendingAdd = null;
        }
        else
        {
            CharDot.Fill = CharUnknown;                     // no profile → offer Add
            CharAddButton.Visibility = Visibility.Visible;
            _pendingAdd = (name!, level);
        }
    }

    private void CharChip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowGear();

    private void CharAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingAdd is not { } add) return;
        Gear.AddDetectedCharacter(add.Name, add.Level);   // creates the profile + makes it active
        ShowGear();                                        // land on Gear so they can set playstyle/classes
        UpdateCharChip();
    }

    // Same-character test by the SHARED identity: slug(name), matching the web app + backend (so the chip's
    // "known character" agrees with what runs/characters key on). Case/punctuation-insensitive.
    private static bool NameEq(string a, string? b)
    {
        string na = DdoGearScanner.Model.Slug.Of(a);
        return na.Length > 0 && na == DdoGearScanner.Model.Slug.Of(b);
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private void RestoreActivePage()
    {
        switch (_settings.ActivePage)
        {
            case "Gear": ShowGear(); break;
            case "Run": ShowRun(); break;
            default: ShowHome(); break;
        }
    }

    // ---- navigation ----

    private void NavHome_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void NavGear_Click(object sender, RoutedEventArgs e) => ShowGear();
    private void NavRun_Click(object sender, RoutedEventArgs e) => ShowRun();

    private void ShowHome()
    {
        _home.SetActiveCharacter(_charStore.Active.Name);   // may have changed on the Gear page
        ShowPage(_home, NavHome, "Home");
    }

    private void ShowGear() => ShowPage(Gear, NavGear, "Gear");

    private void ShowRun()
    {
        Run.Reload();   // pick up any new runs logged while the page was hidden
        ShowPage(Run, NavRun, "Run");
    }

    private void ShowPage(UIElement page, Button active, string key)
    {
        PageHost.Content = page;
        _settings.ActivePage = key;   // remembered so the app reopens on the last page, not Home
        foreach (Button b in new[] { NavHome, NavGear, NavRun })
            b.Background = b == active ? (Brush)FindResource("SelectionBg") : Brushes.Transparent;
    }

    // ---- global menu ----

    private void GlobalMenu_Click(object sender, RoutedEventArgs e) => GlobalMenuPopup.IsOpen = true;

    private SettingsWindow? _settingsWindow;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        GlobalMenuPopup.IsOpen = false;
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow { Owner = this };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private DebugSettingsWindow? _debugWindow;

    private void DebugSettings_Click(object sender, RoutedEventArgs e)
    {
        GlobalMenuPopup.IsOpen = false;
        if (_debugWindow is not null) { _debugWindow.Activate(); return; }
        _debugWindow = new DebugSettingsWindow { Owner = this };
        _debugWindow.Closed += (_, _) => _debugWindow = null;
        _debugWindow.Show();
    }
}
