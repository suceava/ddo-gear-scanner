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
4. **Mods / enchantments** — the bulk. Each affix is prefixed in-game by a small filled gold
   **▶ triangle** and reads `<affix name> +N: <description>` where the description restates the
   value and names the bonus type. See **Mod segmentation by ▶ bullets** below — this is how mods
   are split. Within a mod: Stat from the head (before the colon), Value once, BonusType from the
   description (`+N <Type> bonus`):
   - `Constitution +6: Passive: +6 Enhancement bonus to Constitution` → Stat=Constitution, Value=6,
     BonusType=Enhancement (the default when no type word is found).
   - `Insightful Physical Sheltering +9: …+9 Insight bonus…` → Stat="Physical Sheltering",
     BonusType=Insightful (a known type word leading the name OR named in the description).
   - Valueless named effects (`Increased Weapon Die`, `Manslayer`) are kept as mods with Value 0.
5. **Augment slots** — `(Empty )?<Color> Augment Slot`. Colors: Colorless, Blue, Yellow, Red,
   Orange, Purple, Green. **Empty slots are upgrade gaps.** A filled slot may show the augment name
   after a colon.
6. **Set bonus** — line containing the token `Set` (e.g. `Sentinel's Aspect Set`). Piece counts are
   usually not readable from a single item, so they're left unset.
7. **Binding** — `Bound to Character/Account ...` or `Unbound`.

## Top block: header / name / type / quality (parsed in `TooltipTextParser`)

The lines above the stats are, in order: an optional **"CURRENTLY EQUIPPED"** header (the equipped-
comparison tooltip — skip it), the **item name** (which can WRAP across 2–3 lines), an optional
**item-type** line, an optional **quality** line, then **"Equips to: …"**. The catch: the type line
(`Heavy Armor`, `Tower Shield`, `Bastard Sword (one-handed)`) sits right under the name and looks
like a name continuation. It's separated by being a **known DDO type** (whole-line match against
`ItemTypes`, dropping a trailing `(one-handed)`), so it's captured as `ItemTypeText` instead of
polluting the name. Name gathering stops at the first of: a type line, a quality word (`Normal`,
`Rare`, …), or a content marker (`Equips to`, `Minimum Level`, `Bound`, …). `Equips to:` is the
reliable end-of-top-block boundary (it's right-justified in-game but OCRs as its own line).

NOTE: for equipped items the **calibrated inventory slot overrides** the parser's slot — slot
detection comes from the paper-doll position, not this text.

## Mod segmentation by ▶ bullets (the reliable delimiter)

Text alone CANNOT delimit mods: the ▶ glyph is dropped by OCR, and descriptions contain colon
phrases (`Passive:`, `Weapons and Shields:`) and restated `+N` values, so neither "colon" nor
"value-first" separates an affix line from its description. The gold **▶ triangle** is the only
reliable delimiter. `BulletDetector` finds them geometrically (so it's crop-agnostic):

1. mask the gold core (high R/G, low B),
2. keep small contours approximating a **3-vertex, left-of-center** (right-pointing) triangle,
3. keep only the **dominant x-column** — bullets share an x; gold *keyword* text (DDO highlights
   description keywords in gold too) scatters off-column,
4. drop size-outliers in that column.

It returns the bullets' Y-centres in the OCR image's pixel space. `TooltipTextParser.Parse(lines,
bulletYs)` then assigns each OCR row to the nearest bullet at/above it (rows above the first bullet
= header; rows at/after the first footer marker — Augments / Set Bonuses / Base Value — = footer),
joins each ▶ block, and runs `ExtractModFromBlock` to produce exactly **one mod per bullet**. This
is what removes run-together and double-counting. If no bullets are detected it falls back to the
old line-by-line parse. Detection + OCR MUST run on the same image (the reader uses the 3x-upscaled
crop for both). Verified on a real shield crop: 8 ▶ → 8 clean mods.

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
