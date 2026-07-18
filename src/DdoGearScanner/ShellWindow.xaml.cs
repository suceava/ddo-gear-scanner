using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// The "DDO Companion" main window: a global header (product mark + active-character selector + a
/// global menu) and a left nav rail that swaps the active feature <b>page</b> (Home, Gear Loadout,
/// Run Tracker) in a content host. The click-through overlay, calibration, and debug windows remain
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

    /// <summary>The Gear Loadout page — exposed so <see cref="App"/> can route gear-pipeline events to it.</summary>
    public GearLoadoutView Gear { get; }

    /// <summary>The Run Tracker page — exposed so <see cref="App"/> can wire its calibrate action.</summary>
    public RunTrackerView Run { get; }

    private readonly HomeView _home;

    public ShellWindow(CaptureStore captureStore, CharacterStore charStore, RunStore runStore,
        RunTrackerPipeline runPipeline, AppSettings settings, bool ocrAvailable)
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
        Run = new RunTrackerView(runStore, charStore, runPipeline, settings);
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
    }

    // ---- header character chip ----

    private void UpdateCharChip()
    {
        (string? detName, int? detLevel) = _runPipeline.DetectedCharacter;
        bool detected = !string.IsNullOrWhiteSpace(detName);

        // Show the detected character when we have one; otherwise fall back to the active Gear character
        // so the chip isn't empty.
        string? name = detected ? detName : _charStore.Active?.Name;
        int? level = detected ? detLevel : _charStore.Active?.Level;

        if (string.IsNullOrWhiteSpace(name))
        {
            CharName.Text = "No character detected";
            CharLevel.Text = "";
            CharDot.Fill = (Brush)FindResource("TextMuted");
            CharAddButton.Visibility = Visibility.Collapsed;
            _pendingAdd = null;
            return;
        }

        CharName.Text = name!;
        CharLevel.Text = level is int lv ? $"Lv {lv}" : "";

        bool hasProfile = _charStore.Profiles.Any(p => NameEq(p.Name, name));
        if (!detected)
        {
            CharDot.Fill = (Brush)FindResource("Gold");     // showing the active Gear char (fallback), neutral
            CharAddButton.Visibility = Visibility.Collapsed;
            _pendingAdd = null;
        }
        else if (hasProfile)
        {
            CharDot.Fill = CharMatched;                     // detected + saved
            CharAddButton.Visibility = Visibility.Collapsed;
            _pendingAdd = null;
        }
        else
        {
            CharDot.Fill = CharUnknown;                     // detected, no profile → offer Add
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

    // Same-character test tolerant of case/punctuation/OCR spacing.
    private static bool NameEq(string a, string? b)
    {
        static string N(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        string na = N(a);
        return na.Length > 0 && na == N(b);
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
