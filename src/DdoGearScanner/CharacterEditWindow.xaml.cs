using System.Windows;
using DdoGearScanner.Model;

namespace DdoGearScanner;

/// <summary>New/Edit dialog for a <see cref="CharacterProfile"/>. On Save, <see cref="Result"/> holds
/// the profile (Id is empty for a new character); on Delete, <see cref="DeleteRequested"/> is true.</summary>
public partial class CharacterEditWindow : Window
{
    public CharacterProfile? Result { get; private set; }
    public bool DeleteRequested { get; private set; }

    private readonly string _id;

    public CharacterEditWindow(CharacterProfile? existing, bool canDelete)
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        PlaystyleBox.ItemsSource = new[] { Playstyle.Melee, Playstyle.Ranged, Playstyle.Caster, Playstyle.Unknown };

        _id = existing?.Id ?? "";
        if (existing is not null)
        {
            Title = "Edit Character";
            Heading.Text = "Edit Character";
            NameBox.Text = existing.Name;
            PlaystyleBox.SelectedItem = existing.Playstyle;
            DeleteButton.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            PlaystyleBox.SelectedItem = Playstyle.Melee;
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var playstyle = PlaystyleBox.SelectedItem is Playstyle p ? p : Playstyle.Unknown;
        Result = new CharacterProfile(_id, NameBox.Text, playstyle);
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show($"Delete \"{NameBox.Text}\" and its loadout?", "DDO Companion",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        DeleteRequested = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
