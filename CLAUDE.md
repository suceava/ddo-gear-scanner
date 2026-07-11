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

## App shell — "DDO Companion" (single window, nav rail, pages)

The product is now **DDO Companion** (user-visible name; assembly/namespace/`%APPDATA%` folder stay
`DdoGearScanner`). `ShellWindow` is the main window: a header (product mark + a global ☰ menu with Debug
Settings) + a left **nav rail** (Home · Gear Loadout · Run Tracker) swapping a `ContentControl`. The two
features are **UserControl pages**: `GearLoadoutView` (the old loadout sheet + character selector + gear
menu, extracted from the deleted `CaptureListWindow`) and `RunTrackerView` (extracted from the deleted
`RunTrackerWindow`). `HomeView` is a landing page. The active page is remembered (`AppSettings.ActivePage`).
Overlay, calibration, item/character edit, and debug windows remain separate floating windows. `App`
creates the shell and routes pipeline events to `main.Gear` / `main.Run`. Character selection lives on
the Gear page (the Run Tracker auto-detects character); the run history is NOT character-filtered.

## Run tracker

A **dungeon-run logger** — a SECOND subscriber to `CaptureCoordinator.FrameArrived` (never touches the
gear path), with its own `LocalOcr`. It OCRs **four user-calibrated, window-relative regions** (drawn once
in `RunCalibrationWindow`, persisted to `AppSettings`, re-drawable live): ① quest-entry dialog, ② quest
tracker, ③ chat log, ④ **avatar** (character name + level). Readers are in `Vision/RunScreenReaders.cs`;
all pure parsing is in `Vision/RunTextParser.cs` (unit-tested in `RunTrackerTests`).

**Signals (why each panel):**
- **Name** = the **quest-entry popup** (`EntryPopupReader`, high-scale so "11" doesn't collapse to "1").
  Names get an OCR fix: an onset `l` with no vowel before it → `i` ("Hlgh Road"→"High", "Rlddle"→"Riddle")
  in `FixMisreadI`, leaving real post-vowel `l`s alone ("World", "Hall").
- **Completion** = the quest tracker's **"Status: Completed"** line — but OCR drops the "Status:" so
  `IsTrackerCompleted` matches a line whose only significant word starts with "complet" (rejects objective
  lines that carry other words). This is the PRIMARY completion signal. Backup: a **rising edge** on
  "Adventure Completed" appearing in the chat (robust to a fast, noisy combat chat where line-shift
  alignment fails; baselined per-run so a stale message can't fire it).
- **XP** only ever appears in chat ("You receive N XP") — captured every read and stamped at finalize,
  whichever signal completed the run.
- **Character** = the **avatar region** (`CharacterReader`): name reads on the plain OCR; the level pip
  ("20") sits over the portrait so a second **bright-text threshold pass** recovers it.

**Start / cancel logic** (`RunTrackerPipeline`): popup seen → arm (record the area you stood in). A run
STARTS on either (a) the area **changing** from the armed area, or (b) a **loading screen** — the tracker
was showing a readable area and then went **blank** (the load-out tell). The blank-load path requires the
armed area to have been non-blank, so opening+**cancelling** a popup where the tracker is blank (a
quest-giver spot) does NOT start a run. **Wilderness** areas show a "Slayer: <area> Menaces" counter — if
an active run's tracker reads that, the run is **discarded** (never logged; DDO's entry popup for a
wilderness looks like a quest). Character name+level are OCR'd while idle, cached, and stamped onto the
run at start.

**UI (`RunTrackerView`):** a Current-Run card (quest name = title, colored status **badge**, big timer,
chips incl. **Character**, helper text) with manual **Start / Complete / Cancel** overrides and inline
**✎ rename** of the live run; a **Settings** window (`RunSettingsWindow`) with an **auto-open-wiki-on-
start** toggle (off by default); an "↗ Wiki" button (current run only — never in the table). History is a
DataGrid of **all runs** (`RunStore.AllNewestFirst`) with editable Dungeon/Character/Difficulty/Level/XP,
a ✓/↩ status glyph, and a hover-reveal ✕ delete. `runs.json` persists everything. Tracking is always on
(no toggle — a user would never want it off). Wiki links via `QuestWiki.Slug`.

## Debug system

A global `AppSettings.DebugMode` toggle gates everything. `DebugSettingsWindow` (shell ☰ menu) is
data-bound to `AppSettings`. Two kinds, deliberately separated: **spatial** debug (calibrated region
borders) draws on the click-through game `OverlayWindow`; **data** debug (live chat OCR) lives in the
movable `DebugDiagnosticsWindow`. Region crops + OCR text dump to `%APPDATA%\DdoGearScanner\run-debug\`
every ~5s (tracker/completion/chat/character .png + a log line) — that's how the regions/parsers get tuned.

**Detection is OCR-only (no Claude — user vetoed AI) and field-tuned from those dumps.** Everything is
user-editable, so a miss costs an edit, not a lost run.

**Difficulty auto-detect (in `EntryPopupReader.DetectDifficulty`) — "good enough for now":** the SELECTED
difficulty is read by the LABEL, not the icon — the selected label goes bright WHITE, the rest stay gray,
regardless of the tier's theme colour. (The icon glow is colour-biased: silver Casual is always brightest,
red Reaper darkest — comparing icon brightness just picks the lightest tier, which was a dead end. Same
for near-white on the icon.) Label X for every slot is EXTRAPOLATED from the fixed order (`DiffOrder`) so a
tier stays a candidate when its label OCR merges ("Casual Normal" as one word) or drops. **Reaper is
special**: selecting it swaps the icon for a "N Skull" dropdown, so a "Skull" reading = `Reaper N`. It
carries forward across jittery frames and can show a stale value for a second before settling; the manual
**Difficulty** buttons on the card are the backstop. Tuned from `run-debug/popup.png` + the `[difficulty=…]
[white: …]` log line (crop ↔ log verified against the actually-highlighted tier).

**Still on the design-goal list, NOT yet built:**
- **End-of-quest reward panel** (optional nicety) — DDO's center-screen XP summary would give name +
  difficulty + full XP in one panel; completion works fine via tracker "Completed" + chat XP, so it's not
  required. Stale "reward panel" comments in `App`/`AppSettings`/`RunTrackerPipeline`/`RunRecord` are historical.

## Where things live

- `src/DdoGearScanner.Model` — `GearItem`, `Mod`, `AugmentSlot`, `SetBonus`, `EquipSlot`. Pure, no deps.
- `src/DdoGearScanner.Capture` — capture (copied from pg-loot) + `GameWindowTracker` (DDO match, DWM bounds) + `FrameGrabber`.
- `src/DdoGearScanner.Vision` — `LocalOcr`, `TooltipChangeDetector` (the working detector), `TooltipTextParser`, `InventoryLocator`, `ITooltipReader`/`LocalOcrTooltipReader`, + dead detectors.
- `src/DdoGearScanner` — WPF app ("DDO Companion"): `App`, `ShellWindow` (main; nav rail + pages), `HomeView` / `GearLoadoutView` (loadout sheet + `ItemEditWindow`) / `RunTrackerView` (dungeon-run log) pages, `OverlayWindow` (toast + highlight + region borders), `LowLevelKeyHook` + `HotkeyTrigger`, `CapturePipeline`, `CalibrationController`, `SlotMap`/`SlotInfo`/`SlotRow`, `CaptureStore` (loadout.json), `MatrixWindow`, `RunTrackerPipeline` + `RunStore` (runs.json) + `RunCalibrationWindow` + `RunSettingsWindow`, `DebugSettingsWindow` + `DebugDiagnosticsWindow`, `AppSettings`. (Old `CaptureListWindow`/`RunTrackerWindow` were replaced by the shell + views.)
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
