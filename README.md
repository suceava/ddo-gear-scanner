# ddo-gear-scanner

Windows desktop overlay that reads **Dungeons & Dragons Online (DDO)** gear tooltips off the
screen. Hover a piece of gear in-game, press a hotkey, and the tool captures the tooltip, OCRs
it, parses it into a structured item (name, minimum level, slot, **mods with bonus type**,
augment slots, set bonus, binding), and saves it to a local list.

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
- **Run the app as administrator.** DDO runs elevated, and Windows suppresses a non-elevated app's
  global hotkey while an elevated window has focus — so the capture hotkey wouldn't fire over the
  game. The app's manifest requests administrator, so you'll get one UAC prompt at launch.

## Steps

1. Launch DDO in windowed / borderless windowed mode.
2. Run `DdoGearScanner.exe`. The main window opens.
3. Click **Detect game window** to confirm the tool sees DDO (the DDO row is marked ➤). This also
   reveals the real process name / window class if the defaults need updating.
4. Hover a gear tooltip in DDO and press **ScrollLock** (the default hotkey; rebind via **Set hotkey**, or click **Scan now**). The parsed item
   appears in the left panel and is added to the list; a toast confirms over the game.

Captures persist to `%APPDATA%\DdoGearScanner\captures.json`. Debug crops (for tuning tooltip
detection) go to `%APPDATA%\DdoGearScanner\debug-crops\`.

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
  DdoGearScanner.Model/     pure data model (Mod, AugmentSlot, SetBonus, GearItem) — net8.0, no deps
  DdoGearScanner.Capture/   window capture (copied from pg-loot) + DDO window tracking + FrameGrabber
  DdoGearScanner.Vision/    LocalOcr, tooltip region detection, ITooltipReader, parser, bonus-type list
  DdoGearScanner/           WPF app: hotkey, capture pipeline, list window, click-through overlay
test/
  DdoGearScanner.Vision.Tests/   TooltipTextParser fixtures
```

Layering: `Model` ← `Vision` ← `App`; `Capture` is independent. The OCR backend is pluggable behind
`ITooltipReader` (Phase 1 = local Windows OCR; Phase 2 = Claude vision). The trigger is pluggable
behind `ICaptureTrigger` (Phase 1 = hotkey; later = auto-detect).

## Build & test

```powershell
dotnet build
dotnet test
```

## Distributable .exe

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Produces `dist\DdoGearScanner.exe` (self-contained single file; bundled .NET runtime + OpenCV
natives). Copy it anywhere on a Windows machine — no .NET install required.
