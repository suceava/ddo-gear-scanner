using System.Windows;

namespace DdoGearScanner;

/// <summary>
/// Startup sign-in gate. DDO Companion is a connected ingestion client for your account, so it requires
/// sign-in to run. "Sign in with Google" runs the web-brokered device link (see <see cref="DeviceLinkService"/>);
/// on success the minted key is stored and the dialog returns true so App continues building the shell.
/// Quit exits the app.
/// </summary>
public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        // Open on the app's monitor, not the primary screen. Mid-session sign-out passes an Owner and uses
        // WindowStartupLocation=CenterOwner (WPF handles it). At startup there's no window yet, so center over
        // the shell's saved bounds (physical pixels → SetWindowPlacement, DPI/multi-monitor correct).
        SourceInitialized += (_, _) =>
        {
            if (Owner is null)
            {
                AppSettings s = AppSettings.Instance;
                WindowChrome.CenterOnSavedRect(this, s.WindowLeft, s.WindowTop, s.WindowWidth, s.WindowHeight);
            }
        };
        Loaded += (_, _) => Activate();
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SignInButton.IsEnabled = false;
        StatusText.Text = "Opening your browser — sign in, then return here…";
        try
        {
            DeviceLinkService link = new(() => AppSettings.Instance.SyncWebBase.Trim());
            DeviceLinkService.LinkResult result = await link.LinkAsync();
            if (result.Ok && !string.IsNullOrWhiteSpace(result.ApiKey))
            {
                AppSettings.Instance.SyncApiKey = result.ApiKey!.Trim();
                DialogResult = true;
                Close();
                return;
            }
            StatusText.Text = "✗ " + result.Detail;
        }
        catch (Exception ex) { StatusText.Text = "✗ " + ex.Message; }
        finally { SignInButton.IsEnabled = true; }
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
