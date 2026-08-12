# rts-skeleton — Claude Code project memory

**Goal: an AI modding engine for Command & Conquer: Generals Zero Hour.** An agent,
using skills and templates, authors content — factions, generals, units, armies —
which is validated and measured here and then **compiled to ZH `Data/INI` and run on
the real engine**. ZH supplies the runtime and the packaging; we supply the authoring
model, the validation, and the measurement.

The division of labour, and the reason each half exists:

- **ZH is the runtime.** `~/GeneralsX/GeneralsZH` (built native arm64) executes the
  output. Packaging is already solved: loose files shadow the `.big` archives — that is
  the engine's own mod mechanism, demonstrated live parsing 39,771 lines of loose INI
  with no archive present. A `.big` writer is a convenience, never a blocker.
- **Our sim is the pre-flight.** Lint, counter matrix and skirmish measure a pack in
  seconds — what playtesting costs hours. Determinism is what makes that measurement
  reproducible and attributable to a `contentHash`.
- **The extracted model is the vocabulary.** Their `FieldParse` tables are the target
  schema, so content and knowledge transfer in both directions.

Two consequences that shape every slice:

1. **Their caps are our caps.** 128 upgrade bits (82 used, 46 free), 14 usable command
   slots, 3 weapon slots, 4 veterancy levels, 38 damage types, a closed SpecialPowerType
   enum, `NUM_GENERALS 12`. A pack valid here but over their limits produces a mod that
   will not load. `rts lint --target zh` must enforce them.
2. **Emittable ≠ simulated.** A unit needs a `CommandButton`, a numbered `CommandSet`
   slot and string labels or it is invisible in-game however correct its stats are. We
   must EMIT fields we will never simulate. Coverage therefore has two axes; conflating
   them ships packs that measure beautifully and never appear.

**Divergence is the standing risk.** Two engines now compute the same battle. Where our
model and theirs disagree — additive-in-excess bonuses, `ceil()` tick quantisation, the
two-band splash step — our numbers stop being predictions. Everything copied verbatim
from their source was for exactly this reason, and it is now testable rather than
theoretical.

**Art is deferred, not solved.** 32.9% of shipped models (2,928) are referenced by
nothing and are free to adopt, so new content is playable on existing art immediately.
That is a development accelerator and a personal-use path — **not** a distribution plan.

Deterministic, data-driven RTS sim core (C&C Generals mold). Headless sim +
content model + balancing harness + Godot inspection shell. Full context: README.md.

## Reference material (on disk, no retail assets needed)

Two measured studies justify the roadmap below; every slice cites a number from one
of them.

- `docs/ZERO-HOUR-ANATOMY.md` — the **content model**, measured from the community corpus.
- `docs/ZERO-HOUR-ASSETS.md` — the **shipped assets** and what can be replaced, measured
  from retail. Where the two disagree on a count, **retail wins** (the 104p patch adds
  content: 363 weapons vs 420, 53 armor sets vs 57, 96 sciences vs 100).
- `docs/ZERO-HOUR-MAPS.md` — the **terrain format**, decoded from all 150 shipped maps. The
  container, the eight chunks, the two scale constants and the slope at which their engine
  decides a cell is a cliff.
- `docs/ZERO-HOUR-MODEL.md` — the **extracted model**: what `tools/zhasset` produces, as a
  CONTRACT rather than a finding, so a skill can rely on the shape instead of re-deriving the
  corpus. The JSON shapes, the joins that are not obvious, and the two things the model
  deliberately does not contain because they were measured and found underivable.

Sources on disk:

- `~/work/oss/CnC_Generals_Zero_Hour` — EA's GPL v3 source. **Schema authority**:
  the `FieldParse` tables enumerate every legal key of every content type.
- `~/work/oss/zh-retail/Data/INI` — retail INI, 118 files / 538,362 lines, extracted
  from `INIZH.big`. **Authoritative for planning.**
- `~/work/oss/GeneralsGamePatch/.../Data/INI` — the 104p community corpus, 581,467 lines.
- `~/GeneralsX/GeneralsZH` — retail install, **running natively on arm64**. 35 `.big`
  archives in two layers (ZH is a delta over base Generals in `ZH_Generals/`):
  26,264 files, 2.23 GB, 9,000 models, 7,891 textures.
  `~/GeneralsX/content-mode.sh stock|104p|status` toggles the loose-file overlay.
- `scratchpad/bigtool.py` — `.big` reader/extractor (`list|tree|names|extract`).

**Legal, settled: the GPL covers the engine source only.** `Data/INI`, the `.big`
archives, `generals.csf`, the maps and all art remain EA-copyrighted. No retail-derived
content pack ever ships from this project. If we want the combat-core acceptance test,
build a ZH-*shaped* pack from first principles and assert *qualitative* counter
relationships, not retail values. Do not relitigate this.

Quote measured numbers from these docs rather than reasoning from general RTS knowledge —
the corpus repeatedly contradicts intuition (terrain is gameplay-dead; bonuses are
additive-in-excess, not multiplicative; the generals are forks, not diffs; and art is
110:1 of the bytes but 0% of the simulation).

## Modding the retail game

Emitting new content and MODIFYING retail content are different problems with different
mechanics, and the rules were each paid for with a crash, a hang or a silent misfire. **The GPL
tree is schema authority; the running GeneralsX build is loading authority; they diverge** — so
never conclude a load path works because a file parsed. Carry a witness field whose effect is
unmissable, and check the boot log's `loadDirectory` lines rather than the source.

The 17 measured rules — override channels, what is additive, which shared leaves break when
patched, the emit-time silent failures — and how to drive the game to verify a change, are in
the **`zh-authoring`** skill:
`references/engine-rules.md` and `references/driving.md`.

## Build / verify

- Build: `dotnet build -c Release` (needs .NET 8 SDK; zero NuGet deps by design)
- Run: `dotnet bin/Release/net8.0/rts.dll <verb>` — verbs: `lint`, `duel`, `matrix`,
  `econ`, `faction`, `replay`
- **`./e2e.sh` runs the whole gate below plus the demos and the shell check.** Prefer it.
  **Add a gate by APPENDING a `gate "title"` call — never write the number.** The count is
  derived from the call sites, so two branches can each add one without touching each other's
  lines; the old literal `gate N/22` put the denominator on all 22 lines and made every
  parallel slice conflict across the whole file.
- **After ANY change under Core/, Content/, or Runtime/, run all four and treat failure as a broken build:**
  1. `rts lint`
  2. `rts replay --a crusader --b battlemaster --seed 7` → must print `DETERMINISM OK`
  3. `rts duel --a crusader --b technical --n 20 --seed 42` (smoke)
  4. `rts econ --a "technical*" --b "war_factory,crusader*" --n 20 --seed 42`
     → must print `determinism: OK` (covers the production system's replay surface)
- If `godot/` is touched, also run `./e2e.sh` — it asserts the shell reaches the
  same final state hashes as the harness. A mismatch means presentation has
  leaked into the sim; treat it as a determinism bug, not a rendering bug.

## Hard invariants — never violate, never "optimize away"

- **No `float`/`double` in sim state or sim logic.** All sim math uses `Fix64`
  (Q32.32). Doubles exist only at the content-load boundary
  (`Fix64.FromDoubleAtLoadBoundary`) and in display formatting
  (`ToDoubleForDisplay`). Introducing a float into Runtime/ is a determinism
  bug even if all tests pass on this machine.
- **System order in `Sim.Step` is part of the replay contract:** commands →
  production/economy → **garrison/capture** → **loadout/stat re-resolve** → cooldowns →
  targeting → movement → combat. The holding phase is pinned ahead of loadout because
  capture flips a structure's TEAM, and team is read by targeting (who is an enemy), by
  production (whose factory) and by the defeat test — settle ownership before anything
  asks whose it is. The loadout phase is not optional bookkeeping: a flag gained
  this tick re-selects a conditional variant, which changes weapon and armor class —
  i.e. stats. Without its own pinned phase, whether a shot used the old or new weapon
  would depend on which system ran first, intermittently. Do not
  reorder, merge, or parallelize across units without an explicit design
  decision. Within production: income accrual, then research queue, then unit
  queue, teams in ascending index — research has funding priority over units.
- **Iteration is always ascending unit index.** No dictionary/hash-order
  iteration over sim state, no LINQ ordering that isn't explicitly `Ordinal`.
  **This extends to pack loading:** when layered packs land (slice 1), a directory
  scan must be sorted `Ordinal` by path before loading. ZH's `INI::loadDirectory`
  sorts for exactly this reason — its comment reads *"This keeps things the same
  between machines in a network game."* A directory-scan loader is the most likely
  future source of hash-order nondeterminism in this codebase.
- **A faction variant is a COMPLETE clone, never a hand-listed subset of fields.**
  `UnitProto.CloneForFaction` is memberwise on purpose, so a field added to `UnitProto`
  is carried automatically. *This was a live bug:* the old object initializer copied 9 of
  20 fields, so patching nothing but a war factory's cost dropped its `KindOf` and the
  building stopped being a structure — `rts compile` emitted it with a Locomotor. `Rules`,
  conditional `Variants`, `EnergyProduction` and `BirthFlags` vanished the same way. If you
  add a mutable array field, deep-copy it there; do not revert to an explicit list.
  Note the ordering trap that made it worse: rules compile AFTER `ResolveFactions`, so the
  clone sees an empty rule array and rules must be handed to variants in the rules pass.
- **Content resolves in ONE total order, three stages, each exactly once.** `PackStack` is
  the authoritative statement of it; this is the summary.
  1. **LAYER** — packs fold in ordinal order, per-key last-wins, `null` REMOVES. Produces one
     composed DTO and knows nothing of units or factions.
  2. **DEFAULT** — the composed `defaults` block fills every unit field left unstated.
  3. **INHERIT + ROSTER** — faction `extends` flattens parents before children; within one
     faction, strictly `remove` → `add` → `modify`.

  **There is no entity-level `extends` and there will not be one.** A unit never inherits from
  a unit. Faction `modify` plus layering already express every case, and a third inheritance
  axis is the complexity this order exists to bound.

  Each stage completing before the next is what makes the awkward questions answerable:
  a `modify` is a delta on whatever the unit ends up being *after* layering, so a mod that
  retunes a unit keeps every faction's tweaks on top of the new definition. **"A pack patch
  removes what a faction modify replaced" is therefore not a merge conflict at all** — the
  removal lands in stage 1, so by stage 3 the name does not exist and every reference is an
  ordinary dangling reference, reported with the layer that took it away. The corollary that
  surprises authors: a later layer cannot *amend* a faction's `modify` block, because factions
  are keyed entries and it replaces the whole faction. Layering is for deltas BETWEEN packs;
  faction `extends` is for deltas WITHIN one.

  The two intra-faction adjacencies each buy something. **remove before add** is the DE-FORK
  idiom — drop the parent's variant, re-adopt the shared prototype; add-first would make it a
  duplicate-key error with no way to say "inherit everything except this fork". **remove
  before modify** makes the contradictory pair a deterministic error rather than a patch that
  is computed, allocated and then silently discarded.
- **EVERY hand-written merge must be TOTAL over its DTO, and all of them are checked by
  reflection on every load.** *This was a live bug of exactly the `CloneForFaction` shape:*
  `defaults` was missing from the merge's object initializer, so every multi-layer stack
  silently reverted to the compiler's built-ins. Nothing failed; the numbers were just wrong.
  `AssertMergeIsTotal` now covers `ContentPackDto`, `ZhTargetDto` and `UnitDefaultsDto` —
  all three are merged by object initializers of the same shape, and guarding only the root
  left the other two exposed. Only WRITABLE properties count: a computed one (`ZhTargetDto.Scale`)
  is derived from merged state, not state. e2e probes each type generatively — add a property,
  assert the load is refused, put it back.
  *The hazard this actually buys off is PARALLEL WORK.* Two branches each adding one field to
  `ZhTargetDto` is the likeliest way to lose one: git merges two added initializer lines
  without complaint, and a dropped line reverts that field in every layered stack, silently.
- **In a layered block, ABSENT is not the same as DEFAULT.** A non-nullable field with a C#
  initializer cannot tell "this layer omitted it" from "this layer set it to the default", so
  a mod saying nothing would overwrite the base pack's value with the compiler's. `defaults`,
  `lint`, `ranks` and `zh.worldScale` are therefore nullable, and the root fallbacks live in
  exactly one place (`UnitDefaultsDto.Root`). Any new pack-wide block must be nullable too.
- **Reference resolution is faction-scoped for variants and global for everything else.**
  `ContentDb.ResolveUnitRef` goes through the owning faction's *resolved roster* — never by
  mangling a name into `fid/ref`, because inheritance means only the roster knows which
  fork applies. A shared prototype must NOT be scoped: it is used by every faction that did
  not fork it, so one reference array cannot mean different things to each. That case is
  reported by lint. Retail proves the cost of getting this wrong: 88 bad references / 68
  broken definitions shipped, and `Prerequisites` accounts for none of them — 64 are
  references reached through a shared intermediary.
- **Schema authority is their source; LOADING behaviour is the running engine.** The two
  differ. EA's `GameEngine.cpp` passes no directory to `TheScienceStore`, yet the build we
  target logs `loadDirectory('Data\INI\Science')`. Before assuming a content type can be
  emitted additively, check the boot log's `loadDirectory` lines — not the source.
  The one type that genuinely cannot be additive is `Rank`: its blocks are NUMBERED, not
  named, so emitting a ladder overwrites retail's instead of extending it.
- **Never invent an enum value, and never confirm one by substring.** Every literal
  `ZhCompiler` emits must be copied from a retail block that demonstrably loads, and closed
  enums are checked against their C++ name table, not against a grep of the INI. Five have
  bitten us: `NO_Z_MOTION` (real: `NO_Z_MOTIVE_FORCE`), `Options = CANCELABLE`,
  `IS_PREREQUISITE`, `SpreadFormation = 32` (a BOOL), and `GARRISONABLE` — which greps as
  present only because it is a substring of `GARRISONABLE_UNTIL_DESTROYED`. Each was a hard
  load error found only by booting the engine.
- **Ties break deterministically** (e.g. targeting: `(distSq, unitIdx)`).
  Every new comparison needs a total order.
- **Terrain is CONTENT; a route is STATE.** The passability grid never changes during a
  match, so it folds into `contentHash` and never into the state hash. A unit's current
  route is the opposite: *when* it was planned decides which way it goes round an obstacle,
  so `PathGoalCell`, the cursor and the live tail of waypoints are all hashed (gated on
  `HasPassability`). Treating a path as a cache is the mistake that makes two runs diverge
  only under load, when one of them happens to replan a tick later.
- **A pathfinder is ADVICE; the step is where passability is ENFORCED.** *This was live for
  the first hour of the slice:* A* was correct, routes were sensible, and a SOLID wall with
  no gap in it changed the outcome of a battle by nothing whatsoever — because any unit whose
  search failed fell back to walking straight, and straight went through the wall. Every
  position change must be refused by terrain independently of whether anything planned it.
  Blocked movement SLIDES (full step, then x alone, then y alone, fixed order) so a unit
  brushing a wall keeps fighting instead of freezing for reasons no state hash explains.
- **World→cell is a SHIFT, never a division**, so `cellSize` must be a power of two and lint
  rejects anything else. Checked on the Fix64 *raw*, not the authored double: 0.1 looks like
  a fine number, is not representable, and would leave the grid and the mover disagreeing
  about which cell a unit is in. An arithmetic shift right also floors toward negative
  infinity, which is the rounding a centred grid needs — truncation folds -0 into +0 and puts
  a seam down the middle of every map.
- **A diagonal step needs BOTH orthogonal cells**, in the pathfinder and in the line test
  alike. Without it a unit squeezes through the corner where two blocked cells touch — a wall
  with no gap that units walk through anyway. The line test is a supercover walk for the same
  reason; Bresenham slips between diagonal neighbours.
- **Map rows are oriented to the RENDERER, not to a compass.** Row 0 is the lowest world y
  because the shell draws +y downward, as every 2D canvas does. The alternative reads as
  north-up in the JSON and appears vertically mirrored in play — a bug invisible from either
  the file or the screen alone.
- **Sight and passability are INDEPENDENT, and the pair is the model.** Water stops ground
  movement and hides nothing; a cliff does both. `HasPassability` and `HasLineOfSight` are
  therefore separate opt-in flags — a map of nothing but river changes movement and must not
  perturb a single shot. **The diagonal rule INVERTS between them:** movement needs BOTH
  orthogonal cells clear to cut a corner because a body has width, while sight needs only ONE
  because a line has none. Using the movement rule for sight makes every diagonal corner
  opaque and hands cover to positions that have not earned it.
  *Endpoints are exempt from the sight walk: a unit standing on blocking terrain — spawned
  there, or a structure whose own footprint is a plateau — must still see and be seen, or it
  is blind and invulnerable at once.*
- **An authored map is CHECKED BY A SECOND IMPLEMENTATION.** `Content/ZhMapWriter.cs` writes
  and `tools/zhasset map` reads, and the reader earns that job by decoding all 150 shipped
  maps before it grades ours. A writer checked by its own reader proves only that the two
  agree. e2e asserts the two-sided result: the same authored shapes that measure 27.4s and a
  timeout draw here come out CONNECTED and SEPARATED under their cliff rule there.
- **New randomness gets its own `Pcg32` stream id** (next free integer in
  `Sim`'s constructor). Never share a stream between systems; never reuse an id.
- **Every new sim-state field must be added to the state hash**
  (`World.HashInto` / `Sim.HashState`) in a fixed position.
- **Prototype identity is name-derived, never ordinal.** `World.HashInto` folds in
  `UnitProto.StableId` (FNV-1a64 of the id), *not* `ProtoIdx`. `ProtoIdx` is a
  runtime array index and must never reach a hash, a replay, or the wire. This is
  what makes content growth safe: adding a unit that sorts anywhere cannot change
  what an existing replay means. Renaming a unit legitimately does.
  *This was a real bug, not a hypothetical:* the earlier "append after the base
  units" rule was enforced by alphabetical luck, and `laser_crusader` silently
  renumbered `ranger`/`technical` — `technical vs ranger` moved
  `c773f31f6888b7c8` → `2cdfb299cdae6006` while the two pinned hashes stayed put,
  because they only observe units *alive in the final state*. Pinned hashes are a
  weak guard for this class of bug; `e2e.sh` now also runs a **generative** check
  (inject an unreferenced unit named to sort before and after everything, assert
  every matchup is unchanged). Keep that check — it is the one that actually holds.
- **Sim core stays engine- and package-agnostic**: no NuGet packages, no
  engine types, no wall clock, no I/O inside Core/, Content/ (post-load),
  Runtime/, Harness/. The root `NuGet.config` clears all package sources to
  enforce this; `godot/NuGet.config` re-adds the source for presentation only.
  Never relax the root one — scope an override to the consuming subtree.
- **Presentation reads `Snapshot`, writes `Sim.Enqueue`, and nothing else.**
  Snapshots are immutable, hand out doubles at the display boundary, and can
  never flow back. `Enqueue` rejects commands stamped for the current or a past
  tick, so input is always scheduled ahead — the rule lockstep needs. Do not add
  a "just this once" mutable accessor for the renderer.
- Content lives in `content/*.json`; behavior changes via data edits are
  preferred over code changes. `contentHash` in tool output is the provenance
  anchor — quote it when reporting balance numbers.

## Architecture map

- `Core/` — Fix64, Pcg32 (streamed RNG), Fnv1a64 (state/content hashing)
- `Content/` — JSON schema DTOs; `ContentDb` compiles + lints packs, and
  resolves faction inheritance (`extends`/`add`/`remove`/`modify`) into flat
  rosters plus faction-local unit variants named `faction/unit`
- `Runtime/` — `Command` (replay = contentHash + seed + command log),
  `World` (flat archetype, tombstone slots, per-team `TeamState`: money/finite
  supply pool/income, research + unit queues, researched flags — all hashed),
  `StatSheet` (modifier algebra: `(base + Σadd) × Πmul`), `Sim` (tick loop,
  production system, hash trace)
  `Snapshot` (immutable read seam for renderers; doubles, never flows back)
  `Passability` (terrain grid: one byte/cell, surface in the low 3 bits, world→cell by
  shift) + `PathFinder` (integer 8-way A*, total order on the open set, capped)
- `Harness/` — duel series, pairwise counter matrix, build-order econ
  scenarios (`RunEconSeries`; specs like `"war_factory,crusader*"`),
  determinism verification
- `Program.cs` — CLI verbs; `--json` output is the future MCP tool seam
- `godot/` — Godot 4 presentation shell, its own csproj referencing the sim.
  `SimHost` (fixed-step accumulator + snapshot pair, no Godot types) and
  `Main` (Godot Node2D: draws, HUD, input). Excluded from the sim csproj.

## Known simplifications (intentional; see README for replacement plans)

Flat archetype not ECS; tombstones not generational handles; cumulative-only modifier
stacking; economy is abstract (two team queues, income from a finite pool, cost deducted at
build start).
**Unit-unit collision is still absent and is a different thing from terrain**: units pass
through each other freely, so a chokepoint concentrates fire but never jams, and a
one-cell gap admits an army as fast as a ten-cell one. **Structures do not stamp the grid
either**, which is why "raze the wall to open the choke" is not a mechanic here — in ZH an
obstacle cell is DERIVED from the building standing on it (`CELL_OBSTACLE`, the value our
surface enum deliberately leaves a hole for).
*Struck as slices landed: layered packs (1), upgrades (6), structures and economic targets
(5), powers and the second currency (9), pathing and terrain (12). Do not re-add them.*

**Anti-pattern, never adopt: a presentation-only or partial-field variant type.** ZH's
`ObjectReskin` is restricted to ten appearance fields and cannot change Side, cost,
prerequisites, name, portrait or voice — so EA cloned 477 objects wholesale instead,
shipping 180,490 lines to express 5,385 lines of intent. **The restriction is what
created the duplication.** Our `modify` patch stays unrestricted, however tempting a
"just the visuals" variant looks once a renderer matures.
Don't "fix" these in passing — each is a deliberate slice boundary.

## Roadmap

**The sim roadmap is complete** (slices 0-13: name-derived identity, layered packs, schema
hygiene, modifier semantics, faction-scoped resolution, structures, upgrades, rules engine,
garrison/capture, powers and the science ladder, spatial index, MCP server, passability, line of
sight). **The asset roadmap is complete bar audio** (14 pack carries its art · 15 STRUCK, art
profiles cannot be derived · 16 map objects · 17 authored FX · 19 authored ground).

Open: **audio** — the last untouched category, and 0% of the simulation. And the **lockstep
session layer**, which was always last.

*What shipped and the measurement behind it: `docs/HISTORY.md`. What is still open:
`docs/ROADMAP.md`. What `tools/zhasset` produces: `docs/ZERO-HOUR-MODEL.md`.*

**Deliberately out, on evidence:** terrain types as content (ZH's are inert — 291 blocks, one
call site, a flag nothing sets); prerequisite OR-expressions; slope-modulated speed; elevation
damage and range; chokepoints as a content type; a DDS writer (TGA ships and needs no
compressor); a `.big` writer (loose files shadow archives — that IS the mod mechanism); deriving
art profiles from meshes (measured: `height` within 20% for 26% of meshes).
