# DDO game rules — living reference

Game facts that the scanner and the sibling web planner depend on. The first topic is **bonus
types**, because the planner's stacking math is built entirely on them and the scanner has to
capture the bonus type off every mod (see [TOOLTIP_FORMAT.md](TOOLTIP_FORMAT.md) and
[BonusTypes.cs](src/DdoGearScanner.Vision/BonusTypes.cs)). Source: DDO wiki
[Category:Bonus types](https://ddowiki.com/page/Category:Bonus_types) and each type's page
(crawled 2026-06; the wiki is WAF-protected, pulled via the Wayback Machine + search snippets).

## The master rule

A bonus almost always carries a **type keyword** (Enhancement, Insight, Quality, …). The keyword is
what determines stacking:

- **Same type → does NOT stack. Only the single highest value applies.** This is the default for the
  large majority of types.
- **Different types → stack** (they add together).
- **Untyped bonuses** (no keyword) **always stack.**
- A handful of types are explicitly **always-stacking** even with themselves, and a few have
  **special** rules. Those are the ones worth memorizing — they're listed below.

So the planner's per-stat math is: group a stat's mods by bonus type; within a non-stacking type take
`max`; for stacking types take the `sum` of all instances; then add the group totals together.

## Stacking behavior by type

### Default — same-type does NOT stack (highest/best applies)

These all follow the master rule (multiple of the same type → only the highest counts):

| Type | Notes / common sources |
|---|---|
| **Enhancement** | The default/most common. A **broad** enhancement does NOT stack with a **specific** one — e.g. *Speed* (enhancement to move speed) does not stack with *Striding*. |
| **Competence** | Most often on skills. **Exception:** *Tailwind* (Fatesinger) is labeled competence but actually stacks with other competence. |
| **Insight** (a.k.a. Insightful) | |
| **Quality** | "good workmanship"; only the greatest applies. |
| **Sacred** | power of good. |
| **Profane** | power of evil. |
| **Morale** | hope/courage; highest applies. |
| **Luck** | good/bad fortune; highest applies. |
| **Divine** | highest applies. |
| **Fortune** | highest applies. |
| **Determination** | highest applies. |
| **Equipment** | highest applies. |
| **Inherent** | best one applies (e.g. Tomes). |
| **Festive** | same-type don't stack. |
| **Music** | e.g. Warchanter capstone song doesn't stack with lower chants. |
| **Resistance** | saves; "may or may not stack" depending on source — treat as non-stacking by default, verify per item. |
| **Armor** | armor bonuses to AC don't stack (wear one armor). Different from Shield → those two stack. |
| **Shield** | shield bonuses to AC don't stack with each other; stack with Armor. |
| **Rage** | doesn't stack with other rage (Barbarian Rage, Skaldic Rage). |
| **Psionic** | doesn't stack with other psionic. |
| **Legendary** | Legendary Green Steel / legendary crafting set bonuses; don't stack. |
| **Size** | PnP-style size; highest applies. |
| **Style** (Combat Style) | from fighting-style feats/enhancements; same-type don't stack. |
| **Alchemical** | highest of same; **but** an Alchemical bonus to *Universal* Spell Power and to a *specific* Spell Power are different and DO stack. |
| **Implement** | spellcasting implements; doesn't stack with other implement, **but fully stacks with other (non-implement) Spell Power sources.** |

### Always-stacking — stacks with everything, including itself

| Type | Notes |
|---|---|
| **Artifact** | Rare; mostly named-item set bonuses. Stacks with all. (Update 17 reclassified many previously *untyped* set bonuses to artifact — note: a SET grants its tiers once, so this is about stacking *across different* sources, not double-counting one set.) |
| **Primal** | Stacks with all other bonuses. |
| **Circumstance** | Stacks with all, including other circumstance — *unless* two arise from essentially the same source. |
| **Feat** | "Feat bonuses do stack." |
| **Reaper** | All reaper bonuses fully stack. |
| **Epic** | Gives a stacking bonus (e.g. to a saving throw). |

### Special / partial

| Type | Rule |
|---|---|
| **Exceptional** | Stacks with all other types, but **NOT** with other exceptional. (Historically had odd stacking; cleaned up by U19. Devs avoid adding new exceptional items.) |
| **Guild** | Stacks with other bonus types but **NOT** with other Guild bonuses. |
| **Mythic** | Stacks differently — Mythic bonuses **from each gear slot stack with one another** (so multiple Mythic-slotted items add up). |
| **Special** | Catch-all label on the wiki; behavior varies by source — verify per item, don't assume. |

## How this maps to our apps

- **Scanner:** capture the bonus type verbatim onto each `Mod.BonusType`. The vocabulary in
  `BonusTypes.cs` should cover every type above so the parser recognizes a leading type word and
  pulls it out of the stat name. Keep that list in sync with this table.
- **Planner (stacking math):** classify each type as `non-stacking` (max), `always-stacking` (sum),
  or `special`. The special cases (Enhancement broad-vs-specific, Exceptional-not-with-exceptional,
  Guild-not-with-guild, Mythic-per-slot, Implement-vs-SpellPower, Alchemical-Universal-vs-specific,
  Tailwind-competence) are the ones that break naive "group by type and take max", so they need
  explicit handling.

## Verification status

Retrieved from the wiki this session: Alchemical, Armor, Artifact, Circumstance, Competence,
Determination, Divine, Enhancement, Epic, Equipment, Exceptional, Feat, Festive, Fortune, Guild,
Implement, Inherent, Insight, Luck, Morale, Music, Mythic, Primal, Profane, Quality, Reaper,
Resistance, Sacred. From search snippets (lower confidence, re-verify if it matters): Shield, Rage,
Psionic, Legendary, Style/Combat Style, Size, Special.

_Add more game-rule topics below as they come up (AC calc, spell power, doublestrike/doubleshot,
absorption stacking, etc.)._
