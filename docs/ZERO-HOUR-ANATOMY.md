# Zero Hour, as data

What C&C Generals: Zero Hour actually is when you strip the art away, measured from
EA's GPL v3 source release and from 581,467 lines of the real shipping content corpus.
This document exists to answer one question: **what must our content model contain
before an agent can define a ZH-like game in it?**

Sources, both on disk, neither requiring retail assets:

| What | Where | Role |
|---|---|---|
| Engine source, pinned | `~/work/oss/GeneralsX` @ `GeneralsX-Beta-15` | **schema authority today** — the `FieldParse` tables enumerate every legal key. Note they span `GeneralsMD/Code` (338 files) *and* the fork's hoisted `Core/` (94) |
| EA GPL v3 source | `~/work/oss/CnC_Generals_Zero_Hour` | historical baseline. 2,348 keys vs Beta-15's 2,393: 45 added, none removed |
| Shipping INI corpus | `~/work/oss/GeneralsGamePatch/Patch104pZH/GameFilesEdited/Data/INI` | 581,467 lines / 118 files — what authors actually wrote |
| Real maps | `~/work/oss/GeneralsGamePatch/Maps` | 55 `.map` files, all parsed successfully |

**The corpus rows are deliberately NOT repointed.** Every measurement below was taken against
104p, and the community's active line has since moved to `GeneralsGamePatch2` (2,215 files /
498,416 lines, with `Object.ini` exploded into 2,112 per-object files). Swapping the source line
without re-running the counts would leave a document that cites numbers no listed source
produces. Re-measure first, then repoint.

Retail `.big` archives are *not* needed for any of this. They are needed only to run the
game client, and the client is the least interesting artifact for our purposes.

---

## 1. The headline: the generals are forks, not diffs

Zero Hour's twelve generals are the feature our product is named after. In the shipping
content they are implemented by copy-paste.

- **532** general-prefixed objects (`Lazr_`, `Nuke_`, `Slth_`, `Chem_`, `Demo_`, `Infa_`,
  `AirF_`, `SupW_`, `Tank_`, `Boss_`, plus two General-Challenge prefixes).
- **532 of 532 have an identifiable base counterpart. Zero are authored from scratch.**
- **96.4%** of their lines are byte-identical to the parent after normalising the name prefix
  (median pair 98.4%; 313 of 433 pairs ≥95%).
- **202,322 lines** shipped across ten general files to express roughly **9,297 lines** of
  real difference — a **23.9× duplication tax**. That is 48.9% of the entire 413,763-line
  object corpus.
- Of the 13,049 changed lines: **art 42.0%**, whitespace/comments 23.8%, **gameplay 18.3%**,
  renames 7.8%. Gameplay-relevant change is **1.25% of the general corpus**.
- **The entire numeric balance surface of all twelve generals is ~111 scalar overrides**:
  72 `BuildCost`, 17 `BuildTime`, 11 `MaxHealth`, 5 `EnergyProduction`, 3 `EnergyBonus`,
  3 `ExperienceValue`. Zero `VisionRange`, zero `TransportSlotCount`.
- **42% of pairs (182/433) have no gameplay delta at all** — their whole difference is
  renaming, art and whitespace.

The engine was not missing the mechanism. `DefaultThingTemplate` gives every object implicit
field inheritance, and `map.ini` does real per-scenario patching with `AddModule` /
`RemoveModule` / `ReplaceModule` and subtractive list edits. What's missing is a way to say
*"this faction is that faction, with these changes"* — and, critically, a way to **scope a
reference**.

### The disease, not the symptom

Field-level overrides alone would not have fixed this. A general is **six pointer swaps** in a
34-line `PlayerTemplate`, and those six swaps cascade transitively: new Command Center → new
Dozer → new buildings → new CommandSets → new units. Without reference rewriting, expressing a
diff still forces you to restate the transitive closure.

The cost is measurable, and it shipped as bugs. Re-measured against retail with an explicit
reachability graph (not a text grep): **88 bad references across 68 distinct broken
definitions**, spread over all nine multiplayer generals. A reference counts only when the
same general's fork of the referenced object demonstrably exists.

An earlier pass here reported **67**; the right number at that grain is **68**, and 88 if you
count each bad field rather than each broken definition (52 if you collapse tier-1/2/3
duplicates of one logical power). The count is quoted per grain from now on, because the three
differ by 1.7x.

Where they are matters more than the total. **`Prerequisites` contributes zero.** The volume is
in references reached THROUGH A SHARED INTERMEDIARY: `ObjectCreationList` `Transport`/`Payload`/
`ObjectNames` (64) and `CommandButton.Object` (15). The archetype is `SUPERWEAPON_Paradrop1` —
one shared list, invoked by all three USA generals, delivering the plain `AmericaInfantryRanger`
to generals that each forked their own. `patch104p` independently ships fixes for three of
these (e.g. `Chem_Command_PurchaseScienceMarauderTank`, annotated *"Use correct object for
tooltip. (#2166)"*), which is external confirmation that this is a tracked defect class rather
than an artifact of our methodology.

Per-general: SuperWeapon 19, Laser 18, AirForce 17, Nuke 7, Toxin 6, Tank 6, Infantry 6, Demo 5,
Stealth 4 (bad references). `BossGeneral` and the General's-Challenge `GC_*` forks carry the
same defects and are excluded as single-player-only. The community patch's fix for a GLA hole leak was to clone the hole 39 more times
across 6 files. One bugfix appears 113 times across 18 files; 58.9% of all patch annotations
in `Object/` land in the ten general files.

**This directly validates our `extends`/`add`/`remove`/`modify` slice, and tells us the next
required step: faction-scoped reference resolution.** Our reference graph is still trivial
(units reference only global tech names), which makes the rule cheap to establish now and
impossible to retrofit later.

---

## 2. Being art-free removes 72% of the problem

| Measure | Value |
|---|---|
| Object-block lines that are art/FX | **71.7%** of code lines (213,108 art + 13,932 FX of 392,235) |
| Median lines per buildable object | 324 (p90 944, max 2,066) |
| **Median *simulation* lines per buildable object** | **91** (p10 64, p90 136, max 241, σ **32.5**) |
| Art lines per buildable object | 5 – 1,528, σ **308.5** |
| `BlendTileData` share of a real map file | 592 KB of 685 KB — **81%**, none of it simulation |

The variance is the point. Simulation content is a **fixed-size, bounded, diffable artifact**;
art is where all the size and all the unpredictability live. Our 9-line prototype against ZH's
~91 sim lines is the honest measure of our content-model debt — and it is bounded and
enumerable rather than open-ended.

What that buys us, concretely:

1. **Breadth instead of depth.** ZH shipped twelve generals because twelve was already
   unmaintainable at 202,322 lines. At ~50–200 sim-relevant lines per general we can *generate*
   factions and treat them as a search space rather than a hand-authored set.
2. **Measurement ZH could never afford.** Each general offers 13–18 purchasable sciences against
   **7 total purchase points** — a 7-of-17 choice is **19,448 legal loadouts**, and nobody has
   ever enumerated one.
3. **`contentHash` == simulation identity, exactly.** No art tweak invalidates a replay.

---

## 3. 203 behavior modules collapse to ~15 primitives

ZH's extensibility is named C++ module classes attached to objects in data. The distribution is
extremely skewed:

- **16,685** module declarations across 1,949 objects; **203** distinct modules used
  (223 registered — 20 never instantiated).
- **7 modules cover 50%** of usage. **27 cover 80%.** 50 cover 90%.
- The 60 rarest modules account for **1.07%** of all usage.
- Excluding presentation modules, **86.7% of the 13,682 logic instances are pure data**,
  averaging **3.89 parameter lines**; 930 have zero parameters.

And the families collapse. All eleven `Die`-family classes share **one** filter schema
(`DeathTypes` / `VeterancyLevels` / `ExemptStatus` / `RequiredStatus`) and differ only by a
single typed effect — 5,083 instances, one rule shape. All twenty upgrade modules share
`UpgradeMux` — 1,021 instances, one rule shape.

The canonical antipattern is eight separate C++ classes —
`SabotageSupplyDropzone` / `SupplyCenter` / `Superweapon` / `PowerPlant` / `MilitaryFactory` /
`InternetCenter` / `FakeBuilding` / `CommandCenterCrateCollide` — three uses each, differing
only in which building type they hit. **Every new behavior is a recompile, so every mod that
needs one is dead on arrival.**

Our replacement is therefore not a scripting language. It is a closed rule form:

```
{ on: <closed event enum>, when: <closed filter grammar>, do: [<closed effect enum> + open params] }
```

No control flow, no variables, no loops, fixed arity, statically lintable, bounded cost per
tick — and its schema is directly emittable as an MCP tool definition.

---

## 4. Combat is one table, and the bonus algebra is not multiplicative

- The entire counter system is a **57 × 38 damage-type × armor-class multiplicative lookup**.
  Units carry **no** intrinsic "anti-tank" stat — only a `DamageType` label whose meaning lives
  wholly in the table.
- **47.5% of the 968 hand-authored cells are 0%.** Nearly half the authoring effort is spent
  saying "this does nothing to me."
- Bonuses compose **additively in excess over 1**: `bonus += this - 1.0`. Veteran (110%) +
  upgrade (125%) = **1.35, not 1.375**. Three +25% sources give ×1.75, not ×1.95.
- Area damage is a **two-band step function**, no falloff:
  `dmg = (distSq <= primaryRadiusSq) ? primary : secondary`.
- Every duration is **quantised to integer 30 Hz frames at load with `ceil()`**. This is why
  `DragonTankFlameWeapon` reads as 250 DPS in the file and delivers 150 — a 40% error if you
  trust the authored number.

Our `StatSheet`'s `Π mul` gives the combinatorial answer ZH deliberately avoided. That reframes
our "cumulative-only stacking" from a known simplification into a **semantic choice we should
make on purpose** — and ZH's choice is the one that keeps balance tractable.

---

## 5. Terrain is gameplay-dead; the tactical payoff is elsewhere

This is the most surprising finding, and it should reorder our roadmap.

- `Terrain.ini` has **291 blocks**, a 4-field schema of which **2 are used**. The engine
  consults a `TerrainType` for gameplay at **exactly one call site** — a `RestrictConstruction`
  flag that **no shipping terrain sets**. The table is inert.
- Passability is **one byte per 10×10 cell** (clear/water/cliff/rubble/obstacle/bridge) with
  **no movement cost at all** — binary passable/impassable.
- A locomotor's entire terrain interface is a **5-bit surface mask**, and shipping content uses
  **7 of 32** possible values (GROUND 80, AIR 66, GROUND|RUBBLE 28 account for 174 of 184).
- Elevation confers **no damage bonus, no range bonus, no cover**. `ATTACK_RANGE_IS_2D`. Its
  only effect is a Bresenham line-of-sight gate.
- "Chokepoint" does not exist in the engine — zero occurrences. It is emergent from passability.

**The single terrain-shaped combat modifier in all of Zero Hour is `GARRISONED`** — 1 of 29
weapon-bonus conditions (Range ×1.33, Damage ×1.25) — and it needs **zero terrain geometry**.
272 of 1,948 objects are garrisonable; the authored surface is five lines.

So the ordering follows from evidence rather than intuition: **garrison and capturable
structures before any heightfield**, because they deliver the tactical payoff of terrain at a
fraction of the cost and are measurable with the duel/matrix harness we already have.

---

## 6. Scenarios and AI are already data — the vocabulary is not

- The script VM has **344 actions** and **109 conditions**; **4,765 real scripts** parsed from
  the 55 shipping maps (9,074 conditions, 21,825 actions).
- Shipping content exercises only **142/344 actions (41%)** and **36/109 conditions (33%)**.
- The skirmish AI's base build order is **214 literal `Structure` blocks** in `AIData.ini`.
- All 55 maps parsed byte-exact: `EAR\0` + RefPack compression, chunked container, identical
  chunk inventory in all 55.

So an agent can author unlimited scenarios and **never a single new verb** — the vocabulary is a
hand-written 4,739-line C++ table. Same failure as the module system, same fix.

---

## 7. The hard caps

Compile-time limits that make ZH itself unable to host "any ZH-like game":

| Cap | Value |
|---|---|
| Damage types | 38, closed C++ enum |
| Weapon slots per unit | 3 |
| Upgrade templates, total | **128** (`BitFlags<128>`, one bit per template, assigned in parse order) |
| Veterancy levels | 4 |
| Commands per CommandSet | 18 |
| Starting units per faction | 10 |
| Generals Challenge slots | **12**, `#define NUM_GENERALS`, bound to hard-coded `.wnd` buttons |
| Top-level INI block keywords | 62, unknown token is a hard throw |
| `SpecialPowerType` | closed enum, ~70 values, **no spare slots** |
| Faction identity on the wire | an **ordinal** into `PlayerTemplateStore` — inserting a template anywhere but the end silently renumbers every later faction across clients |

That last one is our own "content growth must never renumber an existing prototype" invariant,
learned the hard way by someone else. Notably `Science` **is** name-keyed and can be reordered
freely — so the engine got it right everywhere except the one place it mattered most.

---

## 8. Determinism: where we are already stricter

ZH is a lockstep RTS built on **IEEE single-precision floats** (`typedef float Real`).
Determinism is bought by forcing bit-identical binaries and content (`exeCRC` / `iniCRC` in the
replay header and lobby) plus pinning the x87 control word to a 24-bit mantissa. It has
**exactly three RNG streams** (logic / client / audio), file-static, with no stream-id
parameter. Of six declared random distributions, **only two are implemented**.

Worse, and directly relevant to us: **sim-authoritative terrain queries live in the renderer.**
`TerrainLogic::isClearLineOfSight` is a pure-virtual stub that crashes with "implement ME"; the
real implementation sits behind `TheTerrainRenderObject` in `GameEngineDevice`, as do
`isCliffCell` and `getGroundHeight`. Combat outcomes therefore depend on the renderer's
heightmap, and the shipped engine **cannot run headless terrain logic**.

That is precisely the leak `CLAUDE.md` forbids. The corpus is the cautionary example, not the
model. Our `Fix64` + per-system `Pcg32` streams + `Snapshot`-only presentation seam are stricter
on every axis, and `e2e.sh` asserting shell/harness hash equality is the check ZH never had.

What we should steal: **load-time tick quantisation with `ceil()`** (pairs exactly with
`Fix64.FromDoubleAtLoadBoundary`), and **late binding by name with an ordinal hint** — map
scripts store both the enum int and a NameKey, re-resolve by name on mismatch, and degrade to
`NO_OP` rather than corrupting.

### …and where we had the same bug

Reading ZH's ordinal faction identity prompted a check of our own, and it found a live
defect. `World.HashInto` folded in `ProtoIdx`, a dense table index produced by an ordinal
sort of the unit ids. So adding a unit renumbered every prototype sorting after it, and
silently rewrote what old replays mean.

It had already happened. Adding `laser_crusader` (which sorts between `crusader` and
`ranger`) moved `technical` from index 3 to 4. Reproduced against the committed baseline:

```
matchup                  pre-layering       after laser_crusader
crusader vs technical    df8b977f31362dda   df8b977f31362dda     ← blind
technical vs ranger      c773f31f6888b7c8   2cdfb299cdae6006     ← drifted
```

The two hashes pinned in `e2e.sh` did not notice, and the reason generalises: **a
final-state hash only observes units alive at the end.** In both pinned scenarios the
renumbered units are on the losing side, so the guard was structurally incapable of
seeing the bug it existed to catch.

Fixed by making identity name-derived — `UnitProto.StableId = FNV-1a64(id)` — and folding
that into the hash instead of `ProtoIdx`, which is now strictly a runtime array index.
Hashes were re-pinned once, deliberately (`facc617cce5e8ce6`, `2e0ed7322422be60`,
`736b5a6718e4a434`), and `e2e.sh` gained a **generative** guard: inject an unreferenced
unit named to sort before and after everything, assert every matchup is unchanged. The
Godot shell independently re-derives the same hashes, so the fix holds across the
presentation seam too.

The lesson is about test design, not hashing: a pinned value is only a guard if it is
*sensitive* to the failure. Prefer a generated adversarial input over a recorded constant.

---

## 9. What to build, in order

Each slice is justified by a specific measurement above.

| # | Slice | Effort | Why here |
|---|---|---|---|
| 1 | **Schema hygiene**: a `defaults` block, load-time duration→tick quantisation with `ceil()`, `unitsPerPurchase`, explicit `default` on `damageVsArmor` rows | S | ZH's `DefaultThingTemplate` is 130 lines serving all 1,949 objects. Without one, every new field is a breaking change to every unit. Trivial now, expensive once packs exist. |
| 2 | **Modifier semantics**: additive-in-excess op; replace the single upgrade condition with a named **flag set**; flag-keyed **conditional variant blocks** | M | Our algebra covers 61 of 1,348 real upgrade references (4.5%). Selectors cover armor 220 + weapon-swap 123 + menu-swap 122. Grammar stays trivial: 1,716 of ~2,300 real condition clauses are literally `None`. |
| 3 | ~~**Faction-scoped reference resolution**~~ — DONE | M | The defect class is real: **68 broken definitions / 88 bad references** in retail. Resolution is scoped for variants; the shared-referrer case, which no resolution rule can fix, is linted. |
| 4 | **Upgrades as a content type**: named boolean + cost + ticks + scope | M | ZH's whole upgrade schema is 8 fields, 3 sim-relevant; an upgrade carries no effect data. Makes `rts matrix` a matrix over (prototype × upgrade state). |
| 5 | **Rules engine** `{on, when, do}`, death event first; **spawn lists** as the single "make objects" indirection | L | Death/damage response is **30.5% of all module instances** (5,083) and collapses to one rule shape. We have zero death vocabulary today. |
| 6 | **Garrison + capturable structures** | M | `GARRISONED` is the only terrain-shaped combat modifier in the entire game and needs no geometry. Attacks the README's own "rushes can only spawn-camp" diagnosis. |
| 7 | **Powers + science/rank second currency**, `rts science-matrix` | L | 153 of 530 power implementations are "spawn this list there". 7 points against 13–18 sciences = 19,448 loadouts on machinery we already have. |
| 8 | **Multi-file packs by `contentHash`** + `rts diff` reporting the delta taxonomy | L | `map.ini` proves the semantics are right; what ZH lacks is provenance. `validate_mod` finally has something to say. |
| 9 | **Passability grid** (one byte/cell + 3-bit surface mask), then pathing | XL | Chokepoints, flanking and water are all emergent from passability. Must deliberately re-baseline the pinned hashes. |
| 10 | **Elevation as a boolean LOS gate only** | M | Elevation confers no damage/range/cover in ZH. Narrow by construction — defer until measurement shows 6 and 9 didn't move the matrix enough. |

**Deliberately out, on evidence:** terrain types (inert), all art/FX/audio, prerequisite OR-expressions
(646 of 659 real rows name exactly one target), slope-modulated speed (3 of 184 locomotors),
elevation damage/range/cover (0 of 29 bonus conditions), chokepoints as a content type
(emergent), and the 60 rarest modules (1.07% of usage — set pieces, not vocabulary).

---

## 10. Open questions worth deciding deliberately

1. **Stacking semantics.** Make additive-in-excess the default and demote `Mul`, add a third op,
   or make it per-stat policy? Any change re-baselines the pinned hashes. One deliberate re-pin,
   or two ops forever plus a lint rule about mixing them?
2. **Bounding the rules engine.** ZH has no bound — a slow death spawns objects that carry their
   own death rules, uncapped. Cap on spawn depth, effects per tick, or total units? On hitting
   it: clamp (hash-safe, silently different) or fail the tick (loud, but a content bug becomes a
   crash)?
3. **Which currency first** — money or skill points? Skill points are the more interesting
   surface (a hard 7-of-17 budget) but need XP accrual per team in the state hash.
4. **Do we import retail ZH as a reference content pack?** Loading the ~25-unit core roster plus
   the 57×38 matrix and asserting `rts matrix` reproduces the real counter relationships would
   validate our combat core against shipping content rather than against itself. Strongest
   acceptance test available — and it raises a provenance question to answer deliberately.
5. **Scatter: geometry or table?** ZH expresses a tank's weakness to infantry *geometrically* —
   a Crusader lands outside its own blast half the time, expected damage 3.0 per shell. Adopt the
   per-shot draw (new RNG stream, higher duel variance) or fold it into a matrix cell?
6. **Is the passability grid part of `contentHash`?** If terrain is immutable it is scenario
   input like a command log. If ever mutable (rubble, destroyed bridges) it must enter
   `World.HashInto`. Determines whether a map is a pack or a parameter — much cheaper to decide
   before the grid exists.
7. **Does `extends` need multiple inheritance?** `BossGeneral` carries
   `IntrinsicSciences = SCIENCE_GLA SCIENCE_AMERICA SCIENCE_CHINA` and remixes across all three
   factions. Cross-faction remix, or evidence that some "variants" are honestly new base factions?
