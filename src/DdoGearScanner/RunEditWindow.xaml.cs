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
        LevelBox.Text = (run.QuestLevel ?? run.CharacterLevel)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        XpBox.Text = run.Xp?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        // XP is only known at completion — don't offer to edit it on an in-progress run.
        if (!run.Completed)
        {
            XpLabel.Visibility = Visibility.Collapsed;
            XpBox.Visibility = Visibility.Collapsed;
        }

        // The editable ComboBox's text field only exists once the template is applied — set it on Loaded so
        // the current/OCR'd difficulty actually shows.
        Loaded += (_, _) => DifficultyBox.Text = run.Difficulty ?? string.Empty;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        int? level = int.TryParse(LevelBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lv) ? lv : null;
        int? xp = int.TryParse(XpBox.Text.Trim().Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ? x : null;
        Result = _run with
        {
            DungeonName = NameBox.Text.Trim(),
            CharacterName = string.IsNullOrWhiteSpace(CharacterBox.Text) ? null : CharacterBox.Text.Trim(),
            Difficulty = string.IsNullOrWhiteSpace(DifficultyBox.Text) ? null : DifficultyBox.Text.Trim(),
            QuestLevel = level,
            Xp = xp,
            Edited = true,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
