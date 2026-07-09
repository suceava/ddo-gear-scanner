using System.Windows;
using DdoGearScanner.Capture;
using DdoGearScanner.Vision;

namespace DdoGearScanner;

public partial class App : Application
{
    // Default capture hotkey (self-heal target): Insert. A normal key that fires WM_HOTKEY reliably
    // (unlike lock keys ScrollLock/Pause), DDO-free.
    private const uint DefaultHotkeyMod = 0;
    private const uint DefaultHotkeyVk = 0x2D;

    private GameWindowTracker? _tracker;
    private CaptureCoordinator? _coordinator;
    private FrameGrabber? _grabber;
    private HotkeyTrigger? _trigger;
    private RunTrackerWindow? _runWindow;
    private DebugDiagnosticsWindow? _diagWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppSettings settings = AppSettings.Instance;
        CharacterStore charStore = CharacterStore.Load();
        CaptureStore store = new();
        store.SwitchTo(charStore.ActiveId);

        // Capture stack: tracker finds the DDO window, coordinator owns the capture session, grabber
        // caches the latest frame (used by slot calibration to locate the rag-doll on demand).
        _tracker = new GameWindowTracker();
        _coordinator = new CaptureCoordinator(_tracker);
        _grabber = new FrameGrabber();
        _coordinator.FrameArrived += _grabber.OnFrame;

        // Vision: local OCR reader (Phase 1).
        LocalOcr ocr = new();
        ITooltipReader reader = new LocalOcrTooltipReader(ocr);

        CapturePipeline pipeline = new(_tracker, reader, store);
        _coordinator.FrameArrived += pipeline.OnFrame; // drives the detection session

        // Run tracker: a SECOND subscriber to the same capture stream, independent of the gear pipeline.
        // Watches the quest-tracker + reward-panel regions and logs dungeon runs (name/difficulty/level/
        // time/XP) to runs.json. Region ratios come from settings so they can be field-tuned.
        RunStore runStore = RunStore.Load();
        // Dedicated OCR engine for the run tracker: it OCRs continuously and could otherwise overlap the
        // gear pipeline's OCR on a shared engine (Windows OCR gives no concurrency guarantee).
        LocalOcr runOcr = new();
        EntryPopupReader entryReader = new(runOcr);
        QuestTrackerReader trackerReader = new(runOcr);
        ChatLogReader chatReader = new(runOcr);
        RunTrackerPipeline runPipeline = new(
            entryReader, trackerReader, chatReader, runStore,
            () => (charStore.Active.Id, charStore.Active.Level),
            new RegionRatios(settings.TrackerX0, settings.TrackerY0, settings.TrackerX1, settings.TrackerY1),
            new RegionRatios(settings.CompletionX0, settings.CompletionY0, settings.CompletionX1, settings.CompletionY1),
            new RegionRatios(settings.ChatX0, settings.ChatY0, settings.ChatX1, settings.ChatY1));
        runPipeline.SetEnabled(settings.RunTrackingEnabled);
        _coordinator.FrameArrived += runPipeline.OnFrame;

        // Inventory slot detection (rag-doll anchor + calibrated slot offsets). Template is embedded.
        InventoryLocator? invLocator = InventoryLocator.TryLoadEmbedded();
        SlotMap slotMap = SlotMap.Load();
        pipeline.SetInventory(invLocator, slotMap);
        CalibrationController calibration = new(_tracker, _grabber, invLocator, slotMap);

        // Overlay (click-through status toast) + main interactive list window.
        OverlayWindow overlay = new();
        overlay.Show();
        overlay.AttachTracker(_tracker);

        CaptureListWindow main = new(store, charStore, settings, reader.IsAvailable);
        main.DetectionToggleRequested += () => pipeline.ToggleSession();
        main.CalibrateRequested += () => { if (calibration.Active) calibration.Cancel(); else calibration.Start(); };
        main.RunTrackerRequested += () =>
        {
            if (_runWindow is not null) { _runWindow.Activate(); return; }
            _runWindow = new RunTrackerWindow(runStore, charStore, runPipeline, settings) { Owner = main };
            _runWindow.Closed += (_, _) => _runWindow = null;
            _runWindow.Show();
        };

        // Spatial debug (region borders) lives on the game overlay and reacts to settings on its own.
        // DATA debug (live chat OCR) lives in a separate movable Debug Diagnostics window, opened/closed
        // to follow the data-debug toggles so the game view stays uncluttered.
        void ApplyDiagWindow()
        {
            bool want = settings.DebugMode && settings.DebugShowChatText;
            if (want)
            {
                if (_diagWindow is null)
                {
                    _diagWindow = new DebugDiagnosticsWindow { Owner = main };
                    runPipeline.ChatDebug += _diagWindow.SetChatDebug;
                    _diagWindow.Closed += (_, _) =>
                    {
                        if (_diagWindow is not null) runPipeline.ChatDebug -= _diagWindow.SetChatDebug;
                        _diagWindow = null;
                    };
                    _diagWindow.Show();
                }
                else _diagWindow.Activate();
            }
            else _diagWindow?.Close();
        }
        settings.PropertyChanged += (_, _) => Dispatcher.Invoke(ApplyDiagWindow);
        ApplyDiagWindow();

        main.RunCalibrateRequested += () =>
        {
            using OpenCvSharp.Mat? frame = _grabber.GrabLatest();
            if (frame is null)
            {
                main.SetStatusText("No game frame captured yet — make sure DDO is running (windowed) and try again.");
                return;
            }
            var cal = new RunCalibrationWindow(frame, settings, runPipeline.SetRegions) { Owner = main };
            cal.ShowDialog();   // saved regions land in AppSettings → overlay borders refit automatically
        };
        calibration.Status += s => { main.SetStatusText(s); overlay.ShowToast(s, true, sticky: true); };
        main.Show();

        if (slotMap.IsDefault && reader.IsAvailable)
            main.SetStatusText("Using the built-in 2560×1440 slot calibration. If slots don't detect, recalibrate via ☰ Menu → Calibrate Slots.");

        // Route pipeline results to both windows (marshaled to UI thread inside the handlers).
        pipeline.Completed += outcome =>
        {
            main.OnCaptureCompleted(outcome);
            overlay.ShowToast(outcome.Success ? outcome.Message : $"⚠ {outcome.Message}", outcome.Success);
            overlay.ShowRegionHighlight(outcome.RegionX, outcome.RegionY, outcome.RegionW, outcome.RegionH, outcome.Success);
        };

        pipeline.SessionChanged += active =>
        {
            overlay.ShowToast(active ? "Detection ON — hover each gear piece" : "Detection stopped", true);
            main.OnSessionChanged(active);
        };

        // Hotkey trigger (Phase 1). Owner window provides the HWND for RegisterHotKey. The combo
        // is whatever the user configured; if it's taken by another app the user rebinds it
        // explicitly via "Set hotkey" (no silent random reassignment).
        _trigger = new HotkeyTrigger(main, settings.HotkeyModifiers, settings.HotkeyVk);
        _trigger.Triggered += _ => { if (calibration.Active) calibration.Record(); else pipeline.ToggleSession(); };
        _trigger.Start();

        // Self-heal: if the saved combo is unavailable (taken by another app), fall back to the
        // documented default (Pause) so the hotkey works out of the box. Single deterministic
        // fallback — not a random reassignment.
        bool healed = false;
        if (!_trigger.Registered && !(settings.HotkeyModifiers == DefaultHotkeyMod && settings.HotkeyVk == DefaultHotkeyVk))
            healed = _trigger.Rebind(DefaultHotkeyMod, DefaultHotkeyVk);

        if (_trigger.Registered)
        {
            settings.HotkeyModifiers = _trigger.ActiveModifiers;
            settings.HotkeyVk = _trigger.ActiveVk;
        }
        main.SetHotkeyStatus(_trigger.Registered, _trigger.ActiveModifiers, _trigger.ActiveVk);
        if (healed)
            main.NoteHotkeyHealed(_trigger.ActiveModifiers, _trigger.ActiveVk);

        // "Set hotkey" → user presses a combo → we try to register it and persist on success.
        main.RebindRequested += (mod, vk) =>
        {
            bool ok = _trigger.Rebind(mod, vk);
            if (ok)
            {
                settings.HotkeyModifiers = mod;
                settings.HotkeyVk = vk;
            }
            return ok;
        };

        _tracker.Start();

        main.Closed += (_, _) => Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trigger?.Dispose();
        _coordinator?.Dispose();
        _grabber?.Dispose();
        _tracker?.Dispose();
        base.OnExit(e);
    }
}
