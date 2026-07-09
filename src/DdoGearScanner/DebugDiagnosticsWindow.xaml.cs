using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DdoGearScanner;

/// <summary>
/// Movable/resizable window for DATA diagnostics (things you read, not things anchored to the game) —
/// currently the live chat OCR, extensible with more sections. Spatial debug (region borders) stays on
/// the game overlay; this holds everything that shouldn't cover the game. Each section shows per its own
/// AppSettings debug toggle.
/// </summary>
public partial class DebugDiagnosticsWindow : Window
{
    private static readonly Brush ChatNewBrush = Freeze(0x7A, 0xE0, 0x7A);   // new lines: green
    private static readonly Brush ChatOldBrush = Freeze(0xA6, 0xA6, 0xA6);   // seen lines: gray
    private static Brush Freeze(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    public DebugDiagnosticsWindow()
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        AppSettings s = AppSettings.Instance;
        WindowChrome.ApplyBounds(this, s.DebugLeft, s.DebugTop, s.DebugWidth, s.DebugHeight, s.DebugMaximized);
        WindowChrome.PersistBounds(this, (l, t, w, h, m) =>
        {
            s.DebugLeft = l; s.DebugTop = t; s.DebugWidth = w; s.DebugHeight = h; s.DebugMaximized = m;
        });
        s.PropertyChanged += (_, _) => Dispatcher.Invoke(ApplySections);
        ApplySections();
    }

    private void ApplySections()
    {
        ChatSection.Visibility = AppSettings.Instance.DebugShowChatText ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Live chat OCR: every line exactly as read, newly-arrived lines in green.</summary>
    public void SetChatDebug(IReadOnlyList<string> allLines, IReadOnlyList<string> newLines)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!AppSettings.Instance.DebugShowChatText) return;
            var isNew = new HashSet<string>(newLines, System.StringComparer.Ordinal);
            ChatDebugList.Children.Clear();
            foreach (string line in allLines)
                ChatDebugList.Children.Add(new TextBlock
                {
                    Text = line,
                    Foreground = isNew.Contains(line) ? ChatNewBrush : ChatOldBrush,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.NoWrap,
                });
            ChatEmpty.Visibility = allLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }
}
