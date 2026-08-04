using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DdoGearScanner.Vision;

namespace DdoGearScanner;

/// <summary>
/// USER settings (vs. DebugSettingsWindow's developer toggles). Currently hosts the app-wide
/// AI-reading (OpenRouter) configuration; data-bound straight to <see cref="AppSettings"/>, so changes
/// apply live — the OpenRouter config provider in App reads these values on every call.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        DataContext = AppSettings.Instance;
        Loaded += (_, _) => UpdateSyncKeyId();
    }

    private void SyncKeyBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSyncKeyId();

    /// <summary>Show the key's public ID — sha256(key) truncated to 12 hex chars — the SAME id the web Account
    /// page lists, so the user can confirm this machine is holding the key the account expects (the secret key
    /// itself is only shown once, at generation).</summary>
    private void UpdateSyncKeyId()
    {
        string key = SyncKeyBox.Text?.Trim() ?? "";
        if (key.Length == 0)
        {
            SyncKeyIdText.Text = "Key ID appears here once you paste a key — it matches the Key ID on the web Account page.";
            return;
        }
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..12];
        SyncKeyIdText.Text = $"Key ID  {id}  —  matches the Key ID on the web Account page.";
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

    private async void SyncTest_Click(object sender, RoutedEventArgs e)
    {
        SyncTestButton.IsEnabled = false;
        SyncTestResult.Text = "Checking…";
        try
        {
            AppSettings s = AppSettings.Instance;
            RunSyncClient client = new(
                () => string.IsNullOrWhiteSpace(s.SyncApiKey) ? null : new SyncConfig(s.SyncApiKey.Trim(), s.SyncApiBase.Trim()),
                _ => "n/a");
            (bool ok, string detail) = await client.ValidateAsync();
            SyncTestResult.Text = (ok ? "✓ " : "✗ ") + detail;
        }
        catch (Exception ex) { SyncTestResult.Text = "✗ " + ex.Message; }
        finally { SyncTestButton.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
