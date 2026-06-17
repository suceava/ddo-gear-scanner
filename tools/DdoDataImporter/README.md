# DdoDataImporter

Converts [DDOBuilderV2](https://github.com/Maetrim/DDOBuilderV2)'s XML game data into the JSON the
scanner uses. DDOBuilder maintains a full, structured item database (one `.item` XML per item) plus
the authoritative bonus-type stacking rules — far better than scraping ddowiki, and it's the same
split `Stat / Value / BonusType` model the scanner already uses.

## Run it

```powershell
dotnet run --project tools/DdoDataImporter
```

That's the whole refresh. It is **re-runnable** (designed to be a scheduled weekly/monthly job):

1. Syncs a sparse, blobless checkout of just `Output/DataFiles` into `tools/DdoDataImporter/.cache/`
   (a `git clone` the first time, `git pull` after — the cache is gitignored).
2. Parses every `*.item` + `BonusTypes.xml`.
3. Writes `data/items.json` + `data/bonustypes.json` at the repo root.

Options: `--source <DataFiles dir>` (skip the network, parse a local copy), `--out <dir>`,
`--cache <dir>`.

## Output

- **`data/items.json`** — the item catalog. Per item: `Name`, `MinLevel`, `Slots` (our `EquipSlot`
  enum names — `Weapon1→MainHand`, `Weapon2→OffHand`, `Ring→[Ring1,Ring2]`), `Type`, `Mods`
  (`Stat`/`Value`/`BonusType`/`Description`), `AugmentSlots`, `Sets`. Purely-cosmetic items are
  skipped; items with missing slot data are kept.
- **`data/bonustypes.json`** — every bonus type + its `Stacking` rule (`Always` / `Highest Only`).
  Embedded into the Vision assembly and loaded by `BonusTypes.cs` — this is the **authoritative**
  source for the stacking matrix (replaced a hand-rolled list that was wrong).

## Mapping decisions

- `stat` ← DDOBuilder's `<Buff><Item>` (their confusing name for the buff's *target*, e.g. "Haggle"),
  or the humanized effect `<Type>` when that's absent/`All` (procs like Vorpal).
- Slots map to our `EquipSlot` enum; cosmetic/internal slots are dropped.
- `effect` (raw type) is intentionally **not** emitted.
