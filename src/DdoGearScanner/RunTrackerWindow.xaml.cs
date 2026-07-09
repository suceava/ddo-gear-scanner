using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DdoGearScanner.Model;
using DdoGearScanner.Vision;

namespace DdoGearScanner;

/// <summary>
/// Shows the logged dungeon runs for the active character (dungeon, difficulty, level, duration, XP,
/// XP/min) plus a live status line for the run in progress. Dungeon/difficulty/XP are inline-editable
/// (best-effort OCR, hand-correctable — same pattern as the gear editor); each row can open the quest's
/// DDO wiki page. New runs stream in from <see cref="RunTrackerPipeline"/> as they finalize.
/// </summary>
public partial class RunTrackerWindow : Window
{
    private readonly RunStore _store;
    private readonly CharacterStore _charStore;
    private readonly RunTrackerPipeline _pipeline;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<RunRow> _rows = new();
    private readonly DispatcherTimer _elapsedTimer;

    private RunRecord? _current;   // the in-progress run mirror, for the live status line
    private QuestEntry? _heldEntry; // the last quest-entry popup captured, before entering

    public RunTrackerWindow(RunStore store, CharacterStore charStore, RunTrackerPipeline pipeline, AppSettings settings)
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        _store = store;
        _charStore = charStore;
        _pipeline = pipeline;
        _settings = settings;

        WindowChrome.ApplyBounds(this, settings.RunLeft, settings.RunTop, settings.RunWidth, settings.RunHeight, settings.RunMaximized);
        WindowChrome.PersistBounds(this, (l, t, w, h, m) =>
        {
            settings.RunLeft = l; settings.RunTop = t; settings.RunWidth = w; settings.RunHeight = h; settings.RunMaximized = m;
        });

        Grid.ItemsSource = _rows;
        TrackingToggle.IsChecked = pipeline.Enabled;
        LoadForActiveCharacter();

        _pipeline.RunFinalized += OnRunFinalized;
        _pipeline.CurrentChanged += OnCurrentChanged;
        _pipeline.EntryHeld += OnEntryHeld;

        // Live "elapsed" readout for the active run.
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateCurrentStatus();
        _elapsedTimer.Start();

        Closed += (_, _) =>
        {
            _pipeline.RunFinalized -= OnRunFinalized;
            _pipeline.CurrentChanged -= OnCurrentChanged;
            _pipeline.EntryHeld -= OnEntryHeld;
            _elapsedTimer.Stop();
        };
    }

    /// <summary>Reload the grid for whichever character is active (called on open, Refresh, and when the
    /// window is re-activated in case the selection changed in the main window).</summary>
    private void LoadForActiveCharacter()
    {
        _rows.Clear();
        foreach (RunRecord r in _store.ForCharacter(_charStore.Active.Id))
            _rows.Add(new RunRow(r, Persist));
        Subtitle.Text = $"{_charStore.Active.Name} — {_rows.Count} run{(_rows.Count == 1 ? "" : "s")} logged";
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        LoadForActiveCharacter();
    }

    private void OnRunFinalized(RunRecord run) => Dispatcher.BeginInvoke(() =>
    {
        if (run.CharacterId == _charStore.Active.Id)
        {
            _rows.Insert(0, new RunRow(run, Persist));
            Subtitle.Text = $"{_charStore.Active.Name} — {_rows.Count} run{(_rows.Count == 1 ? "" : "s")} logged";
        }
    });

    private void OnCurrentChanged(RunRecord? current) => Dispatcher.BeginInvoke(() =>
    {
        _current = current;
        if (current is not null) _heldEntry = null;   // run started → the held popup was consumed
        UpdateCurrentStatus();
    });

    private void OnEntryHeld(QuestEntry? entry) => Dispatcher.BeginInvoke(() =>
    {
        _heldEntry = entry;
        UpdateCurrentStatus();
    });

    private void UpdateCurrentStatus()
    {
        if (!_pipeline.Enabled)
        {
            SetCard("TRACKING OFF", "—", "", TextMuted, Array.Empty<(string, string?)>());
            return;
        }
        if (_current is { } c)
        {
            string name = string.IsNullOrWhiteSpace(c.DungeonName) ? "(unnamed quest)" : c.DungeonName;
            bool done = c.Completed;
            TimeSpan elapsed = done && c.CompletedUtc is { } cu ? cu - c.EnteredUtc : DateTime.UtcNow - c.EnteredUtc;
            var chips = new (string, string?)[] { ("Difficulty", c.Difficulty), ("Quest Lvl", c.QuestLevel?.ToString()), ("XP", c.Xp?.ToString("N0")) };
            SetCard(done ? "✓ COMPLETED" : "● IN PROGRESS", name, Fmt(elapsed), done ? CompletedAccent : GoldBright, chips);
            return;
        }
        if (_heldEntry is { } e)
        {
            SetCard("◇ QUEST READY  ·  enter to start", e.Name, "", Gold,
                new (string, string?)[] { ("Quest Lvl", e.QuestLevel?.ToString()) });
            return;
        }
        SetCard("CURRENT RUN", "No active run", "", TextMuted, Array.Empty<(string, string?)>());
    }

    private void SetCard(string label, string name, string timer, Brush accent, (string Label, string? Value)[] chips)
    {
        CurrentLabel.Text = label;
        CurrentLabel.Foreground = accent;
        CurrentName.Text = name;
        CurrentTimer.Text = timer;
        CurrentCard.BorderBrush = accent;
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

    private void Wiki_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RunRow row) return;
        if (string.IsNullOrWhiteSpace(row.Dungeon)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = QuestWiki.Url(row.Dungeon), UseShellExecute = true });
        }
        catch { /* a bad name / no browser shouldn't crash the app */ }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RunRow row) return;
        _store.Remove(row.Record.Id);
        _rows.Remove(row);
        Subtitle.Text = $"{_charStore.Active.Name} — {_rows.Count} run{(_rows.Count == 1 ? "" : "s")} logged";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadForActiveCharacter();

    private void Tracking_Changed(object sender, RoutedEventArgs e)
    {
        bool on = TrackingToggle.IsChecked == true;
        _pipeline.SetEnabled(on);
        _settings.RunTrackingEnabled = on;
        UpdateCurrentStatus();
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

    public string Level => (Record.QuestLevel ?? Record.CharacterLevel)?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string Entered => Record.EnteredUtc.ToLocalTime().ToString("M/d HH:mm", CultureInfo.CurrentCulture);
    public string Duration => Record.Duration is { } d ? Format(d) : "—";
    public string XpPerMinute => Record.XpPerMinute is { } r ? Math.Round(r).ToString("N0", CultureInfo.CurrentCulture) : "—";
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
