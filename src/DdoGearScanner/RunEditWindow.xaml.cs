using System.Globalization;
using System.Windows;
using System.Windows.Input;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>
/// A small modal editor for the current run — one consistent place to fix the quest name, character,
/// difficulty, level and XP (the same fields the history table edits inline). Returns the edited
/// <see cref="RunRecord"/> in <see cref="Result"/>; the caller applies it.
/// </summary>
public partial class RunEditWindow : Window
{
    private readonly RunRecord _run;
    public RunRecord? Result { get; private set; }

    public RunEditWindow(RunRecord run)
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        _run = run;
        NameBox.Text = run.DungeonName;
        CharacterBox.Text = run.CharacterName ?? string.Empty;
        LevelBox.Text = run.QuestLevel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        CharLevelBox.Text = run.CharacterLevel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        XpBox.Text = run.Xp?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TimeBox.Text = run.Duration is { } d ? Fmt(d) : string.Empty;

        // Time + XP are completion data — an in-progress run's timer is live, so hide the row.
        if (!run.Completed) CompletionRow.Visibility = Visibility.Collapsed;

        // The app's themed ComboBox template is display-only (no editable text box), so difficulty is a
        // NON-editable pick list driven by SelectedItem — the pattern that already works for the character
        // selector. Reaper skull counts are listed so nothing needs typing; an existing non-standard value
        // (e.g. a bare "Reaper") is injected so it stays selectable.
        var items = new List<string> { "Casual", "Normal", "Hard", "Elite" };
        for (int i = 1; i <= 10; i++) items.Add($"Reaper {i}");
        string diff = _run.Difficulty?.Trim() ?? string.Empty;
        if (diff.Length > 0 && !items.Any(d => string.Equals(d, diff, StringComparison.OrdinalIgnoreCase)))
            items.Insert(4, diff);
        DifficultyBox.ItemsSource = items;
        DifficultyBox.SelectedItem = items.FirstOrDefault(d => string.Equals(d, diff, StringComparison.OrdinalIgnoreCase));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        int? level = ParseInt(LevelBox.Text);
        int? charLevel = ParseInt(CharLevelBox.Text);
        int? xp = ParseInt(XpBox.Text);

        // Validate the time. Empty is fine (keeps the existing duration); non-empty must be a real
        // duration (m:ss / h:mm:ss with seconds & minutes under 60 — so "22:84" is rejected, not silently
        // normalized). Editing time keeps the real start and moves the end.
        DateTime? completed = _run.CompletedUtc;
        string timeText = TimeBox.Text.Trim();
        if (_run.Completed && timeText.Length > 0)
        {
            if (ParseDuration(timeText) is not { } dur || dur <= TimeSpan.Zero)
            {
                ShowError("Time must look like 17:23 or 1:17:23 — minutes and seconds under 60.");
                return;
            }
            completed = _run.EnteredUtc + dur;
        }

        if (level is < 1 or > 40) { ShowError("Quest level must be 1–40."); return; }
        if (charLevel is < 1 or > 40) { ShowError("Character level must be 1–40."); return; }

        Result = _run with
        {
            DungeonName = NameBox.Text.Trim(),
            CharacterName = string.IsNullOrWhiteSpace(CharacterBox.Text) ? null : CharacterBox.Text.Trim(),
            Difficulty = DifficultyBox.SelectedItem as string,
            QuestLevel = level,
            CharacterLevel = charLevel,
            Xp = xp,
            CompletedUtc = completed,
            Edited = true,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    // ---- input guards: block non-digits (number fields) / non-time chars (time field), typed or pasted ----
    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    private void TimeChars_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(c => char.IsDigit(c) || c == ':');

    private static void GuardPaste(DataObjectPastingEventArgs e, Func<char, bool> allowed)
    {
        string? s = e.DataObject.GetData(typeof(string)) as string;
        if (s is null || !s.All(allowed)) e.CancelCommand();
    }
    private void Digits_Pasting(object sender, DataObjectPastingEventArgs e) => GuardPaste(e, char.IsDigit);
    private void TimeChars_Pasting(object sender, DataObjectPastingEventArgs e) => GuardPaste(e, c => char.IsDigit(c) || c == ':');

    private static int? ParseInt(string s)
        => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>Parse + VALIDATE a run duration: "M" (minutes), "M:SS", or "H:MM:SS". Seconds and the
    /// h:mm:ss minutes must be 0–59 (so "22:84" is rejected, not normalized). Null if invalid.</summary>
    private static TimeSpan? ParseDuration(string s)
    {
        string[] p = s.Trim().Split(':');
        if (p.Length is < 1 or > 3 || p.Any(x => !int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            return null;
        int[] v = p.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return p.Length switch
        {
            1 => TimeSpan.FromMinutes(v[0]),                                  // plain minutes
            2 when v[1] is >= 0 and < 60 && v[0] >= 0 => new TimeSpan(0, v[0], v[1]),
            3 when v[1] is >= 0 and < 60 && v[2] is >= 0 and < 60 && v[0] >= 0 => new TimeSpan(v[0], v[1], v[2]),
            _ => null,
        };
    }
}
