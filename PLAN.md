# DDO Gear Scanner — desktop OCR tool

## Context

DDO (Dungeons & Dragons Online) has no API and no way to export a character's
equipped gear — every existing tool is manual entry. We want a Windows desktop
overlay that runs on top of the game: hover a piece of gear (the game shows its
tooltip), press a hotkey, and the tool captures the screen, OCRs the tooltip,
parses it into a structured item (name, min level, slot/type, and the full list
of **mods** with bonus type, augment slots, set bonus, binding), and saves it to
a **local list of items + mods**.

It must handle **two kinds of gear**:
- **Named items** — the ideal chase items, recognizable by name.
- **Random / Cannith-crafted items** — truly random, *not* identifiable by name,
  so the tool must extract **every mod** off the tooltip.

Because both paths need the mod list, robust tooltip parsing is the core job.

**Scope for v1:** standalone desktop app only. **Ignore the web app / shared item
DB / backend** for now (a related web planner exists in design, and this tool is
its eventual auto-import, but we wire that up later). Output is a local JSON
catalog. The data model is aligned to the planner's `{name, ml, dropLocation,
mods:[{stat,value,bonusType}], augments, setBonus}` shape so future integration
is free.

Reference app: `c:\code\pg-loot-master` (.NET 8 WPF overlay for Project Gorgon)
— we reuse its capture + overlay + OCR infrastructure.

## Approach

.NET 8 + WPF, mirroring pg-loot. Reuse its `Capture` project (window capture via
`Windows.Graphics.Capture`/DXGI), its transparent click-through overlay pattern,
and its `SidebarOcr` wrapper over the built-in `Windows.Media.Ocr`. Add what
pg-loot lacks: a **global hotkey**, a **cursor-anchored tooltip region detector**,
and a **tooltip text parser** that turns OCR lines into structured mods.

Design two seams so the hard parts are swappable:
- **`ITooltipReader`** — Phase 1 = local OCR + parser; Phase 2 = Claude vision
  (drop-in, returns the same structured item). The structured item is the contract.
- **`ICaptureTrigger`** — Phase 1 = hotkey; later = auto-detect tooltip. Coexist.

### Solution layout (new repo `c:\code\ddo-gear-scanner`)

```
DdoGearScanner.sln
publish.ps1                         (adapted from pg-loot)
src/
  DdoGearScanner.Capture/           COPY of PgLootMaster.Capture (namespace-renamed)
    WindowCapture.cs                verbatim
    Interop/CaptureInterop.cs       verbatim
    CaptureCoordinator.cs           verbatim
    GameWindowTracker.cs            ADAPT — match DDO window (see below)
    NativeMethods.cs                ADAPT — add GetCursorPos + EnumWindows diagnostics
    FrameGrabber.cs                 NEW — cache latest frame for on-demand single-shot
  DdoGearScanner.Model/             NEW — pure records, net8.0, no Windows deps
    Mod.cs, AugmentSlot.cs, SetBonus.cs, GearItem.cs, EquipSlot.cs, ItemStore (DTO)
  DdoGearScanner.Vision/            COPY SidebarOcr only; rest new
    LocalOcr.cs                     ADAPT of SidebarOcr.cs (general Recognize(Mat)->OcrLine[])
    ITooltipRegionDetector.cs       NEW
    DarkBoxRegionDetector.cs        NEW — cursor-anchored tooltip box finder (OpenCV)
    ITooltipReader.cs               NEW — pluggable OCR/vision contract
    LocalOcrTooltipReader.cs        NEW — Phase 1: LocalOcr + TooltipTextParser
    TooltipTextParser.cs            NEW — OCR lines -> GearItem (the brittle core)
    BonusTypes.cs                   NEW — known DDO bonus-type vocabulary
  DdoGearScanner.App/               WPF app (patterns from PgLootMaster)
    App.xaml(.cs)                   ADAPT — wire windows + hotkey + pipeline
    OverlayWindow.xaml(.cs)         ADAPT — click-through overlay (Phase 3 use; minimal v1)
    CaptureListWindow.xaml(.cs)     NEW — interactive: last capture + running item list
    HotKeyService.cs                NEW — RegisterHotKey via HwndSource hook
    ICaptureTrigger.cs / HotkeyTrigger.cs   NEW
    CapturePipeline.cs              NEW — trigger->grab->detect->read->store->show
    CaptureStore.cs                 ADAPT of GameHistoryStore.cs — JSON list in %APPDATA%
    AppSettings.cs                  ADAPT of OverlaySettings.cs — hotkey, OCR backend, debug
test/
  DdoGearScanner.Vision.Tests/      parser unit tests over saved OCR-line fixtures
```

Layering: `Model` (no deps) ← `Vision` (Model + OpenCvSharp4 + Windows.Media.Ocr)
← `App` (Capture + Vision + Model + WPF). `Capture` independent.

### Reuse map

- **Verbatim:** `WindowCapture.cs`, `Interop/CaptureInterop.cs`, `CaptureCoordinator.cs`
  (only namespace + the temp-log filename change).
- **`SidebarOcr.cs` → `LocalOcr.cs`:** keep the Mat→PNG→`SoftwareBitmap`→`RecognizeAsync`
  body and the `OcrLine(Text, Bbox)` record; expose `IsAvailable` + `Recognize(Mat)`.
- **`GameWindowTracker.cs` → adapt** (the one real capture change). DDO client process
  is historically `dndclient64.exe`/`dndclient.exe`; **window class/process must be
  confirmed at runtime** (Steam wrapper may differ). Loosen matching to: process name
  OR title `Contains("Dungeons & Dragons Online")`; make class check best-effort/logged.
  Add `EnumerateCandidateWindows()` → `(pid, process, class, title)` behind a
  "Detect game window" button so we lock the constants from real values.
- **`GameHistoryStore.cs` → `CaptureStore.cs`** and **`OverlaySettings.cs` → `AppSettings.cs`**:
  same `%APPDATA%\DdoGearScanner\` + `JsonSerializer` + swallow-on-error patterns.
- **Do NOT copy:** all Solver/board/sidebar/template/labeler code, OxyPlot, the
  toolbar/history/settings/debug windows — Project-Gorgon-specific.

### Data model (`DdoGearScanner.Model`, aligned to the web planner)

```csharp
public sealed record Mod(string Stat, double Value, string BonusType);   // bonusType "Enhancement" default
public enum AugmentColor { Unknown, Colorless, Blue, Yellow, Red, Orange, Purple, Green }
public sealed record AugmentSlot(AugmentColor Color, string? Filled, bool IsEmpty);
public sealed record SetBonus(string SetName, string? GrantedText = null);
public enum EquipSlot { Unknown, Helm, Goggles, Necklace, Cloak, Belt, Ring1, Ring2,
                        Bracers, Gloves, Boots, Armor, Trinket, MainHand, OffHand, Quiver }

public sealed record GearItem(
    string Name,
    int? MinimumLevel,
    EquipSlot Slot,
    string? ItemTypeText,                 // raw "Armor: Medium", "Weapon: Longsword"
    IReadOnlyList<Mod> Mods,              // the must-have list (named AND random items)
    IReadOnlyList<AugmentSlot> Augments,
    IReadOnlyList<SetBonus> SetBonuses,
    string? Binding,
    bool IsLikelyNamed,                   // heuristic flag (random/crafted -> false)
    string RawOcrText,                    // always retained, nothing lost on parse miss
    DateTime CapturedUtc);
```

`CaptureStore` persists a flat `List<GearItem>` (the "local list of items + mods").
Slot-sheet/loadout grouping is a later concern.

### Phase-1 flow

1. **Hotkey** (`HotkeyTrigger` → `HotKeyService`, default Ctrl+Shift+G) fires while the
   user hovers a tooltip in DDO.
2. **Grab frame** — `FrameGrabber` holds the latest `Mat` from `CaptureCoordinator`'s
   `FrameArrived`; pipeline reads a clone on demand (single-shot, not pg-loot's per-frame loop).
   Record the tracked `GameWindowRect` for coordinate mapping. Keep `IsCursorCaptureEnabled=false`.
3. **Cursor→frame** — `GetCursorPos`, subtract window origin, apply DPI (as pg-loot's overlay does).
4. **Locate tooltip** — `DarkBoxRegionDetector.Detect(frame, cursorPoint)`: threshold the
   dark tooltip background near the cursor, `FindContours`, pick the large near-rectangular
   box closest to the cursor; crop (pad inward off the border). Fallback to a fixed-size
   cursor-anchored rect. Threshold/radius in `AppSettings`; dump debug crops.
5. **Read** — `await reader.ReadAsync(crop)` → `GearItem` (Phase 1: `LocalOcrTooltipReader`).
6. **Store** — append to `CaptureStore`, save.
7. **Show** — `CaptureListWindow` (normal interactive window): "last capture" panel (parsed
   fields + collapsible raw OCR + the crop image) and the running list of captured items.
   The click-through `OverlayWindow` is reserved for Phase 3 on-game drawing.

### Parsing (`TooltipTextParser` — the brittle core, superseded by Claude in Phase 2)

Input = ordered `OcrLine[]` top→bottom. Classify lines:
- **Name** = first/top line. **MinLevel** = regex `Minimum Level[:\s]*(\d+)`.
- **Item type/slot** = `Armor:`/`Weapon:`/accessory keywords → `ItemTypeText`; map keyword
  → `EquipSlot` where unambiguous.
- **Binding** = lines with `Bound`/`Unbound`.
- **Augments** = `(Empty )?(\w+) Augment Slot` → `AugmentSlot(color, filled, isEmpty)`.
- **Set bonus** = lines with `Set`/`Set Bonus` → `SetBonus`.
- **Mods** = remaining affix lines → `Mod(stat, value, bonusType)`. Pull `+N` value; pull a
  leading **bonus-type word** from `BonusTypes` (Insightful, Quality, Profane, Sacred,
  Competence, Exceptional, Enhancement, …); **default `Enhancement`** when no prefix; the
  remainder is the stat name. The known-vocabulary list is what makes this tractable.
- **Named vs random:** `IsLikelyNamed` heuristic — a clean multi-word proper name + recognized
  set/known affix pattern → named; otherwise random/crafted. (Not critical for v1; both parse
  the same way. Real named-item resolution comes when the web DB exists.)
- Never throw; always set `RawOcrText`; low confidence → keep raw, partial mods.

### Phased milestones

- **Phase 1 — local-OCR MVP (build target):** scaffold solution; copy `Capture` (DDO
  constants + diagnostics); `Model`; `HotKeyService`+trigger; `FrameGrabber`+`CapturePipeline`;
  `DarkBoxRegionDetector`; `LocalOcrTooltipReader`+`TooltipTextParser`+`BonusTypes`;
  `CaptureStore`+`AppSettings`; `CaptureListWindow`; `publish.ps1`.
  **Exit:** hover gear → hotkey → parsed item (name, ML, mods w/ bonus type, augments) shown
  and saved to local JSON; persists across restart; works for both a named and a random item.
- **Phase 2 — Claude vision reader (drop-in):** `ClaudeTooltipReader : ITooltipReader` sends the
  tooltip crop + prompt, returns JSON → `GearItem`. Confirm model id/SDK via the `claude-api`
  skill. Key from `AppSettings`/env var; auto-fallback to local when absent. Big accuracy win on
  mod/bonus-type extraction. Optionally hand Claude a looser cursor-region crop (less reliance on
  precise region detection).
- **Phase 3 — later (decide after Phase 1):** auto-detect trigger; loadout/slot grouping;
  web-planner integration (push loadout / pull named-item DB) and community-text export.

### Verification

- **Window match first:** run app, click "Detect game window" with DDO running → confirm it
  finds DDO and logs real process/class/title; set `GameWindowTracker` constants from those.
- **Region detection:** enable debug crop dump; hover several gear pieces at different screen
  positions → confirm crops tightly bound the tooltip.
- **OCR + parse:** compare `CaptureListWindow` fields vs the actual tooltip for a named item and
  a random/crafted item; iterate regexes + `BonusTypes`. Back with `DdoGearScanner.Vision.Tests`
  over saved OCR-line fixtures (regression-safe without launching the game).
- **Persistence:** capture a few items, restart, confirm the list reloads from `%APPDATA%`.
- Use the `run`/`verify` skills to drive the app on Windows each phase.

### Risks / uncertainties

- DDO process name + window class **unconfirmed** — mitigated by loose matcher + the detect
  diagnostic.
- `Windows.Graphics.Capture` needs Win10 1903+ and non-exclusive-fullscreen (windowed/borderless
  safest); confirm `GraphicsCaptureSession.IsSupported()`.
- DPI/multi-monitor: cursor→frame mapping must subtract window origin + apply DPI exactly.
- Mod/bonus-type parsing from OCR prose is inherently brittle (same risk the web planner flagged)
  — acknowledged; Phase 2 Claude vision is the real fix.
- `OcrEngine.TryCreateFromUserProfileLanguages()` may return null (no OCR language pack) — surface
  `IsAvailable=false`.
