# Roadmap, with the evidence for each slice

Provenance rather than plan: every slice below is done, and each entry records the number that
justified it and what it cost to learn. Kept out of `CLAUDE.md` because that file loads every
session and this is history — but kept, because "why is it like this" is the question a
measured decision has to be able to answer a year later.

Open: the **lockstep session layer**, and **asset slice 18 (audio)**.

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
   A `null` value REMOVES a key, and removing a name no earlier layer declared is an error —
   the same reasoning as retail's 88 dangling references, caught one step earlier where the
   author can still see what they meant. See the total-resolution-order invariant above.
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
12. ~~**Passability grid**~~ — done. One byte/cell with the surface in the low three bits,
   a drawn `map` block, integer 8-way A*, and locomotor **surface masks** so the same river
   is a wall to infantry, a road to a hovercraft and nothing at all to aircraft.
   *Measured: the same two armies on the same seed decide in 17.4s across open ground and
   27.4s through one gate; a solid barrier turns the match into a timeout draw. Cost is
   ~1.7x tick time at 2,000 units (2.0s → 3.5s) and nothing at all for a pack with no map.*
   **It did NOT re-baseline the pinned hashes, and this entry used to say it must.** That was
   written before opt-in-by-content became the house discipline across five slices. Gating on
   CONSEQUENCE — a map exists *and* some unit cannot cross some cell — means an inert map is
   bit-identical to no map, which is both true and much more useful than a re-baseline: every
   pinned replay and both generative guards survive. Prefer this over re-baselining whenever
   the feature can be made a genuine no-op.
13. ~~**Elevation as a boolean LOS gate only**~~ — done. Cliff and Impassable break a sight
    line; water and rubble do not. Derived from the surface, never authored separately — the
    two blocking surfaces are exactly the ones the map writer emits as a PLATEAU, so what
    stands up out of the ground is what stops a shot, in both engines.
    *Measured with WATER as the control, which is what makes it a test of sight rather than of
    terrain: two maps of identical shape, a two-cell wall thinner than every gun in the pack,
    decide in **28.4s when the wall only blocks movement and 22.2s when it also blocks
    sight**. Before this slice both measured the same, because both armies simply shot across.*
    **The opaque wall is FASTER, which was not the expected direction.** A transparent wall
    halts both armies at it in a long attritional exchange; an opaque one denies acquisition,
    so they keep moving to the gate and settle it in one concentrated fight. Cover changes
    what a chokepoint MEANS, not merely how long it takes to walk around.
    **COVER DOES NOT TRANSFER, and it is the reverse of what this slice assumed.** ZH HAS the
    check — `Weapon::isClearFiringLineOfSightTerrain` — and its single call site in
    `TurretAI.cpp` is **commented out**, with EA's own note that some weapons (Tomahawk) must
    fire beyond LOS. The only live consumer is `Pathfinder::isAttackViewBlockedByObstacle`,
    which uses terrain LOS to choose where to STAND, and skips it entirely for
    `KINDOF_IMMOBILE` ("we can't move around it"). `isWithinAttackRange` is pure distance, so
    TurretAI's comment that it "checks terrain los now" is stale. Verified identical in
    GeneralsX's own tree — the source of the running build. So a wall that gives COVER here
    gives only a DETOUR there, `ZhLint` reports it per map, and a balance measurement that
    leans on cover does not transfer.
    **CONFIRMED IN A REAL MATCH**, once slice 16 made the test constructible. A map with a
    two-cell cliff wall beside the player start and a neutral Crusader placed 72 world units
    beyond it — inside the hellhound's 80 — with the plateau squarely between. Force-fire
    across the wall: the target's health bar went 96px pure green -> 62px yellow -> gone, and
    the tank was left as a scorch mark. **Our unit never moved.** It shot through a cliff that
    its own pathfinder will not cross, from a standstill, and killed what was behind it.
    *The order matters: an ordinary right-click on a NEUTRAL object is a move order, not an
    attack, and the first attempt looked like LOS blocking when it was simply no order at all.
    Ctrl+click force-fire is the one that tests anything.*


## Roadmap: the ASSET MODEL

The sim roadmap above is finished bar the lockstep layer. What is left is art, and it has its
own order. The governing line is already measured and does not move: **a pack on wholly
authored art borrows 8 retail names, and all 8 are the `zh.sides` HUD.** Content is ours; UI
furniture is borrowed on purpose.

14. ~~**A pack must CARRY its art, and lint must prove every reference resolves.**~~ — done.
    `zh.art` lists the files a pack ships and the compiler copies them into the output, routed
    by extension (`.w3d` to `Art/W3D`, images to `Art/Textures`). `AssetIndex` reads the `.big`
    tables of contents directly plus the loose `Art/` tree — **25,346 assets on this install**
    — and lint refuses any `zh.models` name that resolves to neither an installed asset nor
    one the pack ships.
    *A MAPPING is not a MODEL: the old check only asked whether a `zh.models` entry existed.
    A typo, or an authored mesh not yet built, produced a unit that moves, shoots, dies and
    cannot be seen or clicked, with no error from the engine — the `desertA` shape.*
    **Absent is not failure.** With no install the index is unusable and the check SKIPS; a
    check that cannot run must never look like a check that failed. Only names are read from
    the archives, never bytes.
15. ~~**Derive art profiles FROM THE MESH.**~~ — **STRUCK, on measurement. It cannot be done,
    and `zhasset artvalidate` is the standing proof.** The idea was that the W3D reader could
    supply geometry from vertex bounds and the rig from the hierarchy, unblocking the 2,928
    adoptable meshes that have no measured profile. Derived profiles were compared against the
    536 meshes that DO have one:

    | Derived from the mesh | Median | Within ±20% |
    |---|---|---|
    | `majorRadius` | ×1.02 | 61% |
    | `minorRadius` | ×1.10 | 51% |
    | `height` | **×1.51** | **26%** |

    **Gameplay geometry is a designer-chosen COLLISION size, not a property of the art.** The
    silhouette includes barrels, wings and antennae the footprint excludes — AVLeopard's mesh
    spans 21.9 half-units because of its gun, against a declared `majorRadius` of 15. Unbiased
    on average and wrong by more than a fifth for a third of meshes, which is precisely the
    "units created INSIDE the building and never seen" bug, mechanised.
    *The catastrophic direction is not a clean signal either: 8.9% of retail objects declare a
    radius under 30% of their own mesh half-span, so "much smaller than the art" cannot even
    be a warning without firing on retail content.*
    *Rig names fare better but still fail: the named part is present in the mesh 93% of the
    time for `turretBone` and `muzzleFlash`, 77% for `launchBone`, 65% for `fireFXBone` — and
    presence is not IDENTIFICATION. Several bones are candidates and only the INI says which
    one the weapon fires from.*
    **Consequence for the plan: the free-art pool cannot be made safe by inference.** There are
    two safe paths and inventing a third is what this measurement rules out — adopt a mesh
    retail USES, which has a measured profile, or AUTHOR one, which declares its profile as it
    is written. "2,928 models are free to adopt" remains true about rendering and stays false
    about behaviour.
16. ~~**Place objects in the emitted `.map`.**~~ — done. `map.objects` places a template at a
    position in OUR world units; the writer converts and emits it into `ObjectsList` beside
    the waypoints. Confirmed in a real match: a placed retail Crusader appeared exactly where
    the map put it.
    **The owner is the trap.** `GameLogic` resolves it with `PlayerList::validateTeam`, whose
    miss path is a `DEBUG_CRASH` *before* it falls back to neutral — and DEBUG_CRASH is
    compiled into this build, same as rule 1. Only `"team"`, the neutral side's default, is
    safe from a compiled map: a skirmish opponent's name exists in the LOBBY, not in the map's
    `SidesList`, so naming one is the crash rather than the fallback.
    *This is what finally settled the LOS question below.*
17. ~~**Author an `FXList` and a `ParticleSystem`.**~~ — done. `rts compile` writes three
    particle systems, an `FXList` and the additive sprite they draw with. **This was the last
    category where adopting retail art was structurally required**: an adopted mesh inherits
    its peer's death FX and an authored one inherited nothing, so a wholly authored unit died
    silently and invisibly.
    *Ours is the FALLBACK, never the override — a mesh retail uses brings presentation that
    fits it, and `OCL_CrusaderTurret` throws THAT tank's turret. Structures had no death
    presentation at all and now get `FXListDie`, which 842 retail objects use for this.*
    **Two files, not one.** ParticleSystems go to `Data/INI/ParticleSystem/` and the FXList to
    `Data/INI/FXList/` — the directories each manager actually scans. Emitting both into one
    file removes no work and adds a question about which subsystem parses what, in what order.
    **The three enums were checked against the C++ name tables, not a grep** — and the tables
    contain `SCORCHMARK` and `SMUDGE`, which are legal and appear in no shipped INI. A grep
    would have under-counted the vocabulary, which is exactly why the rule reads that way.
    *An ADDITIVE sprite's empty is BLACK, not transparent: additive blending ignores alpha, so
    the compositing instinct leaves a visible square. Falloff is squared, because a linear ramp
    reads as a flat disc with a hard edge rather than a glow.*
    Verified: legal enums, our own reader walks the emitted FXList to its texture, and the
    engine loads both files (`[INI] load('Data/INI/ParticleSystem/…')`). **The explosion has not
    yet been seen rendering** — the attempt was abandoned when Chrome stole focus mid-sequence,
    which is what prompted the frontmost guard below.
18. **Audio.** (L) *The largest untouched category: not one line is emitted and every unit is
    silent. `SoundEffects`, `Speech`, `Voice` and `MiscAudio` are all in the 42 scanned dirs.
    It is also 0% of the simulation, so it changes no measurement — which is why it is here
    and not higher. Split it the way icons were split: weapon and death sounds are content and
    get authored; EVA and UI chrome stay borrowed.*
19. ~~**Terrain themes.**~~ — done. A pack now authors its own ground by default: a `Terrain`
    block, a 64x64 tile at `Art/Terrain/`, and the map's texture class pointing at it. **A
    map's shape was already ours while its surface stayed EA's — that was the last borrowed
    thing in an emitted map.** Name a retail type in `zh.terrainType` to use theirs instead.
    *`countTiles` is picky in ways that fail as a BLACK MAP and not as an error: uncompressed
    true-colour, 24-32 bpp, at least one whole 64x64 tile (`TILE_PIXEL_EXTENT`). Miss any and
    it returns zero tiles, `readTexClass` returns without opening anything, and nothing is
    logged. e2e asserts all four against the emitted tile.*
    *The tile goes to `Art/Terrain/`, not `Art/Textures/` — `TERRAIN_TGA_DIR_PATH` is where the
    loader looks. `Class` is a closed name table (`TerrainTypes.h`), not a free string.*
    *A mottled grain rather than a flat colour, for the same reason the first mesh test used a
    grid: a flat fill proves the file loaded and hides everything about how it is mapped.*
    Confirmed rendering in a match.

**Deliberately out, on evidence:** a DDS writer (retail is 3,496 `.dds` to 50 `.tga`, but TGA
ships, is supported and needs no DXT compressor); a `.big` writer (loose files shadow archives
— that IS the engine's mod mechanism, and no retail-derived pack ever ships from here anyway);
a modelling suite (units are 220-245 triangles — if primitives stop being enough, a small OBJ
importer is the cheap answer).

Both design decisions this roadmap owed an answer to are now ANSWERED:
**(a)** ~~flag changes re-select loadouts~~ — `LoadoutSystem` runs between
production and cooldowns; see the system-order invariant above.
**(b)** ~~one total resolution order~~ — see the invariant below. There turned out to be
THREE mechanisms, not four: an entity-level `extends` was never built, and deliberately
will not be.

Deliberately out, on evidence: terrain types as content (ZH's are inert — 291 blocks,
one call site, a flag nothing sets); art/FX/audio; prerequisite OR-expressions; slope-
modulated speed; elevation damage/range/cover; chokepoints as a content type.

Lockstep session layer stays last, unchanged.


## The extracted reference model (`tools/zhasset`)

The extraction exists so an agent can reason over ZH's content and derive new content from it,
rather than re-deriving the corpus each time. Three commands carry the model; `reference/` is
their output and is gitignored — ship the extractor, never the extract.

- `catalogue` — the resolved corpus: 15 factions, 2,102 objects, 363 weapons, 96 sciences,
  79 powers. Answers *what is in the game*.
- **`dossier <object>`** — one object COMPLETE, with its asset closure. Answers *how do I make
  another one of these*. Full definition, every `ModelConditionState` with its model and
  animation, the textures named INSIDE each `.w3d`, icons resolved through both hops, sounds,
  and the death FX/OCL entry points.
  *`AmericaWarFactory` is 86 draw states — 36 of them construction, 9 of them door animations —
  across 36 models and 155 distinct textures. **Building assembly is not a separate asset**; it
  is `ACTIVELY_BEING_CONSTRUCTED` / `PARTIALLY_CONSTRUCTED` states, and a summary reporting only
  the default model misses the whole build-up.*
- **`techtree [faction]`** — the graph, joined across five files none of which names it:
  `PlayerTemplate` roots, `Object` command sets and `Prerequisites`, `CommandSet` slots,
  `CommandButton` verbs, `Science`/`Rank`. Every playable faction resolves to 4 tiers.
  **The verb is the edge, and there are more than the obvious one:** `UNIT_BUILD` (259) makes
  units, **`DOZER_CONSTRUCT` (192) makes STRUCTURES** — a tree built from `UNIT_BUILD` alone
  stops dead at the dozer — **`PURCHASE_SCIENCE` (82) IS the experience tree**, and
  `OBJECT_UPGRADE` is per-object where `PLAYER_UPGRADE` is per-player. Some lines carry a
  trailing space, so the verb is normalised before comparing.
  *The two trees are ONE graph: an `Object`'s `Prerequisites` can require a Science and a
  `SPECIAL_POWER` button can require one, so superweapons and the promotion ladder are reached
  from different roots of the same structure. Measured: 5 ranks at 0/800/1500/2500/5000 grant
  7 purchase points a game against 14-24 reachable sciences per faction.*

- **`fx <name>`** — expands an `FXList` / `ParticleSystem` / `OCL` into its transitive closure,
  which is where `dossier` used to stop. A name like `FX_StructureMediumDeath` told an author
  nothing about what to author, and FX is the last category where adopted art is still
  structurally required: an authored mesh dies silently and invisibly today.
  **The graph recurses in three places** and missing any one truncates the closure —
  `FXListAtBonePos` nests another FXList, and a ParticleSystem's `SlaveSystem` /
  `PerParticleAttachedSystem` name further systems. Cycles exist, so every walk carries a
  seen-set. Measured: **429 FXLists, 1,087 ParticleSystems, 294 OCLs, 179 slave links**.
  ***1,087 particle systems draw on only 81 distinct textures.*** That ratio is the case for
  authoring FX being cheap: 81 images cover every explosion in the game, and a from-scratch set
  is a modest art task rather than a pipeline.
  *`dossier` now expands death effects rather than naming them, and walks the debris models'
  own textures — a thrown turret is a mesh like any other. `AmericaTankCrusader`'s 6 FX/OCL
  names resolve to 12 particle systems, 6 textures, 4 sounds and 14 debris models.*

- **`nearest --like <object>`** / `nearest --role … --cost …` — the clone entry point, and the
  first step of the authoring loop: pick the nearest existing thing, take everything it is made
  of, change some, emit it.
  **Substitutability is ROLE, not cost and not stats.** A 900-cost tank and a 900-cost jet share
  a number and nothing else, while a Crusader and a Battlemaster are the same thing under
  different flags. So role is a Jaccard overlap on `KindOf` weighted 3x against everything else,
  with the entries that say nothing about role removed — `PRELOAD`, `SCORE`, `SELECTABLE`,
  `CAN_CAST_REFLECTIONS` are on almost everything and leaving them in inflates every object's
  similarity to every other. Numeric axes compare as RATIOS, not differences: 100 vs 200 damage
  is the same distance as 1000 vs 2000.
  *Distances are reported PER AXIS, because an agent picking a template needs to know WHY —
  "same role, 18% cheaper, identical range" is actionable and "score 0.026" is not.*
  *`CINE_` cutscene clones are excluded by default: they are byte-identical copies, they rank
  first every time, and cloning a clone inherits nothing the original lacks.*
  Sanity: `--like AmericaTankCrusader` returns Paladin (its own upgrade) then Battlemaster
  (China's equivalent); a spec for a cheap fast anti-infantry China vehicle returns the Gattling
  Tank, which is precisely what that unit is.
