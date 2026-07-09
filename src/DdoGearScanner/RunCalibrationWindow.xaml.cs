using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner;

/// <summary>
/// Lets the user calibrate the run-tracker's OCR regions by drawing boxes on a screenshot of their own
/// UI — the same "set it once, it's stored" approach the app uses for gear-slot positions. This is why
/// the tracker OCRs only the popup / quest-tracker panels instead of the whole window: the user tells it
/// where those panels sit on THEIR layout, so nothing is hard-coded or scanned blindly.
/// </summary>
public partial class RunCalibrationWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<RegionRatios, RegionRatios, RegionRatios> _onApply;   // (tracker, popup, chat) → live-apply

    private double _dispW, _dispH;
    private enum Target { Popup, Tracker, Chat }
    private Target _active = Target.Popup;

    private RegionRatios _popup;
    private RegionRatios _tracker;
    private RegionRatios _chat;

    private readonly Rectangle _popupBox = new();
    private readonly Rectangle _trackerBox = new();
    private readonly Rectangle _chatBox = new();
    private Point _dragStart;
    private bool _dragging;

    public RunCalibrationWindow(OpenCvMat frame, AppSettings settings, Action<RegionRatios, RegionRatios, RegionRatios> onApply)
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        _settings = settings;
        _onApply = onApply;

        // Fit the (4K) frame into the window while keeping aspect; ratios are scale-independent. maxH is
        // kept below the window height minus the header so the bottom (chat log) is never clipped.
        const double maxW = 1500, maxH = 640;
        double scale = Math.Min(maxW / frame.Width, maxH / frame.Height);
        _dispW = frame.Width * scale;
        _dispH = frame.Height * scale;
        FrameImage.Source = ToBitmap(frame);
        FrameImage.Width = _dispW; FrameImage.Height = _dispH;
        DrawCanvas.Width = _dispW; DrawCanvas.Height = _dispH;

        _popup = new RegionRatios(settings.CompletionX0, settings.CompletionY0, settings.CompletionX1, settings.CompletionY1);
        _tracker = new RegionRatios(settings.TrackerX0, settings.TrackerY0, settings.TrackerX1, settings.TrackerY1);
        _chat = new RegionRatios(settings.ChatX0, settings.ChatY0, settings.ChatX1, settings.ChatY1);

        StyleBox(_popupBox, Color.FromRgb(0xE6, 0xC6, 0x6A), "Popup");        // gold
        StyleBox(_trackerBox, Color.FromRgb(0x6A, 0xC6, 0xE6), "Quest tracker"); // cyan
        StyleBox(_chatBox, Color.FromRgb(0x7A, 0xD6, 0x7A), "Chat log");      // green
        DrawCanvas.Children.Add(_popupBox);
        DrawCanvas.Children.Add(_trackerBox);
        DrawCanvas.Children.Add(_chatBox);
        RedrawBoxes();
        UpdateMode();
    }

    private static BitmapSource ToBitmap(OpenCvMat frame)
    {
        OpenCvSharp.Cv2.ImEncode(".png", frame, out byte[] png);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static void StyleBox(Rectangle box, Color color, string _)
    {
        box.Stroke = new SolidColorBrush(color);
        box.StrokeThickness = 2;
        box.Fill = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
    }

    private void RedrawBoxes()
    {
        Place(_popupBox, _popup);
        Place(_trackerBox, _tracker);
        Place(_chatBox, _chat);
    }

    private void Place(Rectangle box, RegionRatios r)
    {
        Canvas.SetLeft(box, r.X0 * _dispW);
        Canvas.SetTop(box, r.Y0 * _dispH);
        box.Width = Math.Max(0, (r.X1 - r.X0) * _dispW);
        box.Height = Math.Max(0, (r.Y1 - r.Y0) * _dispH);
    }

    private void PopupMode_Click(object sender, RoutedEventArgs e) { _active = Target.Popup; UpdateMode(); }
    private void TrackerMode_Click(object sender, RoutedEventArgs e) { _active = Target.Tracker; UpdateMode(); }
    private void ChatMode_Click(object sender, RoutedEventArgs e) { _active = Target.Chat; UpdateMode(); }

    private void UpdateMode()
    {
        HintText.Text = _active switch
        {
            Target.Popup => "▶ Now drag a box around the QUEST-ENTRY DIALOG (gold), then Save.",
            Target.Tracker => "▶ Now drag a box around the TOP of the QUEST-TRACKER panel (cyan), then Save.",
            _ => "▶ Now drag a box around the BOTTOM few lines of your CHAT LOG (green) — where 'Adventure Completed' shows — then Save.",
        };
        PopupModeButton.FontWeight = _active == Target.Popup ? FontWeights.Bold : FontWeights.Normal;
        TrackerModeButton.FontWeight = _active == Target.Tracker ? FontWeights.Bold : FontWeights.Normal;
        ChatModeButton.FontWeight = _active == Target.Chat ? FontWeights.Bold : FontWeights.Normal;
    }

    private void Canvas_Down(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(DrawCanvas);
        DrawCanvas.CaptureMouse();
    }

    private void Canvas_Move(object sender, MouseEventArgs e)
    {
        if (_dragging) SetActive(_dragStart, e.GetPosition(DrawCanvas));
    }

    private void Canvas_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        DrawCanvas.ReleaseMouseCapture();
        SetActive(_dragStart, e.GetPosition(DrawCanvas));
    }

    private void SetActive(Point a, Point b)
    {
        double x0 = Clamp01(Math.Min(a.X, b.X) / _dispW);
        double y0 = Clamp01(Math.Min(a.Y, b.Y) / _dispH);
        double x1 = Clamp01(Math.Max(a.X, b.X) / _dispW);
        double y1 = Clamp01(Math.Max(a.Y, b.Y) / _dispH);
        var r = new RegionRatios(x0, y0, x1, y1);
        switch (_active)
        {
            case Target.Popup: _popup = r; break;
            case Target.Tracker: _tracker = r; break;
            default: _chat = r; break;
        }
        RedrawBoxes();
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // A too-small box is almost certainly a mis-click — keep the old region instead of breaking OCR.
        if ((_popup.X1 - _popup.X0) > 0.02 && (_popup.Y1 - _popup.Y0) > 0.02)
        {
            _settings.CompletionX0 = _popup.X0; _settings.CompletionY0 = _popup.Y0;
            _settings.CompletionX1 = _popup.X1; _settings.CompletionY1 = _popup.Y1;
        }
        if ((_tracker.X1 - _tracker.X0) > 0.02 && (_tracker.Y1 - _tracker.Y0) > 0.02)
        {
            _settings.TrackerX0 = _tracker.X0; _settings.TrackerY0 = _tracker.Y0;
            _settings.TrackerX1 = _tracker.X1; _settings.TrackerY1 = _tracker.Y1;
        }
        if ((_chat.X1 - _chat.X0) > 0.02 && (_chat.Y1 - _chat.Y0) > 0.02)
        {
            _settings.ChatX0 = _chat.X0; _settings.ChatY0 = _chat.Y0;
            _settings.ChatX1 = _chat.X1; _settings.ChatY1 = _chat.Y1;
        }
        _onApply(_tracker, _popup, _chat);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
