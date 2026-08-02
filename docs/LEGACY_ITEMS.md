# Scanned items vs. the catalog — identity, instance truth, legacy & variants

> Design doc + decision log for how a SCANNED item relates to its DDOBuilder catalog entry, and the
> roadmap for legacy/variant detection and UI badging (desktop app + web planner). Written 2026-07
> when the OpenRouter LLM reader made tooltip reads accurate enough to expose the difference.

## The two-layer model (DECIDED)

A scanned item carries **two distinct things**:

1. **Catalog identity** — *which* item this is (`Scepter of the Ogre Magi`, joinable to the
   DDOBuilder catalog and to the web planner's `slug(Name)-ml<ML>` wire id). Produced by
   `NamedItemMatcher` on the OCR'd/LLM-read name; a high-confidence match canonicalizes the stored
   Name and sets `GearItem.Matched` (= "identity confirmed", see below).
2. **Instance stats** — what is actually printed on *this copy's* tooltip (the mods the game
   applies). When the LLM (OpenRouter) read the tooltip, these are kept verbatim; the catalog's
   mods are **never** swapped in over an LLM read. Local-OCR reads still get the full catalog
   replacement (OCR mods are unreliable enough that catalog data is strictly better there).

**Why instance stats win (the legacy-items insight, from Dan):** DDO has updated the stats on many
items over the years but left already-acquired copies with their ORIGINAL stats ("legacy" items).
A 10+-year account owns plenty. The catalog describes the *current* drop; only the tooltip knows
what *your copy* does — e.g. a legacy Scepter of the Ogre Magi reads Potency +44 / Implement +15
while the catalog says +52 / +12. Overwriting instance data with catalog data is a category error,
not a tuning problem. (`RawOcrText` always retains the untouched LLM/OCR output either way.)

**Mod-name normalization happens at the JOIN, never at capture.** The tooltip says "Efficient
Metamagic - Extend II"; the catalog vocabulary says "Efficient Extend II". The planner's stacking
engine needs the catalog vocabulary — so map tooltip stat names onto the matched catalog item's own
mod names (a bounded set per item, contains/edit-distance is enough) at integration/display time,
keeping tooltip VALUES. Capture storage stays source-true.

## Web-planner inventory integration (FUTURE — schema decided, not built)

Planner inventory entry = **catalog ref + optional `scannedMods` override** (nullable).
- No override → planner uses catalog stats (today's behavior; hand-added items unchanged).
- Override present → stacking math uses the real scanned values; UI badges the item.
The inventory backend/schema doesn't exist yet, so this is a nullable field decided before the
schema freezes. The scanner's future push payload = catalog id + instance mods (+ flags below).

## Legacy vs. variant detection (FUTURE — design)

"Why don't my mods match the catalog?" has two benign explanations that can CO-OCCUR:
- **Variant**: the name matches several catalog entries (heroic/epic/legendary tiers, often same
  name at different MLs) and we compared against the wrong one.
- **Legacy**: compared against the *right* variant, the values still differ because the game
  updated the item after this copy dropped.

Resolution order (variant is not a flag, it's picking the right comparison target):
1. Collect all catalog entries whose normalized name matches (not just the single best).
2. Pick the variant by **ML** (exact tooltip-ML match preferred; else nearest).
3. Diff the scan's mods against THAT entry, per mod (names compared post-normalization):
   - `match` — same stat, same value.
   - `value-differs` — same stat, different value → **legacy value**; note direction
     (lower than catalog = the "alert" case, higher = lucky you).
   - `scan-only` — mod on the tooltip but not in the catalog entry → legacy loot-table change,
     or the variant pick was wrong (if ANOTHER variant contains it, prefer re-picking that variant).
   - `catalog-only` — catalog lists a mod the tooltip doesn't show.
4. Item-level rollup: any non-`match` ⇒ **Legacy** badge; "both legacy AND variant" resolves
   naturally (an old copy of the heroic tier = variant pick by ML, then legacy diffs vs it).
   When ML itself changed over the years the variant pick is ambiguous — badge as
   "mismatch (legacy or variant)" rather than guessing.

## UI ideas parking lot (DO NOT LOSE — Dan's notes, 2026-07)

Desktop app (item detail) and web app (inventory/item card), when identity matched:
- **Item badge**: "Legacy" when any mod diff exists; stronger **alert styling when the scanned
  values are LOWER than catalog** (your item is worse than the current drop — maybe re-farm).
- **Per-mod highlight**: flag each mismatched mod inline (legacy value / scan-only / catalog-only),
  ideally showing the catalog value alongside, e.g. `Potency +44 (current: +52)`.
- **Ambiguity state**: "legacy or variant" when the variant pick isn't certain.
- Free feature: a "legacy gear report" — every scanned item whose stats trail the current version.

## Status

- DONE: LLM captures keep instance mods; name-only canonicalization; `Matched` = identity confirmed.
- FUTURE: per-mod diff engine + badges (desktop), planner `scannedMods` override + join-time name
  normalization + badges (web), legacy-vs-variant resolution, legacy gear report.
