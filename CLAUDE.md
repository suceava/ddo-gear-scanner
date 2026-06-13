# CLAUDE.md — orientation for AI/agent sessions

Read this first when picking up work on this repo.

## What this is

A Windows .NET 8 + WPF desktop overlay that reads **DDO (Dungeons & Dragons Online)** gear
tooltips via screen capture + OCR and saves them as a structured local list of items + mods.
Hover gear → press hotkey (default ScrollLock; rebindable) → capture → locate tooltip → OCR → parse → store → show.

Handles **named** items (recognizable) and **random/Cannith-crafted** items (no useful name →
extract every mod). The hard, central job is parsing **mods with bonus type**.

## Two-app context (why this exists)

There is a sibling project (design stage): a **web DDO gear planner** that scrapes ddowiki into a
clean item DB and offers reverse stat-finding + a "tetris/matrix" stacking-puzzle view + farming
guidance. The planner's stated weakness is that *DDO has no gear export — everything is manual
entry*. **This desktop tool is that missing auto-import.** For v1 we **ignore the web app/backend
entirely** and just produce a local JSON catalog; integration (push loadout / pull named-item DB,
identify named items by fuzzy name-match against the DB) is a later, undecided step. The data model
(`Mod{Stat,Value,BonusType}`, augments, set bonus) is deliberately aligned to the planner's shape so
integration is cheap later.

## Reference app

Built by reusing [pg-loot-master](../pg-loot-master) (a Project: Gorgon match-3 overlay):
`WindowCapture` / `CaptureInterop` / `CaptureCoordinator` copied verbatim; `GameWindowTracker`
adapted to find DDO; `SidebarOcr`→`LocalOcr`; `GameHistoryStore`→`CaptureStore`;
`OverlaySettings`→`AppSettings`; the click-through overlay pattern reused. Its docs convention
(README + PLAN + a living domain doc) is mirrored here.

## Where things live

- `src/DdoGearScanner.Model` — `GearItem`, `Mod`, `AugmentSlot`, `SetBonus`, `EquipSlot`. Pure, no deps.
- `src/DdoGearScanner.Capture` — capture stack + `GameWindowTracker` (DDO match + `EnumerateCandidateWindows` diagnostic) + `FrameGrabber`.
- `src/DdoGearScanner.Vision` — `LocalOcr`, `DarkBoxRegionDetector` (`ITooltipRegionDetector`), `ITooltipReader` + `LocalOcrTooltipReader`, `TooltipTextParser` (the brittle core), `BonusTypes`.
- `src/DdoGearScanner` — WPF app: `App`, `CaptureListWindow` (main UI), `OverlayWindow` (click-through toast), `HotKeyService` + `HotkeyTrigger` (`ICaptureTrigger`), `CapturePipeline`, `CaptureStore`, `AppSettings`.
- `test/DdoGearScanner.Vision.Tests` — parser fixtures.

## Key design seams (don't collapse these)

- **`ITooltipReader`** — Phase 1 local OCR; Phase 2 Claude vision is a drop-in returning the same `GearItem`. The model is the contract.
- **`ICaptureTrigger`** — Phase 1 hotkey; later auto-detect trigger coexists.
- **`ITooltipRegionDetector`** — Phase 1 dark-box near cursor; replaceable.

## Build / test / run

```powershell
dotnet build
dotnet test                 # parser tests (Windows)
dotnet run --project src\DdoGearScanner   # needs DDO running + windowed mode for a real capture
```

**Must run elevated.** DDO runs as administrator; Windows UIPI suppresses a non-elevated app's
global hotkey while an elevated window is focused, so the capture hotkey won't fire over the game.
The app.manifest requests `requireAdministrator` (one UAC prompt at launch). For `dotnet run`,
launch from an elevated terminal.

## Detection notes (current sticking point)

Tooltip region detection is the hard part. DDO's whole UI is gold-bordered, so the working approach
is `CornerTemplateRegionDetector`: match the tooltip's unique **corner coil ornament** (mask so the
background through the ornament is ignored), mirror it for the left corner to get top + width, then
find the **top/bottom horizontal gold bars** (solid, span the chains) for the vertical bounds.
**Open issue:** DDO colors the border/coil by item **quality**, so a single gold template only
matches gold-tier items — needs either color-blind matching (one template, match shape/ignore hue)
or one template per quality. Full frames are dumped to `%APPDATA%\DdoGearScanner\debug-crops\`
(`frame-*.png`) on every scan for offline tuning. Offline detector test + fixtures live in
`test/DdoGearScanner.Vision.Tests` (CornerTemplateRegionDetectorTests).

## Status & next steps

- **Phase 1 (local-OCR MVP): implemented.** Verify end-to-end on Windows with DDO running:
  Detect game window → confirm/lock `GameWindowTracker` constants → hover gear → hotkey → check
  parsed fields vs the tooltip → iterate `TooltipTextParser` + `BonusTypes` against debug crops.
- **Open runtime unknown:** DDO's real process name / window class (defaults assume
  `dndclient64`/`dndclient` + title "Dungeons & Dragons Online"; the Detect button reveals the truth).
- **Phase 2:** `ClaudeTooltipReader : ITooltipReader` (use the `claude-api` skill for current model id/SDK).
- **Phase 3:** auto-detect trigger; loadout/slot grouping; web-planner integration + export.

## Conventions

- Match the surrounding code style. Capture files copied from pg-loot keep their structure.
- Parser must never throw and must always keep `RawOcrText`.
- Update `TOOLTIP_FORMAT.md` when you learn something new about DDO tooltips; add a fixture for any
  real tooltip that parsed wrong.
