using System.Windows;
using DdoGearScanner.Vision;

namespace DdoGearScanner;

/// <summary>
/// USER settings (vs. DebugSettingsWindow's developer toggles). Hosts the app-wide AI-reading (OpenRouter)
/// config. The DDO Companion account (sign-in / sign-out) lives in the shell header's account menu, not here.
/// Data-bound to <see cref="AppSettings"/> so changes apply live.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        DataContext = AppSettings.Instance;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestResult.Text = "Testing…";
        try
        {
            AppSettings s = AppSettings.Instance;
            OpenRouterClient client = new(() => string.IsNullOrWhiteSpace(s.OpenRouterApiKey)
                ? null
                : new OpenRouterConfig(s.OpenRouterApiKey.Trim(), s.OpenRouterModel.Trim()));
            (bool ok, string detail) = await client.TestAsync();
            TestResult.Text = (ok ? "✓ " : "✗ ") + detail;
        }
        catch (Exception ex) { TestResult.Text = "✗ " + ex.Message; }
        finally { TestButton.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
