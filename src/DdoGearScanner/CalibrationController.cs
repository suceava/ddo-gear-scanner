using DdoGearScanner.Capture;
using DdoGearScanner.Model;
using DdoGearScanner.Vision;

namespace DdoGearScanner;

/// <summary>
/// One-time slot calibration: the user hovers each equipment slot in turn and presses the hotkey;
/// for each we locate the rag-doll in the current frame and record the cursor's offset from it.
/// The result is a <see cref="SlotMap"/> that follows the inventory window wherever it's dragged.
/// </summary>
public sealed class CalibrationController
{
    // Clockwise around the paper-doll from the top (Head at ~11 o'clock), then the weapon row below.
    private static readonly EquipSlot[] Order =
    {
        EquipSlot.Helmet, EquipSlot.Necklace, EquipSlot.Trinket, EquipSlot.Cloak, EquipSlot.Belt,
        EquipSlot.Ring2, EquipSlot.Gloves, EquipSlot.Boots, EquipSlot.Ring1, EquipSlot.Bracers,
        EquipSlot.Armor, EquipSlot.Goggles, EquipSlot.MainHand, EquipSlot.OffHand, EquipSlot.Quiver,
    };


    private readonly GameWindowTracker _tracker;
    private readonly FrameGrabber _grabber;
    private readonly InventoryLocator? _locator;
    private readonly SlotMap _map;
    private int _index = -1;
    private OpenCvSharp.Point _anchor;   // rag-doll position located once at Start (frame-relative)

    public CalibrationController(GameWindowTracker tracker, FrameGrabber grabber, InventoryLocator? locator, SlotMap map)
    {
        _tracker = tracker; _grabber = grabber; _locator = locator; _map = map;
    }

    public bool Active => _index >= 0;

    /// <summary>Prompt/result text for the UI.</summary>
    public event Action<string>? Status;

    public void Start()
    {
        if (_locator is null) { Status?.Invoke("Slot calibration unavailable — rag-doll template missing."); return; }

        // Locate the rag-doll ONCE now, while no tooltip is up (you just clicked the button, so the
        // cursor is off the slots). We reuse this anchor for every slot — hovering a slot pops a
        // tooltip that would cover the rag-doll and break re-location. The anchor is frame-relative,
        // so you can still move the inventory window during calibration.
        GameWindowRect? rect = _tracker.CurrentRect;
        using OpenCvSharp.Mat? frame = _grabber.GrabLatest();
        OpenCvSharp.Point? a = (rect is not null && frame is not null) ? _locator.Locate(frame) : null;
        if (a is null)
        {
            Status?.Invoke("Open your inventory (paper-doll visible, no tooltip showing), then click Calibrate slots again.");
            return;
        }

        _anchor = a.Value;
        _map.Offsets.Clear();
        _index = 0;
        Prompt();
    }

    public void Cancel()
    {
        if (!Active) return;
        _index = -1;
        Status?.Invoke("Calibration cancelled.");
    }

    /// <summary>Record the current slot from the cursor's position (called on the hotkey). Uses the
    /// anchor located at Start, mapped through the CURRENT window position, so moving the window
    /// mid-calibration is fine and the slot's tooltip covering the rag-doll doesn't matter.</summary>
    public void Record()
    {
        if (!Active) return;

        GameWindowRect? rect = _tracker.CurrentRect;
        if (rect is null) { Status?.Invoke("DDO window not found. Is the game running?"); return; }

        (int cxs, int cys) = GameWindowTracker.GetCursorScreenPosition();
        int dx = (cxs - rect.Value.Left) - _anchor.X;
        int dy = (cys - rect.Value.Top) - _anchor.Y;
        _map.Set(Order[_index], dx, dy);
        _index++;

        if (_index >= Order.Length)
        {
            _map.Save();
            _index = -1;
            Status?.Invoke($"Calibration complete — {Order.Length} slots saved. Captures will now be tagged with their slot.");
        }
        else
        {
            Prompt();
        }
    }

    private void Prompt()
        => Status?.Invoke($"Calibrating {_index + 1}/{Order.Length}: hover the CENTER of your \"{SlotInfo.Label(Order[_index])}\" slot, then press the hotkey. (Calibrate again to cancel.)");
}
