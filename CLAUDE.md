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

**Parsing** (`TooltipTextParser`): skips a "CURRENTLY EQUIPPED" header, gathers a multi-line name,
stops at the known item-type line (Heavy Armor / Tower Shield / Bastard Sword (one-handed) —
captured as ItemTypeText) / quality line / "Equips to:" / Min Level. The calibrated SLOT overrides
the parser's slot for equipped items.

**Mods are segmented by the gold ▶ bullets, NOT by text** (`BulletDetector` + the `Parse(lines,
bulletYs)` overload). Text can't delimit mods — the ▶ is dropped by OCR and descriptions contain
colons + restated values. `BulletDetector` finds the ▶ glyphs geometrically (small 3-vertex
right-pointing gold triangles sharing a dominant x-column; DDO's gold keyword-text is rejected by
shape/column), returns their Y-centres, and the parser slices OCR rows into one ▶-block-per-mod,
extracting Stat+Value from the head and BonusType from the description (`+N <Type> bonus`). This
killed the run-together + double-counting. Detection + OCR run on the SAME 3x-upscaled crop so the
coordinates line up. Falls back to line-by-line parse when no bullets are found. See TOOLTIP_FORMAT.md.

## Run tracker (second feature, added on top of the gear scanner)

A **dungeon-run logger** that reuses the SAME capture stream as the gear scanner. `RunTrackerPipeline`
is a SECOND subscriber to `CaptureCoordinator.FrameArrived` (never touches the gear path). It OCRs
**three user-calibrated, window-relative regions** — NOT full-window OCR, NOT hard-coded ratios. The
user draws the boxes once in `RunCalibrationWindow` (a captured frame + 3 draggable rects: ① quest-entry
dialog, ② quest tracker, ③ chat log); the ratios persist to `AppSettings` and can be re-drawn live.

**Why three panels, and why calibrated:** DDO's ornate quest-title font OCRs to garbage, and panel
positions vary per user's UI layout. So the name comes from the **quest-entry popup** (clean font,
`EntryPopupReader`, OCR'd at high scale so "11" doesn't collapse to "1"); the **quest tracker**
(`QuestTrackerReader`) is a secondary signal; and **completion** is read off the **chat log**
(`ChatLogReader`) where "Adventure Completed" appears in a clean, persistent font. Readers live in
`Vision/RunScreenReaders.cs`; all pure parsing (name-above-"Difficulty" geometry, OCR-tolerant level
correction, `IsAdventureCompleted`, chat XP extraction) is in `Vision/RunTextParser.cs`, unit-tested in
`RunTrackerTests`. Gets its own `LocalOcr` so continuous OCR can't race the gear OCR.

**State machine** (`RunTrackerPipeline`): quest-entry popup seen → arm; run **starts** only if the
player's area then *changes* from where they stood at the popup (so hitting Cancel doesn't start a run).
**Completion** fires on a fresh "Adventure Completed" chat line (or tracker Completed) past a minimum
duration. New chat lines are found by **append-only shift detection** (chat scrolls up by N lines;
align by shift, not similarity) — conservative: no clean alignment → treat nothing as new, since a
missed line beats a false completion. The finalized run KEEPS the popup-derived name (never tracker
garbage). `RunRecord` (dungeon, difficulty, char level, entry/exit time, XP, XP/min) persists to
`runs.json`, **hand-editable** in `RunTrackerWindow`. Each row opens the ddowiki quest page via
`QuestWiki.Slug` (spaces→`_`, URL-encoded, slugify-and-go — no quest table).

**Debug system.** A global `AppSettings.DebugMode` toggle gates everything. `DebugSettingsWindow`
(☰ Menu → Debug Settings…) is data-bound to `AppSettings` with per-feature checkboxes. Two kinds of
debug output, deliberately separated: **spatial** debug (the calibrated region borders) draws on the
click-through game `OverlayWindow` and reacts to settings via `AppSettings.PropertyChanged`; **data**
debug (live chat OCR — every line exactly as read, newly-detected lines green) lives in the movable/
resizable `DebugDiagnosticsWindow` so it doesn't cover the game. `App` opens/closes that window to
follow the data-debug toggle. The window has stacked sections so future diagnostics drop in as panels.

**Detection is OCR-only (no Claude — user vetoed AI) and still being field-tuned.** Everything is
user-editable, so a miss costs an edit, not a lost run.

**Still on the design-goal list, NOT yet built (don't drop these):**
- **End-of-quest reward panel** — DDO's center-screen completion/XP summary. Richest single source
  (quest name + difficulty + full XP breakdown in one panel), so still the wanted authoritative data
  source at finalize. Current state: the chat-log "Adventure Completed" line is only the completion
  *trigger*, and chat XP is best-effort; the original `CompletionPanelReader` was never finished and was
  removed. A 4th calibrated region + reader is the intended way back in. (Stale "reward panel" comments
  in `App`/`AppSettings`/`RunTrackerPipeline`/`RunRecord` are leftovers pointing at this goal.)
- **Automatic difficulty capture** — read the highlighted difficulty radio in the entry popup (and/or
  from the reward panel above).

## Where things live

- `src/DdoGearScanner.Model` — `GearItem`, `Mod`, `AugmentSlot`, `SetBonus`, `EquipSlot`. Pure, no deps.
- `src/DdoGearScanner.Capture` — capture (copied from pg-loot) + `GameWindowTracker` (DDO match, DWM bounds) + `FrameGrabber`.
- `src/DdoGearScanner.Vision` — `LocalOcr`, `TooltipChangeDetector` (the working detector), `TooltipTextParser`, `InventoryLocator`, `ITooltipReader`/`LocalOcrTooltipReader`, + dead detectors.
- `src/DdoGearScanner` — WPF app: `App`, `CaptureListWindow` (loadout sheet + detail + the `ItemEditWindow` editor), `OverlayWindow` (toast + highlight), `LowLevelKeyHook` + `HotkeyTrigger`, `CapturePipeline`, `CalibrationController`, `SlotMap`, `SlotInfo`, `SlotRow`, `CaptureStore` (loadout.json), `MatrixWindow` (stacking puzzle), `RunTrackerPipeline` + `RunStore` (runs.json) + `RunTrackerWindow` (dungeon-run log), `AppSettings`.
- `tools/DdoDataImporter` — re-runnable console tool that imports DDOBuilderV2's game data → `data/items.json` + `data/bonustypes.json`. See its README.
- `data/` — generated catalog (structured data; NOT `assets/`, which is binary media like ragdoll.png). `bonustypes.json` is embedded into Vision and is the authoritative stacking source.
- `test/DdoGearScanner.Vision.Tests` — parser fixtures + dev diagnostics (machine-specific, read from %APPDATA%).

## DDOBuilder data import (the item DB + authoritative bonus types)

[DDOBuilderV2](https://github.com/Maetrim/DDOBuilderV2) ships a full structured item database (~8,500
`.item` XMLs) and the game's real bonus-type stacking rules, in the same split `Stat/Value/BonusType`
model we use. `tools/DdoDataImporter` (run: `dotnet run --project tools/DdoDataImporter`) sparse-clones
that data, converts it, and writes `data/items.json` + `data/bonustypes.json` — built to be a scheduled
refresh, not a one-off.

**Bonus-type STACKING is now data-driven and authoritative.** `BonusTypes.StacksWithSelf` loads the
self-stacking set from the embedded `bonustypes.json` (`Stacking == "Always"`). The previous
hand-crawled list (`GAME_RULES.md`) was WRONG — it stacked Artifact/Primal/Circumstance/Feat/Epic
(actually Highest Only) and missed Destiny/Unique/Penalty/Weapon DR/Armor&Shield Enhancement. The only
self-stacking types are Armor Enhancement, Destiny, Mythic, Penalty, Reaper, Shield Enhancement,
Stacking, Unique, Untyped, Weapon DR. `items.json` is generated but not yet consumed (named-item
matching is the next step). `BonusTypes.All`/`UserSelectable` stay curated (parser prefixes / editor
dropdown) — only stacking comes from the data.

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
