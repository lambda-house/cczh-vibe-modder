# The extracted model

Fourth study, and the one that is a **contract rather than a finding**. `ZERO-HOUR-ANATOMY.md`
measures the content model, `ZERO-HOUR-ASSETS.md` the shipped art, `ZERO-HOUR-MAPS.md` the
terrain format. This one says what `tools/zhasset` *produces*, so a skill can rely on the shape
instead of re-deriving the corpus every time it wants to answer a question.

Everything below is emitted as JSON with `--json`. The human-readable form is a summary and is
not a stable interface; the JSON is.

**Provenance rule, unchanged: ship the extractor, never the extract.** `reference/` is generated
locally and gitignored. EA's GPL release covers the engine source; `Data/INI`, the archives and
the art stay theirs. Nothing here writes retail bytes anywhere — the archive readers take
*names* and, for meshes, geometry counts.

## The loop this exists to serve

```
nearest --like X    which existing thing to start from
dossier <object>    everything it is made of, transitively
techtree <faction>  where it sits, what gates it, what it unlocks
  ... edit ...
rts compile         emit it back as Data/INI + art the engine loads
```

Each step answers a question the previous one raises, and each is a separate command because
each is separately useful.

## `catalogue` — what is in the game

One pass over the corpus. 15 factions, 2,102 objects, 363 weapons, 53 armor sets, 182
locomotors, 81 upgrades, 96 sciences, 79 powers, 247 terrain types.

```
{ factions: { <id>: {side, baseSide, playable, startingBuilding, intrinsicSciences} },
  rosterBySide: { <side>: [<object>...] },
  objects:  { <name>: {file, reskin, side, buildCost, buildTime, kindOf[], isStructure,
                       commandSet, vision, lines} },
  weapons:  { <name>: {PrimaryDamage, DamageType, AttackRange, DelayBetweenShots,
                       PrimaryDamageRadius, ClipSize, ClipReloadTime} },
  locomotors, upgrades, sciences, specialPowers, terrain, armor }
```

Everything else joins against this, so it is the one file worth generating first.

## `dossier <object>` — one object, complete

The full definition plus the **transitive asset closure**. Twenty keys; the ones that are not
self-evident:

| Key | What it is |
|---|---|
| `drawStates[]` | every `ModelConditionState`: `{condition, model, animation, turret, weaponLaunchBone, weaponMuzzleFlash, …}` |
| `assets{}` | model name → `{textures[], animations[]}`, read from **inside** the `.w3d` |
| `icons{}` | `selectPortrait` / `buttonImage` → `{image, texture, coords, textureWidth, textureHeight}` |
| `death[]` | the FX/OCL names an object dies with |
| `deathClosure{}` | those names EXPANDED: `{textures[], models[], sounds[], particles[], missing[]}` |
| `verbatim` | the raw INI block, with `--verbatim` |

Three things here are not obvious and each cost a bug to learn:

**Building assembly is not a separate asset.** It is `ModelConditionState`s.
`AmericaWarFactory` has **86 states — 36 construction** (`AWAITING_CONSTRUCTION`,
`PARTIALLY_CONSTRUCTED`, `ACTIVELY_BEING_CONSTRUCTED`) **and 9 door animations**, across 36
models and 155 distinct textures. Anything reporting only the default model misses the entire
build-up, and the doors are why a production queue can fill and never drain.

**An object's INI never names its textures.** They are `TEXTURE_NAME` chunks inside the `.w3d`,
so the only way to know what a mesh paints itself with is to read the mesh. This is done
straight out of the archive without extracting it.

**Icons are a two-hop lookup.** `SelectPortrait` → a `MappedImage` name → a *rectangle in a
shared texture page*. A `MappedImage` whose texture is absent renders as a blank tile with no
error, which is why every hop reports `MISSING` rather than omitting.

## `techtree [faction]` — the graph ZH never states

Joined across five files, none of which names the graph: `PlayerTemplate` roots, `Object`
command sets and `Prerequisites`, `CommandSet` slots, `CommandButton` verbs, `Science`/`Rank`.

```
{ ranks: [ {rank, skillPointsNeeded, purchasePointsGranted, sciencesGranted[]} ],
  factions: { <id>: { side, playable, startingBuilding, intrinsicSciences[],
                      depth:    { <object>: <tier> },
                      producers:{ <object>: {builds[], constructs[], upgrades[],
                                             powers[], sciences[]} },
                      superweapons: { <power>: {fromObject, science[], requiredScience,
                                                reloadTime, enum} },
                      sciences: { <science>: {...catalogue fields, reachable} },
                      prerequisites: { <object>: {objects[[]], sciences[[]]} } } } }
```

**THE VERB IS THE EDGE**, and there are four that matter:

| Verb | Count | Edge |
|---|---|---|
| `UNIT_BUILD` | 259 | producer → unit |
| **`DOZER_CONSTRUCT`** | 192 | dozer → **structure** |
| **`PURCHASE_SCIENCE`** | 82 | **the experience tree** |
| `PLAYER_UPGRADE` / `OBJECT_UPGRADE` | 58 / 37 | per-player / per-object |

A tree built from `UNIT_BUILD` alone stops dead at the dozer with two tiers, because structures
are what carry the next tier's command sets. Some lines carry a trailing space; normalise the
verb before comparing.

**The build tree and the promotion tree are ONE graph.** An `Object`'s `Prerequisites` can
require a `Science`, and a `SPECIAL_POWER` button can require one — so superweapons and the
rank ladder are reached from different roots of the same structure.

`depth` is **computed, not read**: no file states it. BFS from the starting building is the only
thing that says how far into a tree a unit sits.

Measured: every one of the 13 playable factions resolves to **4 tiers**, 26–37 objects, 13–30
upgrades, 14–24 reachable sciences, 8–18 superweapons. The ladder is 5 ranks at
0/800/1500/2500/5000 granting 1/1/1/1/3 = **7 purchase points for an entire game**, which is why
the science tree's shape matters more than its size.

## `nearest` — which existing thing to clone

`--like <object>`, or a spec: `--role`, `--cost`, `--damage`, `--range`, `--speed`, `--side`.

```
{ target, matches: [ {object, score, axes{role,cost,damage,range,speed,dtype},
                      cost, damage, range, speed, side, role[]} ] }
```

**Substitutability is ROLE, not cost and not stats.** A 900-cost tank and a 900-cost jet share a
number and nothing else. Role is a Jaccard overlap on `KindOf` weighted 3× against every other
axis, with the entries that say nothing about role stripped — `PRELOAD`, `SCORE`, `SELECTABLE`,
`CAN_CAST_REFLECTIONS` are on almost everything and leaving them in collapses the ranking toward
cost.

Numeric axes compare as **ratios, not differences**: 100 vs 200 damage is the same distance as
1000 vs 2000. `axes` is reported per-axis because a caller needs to know *why* — "same role, 18%
cheaper, identical range" is actionable, "score 0.026" is not.

`CINE_` cutscene clones are excluded by default: byte-identical copies that rank first every
time, where cloning a clone inherits nothing the original lacks.

## `fx <name>` — an explosion, expanded

```
{ tree:    { name, kind: FXList|ParticleSystem|ObjectCreationList, ... },
  closure: { textures[], models[], sounds[], particles[], missing[] } }
```

**Recurses in three places**, and missing any one truncates the closure: `FXListAtBonePos` nests
another FXList; a ParticleSystem's `SlaveSystem` / `PerParticleAttachedSystem` name further
systems; an OCL's `CreateDebris ModelNames` and `CreateObject ObjectNames` are meshes and
objects in their own right. Cycles exist — carry a seen-set.

Measured: 429 FXLists, 1,087 ParticleSystems, 294 OCLs, 179 slave links — and **1,087 particle
systems draw on only 81 distinct textures.** That reuse ratio is the case for authoring FX being
cheap: 81 small additive sprites cover every explosion in the game.

## What the model deliberately does NOT contain

Both are negative results, both measured, and both are load-bearing — a caller that assumes
otherwise will be wrong in a way nothing reports.

**Art profiles cannot be derived from the mesh** (`zhasset artvalidate` is the standing proof).
Gameplay geometry is a designer-chosen *collision* size, not a property of the art: derived
`majorRadius` lands within ±20% for 61% of meshes and `height` for 26%. The silhouette includes
barrels and wings the footprint excludes. So the 2,928 adoptable meshes are free about
*rendering* and not about *behaviour*, and the two safe paths are to adopt a mesh retail **uses**
(inheriting its measured profile) or to **author** one (which declares its profile as written).

**Line of sight does not transfer.** We model cover; ZH does not check firing LOS anywhere —
`Weapon::isClearFiringLineOfSightTerrain` is commented out at its only call site, and terrain
LOS survives solely in the pathfinder's choice of firing position. Confirmed in a real match: a
unit killed a target through a cliff its own pathfinder will not cross, from a standstill.

## Regenerating

```
zhasset catalogue        # first: everything else joins against it
zhasset artprofile       # measured mesh contracts, for adopted art
```

Both write to `reference/`, which is gitignored. Absence is handled everywhere: a command that
needs a missing input says so and exits, and checks that cannot run report as skipped rather
than as failed.
