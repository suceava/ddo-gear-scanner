using System.Windows;

namespace DdoGearScanner;

/// <summary>
/// Settings for the run tracker. Checkboxes bind straight to <see cref="AppSettings"/> (a bindable
/// singleton), so flipping one persists it immediately. Add new run-tracker options here + a matching
/// AppSettings flag — mirrors <see cref="DebugSettingsWindow"/>.
/// </summary>
public partial class RunSettingsWindow : Window
{
    public RunSettingsWindow()
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        DataContext = AppSettings.Instance;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
