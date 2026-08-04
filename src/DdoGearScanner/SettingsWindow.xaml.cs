using System.Security.Cryptography;
using System.Text;
using System.Windows;
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
        Loaded += (_, _) => RefreshSyncKeyView();
    }

    private bool _editingSyncKey;

    private void SyncChangeKey_Click(object sender, RoutedEventArgs e)
    {
        _editingSyncKey = true;
        RefreshSyncKeyView();
        SyncKeyBox.Focus();
        SyncKeyBox.SelectAll();
    }

    private void SyncDone_Click(object sender, RoutedEventArgs e)
    {
        _editingSyncKey = false;
        RefreshSyncKeyView();
    }

    /// <summary>Connected: show the key's public ID (sha256(key)[:12] — the SAME id the web Account page lists)
    /// and hide the raw secret, since the user never needs to read it back. No key set (or "Change key"): show
    /// the paste box instead.</summary>
    private void RefreshSyncKeyView()
    {
        string key = (AppSettings.Instance.SyncApiKey ?? "").Trim();
        bool showEntry = _editingSyncKey || key.Length == 0;
        SyncEntryPanel.Visibility = showEntry ? Visibility.Visible : Visibility.Collapsed;
        SyncConnectedPanel.Visibility = showEntry ? Visibility.Collapsed : Visibility.Visible;
        SyncDoneButton.Visibility = showEntry ? Visibility.Visible : Visibility.Collapsed;
        SyncChangeButton.Visibility = showEntry ? Visibility.Collapsed : Visibility.Visible;
        if (!showEntry)
        {
            string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..12];
            SyncKeyIdText.Text = $"✓ Connected  ·  Key ID  {id}";
        }
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
