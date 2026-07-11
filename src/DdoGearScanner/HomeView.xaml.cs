using System.Windows.Controls;

namespace DdoGearScanner;

/// <summary>
/// The landing page inside <see cref="ShellWindow"/>: product mark + two feature tiles that navigate
/// to the Gear Loadout and Run Tracker pages. Deliberately light — the nav rail is always available,
/// so this is just a friendly entry point.
/// </summary>
public partial class HomeView : UserControl
{
    /// <summary>Raised when the user clicks the Gear Loadout tile.</summary>
    public event Action? NavigateGear;

    /// <summary>Raised when the user clicks the Run Tracker tile.</summary>
    public event Action? NavigateRun;

    public HomeView() => InitializeComponent();

    /// <summary>Shell updates the "active character" line here whenever the selection changes.</summary>
    public void SetActiveCharacter(string name) => CharacterLine.Text = $"Active character:  {name}";

    private void Gear_Click(object sender, System.Windows.RoutedEventArgs e) => NavigateGear?.Invoke();
    private void Run_Click(object sender, System.Windows.RoutedEventArgs e) => NavigateRun?.Invoke();
}
