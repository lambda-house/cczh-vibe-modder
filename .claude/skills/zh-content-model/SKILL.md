---
name: zh-content-model
description: Explain Zero Hour's content model — what an Object, Weapon, Armor, Locomotor, PlayerTemplate, Science, Upgrade or SpecialPower actually is as data, how factions bind to units, and which parts are pure data vs C++. Use when designing content types, mapping ZH concepts onto this platform, or answering "how does ZH model X".
---

# Zero Hour's content model

**Their schema is the target model for this platform.** We adapt to it rather than grow a
parallel vocabulary, so that content and knowledge transfer both directions. Coverage is
measured against it: `tools/zhasset coverage --gaps`.

The authority is the engine's `FieldParse` tables — every INI block type declares one, and
it enumerates the legal keys plus a parser that implies each type. Extract with
`tools/zhasset schema` → **253 tables, 2,993 fields**.

## The content types that matter

| Type | Fields | What it is |
|---|---:|---|
| `Object` (ThingTemplate) | **115** | a unit, structure, projectile or prop |
| `Weapon` | **76** | the entire offensive stat block |
| `Locomotor` | **60** | how a thing moves |
| `PlayerTemplate` | **48** | a playable faction |
| `Armor` | small | a sparse damageType → multiplier row |
| `Science` / `Upgrade` / `SpecialPower` | 5 / 8 / 12 | the progression vocabulary |

Retail instances: 15 PlayerTemplates (13 playable), 2,102 objects (574 buildable = 366 units
+ 208 structures), 363 weapons, 53 armor sets, 182 locomotors, 96 sciences, 81 upgrades,
79 special powers, 247 terrain types.

## An Object owns almost none of its own numbers

This is the thing to internalise. The `Object` block itself has **no health, no speed, no
damage**. It delegates:

- health → `Body = ActiveBody` module, `MaxHealth`
- movement → `Locomotor = SET_NORMAL <name>` resolving to a separate `Locomotor` block
- damage → `WeaponSet` → a separate `Weapon` block
- damage taken → `ArmorSet` → a separate `Armor` block
- everything else it *does* → a list of `Behavior = <ModuleClassName> <tag>` entries

The Object's own fields are economy, taxonomy (`KindOf`, a 120-bit flag set), footprint,
sensing and UI. Composition is by **name reference into flat tables** — which is why adding
a unit that reuses existing art is 4 files and zero binary assets.

## Combat is one table

All rock-paper-scissors is a **57 × 38 damageType × armorClass multiplicative lookup**.
Units carry no "anti-tank" stat — only a `DamageType` label whose meaning lives entirely in
that table. **47.5% of hand-authored cells are 0%**: half the effort is saying "this does
nothing to me."

Two rules worth copying exactly:

- **Bonuses compose additively-in-excess**, not multiplicatively (`bonus += this - 1.0`).
  Veteran 1.10 + upgrade 1.25 = **1.35, not 1.375**. Deliberately anti-combinatorial.
- **Durations quantise to whole 30 Hz ticks with `ceil()` at parse time.** This is why one
  retail flame weapon reads 250 DPS in the file and delivers 150.

## A faction is a string

`Side` is a free-form `AsciiString` that nothing validates. The whole chain —
PlayerTemplate → `Side` → objects carrying that Side → CommandSet → CommandButton →
Object → Model — resolves through string keys. So a 4th faction is pure data, 6–9 files,
**zero art**.

The catch: there is **no roster declaration anywhere**. Membership is implicit in each
object's `Side` string, and buildability is authored *twice* (the unit's `Prerequisites`
and the producing structure's `CommandSet`) with nothing cross-checking them.

## What is data and what is C++

**Pure data:** stats, weapons, armor, locomotors, terrain types, factions, upgrades,
sciences, powers, command sets, strings, per-scenario `map.ini` overrides.

**C++ walls:** the ~217 behavior module classes, 38 damage types, 3 weapon slots,
4 veterancy levels, 128 upgrade bits (82 used), 18 command-bar slots, `NUM_GENERALS 12`,
and faction identity as an **ordinal on the wire**.

The module system is the crux: **203 modules are used, but 7 cover 50% of usage and 27 cover
80%**, and **86.7% of non-draw module instances are pure data averaging 3.9 parameter
lines**. Roughly 15 declarative primitives would express most of the game — which is the
design target for our rules engine, not a scripting language.

## What ZH got wrong, and we should not copy

- **Generals are forks, not diffs.** `ChildObject` exists in the engine and is used **zero**
  times in shipping content; 97% of general content is byte-identical duplication.
- **`ObjectReskin` is restricted to ten appearance fields** — and that restriction is
  precisely what forced 477 wholesale clones. Never add a presentation-only variant type.
- **Faction identity is an ordinal**, so inserting a PlayerTemplate renumbers every later
  faction across clients. (Their `Science` store is name-keyed and reorders freely — they got
  it right everywhere except where it mattered.)

Full measurements: `docs/ZERO-HOUR-ANATOMY.md` and `docs/ZERO-HOUR-ASSETS.md`.
