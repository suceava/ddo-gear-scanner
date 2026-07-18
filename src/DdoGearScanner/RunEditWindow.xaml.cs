using System.Globalization;
using System.Windows;
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

        // XP is only known at completion — don't offer to edit it on an in-progress run.
        if (!run.Completed)
        {
            XpLabel.Visibility = Visibility.Collapsed;
            XpBox.Visibility = Visibility.Collapsed;
        }

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
        int? level = int.TryParse(LevelBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lv) ? lv : null;
        int? charLevel = int.TryParse(CharLevelBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cl) ? cl : null;
        int? xp = int.TryParse(XpBox.Text.Trim().Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ? x : null;
        Result = _run with
        {
            DungeonName = NameBox.Text.Trim(),
            CharacterName = string.IsNullOrWhiteSpace(CharacterBox.Text) ? null : CharacterBox.Text.Trim(),
            Difficulty = DifficultyBox.SelectedItem as string,
            QuestLevel = level,
            CharacterLevel = charLevel,
            Xp = xp,
            Edited = true,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
