# DDO tooltip format & parsing — living reference

This is the living reference for how DDO gear tooltips are structured and how
`TooltipTextParser` turns OCR'd lines into a `GearItem`. **Read this before changing the parser**,
and append real observed tooltips as they're captured. It is the analogue of pg-loot's
`GAME_RULES.md`.

## The two kinds of gear

- **Named items** — fixed, recognizable names (the chase items the web planner cares about). Often
  carry a **set bonus**. `IsLikelyNamed` heuristic flags these.
- **Random / Cannith-crafted items** — procedurally named (frequently `+N <noun> of <suffix>`), no
  useful identity. The value is entirely in the **mod list**, so we must extract every mod. These
  are NOT identifiable by name.

Because both need the full mod list, robust mod parsing is the core job — and the brittle part the
Phase-2 Claude vision reader is meant to replace.

## Tooltip anatomy (top to bottom, typical)

1. **Item name** — first line. Colored by rarity in-game (color is dropped by OCR).
2. **Minimum Level: N** — `Minimum Level[:\s]*N`.
3. **Item type / slot** — e.g. `Cloak`, `Ring`, `Armor: Medium`, `Weapon: Longsword`. Mapped to an
   `EquipSlot` where unambiguous (Ring → Ring1; user re-files Ring2 manually).
4. **Mods / enchantments** — the bulk. Usually `[<BonusType>] <Stat> +N`:
   - `Constitution +14` → Stat=Constitution, Value=14, **BonusType=Enhancement** (the default when
     no explicit type word leads the line).
   - `Insightful Healing Amplification +28` → BonusType=Insightful, Stat="Healing Amplification".
   - Valueless named effects (`True Seeing`, `Feather Falling`) are kept as mods with Value 0.
5. **Augment slots** — `(Empty )?<Color> Augment Slot`. Colors: Colorless, Blue, Yellow, Red,
   Orange, Purple, Green. **Empty slots are upgrade gaps.** A filled slot may show the augment name
   after a colon.
6. **Set bonus** — line containing the token `Set` (e.g. `Sentinel's Aspect Set`). Piece counts are
   usually not readable from a single item, so they're left unset.
7. **Binding** — `Bound to Character/Account ...` or `Unbound`.

## Bonus types (the stacking-critical field)

Same-type bonuses to a stat **don't stack** (highest wins); different types stack. This is what the
web planner's matrix math depends on, so the bonus type is a first-class field even though it's the
hardest thing to OCR. The known vocabulary lives in `BonusTypes.cs` — keep it in sync with reality:

`Enhancement` (default), `Insightful`/`Insight`, `Quality`, `Competence`, `Exceptional`, `Profane`,
`Sacred`, `Artifact`, `Festive`, `Morale`, `Luck`, `Resistance`, `Deflection`, `Natural Armor`,
`Dodge`, `Primal`, `Alchemical`, `Legendary`, `Stacking`, …

A leading word from this list is treated as the bonus type; otherwise the line defaults to
Enhancement.

## Parser contract

- **Never throws.** A bad parse degrades to "name + raw text", never data loss.
- **Always retains `RawOcrText`** so nothing captured is lost.
- Operates on plain strings → unit-testable from fixtures in
  `test/DdoGearScanner.Vision.Tests` without launching the game.

## Known gaps / TODO (local-OCR path)

- Multi-line affixes (description wrapping under a property) aren't merged.
- Set piece counts and the granted-bonus text aren't extracted.
- OCR digit confusion (O/0, l/1) is only partially normalized in the value token.
- Ring1 vs Ring2 / weapon main vs off-hand can't be disambiguated from the tooltip alone.

These are accepted for v1 — Phase 2 (Claude vision) is expected to supersede classification. Record
new real tooltips below as fixtures.

## Observed tooltips (append real captures here)

_None yet — paste raw OCR output + a note on what parsed wrong, and add it as a test fixture._
