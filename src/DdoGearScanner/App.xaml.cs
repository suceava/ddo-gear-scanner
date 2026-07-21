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
    private DebugDiagnosticsWindow? _diagWindow;
    private RunSyncService? _runSync;

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

        // Vision: local OCR reader. Everything OCRs through the IOcrEngine seam; Windows OCR won the
        // 2026-07 engine bake-off (Tesseract/Paddle lost on real gameplay and were removed).
        LocalOcr ocr = new();

        // AI reading (OpenRouter) — USER setting, app-wide. The config provider reads live settings, so
        // toggling it in Settings applies immediately, no restart. Event reads only: gear tooltips (below),
        // quest-entry popup + avatar (run pipeline); polling stays on local OCR.
        OpenRouterClient llmClient = new(() =>
            settings.LlmEnabled && !string.IsNullOrWhiteSpace(settings.OpenRouterApiKey)
                ? new OpenRouterConfig(settings.OpenRouterApiKey.Trim(), settings.OpenRouterModel.Trim())
                : null);
        ITooltipReader reader = new OpenRouterTooltipReader(llmClient, new LocalOcrTooltipReader(ocr));

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
        CharacterReader characterReader = new(runOcr);
        RunTrackerPipeline runPipeline = new(
            entryReader, trackerReader, chatReader, characterReader, runStore,
            () => (charStore.Active.Id, charStore.Active.Level),
            new RegionRatios(settings.TrackerX0, settings.TrackerY0, settings.TrackerX1, settings.TrackerY1),
            new RegionRatios(settings.CompletionX0, settings.CompletionY0, settings.CompletionX1, settings.CompletionY1),
            new RegionRatios(settings.ChatX0, settings.ChatY0, settings.ChatX1, settings.ChatY1),
            new RegionRatios(settings.CharacterX0, settings.CharacterY0, settings.CharacterX1, settings.CharacterY1),
            new OpenRouterRunReader(llmClient));
        runPipeline.SetEnabled(settings.RunTrackingEnabled);
        _coordinator.FrameArrived += runPipeline.OnFrame;

        // Cloud sync: push finalized runs to the DDO Gear Planner account when an API key is set in Settings.
        // The local runs.json stays authoritative; this is a best-effort outbox on top (see RunSyncService).
        // Character name for a run = the OCR'd avatar name, else the active gear-profile's name.
        RunSyncClient syncClient = new(
            () => string.IsNullOrWhiteSpace(settings.SyncApiKey)
                ? null
                : new SyncConfig(settings.SyncApiKey.Trim(), settings.SyncApiBase.Trim()),
            run => !string.IsNullOrWhiteSpace(run.CharacterName)
                ? run.CharacterName!
                : charStore.Profiles.FirstOrDefault(p => p.Id == run.CharacterId)?.Name ?? "Unknown");
        _runSync = new RunSyncService(runStore, syncClient);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.SyncApiKey) or nameof(AppSettings.SyncApiBase)) _runSync?.Start();
        };

        // Inventory slot detection (rag-doll anchor + calibrated slot offsets). Template is embedded.
        InventoryLocator? invLocator = InventoryLocator.TryLoadEmbedded();
        SlotMap slotMap = SlotMap.Load();
        pipeline.SetInventory(invLocator, slotMap);
        CalibrationController calibration = new(_tracker, _grabber, invLocator, slotMap);

        // Overlay (click-through status toast) + main interactive list window.
        OverlayWindow overlay = new();
        overlay.Show();
        overlay.AttachTracker(_tracker);

        // The "DDO Companion" shell hosts the Gear Loadout + Run Tracker pages; App routes the
        // gear/run pipeline events to the embedded views via main.Gear / main.Run.
        ShellWindow main = new(store, charStore, runStore, runPipeline, settings, reader.IsAvailable);
        // Cloud-sync status shows in the Run tracker's control bar; subscribe before the first drain so the
        // initial state paints, then kick off a drain of anything unsynced from a previous session.
        _runSync.StatusChanged += main.Run.SetSyncStatus;
        _runSync.Start();
        main.Gear.DetectionToggleRequested += () => pipeline.ToggleSession();
        main.Gear.CalibrateRequested += () => { if (calibration.Active) calibration.Cancel(); else calibration.Start(); };

        // Instant capture highlight at DETECTION time (gold); the outcome below re-colors it when the
        // read (OCR or the slower LLM) completes.
        pipeline.RegionDetected += (x, y, w, h) => overlay.ShowRegionHighlight(x, y, w, h, success: true);

        // Gear-capture feedback on the game overlay: calibrated slot markers while a session is on
        // (drag the inventory until they line up), or an explicit "not located" hint — a moved
        // inventory / different UI scale must fail VISIBLY, not silently skip every capture.
        pipeline.SlotOverlayChanged += state => overlay.Dispatcher.BeginInvoke(() =>
        {
            if (!state.SessionActive || !settings.ShowSlotMarkers || !state.Calibrated) { overlay.HideSlotMarkers(); return; }
            if (state.AnchorKnown) overlay.ShowSlotMarkers(state.Points, state.Radius);
            else overlay.ShowSlotHint(
                $"Gear capture: inventory paper-doll NOT located — captures are being skipped. Open your inventory. " +
                $"(best match {state.BestScore:P0}; if it stays low with the inventory open, this character's UI scale " +
                $"differs from calibration — recalibrate slots)");
        });
        settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.ShowSlotMarkers)) pipeline.RefreshSlotOverlay(); };

        // Debug crop dumps: remove the old inconsistently-named folders once, and wipe a feature's dump
        // folder the moment its checkbox is unchecked (so turning it off actually reclaims the disk).
        DebugPaths.RemoveLegacyFolders();
        Vision.VisionDebug.RunDir = DebugPaths.Run;
        void SyncVisionDebug() => Vision.VisionDebug.DumpRunRegions = settings.DebugMode && settings.DebugDumpRunRegions;
        SyncVisionDebug();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.DebugMode) or nameof(AppSettings.DebugDumpRunRegions)) SyncVisionDebug();
            if (e.PropertyName == nameof(AppSettings.DebugDumpGearCrops) && !settings.DebugDumpGearCrops) DebugPaths.ClearGear();
            if (e.PropertyName == nameof(AppSettings.DebugDumpRunRegions) && !settings.DebugDumpRunRegions) DebugPaths.ClearRun();
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
        // Initial ApplyDiagWindow() is deferred until after main.Show() below — it may set
        // Owner = main, and WPF forbids that until the owner window has been shown.

        main.Run.RunCalibrateRequested += () =>
        {
            using OpenCvSharp.Mat? frame = _grabber.GrabLatest();
            if (frame is null)
            {
                main.Gear.SetStatusText("No game frame captured yet — make sure DDO is running (windowed) and try again.");
                return;
            }
            var cal = new RunCalibrationWindow(frame, settings, runPipeline.SetRegions) { Owner = main };
            cal.ShowDialog();   // saved regions land in AppSettings → overlay borders refit automatically
        };
        calibration.Status += s => { main.Gear.SetStatusText(s); overlay.ShowToast(s, true, sticky: true); };
        main.Show();
        ApplyDiagWindow();   // now that main is shown, the diag window may take Owner = main

        if (slotMap.IsDefault && reader.IsAvailable)
            main.Gear.SetStatusText("Using the built-in 2560×1440 slot calibration. If slots don't detect, recalibrate via ☰ Menu → Calibrate Slots.");

        // Route pipeline results to the gear page + overlay (marshaled to UI thread inside the handlers).
        pipeline.CaptureStarted += (slot, png) => main.Gear.OnCaptureStarted(slot, png);
        pipeline.Completed += outcome =>
        {
            main.Gear.OnCaptureCompleted(outcome);
            overlay.ShowToast(outcome.Success ? outcome.Message : $"⚠ {outcome.Message}", outcome.Success);
            overlay.ShowRegionHighlight(outcome.RegionX, outcome.RegionY, outcome.RegionW, outcome.RegionH, outcome.Success);
        };

        pipeline.SessionChanged += active =>
        {
            overlay.ShowToast(active ? "Detection ON — hover each gear piece" : "Detection stopped", true);
            main.Gear.OnSessionChanged(active);
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
        main.Gear.SetHotkeyStatus(_trigger.Registered, _trigger.ActiveModifiers, _trigger.ActiveVk);
        if (healed)
            main.Gear.NoteHotkeyHealed(_trigger.ActiveModifiers, _trigger.ActiveVk);

        // "Set hotkey" → user presses a combo → we try to register it and persist on success.
        main.Gear.RebindRequested += (mod, vk) =>
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
        _runSync?.Dispose();
        _trigger?.Dispose();
        _coordinator?.Dispose();
        _grabber?.Dispose();
        _tracker?.Dispose();
        base.OnExit(e);
    }
}
