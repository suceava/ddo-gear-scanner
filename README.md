# ddo-gear-scanner

Windows desktop overlay that reads **Dungeons & Dragons Online (DDO)** gear tooltips off the
screen and builds your **equipped loadout**. Start a detection session, hover each equipped slot on
the inventory paper-doll, and the tool captures the tooltip, OCRs it, parses it into a structured
item (name, type, minimum level, **mods with bonus type**, augment slots, set bonus, binding), tags
it with its equipment slot, and fills it into the loadout sheet (re-capturing a slot overwrites it).

DDO has no API and no gear export — this is the missing "read my equipped gear" piece. It
handles both **named items** (the recognizable chase items) and **random / Cannith-crafted
items** (no useful name, so every mod is extracted).

> Living references: [TOOLTIP_FORMAT.md](TOOLTIP_FORMAT.md) — how DDO tooltips are structured and
> how we parse them (read before touching the parser). [PLAN.md](PLAN.md) — the build plan and
> phased roadmap. [CLAUDE.md](CLAUDE.md) — orientation for AI/agent sessions.

Modeled on [pg-loot-master](../pg-loot-master) (a Project: Gorgon overlay); the capture, overlay,
and OCR infrastructure are reused from it.

---

# Run it

## Requirements

- **Windows 10 (2004 / May 2020) or Windows 11**, x64. Bundled .NET 8 runtime needs it.
- **Visual C++ 2015–2022 Redistributable** for the OpenCV native libs (almost always already
  installed; grab [vc_redist.x64](https://aka.ms/vs/17/release/vc_redist.x64.exe) if you see a
  `vcruntime140.dll` error).
- **DDO in windowed or borderless windowed mode.** Exclusive fullscreen bypasses the Windows
  Graphics Capture API and nothing will be captured.
- A **Windows OCR language pack** (English is present by default on most installs).
- No admin / UAC needed (DDO isn't elevated; the hotkey uses a low-level keyboard hook that fires
  over the game regardless).

## Steps

1. Launch DDO in windowed / borderless windowed mode and **open your inventory** (paper-doll visible).
2. Run `DdoCompanion.exe`.
3. **Calibrate once:** with the inventory open and no tooltip showing, click **Calibrate slots** and
   follow the on-screen prompts — hover the center of each slot it names and press the hotkey. (Saved;
   only redo if you change resolution / UI scale.)
4. Press the hotkey (default **Insert**, rebind via **Set hotkey**) to start **Detection**, then hover
   each equipped piece. Each tooltip fills its slot in the loadout sheet; click a slot to see details.

Persists to `%APPDATA%\DdoGearScanner\` (`loadout.json`, `slotmap.json`, `settings.json`). Debug crops
go to `debug-crops\`.

The tool only reads pixels from the DDO window via Windows Graphics Capture. It never touches the
game process, files, or network.

---

# Develop

.NET 8 + WPF + C#, Windows-only. `Windows.Graphics.Capture` for screen capture, `Windows.Media.Ocr`
for OCR, OpenCvSharp4 for image processing.

## Layout

```
DdoGearScanner.sln
src/
  DdoGearScanner.Model/     pure data model (Mod, AugmentSlot, SetBonus, GearItem, EquipSlot) — net8.0
  DdoGearScanner.Capture/   window capture (copied from pg-loot) + DDO window tracking + FrameGrabber
  DdoGearScanner.Vision/    LocalOcr, TooltipChangeDetector (motion), TooltipTextParser, InventoryLocator
  DdoGearScanner/           WPF app: low-level-hook hotkey, pipeline, loadout window, overlay,
                            slot calibration (CalibrationController/SlotMap/SlotInfo), CaptureStore
test/
  DdoGearScanner.Vision.Tests/   TooltipTextParser fixtures + dev diagnostics
assets/
  inventory/ragdoll.png     rag-doll anchor template for inventory slot detection
```

Layering: `Model` ← `Vision` ← `App`; `Capture` is independent. **Read [CLAUDE.md](CLAUDE.md) for
how detection actually works** — it diverged substantially from [PLAN.md](PLAN.md). The OCR backend
is pluggable behind `ITooltipReader` (local Windows OCR today).

## Build & test

```powershell
dotnet build
dotnet test
```

## Distributable .exe

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Produces `dist\DdoCompanion.exe` (self-contained single file; bundled .NET runtime + OpenCV
natives). Copy it anywhere on a Windows machine — no .NET install required.
