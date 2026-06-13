# CLAUDE.md — orientation for AI/agent sessions

Read this first when picking up work on this repo.

## What this is

A Windows .NET 8 + WPF desktop overlay that reads **DDO (Dungeons & Dragons Online)** gear
tooltips via screen capture + OCR and builds the character's **equipped loadout** (one item per
equipment slot). DDO has no API / no gear export — this is the missing "read my equipped gear"
piece. Built reusing capture/overlay infra from [pg-loot-master](../pg-loot-master).

Workflow: open the inventory, press the hotkey to start a **detection session**, hover each
equipped slot on the paper-doll; each tooltip is captured, OCR'd, parsed, tagged with its slot, and
filled into the loadout sheet (re-capturing a slot overwrites it).

## How it actually works (this diverged a LOT from PLAN.md — trust this file)

**Hotkey** = bare **Insert** by default, via a **low-level keyboard hook** (`LowLevelKeyHook`,
WH_KEYBOARD_LL) — `RegisterHotKey` gets suppressed over a focused game; the LL hook fires anyway.
DDO is NOT elevated (no UAC), so the app runs **asInvoker / no admin / no UAC** (an earlier
requireAdministrator guess was wrong). Rebindable via "Set hotkey".

**Tooltip detection = motion, not appearance** (`TooltipChangeDetector`). Appearance-based
detection is a dead end on DDO (whole UI gold-bordered; borders recolored + flourished per quality;
silver borders look like the stone scenery; normals have no ornament). Instead: a session diffs
each frame against a **self-maintaining baseline** (background with no tooltip). The tooltip is the
changed block nearest the cursor; full extent captured. Baseline refreshes when quiet, re-bases on
big change (camera move), ignores cursor-moving frames, dedupes by cursor movement, and waits for
the change to STOP growing (so it captures after the tooltip finishes drawing). Dead/abandoned
detectors kept for reference: `CornerTemplateRegionDetector`, `DarkPanelRegionDetector`,
`TooltipBorderRegionDetector`, `DarkBoxRegionDetector` + their diagnostics. Single-shot "Scan now"
still uses a region detector; the session uses the change detector.

**Overlay alignment:** the Graphics.Capture frame is the full WINDOW (incl. title bar). Use
`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` for the window rect (not GetClientRect) so the
overlay highlight lines up with the frame.

**Inventory slot detection.** Anchor = the generic gray **rag-doll** figure (identical for every
character; matches live at 1.000). `InventoryLocator` template-matches `assets/inventory/ragdoll.png`
(deployed to `Inventory/`). `SlotMap` (slotmap.json) stores each slot's cursor offset from the
rag-doll anchor. `CalibrationController`: "Calibrate slots" locates the rag-doll ONCE (a slot's
tooltip would cover it), then the user hovers each slot clockwise (Head→Neck→Trinket→Cloak→Waist→
Ring2→Hands→Feet→Ring1→Wrists→Armor→Eyes, then MainHand/OffHand/Quiver — in-game "Equips to:"
labels) and presses Insert. At capture the pipeline caches the anchor from CLEAN frames
(`LastFrameClean`), tags the item with its slot, and SKIPS captures where the inventory is open but
the cursor isn't on a slot (ignores bag items). `SlotInfo` holds the slot order + labels.

**Parsing** (`TooltipTextParser`, still crude but improving): skips a "CURRENTLY EQUIPPED" header,
gathers a multi-line name, stops at the known item-type line (Heavy Armor / Tower Shield / Bastard
Sword (one-handed) — captured as ItemTypeText) / quality line / "Equips to:" / Min Level. Mods are
`Stat +N (BonusType)`; bonus type still often buried in sentences ("+13 Competence Bonus to…").
The calibrated SLOT overrides the parser's slot for equipped items.

## Where things live

- `src/DdoGearScanner.Model` — `GearItem`, `Mod`, `AugmentSlot`, `SetBonus`, `EquipSlot`. Pure, no deps.
- `src/DdoGearScanner.Capture` — capture (copied from pg-loot) + `GameWindowTracker` (DDO match, DWM bounds) + `FrameGrabber`.
- `src/DdoGearScanner.Vision` — `LocalOcr`, `TooltipChangeDetector` (the working detector), `TooltipTextParser`, `InventoryLocator`, `ITooltipReader`/`LocalOcrTooltipReader`, + dead detectors.
- `src/DdoGearScanner` — WPF app: `App`, `CaptureListWindow` (loadout sheet + detail), `OverlayWindow` (toast + highlight), `LowLevelKeyHook` + `HotkeyTrigger`, `CapturePipeline`, `CalibrationController`, `SlotMap`, `SlotInfo`, `SlotRow`, `CaptureStore` (loadout.json), `AppSettings`.
- `test/DdoGearScanner.Vision.Tests` — parser fixtures + dev diagnostics (machine-specific, read from %APPDATA%).

## Two-app context

Sibling project (design stage, NOT this repo): a web DDO gear planner (scrapes ddowiki, reverse
stat-finding, tetris/matrix stacking-puzzle). Its weakness: DDO has no gear export. This desktop
tool is that missing auto-import. The loadout UI here was deliberately reshaped toward the planner's
"slot list = character sheet" shape. v1 ignores the web app/backend; the `Mod{Stat,Value,BonusType}`
model is aligned for cheap integration later.

## Build / test / run

```powershell
dotnet build
dotnet test                 # parser tests (Windows)
# run: launch the built exe (asInvoker, no UAC). dotnet run also works.
```

Debug dumps go to `%APPDATA%\DdoGearScanner\debug-crops\` (`frame-<ts>-c<X>_<Y>.png` with cursor in
the name, + `crop-*.png`). Persistence: `loadout.json`, `slotmap.json`, `settings.json` in `%APPDATA%`.

## Status & next

Working: capture, motion detection, hotkey-over-game, overlay highlight, slot calibration + tagging
+ gating, loadout-sheet UI, name/type parsing. **User vetoed AI/Claude.** Next candidates: finish
the mod/bonus-type parsing; persist per-item crops; clean up the dead detector code; maybe push the
UI closer to the web prototype.
