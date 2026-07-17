using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly AppSettings _settings;

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
