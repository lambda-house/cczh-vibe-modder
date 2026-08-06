# rts-skeleton

Walking skeleton for a deterministic, data-driven RTS engine core in the
Command & Conquer Generals mold. This is the thinnest end-to-end slice through
the risky architecture: deterministic simulation, AI-open content model,
modifier algebra, replay contract, and batch balancing harness. Rendering is
deliberately absent — the sim is a headless library; a Godot (or any) shell
reads snapshots from it later.

Zero external dependencies. .NET 8, `dotnet build`, done.

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

3. **One modifier algebra.** Veterancy ranks are `(stat, op, value)` bundles
   resolved as `(base + Σadd) × Πmul` (`Runtime/StatSheet.cs`). Upgrades,
   general's powers, pilot bonuses, auras and achievement perks are the same
   mechanism with different sources/durations — fields to add, not systems.

4. **Batch balancing harness.** `rts duel` runs cost-normalized N-run series;
   `rts matrix` produces the full pairwise counter table and flags dominant /
   strictly-dominated prototypes. ~1,200 full battles run in a few seconds on
   one core. `--json` output is the seam for wrapping these verbs as MCP tools
   (`validate_mod`, `run_matchup`, `query_counter_matrix`) — transport
   plumbing only.

## Run it

```
dotnet build -c Release
alias rts='dotnet bin/Release/net8.0/rts.dll'

rts lint
rts replay --a crusader --b battlemaster --seed 7
rts duel   --a crusader --b technical --n 200 --seed 42
rts matrix --n 100 --seed 1
rts matrix --n 100 --seed 1 --json
```

## Observed results (shipped pack, seed 1, n=100/pair)

```
              battlemaster  crusader  ranger  technical
battlemaster       -            0%      100%     100%
crusader         100%           -       100%     100%
ranger             0%           0%        -        0%
technical          0%           0%      100%       -
note: 'crusader' wins every matchup — dominant, needs a counter
note: 'ranger' loses every matchup — strictly dominated
```

The shipped pack is intentionally unbalanced; the harness finding it is the
demo. A single data edit (battlemaster cannon damage 65 → 68/69/70) moves the
crusader matchup 100% → 61% → 18.5% → 5%: outcomes are near-step functions of
stats away from equilibrium (Lanchester concentration effects) but
distributional near it, so a balancing agent can bisect stat space against the
win-rate metric. Scenario diversification (spawn jitter, split engagements,
micro policies) widens the informative region — harness work, not sim work.

## Layout

```
Core/       Fix64 (Q32.32), Pcg32, Fnv1a64 — the determinism substrate
Content/    JSON schema DTOs + ContentDb (load, resolve to dense tables, lint)
Runtime/    Command, World (flat archetype + state hash), StatSheet, Sim (tick loop)
Harness/    Duel series, counter matrix, determinism verification
content/    game.json — the AI-open content pack
Program.cs  CLI verbs: lint | duel | matrix | replay  (--json everywhere useful)
```

## Skeleton simplifications, and what replaces them

- Flat unit archetype built from component-shaped content → real ECS storage
  so prototypes can add/omit components (turrets, transports, detachable
  pilots as child entities). Loader/storage refactor; content format is ready.
- Tombstone entity slots, no index reuse → freelist + generation-tagged handles.
- No collision/pathing → flow fields + local avoidance, presentation-era work.
- Modifier stacking is cumulative-additive only → stacking rules
  (unique-by-source, max-one, diminishing) and durations as modifier fields.
- Tech DAG is linted and queryable (`CanProduce`) but the harness spawns
  directly → production/economy loop (money, build queues, power) is the next
  sim system, driven by the same command model.
- Factions are flat unit lists → base + delta patch layering (the Zero Hour
  generals model); AI-generated factions become reviewable diffs.
- JSON-with-comments content → YAML once external packages are acceptable.
- FNV-1a state hash → xxHash3 when state size grows.

## Next steps, in order of risk retired

1. Production/economy system + build-order harness scenarios (macro balance).
2. Godot 4 + C# shell consuming read-only snapshots at render rate,
   interpolating between 30 Hz sim ticks; input → command queue.
3. MCP server wrapping the harness verbs; content bundles addressed by
   `contentHash` for reproducible agent-driven balance runs.
4. Lockstep session layer: exchange command logs, per-tick hash comparison for
   desync detection — the machinery `rts replay` already exercises in-process.
