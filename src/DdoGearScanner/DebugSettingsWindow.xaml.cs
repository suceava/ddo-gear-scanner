using System.Windows;

namespace DdoGearScanner;

/// <summary>
/// Central debug panel. Checkboxes bind straight to <see cref="AppSettings"/> (a bindable singleton), so
/// flipping one persists it and the overlay reacts via PropertyChanged — no wiring per option. A master
/// "debug mode" gates the rest; add new debug options here + a matching AppSettings flag.
/// </summary>
public partial class DebugSettingsWindow : Window
{
    public DebugSettingsWindow()
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        DataContext = AppSettings.Instance;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
