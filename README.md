# rts-skeleton

**Goal: a Zero Hour-like platform for customising factions and units.** The
product is the content pipeline — author a faction, validate it, and measure
what it did to balance — not a game client. Rendering exists only to inspect
the sim.

Walking skeleton for a deterministic, data-driven RTS engine core in the
Command & Conquer Generals mold. This is the thinnest end-to-end slice through
the risky architecture: deterministic simulation, AI-open content model,
modifier algebra, replay contract, and batch balancing harness. Rendering is
deliberately absent — the sim is a headless library; a Godot (or any) shell
reads snapshots from it later.

Zero external dependencies in the sim. .NET 8, `dotnet build`, done. The
optional Godot shell in `godot/` is the one place packages are allowed, and it
depends on the sim rather than the other way round.

## What it proves

1. **Bit-identical determinism.** Q32.32 fixed-point math everywhere
   (`Core/Fix64.cs`), no floats in sim state, PCG32 RNG with per-purpose
   streams, fixed system order, index-ordered iteration, deterministic
   tie-breaks. `rts replay` runs the same inputs twice and compares FNV-1a
   state-hash traces. A replay — and a lockstep multiplayer session — is
   exactly `(contentHash, seed, command log)`.

2. **AI-open content model.** `content/game.json` defines damage/armor
   classes, weapons, component-shaped unit prototypes, veterancy tracks, and a
   tech DAG. The engine ships a closed vocabulary of components and stats with
   open parameters; generators (human or model) emit data, never code.
   `rts lint` validates reference integrity, DAG acyclicity, threshold
   monotonicity, and coarse DPS/cost bands before anything simulates.

3. **Factions as diffs, not copies.** A base faction lists a roster; a general
   `extends` one and applies a patch — `add`/`remove` for roster surgery,
   `modify` for stat and cost changes that materialize a faction-local variant
   (`china_tank/battlemaster`). That is the Zero Hour generals model, and it
   means a generated faction is a reviewable diff against its parent rather
   than a forked roster that silently drifts. Patches resolve through the same
   modifier algebra as veterancy, baked into base stats once at load, so
   veterancy still layers on top at runtime — one mechanism, not two.
   Crucially, **growing the content pack never changes what an old replay
   means**, because prototype identity is name-derived (`UnitProto.StableId`,
   FNV-1a64 of the id) rather than a table index.

   That was earned the hard way, and the story is worth keeping. The first
   version of this claim was false: identity *was* the dense index, "append
   after the base units" was enforced by nothing but alphabetical luck, and
   adding `laser_crusader` silently renumbered `ranger` and `technical`. The
   two pinned e2e hashes did not notice, because a final-state hash only
   observes units that are *alive at the end* — and in both pinned scenarios
   the renumbered units are dead. `technical vs ranger` had in fact moved from
   `c773f31f6888b7c8` to `2cdfb299cdae6006`. Zero Hour ships the same bug in
   the same place: faction choice travels the wire as an index into
   `PlayerTemplateStore`, so inserting a template renumbers every later faction
   across clients, while its name-keyed `Science` store reorders freely.
   `e2e.sh` now carries a **generative** guard — inject an unreferenced unit
   named to sort before and after everything, assert every matchup is unchanged
   — rather than trusting pinned values to be sensitive to the thing they were
   supposed to be guarding.

4. **One modifier algebra.** Veterancy ranks are `(stat, op, value)` bundles
   resolved as `(base + Σadd) × Πmul` (`Runtime/StatSheet.cs`). Upgrades,
   general's powers, pilot bonuses, auras and achievement perks are the same
   mechanism with different sources/durations — fields to add, not systems.

5. **Batch balancing harness.** `rts duel` runs cost-normalized N-run series;
   `rts matrix` produces the full pairwise counter table and flags dominant /
   strictly-dominated prototypes. ~1,200 full battles run in a few seconds on
   one core. `--json` output is the seam for wrapping these verbs as MCP tools
   (`validate_mod`, `run_matchup`, `query_counter_matrix`) — transport
   plumbing only.

6. **Production/economy loop, command-driven.** Each team has money, a finite
   supply pool, per-tick income, a research queue and a unit build queue —
   all sim state, all hashed. Tech nodes now carry cost and research time, so
   the tech DAG gates production for real. Costs deduct when a queue head
   *starts* (stalling until prerequisites and funds allow), which means an
   entire build order can be issued as commands at tick 0 and the economy
   paces it — a macro scenario is still exactly `(contentHash, seed, command
   log)`. `rts econ` runs build-order-vs-build-order series and re-proves
   determinism on every invocation.

7. **Presentation is a consumer, not a participant.** `godot/` is a separate
   Godot 4 project that references the sim as a library and reads immutable
   `Snapshot` objects, interpolating between 30 Hz ticks at frame rate. The sim
   has no engine types, no clock and no frame concept; the shell has no way to
   write sim state except by queueing commands. `e2e.sh` proves it: the shell
   run headless reaches **the same final state hash** as the batch harness for
   the same `(contentHash, seed, command log)`, and fails the build if it
   doesn't.

## Run it

```
dotnet build -c Release
alias rts='dotnet bin/Release/net8.0/rts.dll'

rts lint
rts replay --a crusader --b battlemaster --seed 7
rts duel   --a crusader --b technical --n 200 --seed 42
rts matrix --n 100 --seed 1
rts matrix --n 100 --seed 1 --json
rts econ   --a "technical*" --b "war_factory,crusader*" --n 50 --seed 42
rts faction                      # all rosters, with each general's diff
rts faction --id usa_laser --json
```

Or run everything — build, the five-step verification gate, balance demos, the
prototype-drift regression and the shell equivalence check — with `./e2e.sh`.

Econ build orders are comma-separated unit/tech ids, all enqueued at tick 0;
`id*N` repeats N times and a trailing unit `id*` repeats to the resource cap.
Each side gets `--money` up front plus `--income` per second drawn from a
finite `--pool`; defeat = no live units, nothing buildable in the queue, and
remaining resources can never cover another unit.

## Observed results (shipped pack, seed 1, n=100/pair)

`contentHash=c961a94f5e2d4a3a`. Rows include the two general variants:

```
                         bmaster  crusader  laser_cru  ranger  technical  tank/bmaster
battlemaster                -        0%        0%       100%     100%         0%
crusader                  100%        -       100%      100%     100%         0%
laser_crusader            100%       0%          -      100%     100%         0%
ranger                      0%       0%        0%         -        0%         0%
technical                   0%       0%        0%       100%       -          0%
china_tank/battlemaster   100%     100%      100%       100%     100%          -
note: 'ranger' loses every matchup — strictly dominated
note: 'china_tank/battlemaster' wins every matchup — dominant, needs a counter
```

**Both generals shipped broken, and the harness said so immediately.** This is
the platform working, and it is the whole point of the goal:

- `china_tank` patched the Battlemaster to be cheaper (750 vs 800), tougher
  (0.85 armor factor, +40 HP) *and* faster to build. Cost-normalization then
  gives it more units that are also individually better — strictly dominant, a
  general nobody would play against.
- `laser_crusader` is the opposite failure: +25% damage, +1 range and near-zero
  spread, but 1100 cost against 900 means 3 of them face 4 stock Crusaders and
  it loses 0–100. Lanchester's square law punishes paying for quality with
  numbers far harder than the stat sheet suggests.

Neither is obvious from reading the diff; both are obvious after one `rts
matrix`. Authoring a faction is cheap, and knowing whether it is playable is
now also cheap.

The shipped pack is intentionally unbalanced; the harness finding it is the
demo. A single data edit (battlemaster cannon damage 65 → 68/69/70) moves the
crusader matchup 100% → 61% → 18.5% → 5%: outcomes are near-step functions of
stats away from equilibrium (Lanchester concentration effects) but
distributional near it, so a balancing agent can bisect stat space against the
win-rate metric. Scenario diversification (spawn jitter, split engagements,
micro policies) widens the informative region — harness work, not sim work.

## Observed macro results (`rts econ`, contentHash 64c1064b1ad11806)

Defaults (money 3000, income 50/s, pool 15000, n=50, seed 42) unless noted:

```
technical*                  vs  war_factory,crusader*         0% – 100%
technical*                  vs  war_factory,battlemaster*     0% – 100%
technical*                  vs  barracks,ranger*             100% –  0%
barracks,ranger*4,war_factory,crusader*  vs  technical*       0% – 100%
war_factory,battlemaster*   vs  war_factory,crusader*        100% –  0%
```

Three macro findings the set-piece matrix cannot see:

- **Rushes can't convert timing without economic targets.** The technical rush
  owns the map for the first 55 s but there is nothing to destroy, so it
  spawn-camps and trades attritionally — and technicals trade badly against
  tanks (the counter matrix reasserts itself). It still crushes infantry
  openings and punishes greed: prefixing `barracks,ranger*4` before the war
  factory flips the crusader tech-up from 100% win to 0%.
- **Fielding rate beats per-unit quality near equilibrium.** Crusader beats
  battlemaster in the set-piece duel at budget 3600 (100%) and at rounding-free
  7200 (99%), yet loses the streamed macro war 0%–100%: 800 cost / 400 build
  ticks fields units faster than 900 / 450, and that lead compounds through
  Lanchester concentration when armies arrive as trickles instead of lines.
- **Draws become readable.** Tick-cap games report average end net worth
  (money + pool + fielded army value), so economic attrition is measurable
  even without elimination.

## The shell (`godot/`)

```
# Godot 4.7 with .NET support ("mono" build) required
cd godot && dotnet build
godot --path godot                          # windowed
godot --headless --path godot -- --verify   # state-hash equivalence vs harness
godot --path godot -- --shots /tmp/out      # render PNGs at fixed ticks
```

Keys: `space` pause, `+`/`-` speed (1×–32×), `1` set-piece duel, `2` macro
battle, `R` restart, `S` next seed, `Esc` quit.

Shapes encode armor class (the thing the counter matrix keys on): circles are
infantry, small rectangles light vehicles, large rectangles heavy vehicles.
Health bars, muzzle flashes and veterancy chevrons are all snapshot fields, not
renderer state.

Watching the macro battle explains the table above better than the table does:
both sides tech up in an empty field for ~65 s, then feed tanks into the middle
**one at a time** as each finishes building. There is no massed line and no
concentration advantage to be had — just a stream of 1-vs-1 fights that the
faster-producing side wins on tempo. That is why battlemaster beats crusader
here and loses the set-piece duel, and it is the sort of thing that is obvious
in ten seconds of watching and easy to misread in a win-rate cell.

Two deliberate boundaries the shell respects:

- `Snapshot` is the entire read surface, and it hands out doubles — the same
  display-boundary conversion `Fix64.ToDoubleForDisplay` exists for. Nothing a
  renderer computes (interpolation alpha, screen coords, frame deltas) can
  reach sim state, so smooth rendering is free of determinism risk.
- `Sim.Enqueue` is the entire write surface, and it refuses commands stamped
  for the current or a past tick. Input must be scheduled ahead, which is
  exactly the discipline lockstep needs — the shell is already playing by
  roadmap item 4's rules.

## Layout

```
Core/       Fix64 (Q32.32), Pcg32, Fnv1a64 — the determinism substrate
Content/    JSON schema DTOs + ContentDb (load, resolve to dense tables, lint)
Runtime/    Command, World (flat archetype + team economy state + state hash),
            StatSheet, Sim (tick loop incl. production system), Snapshot (read seam)
Harness/    Duel series, counter matrix, build-order scenarios, determinism verification
content/    game.json — the AI-open content pack
Program.cs  CLI verbs: lint | duel | matrix | econ | replay  (--json everywhere useful)
godot/      Godot 4 presentation shell — separate project, references the sim
e2e.sh      build + verification gate + balance demos + shell equivalence check
```

`NuGet.config` at the root clears all package sources, so an accidental
`PackageReference` in the sim is a build error rather than a silent dependency.
`godot/NuGet.config` re-adds the source for the presentation subtree only.

## Skeleton simplifications, and what replaces them

- Flat unit archetype built from component-shaped content → real ECS storage
  so prototypes can add/omit components (turrets, transports, detachable
  pilots as child entities). Loader/storage refactor; content format is ready.
- Tombstone entity slots, no index reuse → freelist + generation-tagged handles.
- No collision/pathing → flow fields + local avoidance, presentation-era work.
- Modifier stacking is cumulative-additive only → stacking rules
  (unique-by-source, max-one, diminishing) and durations as modifier fields.
- Economy is abstract: two queues per team (research, units), income as a rate
  drawn from a finite pool, costs deducted at build start → per-building queues,
  power caps, and incremental payment once buildings exist as entities.
  **This currently caps the macro strategy space.** One serial queue per team
  means army throughput is bounded by build time, not by money: the same
  matchup at 100, 200 and 400 income/s returns bit-identical results (298.82 s,
  net worth 3640/700), because everything above ~50/s piles up unspendable.
  Until production can run in parallel, "spend faster" is not a strategy the
  harness can express, and net worth over-counts money that can never be used.
- Terrain blocks movement and nothing else: there is no line of sight, so a wall
  thinner than weapon range measures as nothing at all — both sides walk up to it
  and shoot over it. Cover, and therefore a chokepoint worth *holding*, needs the
  LOS slice.
- No unit-unit collision, which is a different thing from terrain. Units pass
  through each other, so a chokepoint concentrates fire but never jams and a
  one-cell gap admits an army as fast as a ten-cell one.
- Structures do not stamp the passability grid, so razing a building never opens
  a route. In Zero Hour an obstacle cell is derived from the building standing on
  it; ours is authored terrain only.
- Capture is proximity-based and there are only two teams, so a capturable
  building is always somebody's — there is no neutral owner to take it from, and
  attackers stop at weapon range and shell it rather than walk onto it.
- JSON-with-comments content → YAML once external packages are acceptable.
- FNV-1a state hash → xxHash3 when state size grows.

## Next steps, in order of risk retired

1. ~~Production/economy system + build-order harness scenarios (macro
   balance).~~ Done: per-team money/pool/income, research + unit queues, tech
   gating, `rts econ`. Left open for the building slice: power, per-building
   queues, destructible economy.
2. ~~Godot 4 + C# shell consuming read-only snapshots at render rate,
   interpolating between 30 Hz sim ticks; input → command queue.~~ Done:
   `godot/`, with `Sim.Enqueue` as the scheduled-command write seam. Open:
   camera control, unit selection, issuing orders by mouse (the seam exists;
   only UI is missing), sprites instead of primitives.
3. ~~Base + delta faction layering~~ Done: `extends`/`add`/`remove`/`modify`,
   `rts faction`, prototype-index stability under content growth.
4. **Upgrades and general's powers as content types** — the algebra exists, the
   content vocabulary doesn't. This is what makes a general feel like a general
   rather than a stat tweak.
5. **Multi-file content packs** — a mod is a patch file over a base pack,
   addressed by `contentHash`, so customisation composes the way factions do.
6. MCP server wrapping the harness verbs (`validate_mod`, `run_matchup`,
   `query_counter_matrix`) — the interface through which an agent authors and
   evaluates a faction. Worth doing once 4 and 5 give it something to say.
7. Parallel production, then collision/pathing. Both move balance numbers, so
   they belong before anyone tunes a faction seriously.
8. Lockstep session layer: exchange command logs, per-tick hash comparison for
   desync detection — the machinery `rts replay` already exercises in-process.
