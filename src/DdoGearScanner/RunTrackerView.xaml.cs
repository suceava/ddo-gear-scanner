using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DdoGearScanner.Model;
using DdoGearScanner.Vision;

namespace DdoGearScanner;

/// <summary>
/// The Run Tracker page: logged dungeon runs for the active character (dungeon, difficulty, level,
/// duration, XP, XP/min) plus a live status card for the run in progress. Dungeon/difficulty/XP are
/// inline-editable (best-effort OCR, hand-correctable — same pattern as the gear editor). New runs
/// stream in from <see cref="RunTrackerPipeline"/> as they finalize. Hosted as a page inside
/// <see cref="ShellWindow"/>; it stays alive (and keeps updating) even when another page is shown.
/// </summary>
public partial class RunTrackerView : UserControl
{
    private readonly RunStore _store;
    private readonly RunTrackerPipeline _pipeline;
    private readonly ObservableCollection<RunRow> _rows = new();
    private readonly DispatcherTimer _elapsedTimer;

    private RunRecord? _current;   // the in-progress run mirror, for the live status line
    private QuestEntry? _heldEntry; // the last quest-entry popup captured, before entering

    /// <summary>Raised when the user clicks the Calibrate Run Regions button (App opens the calibrator).</summary>
    public event Action? RunCalibrateRequested;

    public RunTrackerView(RunStore store, CharacterStore charStore, RunTrackerPipeline pipeline, AppSettings settings)
    {
        InitializeComponent();
        _store = store;
        _pipeline = pipeline;

        Grid.ItemsSource = _rows;
        LoadRuns();

        _pipeline.RunFinalized += OnRunFinalized;
        _pipeline.CurrentChanged += OnCurrentChanged;
        _pipeline.EntryHeld += OnEntryHeld;

        // Live "elapsed" readout for the active run. The view lives for the app's lifetime, so the
        // subscriptions + timer are never torn down (process exit handles that).
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateCurrentStatus();
        _elapsedTimer.Start();
        UpdateCurrentStatus();
    }

    /// <summary>Reload the grid for whichever character is active. Called on first load, on Refresh,
    /// when the shell navigates to this page, and when the active character changes in the shell.</summary>
    public void Reload() => LoadRuns();

    private void LoadRuns()
    {
        _rows.Clear();
        foreach (RunRecord r in _store.AllNewestFirst())
            _rows.Add(new RunRow(r, Persist));
    }

    private void OnRunFinalized(RunRecord run) => Dispatcher.BeginInvoke(() =>
    {
        _rows.Insert(0, new RunRow(run, Persist));
    });

    private string? _autoOpenedRunId;
    private void OnCurrentChanged(RunRecord? current) => Dispatcher.BeginInvoke(() =>
    {
        _current = current;
        if (current is not null) _heldEntry = null;   // run started → the held popup was consumed

        // Auto-open the quest's wiki page once per NEW run, if the setting is on (default off).
        if (current is { Completed: false } run && run.Id != _autoOpenedRunId
            && AppSettings.Instance.AutoOpenWiki && !string.IsNullOrWhiteSpace(run.DungeonName))
        {
            _autoOpenedRunId = run.Id;
            OpenWiki(run.DungeonName);
        }
        UpdateCurrentStatus();
    });

    private void OnEntryHeld(QuestEntry? entry) => Dispatcher.BeginInvoke(() =>
    {
        _heldEntry = entry;
        UpdateCurrentStatus();
    });

    private static readonly (string, string?)[] NoChips = Array.Empty<(string, string?)>();

    private void UpdateCurrentStatus()
    {
        // ✎ rename is offered only for an in-progress run (finished runs are renamed in the table).
        if (!_renaming)
            RenameButton.Visibility = _current is { Completed: false } ? Visibility.Visible : Visibility.Collapsed;
        if (_current is { } c)
        {
            string name = string.IsNullOrWhiteSpace(c.DungeonName) ? "(unnamed quest)" : c.DungeonName;
            bool done = c.Completed;
            TimeSpan elapsed = done && c.CompletedUtc is { } cu ? cu - c.EnteredUtc : DateTime.UtcNow - c.EnteredUtc;
            string? who = WhoChip((c.CharacterName, c.CharacterLevel));
            var chips = new (string, string?)[] { ("Character", who), ("Difficulty", c.Difficulty), ("Quest Lvl", c.QuestLevel?.ToString()), ("XP", c.Xp?.ToString("N0")) };
            SetActionButtons(start: false, complete: !done, cancel: !done, wiki: !string.IsNullOrWhiteSpace(c.DungeonName));
            SetCard(name, done ? "COMPLETED" : "IN PROGRESS", done ? CompletedAccent : GoldBright, null, Fmt(elapsed), chips);
            return;
        }
        if (_heldEntry is { } e)
        {
            SetActionButtons(start: true, complete: false, cancel: false, wiki: !string.IsNullOrWhiteSpace(e.Name));
            SetCard(e.Name, "READY", Gold, "Enter the quest to start the run, or Start it manually.", "",
                new (string, string?)[] { ("Character", WhoChip(_pipeline.DetectedCharacter)), ("Quest Lvl", e.QuestLevel?.ToString()) });
            return;
        }
        SetActionButtons(start: true, complete: false, cancel: false, wiki: false);
        SetCard("No active run", null, null,
            "Enter a quest and it'll appear here automatically — or Start a run manually if detection misses it.",
            "", new (string, string?)[] { ("Character", WhoChip(_pipeline.DetectedCharacter)) });
    }

    // "Name · Level" for the character chip, or just the name if level is unknown (null when neither).
    private static string? WhoChip((string? Name, int? Level) c)
        => c.Name is { } n ? (c.Level is { } l ? $"{n} · {l}" : n) : null;

    private void SetActionButtons(bool start, bool complete, bool cancel, bool wiki)
    {
        StartButton.Visibility = start ? Visibility.Visible : Visibility.Collapsed;
        CompleteButton.Visibility = complete ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        WikiButton.Visibility = wiki ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => _pipeline.ManualStart();
    private void Complete_Click(object sender, RoutedEventArgs e) => _pipeline.ManualComplete();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _pipeline.ManualCancel();
    private void OpenWiki_Click(object sender, RoutedEventArgs e) => OpenWiki(_current?.DungeonName ?? _heldEntry?.Name);

    // ---- inline rename of the in-progress run (fix an OCR mis-parse without waiting for the table) ----

    private bool _renaming;

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { Completed: false } run) return;
        _renaming = true;
        CurrentTitleEdit.Text = run.DungeonName;
        TitleRow.Visibility = Visibility.Collapsed;
        CurrentTitleEdit.Visibility = Visibility.Visible;
        CurrentTitleEdit.Focus();
        CurrentTitleEdit.SelectAll();
    }

    private void CurrentTitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitRename(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelRename(); e.Handled = true; }
    }

    private void CurrentTitleEdit_LostFocus(object sender, RoutedEventArgs e) => CommitRename();

    private void CommitRename()
    {
        if (!_renaming) return;
        _renaming = false;
        string name = CurrentTitleEdit.Text.Trim();
        EndRename();
        if (!string.IsNullOrWhiteSpace(name)) _pipeline.SetCurrentName(name);
        UpdateCurrentStatus();
    }

    private void CancelRename()
    {
        if (!_renaming) return;
        _renaming = false;
        EndRename();
    }

    private void EndRename()
    {
        CurrentTitleEdit.Visibility = Visibility.Collapsed;
        TitleRow.Visibility = Visibility.Visible;
    }

    private static void OpenWiki(string? questName)
    {
        if (string.IsNullOrWhiteSpace(questName)) return;
        try { Process.Start(new ProcessStartInfo { FileName = QuestWiki.Url(questName), UseShellExecute = true }); }
        catch { /* a bad name / no browser shouldn't crash the app */ }
    }

    /// <summary>Sets the card. <paramref name="title"/> is the dominant line (quest name during a run,
    /// otherwise the state). <paramref name="badge"/> is a small colored status pill (null = hidden).</summary>
    private void SetCard(string title, string? badge, Brush? badgeColor, string? hint, string timer, (string Label, string? Value)[] chips)
    {
        CurrentTitle.Text = title;

        if (string.IsNullOrEmpty(badge))
            StatusBadge.Visibility = Visibility.Collapsed;
        else
        {
            StatusBadge.Visibility = Visibility.Visible;
            StatusBadge.Background = badgeColor;
            StatusBadgeText.Text = badge;
        }

        CurrentHint.Text = hint ?? "";
        CurrentHint.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
        CurrentTimer.Text = timer;
        CurrentCard.BorderBrush = badgeColor ?? Res("BorderGold");

        CurrentMeta.Children.Clear();
        foreach ((string chipLabel, string? value) in chips)
            if (!string.IsNullOrWhiteSpace(value)) CurrentMeta.Children.Add(Chip(chipLabel, value!));
    }

    private Border Chip(string label, string value)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = label + "  ", Foreground = TextMuted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = value, Foreground = ParchStrong, FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Background = BgRaised,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 9, 0),
            Child = sp,
        };
    }

    private static readonly Brush CompletedAccent = Freeze(0x8F, 0xCF, 0x8A);   // natural green
    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private Brush Res(string key) => (Brush)FindResource(key);
    private Brush GoldBright => Res("GoldBright");
    private Brush Gold => Res("Gold");
    private Brush TextMuted => Res("TextMuted");
    private Brush ParchStrong => Res("ParchStrong");
    private Brush BgRaised => Res("BgRaised");

    private static string Fmt(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // Persist a row after an inline edit.
    private void Persist(RunRow row) => _store.Update(row.Record);

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // The bound property setter already persisted; nothing extra needed here. (Hook kept so the
        // commit path is explicit and easy to extend, e.g. re-sorting.)
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RunRow row) return;
        _store.Remove(row.Record.Id);
        _rows.Remove(row);
    }

    private void CalibrateRun_Click(object sender, RoutedEventArgs e) => RunCalibrateRequested?.Invoke();

    private RunSettingsWindow? _settingsWindow;
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new RunSettingsWindow { Owner = Window.GetWindow(this) };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

}

/// <summary>Editable view of one <see cref="RunRecord"/> for the grid. Editing Dungeon/Difficulty/XP
/// rebuilds the record (marking it Edited) and calls back to persist; read-only columns are derived.</summary>
public sealed class RunRow : INotifyPropertyChanged
{
    private readonly Action<RunRow> _onEdit;
    public RunRecord Record { get; private set; }

    public RunRow(RunRecord record, Action<RunRow> onEdit)
    {
        Record = record;
        _onEdit = onEdit;
    }

    public string Dungeon
    {
        get => Record.DungeonName;
        set
        {
            string v = value?.Trim() ?? string.Empty;
            if (v == Record.DungeonName) return;
            Record = Record with { DungeonName = v, Edited = true };
            Commit(nameof(Dungeon));
        }
    }

    public string? Difficulty
    {
        get => Record.Difficulty;
        set
        {
            string? v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v == Record.Difficulty) return;
            Record = Record with { Difficulty = v, Edited = true };
            Commit(nameof(Difficulty));
        }
    }

    public string XpText
    {
        get => Record.Xp?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            int? xp;
            if (string.IsNullOrWhiteSpace(value)) xp = null;
            else if (int.TryParse(value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) xp = n;
            else return;   // reject non-numeric edits, keep the old value
            if (xp == Record.Xp) return;
            Record = Record with { Xp = xp, Edited = true };
            Commit(nameof(XpText));
            Raise(nameof(XpPerMinute));
        }
    }

    public string LevelText
    {
        get => (Record.QuestLevel ?? Record.CharacterLevel)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            int? lvl;
            if (string.IsNullOrWhiteSpace(value)) lvl = null;
            else if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) lvl = n;
            else return;   // reject non-numeric edits
            if (lvl == Record.QuestLevel) return;
            Record = Record with { QuestLevel = lvl, Edited = true };
            Commit(nameof(LevelText));
        }
    }
    public string Entered => Record.EnteredUtc.ToLocalTime().ToString("M/d HH:mm", CultureInfo.CurrentCulture);
    public string Duration => Record.Duration is { } d ? Format(d) : "—";
    public string XpPerMinute => Record.XpPerMinute is { } r ? Math.Round(r).ToString("N0", CultureInfo.CurrentCulture) : "—";
    public string? Character
    {
        get => Record.CharacterName;
        set
        {
            string? v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v == Record.CharacterName) return;
            Record = Record with { CharacterName = v, Edited = true };
            Commit(nameof(Character));
        }
    }

    public bool Completed => Record.Completed;
    public string Status => Record.Completed ? "Completed" : "Left";

    private static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void Commit(string prop)
    {
        Raise(prop);
        _onEdit(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
