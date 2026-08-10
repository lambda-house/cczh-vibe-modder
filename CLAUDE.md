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

## Modding the retail game: rules measured against the RUNNING engine

Emitting new content and MODIFYING retail content are different problems with different
mechanics. Each rule below cost a crash, a hang or a silent misfire to establish, and several
contradict what EA's source implies — the GPL tree is schema authority, the running GeneralsX
build is loading authority, and **they diverge**.

1. **`Data/INI/**` is ADD-NEW-NAMES ONLY.** Redeclaring an existing block there is a fatal
   load error: the engine aborts at 29 of 42 subsystems and never reaches its main loop.
   EA's source reads as though `INI_LOAD_OVERWRITE` would patch in place — both
   `ThingFactory::parseObjectDefinition` and `ControlBar::parseCommandSetDefinition` find the
   existing block and call `initFromINI` — but both guard the duplicate with `DEBUG_CRASH`,
   which is compiled into this build. Proven with a probe that redeclared
   `AmericaWarFactoryCommandSet` using nothing but a RETAIL button.
   *Consequence: a pack cannot extend a retail faction's menu. A pack is a NEW FACTION.*
2. **`<mapdir>/map.ini` is the ONLY override channel.** `GameLogic::loadMapINI` is the sole
   caller using `INI_LOAD_CREATE_OVERRIDES`, and it runs from `startNewGame` — so overrides
   reload on every match start, and iterating costs a match restart, not a game restart.
3. **Never declare a NEW name in `map.ini`.** It gets marked as an override, and
   `LocomotorStore::reset()` (Locomotor.cpp:573) then calls `m_locomotorTemplates.erase(it)`
   WITHOUT reassigning the iterator — the loop never advances and the game hangs on match
   teardown. `Science.cpp` writes the same loop correctly. Confirmed by sampling a beachballed
   process: 448 of 813 samples in that function.
4. **Never patch a shared leaf in place; it does not merely fail, it BREAKS the leaf.**
   `WeaponStore::newOverride` copies the parent then chains new->old via
   `friend_setNextTemplate`, while the store's lookup still returns the ORIGINAL — the override
   is reachable by nothing and the weapon goes inert. Overriding a Ranger's rifle damage took
   it from 5 to ZERO. `ThingFactory::newOverride` chains the other way
   (`child->setNextOverride`), which is why OBJECT overrides work.
   **The pattern that works: new leaf in `Data/INI`, then override the Object to point at it.**
   Proven for both weapons and locomotors.
5. **A `WeaponSet`/`ArmorSet` inside an override is TOTAL, not additive.**
   `ThingFactory::newOverride` calls `setCopiedFromDefault()`, so the first set parsed clears
   every inherited one. Restate all of them or none.
6. **Modules: `AddModule` to add, `ReplaceModule` to replace** — same module type, and a NEW
   UNIQUE tag. Syntax verbatim from their shipped `Maps/MD_USA01_CINE/map.ini`.
7. **`GameData` is NOT reachable from `Data/INI` on this build.** A file under
   `Data/INI/GameData/` parses cleanly and changes nothing — probed with
   `DefaultStartingCash`, which stayed at retail 10000. That directory scan is a GeneralsX
   addition; EA's `initSubsystem` passes only `Default/GameData.ini` and `GameData.ini` with
   no dirpath. **Never conclude a load path works because the file parsed without error** —
   carry a witness field whose effect is unmissable.

Retail's own 41 `map.ini` files corroborate the shape: `Object` 272, `Buildable` 64,
`CommandSet` 27, `Science` 26, `Locomotor` 18, `Upgrade` 14, `Weapon` 6, `SpecialPower` 4,
`ParticleSystem` 4 — and **zero** `GameData`. They override locomotors but never declare a new
one there (rule 3), and touch weapons only alongside an object override (rule 4).

8. **A new faction needs its own `ControlBarScheme` or it is selectable and UNPLAYABLE.**
   `setControlBarSchemeByPlayer` matches `CBScheme->m_side` against the player's Side and
   leaves the bar unset when nothing matches; retail ships schemes for America, China, GLA and
   Observer only. We emit a DELIBERATELY MINIMAL one — retail's are 87 lines of layout, and
   restating them would copy EA's content for no simulation benefit. Note the syntax: these
   fields take NO `=` sign (`Side hellfire`, not `Side = hellfire`).

9. **A parsing object is not a WORKING object.** Every gap below was silent — the file
   parses, the engine boots 42/42, and something simply never happens. Found by field-diffing
   our emitted objects against `AmericaWarFactory` and `AmericaTankCrusader`:

   | Missing | Symptom |
   |---|---|
   | `ProductionUpdate` | build buttons render, clicking does nothing |
   | `DefaultProductionExitUpdate` | no door to exit through |
   | `NumDoorAnimations` > 0 with no door states | queue fills and never drains |
   | `PhysicsBehavior` | unit is created but cannot move or be selected |
   | `BuildCompletion` | authored in our content, never emitted |
   | `FactoryExitWidth` | defaults to 0 |
   | `SelectPortrait` / `ButtonImage` | blank tiles in the control bar |
   | `ControlBarScheme` for the Side | faction selectable but has no command bar |

10. **ADOPTED ART HAS A SCHEMA.** "2,928 unreferenced models are free to adopt" is measured
   and true, and it is about RENDERING only. A mesh comes with a contract, and every clause
   of it fails SILENTLY — the pack lints, compiles, boots 42/42 and plays wrong:

   | Clause | Get it wrong and… |
   |---|---|
   | dimensions | units are created INSIDE the building and never seen |
   | bones (`WeaponLaunchBone`, `WeaponFireFXBone`) | shots come from the wrong place |
   | sub-objects (`WeaponMuzzleFlash`) | the flash mesh is never hidden — a permanent flame |
   | turret (`Turret` + `ControlledWeaponSlots`) | the unit never fires, with no error |
   | `AutoAcquireEnemiesWhenIdle` | the unit watches enemies walk past it |

   All four were hit adopting ONE model, `AVCrusader`. This belongs in "Divergence is the
   standing risk", not under "art is deferred": art is deferred as *authoring*, but adopting
   it is a live source of wrong behaviour today.

   *An adopted model therefore needs an ART PROFILE — geometry, bones, sub-objects, turret —
   and the profile is per-MESH, not per-unit. `zh.artRig` and `zh.turreted` are the stopgap;
   structure geometry is still hardcoded to AmericaWarFactory's numbers. The real fix is a
   catalogue of adoptable meshes with measured profiles, which `tools/zhasset` can generate.*

**VALIDATED END TO END, IN A REAL MATCH.** A faction authored here is selectable in the
skirmish dropdown, has a working command bar, builds a unit authored here, and that unit
rolls out of the factory and is selectable and commandable. Separately, a
RETAIL unit's damage, locomotion, scale and model are all modifiable from a pack. Both halves
are emitted by `rts compile`, not hand-written.

11. **`GameData` IS NOT MODDABLE BY DATA on this build — from either channel.** Closed, not
   open. A `GameData` block under `Data/INI/GameData/` parses and does nothing; the same block
   in `map.ini` also does nothing. Both were probed with `DefaultStartingCash = 999999`, which
   stayed at retail 10000 in both cases. That is why camera zoom could not be changed: every
   `MaxCameraHeight` we set was never in play. Everything downstream checked out — the wheel
   really does drive `View::zoomOut()` -> `setHeightAboveGround(+10)` clamped by
   `m_maxCameraHeight`, `calcCameraConstraints` clamps x/y only, and `GlobalData::reset()` pops
   to the original instance an in-place patch writes to. The wall is the LOAD PATH, and it is a
   GeneralsX divergence: EA's `initSubsystem` passes no dirpath for `TheWritableGlobalData`, and
   no retail `map.ini` overrides `GameData` either. Do not spend more time on it from content.

12. **The model format is READ AND WRITTEN, verified byte-identically.** W3D is IFF-style
   chunks and the taxonomy is EA's GPL header `Tools/WW3D/pluglib/w3d_file.h` — engine source,
   the half the GPL covers. `zhasset w3dround` re-emits every sample byte-for-byte (the high
   bit of `size` marks a container, so sizes are recomputed, not remembered), and
   `zhasset w3dbox` AUTHORS geometry — vertices, normals, per-face normals with plane
   distances, and UVs, all computed — into a template that supplies only material state.
   **Confirmed rendering in-game.** Units are 220-245 triangles, so re-modelling is a modest
   art task; the standalone zero-EA-art route is engineering, not aspiration.
   **Topology is arbitrary**, verified in-game: a 20-sided cylinder of 122 vertices / 80
   triangles written into a 24-vertex template renders as a cylinder. That needs
   `MESH_HEADER3`'s `NumTris` (offset 40) and `NumVertices` (offset 44) rewritten AND every
   per-vertex array resized in step — `VERTICES`, `VERTEX_NORMALS`, `VERTEX_SHADE_INDICES`,
   `STAGE_TEXCOORDS` — with `TRIANGLES` indexing into them. A mismatch between any of them
   renders as nothing at all. Bounds come from the actual vertices, not the requested size,
   which differs for anything that is not a box.
   *An authored mesh DECLARES its own art profile as it is written, so the catalogue is
   measured for adopted art and declared for authored art.*
   *Still not done, and not claimed: skinning (`VERTEX_INFLUENCES` against a `HIERARCHY`) and
   animation (`ANIMATION_CHANNEL`). So this authors static props and buildings, not characters.
   Textures are still the template's — UV layout is authored, the surface is not.*

## Build / verify

- Build: `dotnet build -c Release` (needs .NET 8 SDK; zero NuGet deps by design)
- Run: `dotnet bin/Release/net8.0/rts.dll <verb>` — verbs: `lint`, `duel`, `matrix`,
  `econ`, `faction`, `replay`
- **`./e2e.sh` runs the whole gate below plus the demos and the shell check.** Prefer it.
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
- `Harness/` — duel series, pairwise counter matrix, build-order econ
  scenarios (`RunEconSeries`; specs like `"war_factory,crusader*"`),
  determinism verification
- `Program.cs` — CLI verbs; `--json` output is the future MCP tool seam
- `godot/` — Godot 4 presentation shell, its own csproj referencing the sim.
  `SimHost` (fixed-step accumulator + snapshot pair, no Godot types) and
  `Main` (Godot Node2D: draws, HUD, input). Excluded from the sim csproj.

## Known simplifications (intentional; see README for replacement plans)

Flat archetype not ECS; tombstones not generational handles; no
collision/pathing; cumulative-only modifier stacking; no upgrade/general's-power
content types yet (the algebra exists, the vocabulary doesn't); content is one
monolithic game.json rather than layered packs; economy is abstract (two team queues, income from a
finite pool, cost deducted at build start, no power, no buildings — so rushes
can only spawn-camp, there are no economic targets).

**Anti-pattern, never adopt: a presentation-only or partial-field variant type.** ZH's
`ObjectReskin` is restricted to ten appearance fields and cannot change Side, cost,
prerequisites, name, portrait or voice — so EA cloned 477 objects wholesale instead,
shipping 180,490 lines to express 5,385 lines of intent. **The restriction is what
created the duplication.** Our `modify` patch stays unrestricted, however tempting a
"just the visuals" variant looks once a renderer matures.
Don't "fix" these in passing — each is a deliberate slice boundary.

## Roadmap order (evidence-first; every slice cites docs/ZERO-HOUR-ANATOMY.md)

Done: production/economy + build-order scenarios; Godot shell on snapshots;
base + delta faction layering (`rts faction`); **`rts compile --target zh`** —
packs leave the harness as additive `Data/INI` the retail engine loads (9 content
types, 42/42 subsystems, 0 parse errors), with `rts lint --target zh` reporting
caps / round-trip loss / unmappable mechanics as three separate failure kinds.

0. ~~**Name-derived prototype identity**~~ — done. `UnitProto.StableId` replaced
   `ProtoIdx` in the state hash; hashes re-pinned once, deliberately; `e2e.sh` gained
   a generative renumbering guard. This was a verified live bug, not a precaution.

1. ~~**Layered content packs + `rts diff`**~~ — done. N-layer `--mod` stacking, ordinal
   sorted; `contentHash` is an ordered hash over `(ordinal, packName, version, bytesHash)`
   plus resolved content; `rts diff` emits the delta taxonomy and duplication ratio.
   *Rationale: this is the authoring substrate. Everything below is a sim verb, and
   every one of them would otherwise be authored by hand-editing a single monolithic
   `game.json` — which is precisely the workflow this product exists to replace. It
   also converts every later slice into a measurable diff ("author a mod, measure the
   delta against base"), which is the loop the whole agent layer assumes. Do it first
   and the rest get cheaper to validate; do it last and you pay monolithic-edit cost
   every time. ZH's `map.ini` proves the semantics (`AddModule`/`RemoveModule`/
   `ReplaceModule`, subtractive list edits, override-creates-new-instance); what it
   lacks is provenance, which is exactly what `contentHash` supplies.*

2. ~~**Schema hygiene**~~ — done. `defaults` block, load-time duration→tick quantisation
   with `ceil()`, `unitsPerPurchase`, explicit `default` on `damageVsArmor` rows.
   *ZH's `DefaultThingTemplate` is 130 lines serving all 1,949 objects; without one,
   every new field is a breaking change to every existing unit.*
3. ~~**Modifier semantics**~~ — done. Additive-in-excess op; the upgrade condition is a
   named **flag set**, not one bit; flag-keyed **conditional variant blocks** that
   re-resolve in their own pinned `Sim.Step` phase.
   *Our algebra covers 4.5% of ZH's real upgrade references; selectors cover the rest.
   1,716 of ~2,300 real condition clauses are literally `None`, so the grammar is tiny.*
4. ~~**Faction-scoped reference resolution**~~ — done. A faction-local variant resolves its
   references through its OWN faction's resolved roster (never by mangling the name into
   `fid/ref` — inheritance means only the roster knows which fork applies), falling back to
   global. Covers object prerequisites and rule spawn targets.
   *Re-measured against retail: 88 bad references across 68 broken definitions, all nine
   multiplayer generals; `patch104p` ships fixes for several, so it is a tracked defect
   class. The docs previously said 67 — right grain, off by one; quote the grain.*
   **The corpus moved the target.** `Prerequisites` contributes ZERO of retail's defects;
   64 of 88 are references reached through a SHARED INTERMEDIARY (their `ObjectCreationList`
   `Transport`/`Payload`/`ObjectNames`). Our analogue is rule effects, not prerequisites.
   **A shared prototype cannot be scoped and must not pretend to be**: it is used by every
   faction that did not fork it, so one reference array cannot mean different things to
   each. That case is LINTED, not resolved — the honest split.
5. ~~**Structures**~~ — done. A unit with speed 0, `KindOf` role flags and
   `BuildCompletion`; prerequisites are objects, so razing a factory revokes buildability.
   *Retail splits 570 buildables into 379 units / 261 structures, and a minimum viable ZH
   faction is five objects of which THREE are buildings. We have no structure concept at
   all — which is exactly why "rushes can only spawn-camp, there are no economic targets"
   appears in Known simplifications. Smaller than upgrades, and it unblocks the harness's
   biggest known gap.*
6. ~~**Upgrades as a content type**~~ — done as tech nodes granting flags, which is their
   model: an upgrade carries no effect data, it sets a bit and a keyed loadout is selected.
   Compiles to a real `Upgrade` block + `WeaponSetUpgrade`/`ArmorUpgrade`/`MaxHealthUpgrade`.
   **Their one PLAYER_UPGRADE bit per object is a reported divergence** for any unit with
   more than one variant — the same restriction that forced 268 `ConflictsWith` lines on
   retail. Ours keeps a flag *set*; only the first variant survives compilation.
7. ~~**Rules engine** `{on, when, do}` (death event first) + **spawn lists**~~ — done.
   Closed event enum (death), closed filter (required flags), closed effect enum
   (spawn|grantMoney|damageInRadius|grantFlag) with open parameters. Cascades bounded at
   4 generations and CLAMPED, never thrown — a content bug degrades a battle, it does not
   crash a sweep. Spawn placement has its own `Pcg32` stream (id 2) so adding a wreck
   cannot shift a single combat roll. Next events to add: damaged, built, timer.
8. ~~**Garrison + capturable structures**~~ — done. `garrisonCapacity` on a structure, a
   `Garrison` command, occupants strictly untargetable, and `clearsGarrison` on a weapon as
   the only way damage reaches them — through the HOST, which is ZH's model. Plus
   `CAPTURABLE` + `AutoDepositUpdate`-style deposits, in a pinned `HoldingSystem` phase
   between production and loadout because capture flips a TEAM.
   *Terrain's tactical payoff with zero geometry, and the numbers are theirs: 17 of 363
   weapons (4.7%) set `AllowAttackGarrisonedBldgs`, `ContainMax` is 10 on 204 of 236
   buildings, the derrick is 200 cash per 12s on 2000 hitpoints.*
   **`GARRISONABLE` is NOT a KindOf** — their bit table has only `NO_GARRISON`,
   `STEALTH_GARRISON`, `GARRISONABLE_UNTIL_DESTROYED`. Garrisonability is the PRESENCE OF
   THE `GarrisonContain` MODULE. Emitting the invented name was a hard load error; a grep
   missed it because it is a substring of the real one. `e2e.sh` guards it.
   *Known limit, stated not hidden:* capture is proximity-based and `World.TeamCount` is 2,
   so there is no neutral owner — a capturable building is always someone's, and attackers
   stop at weapon range and shell it rather than walk onto it.
9. ~~**Powers + science/rank second currency**, `rts science-matrix`~~ — done. Skill points
   are earned by KILLING and by nothing else; ranks convert them to purchase points;
   sciences cost points and grant team flags, which the existing variant machinery already
   consumes. Activated powers are flag-gated, on a recharge, and fire the SAME closed effect
   vocabulary the death rules use — the payoff for having kept that vocabulary closed.
   *Their whole ladder is five `Rank.ini` blocks (0/800/1500/2500/5000 needed, 1/1/1/1/3
   granted = seven points a game) against 13-20 purchasable sciences per faction, every one
   costing exactly 1. The tree's shape is entirely in its prerequisites.*
   **Their default is that skill-point value IS experience value** — not one retail object
   overrides `SkillPointValue` — so one number feeds both ladders. But the PLAYER's ladder
   must not depend on the killer carrying a veterancy track, or whole rosters never rank up.
   **`rts science-matrix` plays every legal loadout from BOTH sides** and counts the
   science-owner's wins. Not politeness: ascending unit index makes team 0 resolve first, and
   an EMPTY loadout measured 11/12 for team 0 before the swap. A 50% baseline is what makes
   the column readable — read any mirror result here as a delta, never as an absolute.
10. ~~**Spatial index (uniform grid)**~~ — done. `Runtime/SpatialGrid.cs`: counting-sort
   buckets over Fix64 raw shifts, no floats and no hash containers, rebuilt **twice** a tick.
   Two, not one, because targeting must see units produced this tick while combat must see
   positions after this tick's movement — one rebuild would be stale for one of them, and a
   stale broad phase is a wrong answer, not a slow one.
   *Measured: ~2,000 units 6.9s → 2.1s; ~4,000 units 75.9s → 7.9s against the brute path.
   Below ~800 units it is a wash, which is the honest crossover for this content.*
   **`Query` returns candidates ASCENDING and that is load-bearing**, not tidiness: splash
   applies damage in visit order, deaths feed the rule cascade, and the cascade draws from a
   `Pcg32` stream — an unsorted broad phase would silently reorder RNG draws.
   **The cell size ADAPTS to the occupied extent.** The first version clamped outliers into
   edge cells: still correct, but at 4,000 units spread over 12,000 world units everything
   past the cap piled into two rows and it degenerated to worse-than-quadratic (28.8s).
   **`--brute` disables it so equivalence is TESTABLE** — e2e asserts identical state hashes
   at three scales plus a splash case. Note `--brute` is an equivalence ORACLE, not a fair
   performance baseline: it materialises the candidate list, so it is slower than the
   pre-slice nested scan was.
11. ~~**MCP server**~~ — done. `rts mcp` speaks JSON-RPC 2.0 over newline-delimited stdio
   with ZERO dependencies (System.Text.Json is BCL), so the root `NuGet.config`'s `<clear/>`
   stays untouched. Seven tools: `put_pack`, `validate_mod`, `run_matchup`,
   `query_counter_matrix`, `compare_packs`, `list_units`, `compile_pack`.
   **`put_pack` stores by `contentHash` and returns it**, so the loop is stateless for the
   agent — upload once, then reference by hash. Two agents authoring byte-identical content
   collide on the same entry, which is correct.
   **Every result carries `contentHash`.** A balance number without it is not reproducible,
   and reproducibility is the entire reason the core is deterministic.
   **Errors are repair instructions.** A bad prototype id returns "no prototype 'x'.
   Available: ..." rather than a `KeyNotFoundException` — the same principle as the harness
   reporting WHY a build queue stalled instead of showing a flat draw.
   *stdout is the protocol channel: diagnostics go to stderr, and one stray `Console.WriteLine`
   desyncs the transport.*
12. **Passability grid** — one byte/cell + 3-bit surface mask, then pathing. (XL)
   *Must deliberately re-baseline the pinned hashes. Chokepoints/flanking/water are
   emergent from passability, not authored.*
13. **Elevation as a boolean LOS gate only**. (M) *Conditional — defer until measurement
    shows 7 and 11 didn't move the counter matrix enough.*

Two design decisions this roadmap owes an answer to before the slice that needs them:
**(a)** ~~flag changes re-select loadouts~~ — ANSWERED: `LoadoutSystem` runs between
production and cooldowns; see the system-order invariant above. **(b)** `defaults` → entity `extends` → faction
`modify` → pack `patch` is four inheritance mechanisms; state one total resolution order
and what happens when a pack patch removes what a faction modify replaced.

Deliberately out, on evidence: terrain types as content (ZH's are inert — 291 blocks,
one call site, a flag nothing sets); art/FX/audio; prerequisite OR-expressions; slope-
modulated speed; elevation damage/range/cover; chokepoints as a content type.

Lockstep session layer stays last, unchanged.
