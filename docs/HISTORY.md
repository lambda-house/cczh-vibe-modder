# History — what was built, and the number that justified it

A log, not a manual. Each slice records what shipped and the measurement that made it worth
shipping; the operational detail — how to do the thing, and what bites you — lives in the
skills, and is deliberately NOT repeated here.

- **Invariants that constrain all work** → `CLAUDE.md`
- **How to author, emit and verify** → `zh-authoring` (+ `references/engine-rules.md`,
  `references/driving.md`)
- **What ZH's content model IS** → `zh-content-model`, `docs/ZERO-HOUR-ANATOMY.md`
- **What the extraction produces** → `docs/ZERO-HOUR-MODEL.md`

## Simulation core (slices 0-13, all done)

| # | Slice | The number behind it |
|---|---|---|
| 0 | Name-derived prototype identity | a verified live bug: `laser_crusader` silently renumbered `ranger`/`technical`, moving a matchup hash while both pinned hashes stayed put |
| 1 | Layered content packs + `rts diff` | the authoring substrate — without it every later slice is hand-edited into one monolithic `game.json`, the workflow this product replaces |
| 2 | Schema hygiene (`defaults`, tick quantisation) | without a defaults block every new field is a breaking change to every existing unit — retail's own solution, sized in `zh-content-model` |
| 3 | Modifier semantics (additive-in-excess, flag sets) | our algebra covers 4.5% of real upgrade references; the condition grammar needed is tiny — counted in `zh-content-model` |
| 4 | Faction-scoped reference resolution | 88 bad references / 68 broken definitions ship in retail, in all nine generals — a tracked defect class |
| 5 | Structures as economic targets | 570 buildables split 379 units / 261 structures; a minimum faction is 5 objects, 3 of them buildings |
| 6 | Upgrades as tech nodes granting flags | an upgrade carries no effect data — it sets a bit and a keyed loadout is selected |
| 7 | Rules engine `{on, when, do}` + spawn lists | cascades bounded at 4 generations and CLAMPED: a content bug degrades a battle, it does not crash a sweep |
| 8 | Garrison + capturable structures | terrain's tactical payoff with zero geometry: 17 of 363 weapons clear buildings, `ContainMax` is 10 on 204 of 236 |
| 9 | Powers + the science/rank second currency | five `Rank` blocks grant 7 purchase points a game against 13-20 sciences per faction |
| 10 | Spatial index (uniform grid) | ~2,000 units 6.9s → 2.1s; ~4,000 units 75.9s → 7.9s. Below ~800 it is a wash |
| 11 | MCP server | the agent-facing seam; every result carries `contentHash`, because a balance number without one is not reproducible |
| 12 | Passability grid + integer A* | same armies, same seed: 17.4s open, 27.4s through one gate, timeout draw against a solid wall |
| 13 | Line of sight as a boolean gate | identical two-cell wall: 28.4s when it only blocks movement, 22.2s when it also blocks sight |

Three of these carry a decision worth not relitigating:

- **Slice 12 did NOT re-baseline the pinned hashes**, and the entry used to say it must. Gating
  on CONSEQUENCE — a map exists *and* some unit cannot cross some cell — makes an inert map
  bit-identical to no map. Prefer that over re-baselining whenever a feature can be a genuine
  no-op; it is now the house discipline across six slices.
- **Slice 13's opaque wall is FASTER**, which was not the expected direction. A transparent wall
  halts both armies in a long attritional exchange; an opaque one denies acquisition, so they
  keep moving and settle it in one fight. Cover changes what a chokepoint MEANS.
- **Slice 4: a shared prototype cannot be faction-scoped and must not pretend to be.** It is
  used by every faction that did not fork it, so one reference array cannot mean different
  things to each. That case is LINTED, not resolved — the honest split.

## Asset model (slices 14-19)

| # | Slice | Outcome |
|---|---|---|
| 14 | A pack carries its art; every reference resolves | `AssetIndex` reads `.big` tables of contents directly — 25,346 assets on this install — and refuses a model name that resolves to no file |
| 15 | Derive art profiles from the mesh | **STRUCK on measurement.** Derived `height` lands within ±20% for 26% of meshes. Gameplay geometry is a designer-chosen COLLISION size, not a property of the art. `zhasset artvalidate` is the standing proof |
| 16 | Place objects in the emitted `.map` | confirmed in a match, and it is what made the LOS question testable |
| 17 | Author an `FXList` and `ParticleSystem` | the last category where adopting retail art was structurally required |
| 18 | A pack has a voice | 8,638 shipped audio files, 1,049.6 MB — and the plurality format (5,157 of 8,582 waves) is mono 22,050/16-bit PCM, so authoring needs a 44-byte header and no encoder |
| 19 | Author the ground | a map's shape was already ours while its surface stayed EA's |

**The line that governs the asset model, measured and unmoved: a pack on wholly authored art
borrows 8 retail names, and all 8 are the `zh.sides` HUD.** Content is ours; UI furniture is
borrowed on purpose.

**Slice 18 ended on a negative that is worth more than the feature.** The emission is real
and additive — `Data/INI/SoundEffects/` is scanned, our blocks parse, and an invalid enum value in
them provably kills the process — but **the engine we test on cannot make a sound from any input**:
GeneralsX replaced Miles with OpenAL and its manager allocates sources and never buffers a sample.
Two probes run back to back also separated a pair of failures that had been conflated: a misspelt
KEY is silently dropped and boots clean, while a bad enum VALUE throws and names the file. So "it
booted" proves every enum literal and proves nothing about a field name — recorded as
`zh-authoring` rules 18 and 19.

**Slice 15 is the important one to remember**, because it closes a door: the free-art pool
cannot be made safe by inference. Two safe paths, and inventing a third is what the measurement
rules out — adopt a mesh retail USES and inherit its measured profile, or AUTHOR one and declare
the profile as it is written. "2,928 models are free to adopt" stays true about rendering and
false about behaviour.

## Design decisions this roadmap owed an answer to

- **Flag changes re-select loadouts** — `LoadoutSystem` runs between production and cooldowns.
  See the system-order invariant in `CLAUDE.md`.
- **One total resolution order** — three mechanisms, not four. An entity-level `extends` was
  never built and deliberately will not be: a unit never inherits from a unit, because faction
  `modify` plus layering already express every case and a third inheritance axis is exactly the
  complexity that order exists to bound.

## Deliberately out, on evidence

Terrain types as content (ZH's are inert — 291 blocks, one call site, a flag nothing sets);
prerequisite OR-expressions; slope-modulated speed; elevation damage and range; chokepoints as a
content type; a DDS writer (TGA ships and needs no compressor); a `.big` writer (loose files
shadow archives — that IS the mod mechanism); deriving art profiles from meshes.

*This list used to include "art/FX/audio" wholesale. Slices 14-19 overtook it in full: art, FX,
terrain and audio are all authored now. **The asset model is complete.***
