# rts-skeleton — Claude Code project memory

Deterministic, data-driven RTS sim core (C&C Generals mold). Headless sim +
content model + balancing harness. No rendering yet. Full context: README.md.

## Build / verify

- Build: `dotnet build -c Release` (needs .NET 8 SDK; zero NuGet deps by design)
- Run: `dotnet bin/Release/net8.0/rts.dll <verb>` — verbs: `lint`, `duel`, `matrix`, `replay`
- **After ANY change under Core/, Content/, or Runtime/, run all three and treat failure as a broken build:**
  1. `rts lint`
  2. `rts replay --a crusader --b battlemaster --seed 7` → must print `DETERMINISM OK`
  3. `rts duel --a crusader --b technical --n 20 --seed 42` (smoke)

## Hard invariants — never violate, never "optimize away"

- **No `float`/`double` in sim state or sim logic.** All sim math uses `Fix64`
  (Q32.32). Doubles exist only at the content-load boundary
  (`Fix64.FromDoubleAtLoadBoundary`) and in display formatting
  (`ToDoubleForDisplay`). Introducing a float into Runtime/ is a determinism
  bug even if all tests pass on this machine.
- **System order in `Sim.Step` is part of the replay contract:** commands →
  cooldowns → targeting → movement → combat. Do not reorder, merge, or
  parallelize across units without an explicit design decision.
- **Iteration is always ascending unit index.** No dictionary/hash-order
  iteration over sim state, no LINQ ordering that isn't explicitly `Ordinal`.
- **Ties break deterministically** (e.g. targeting: `(distSq, unitIdx)`).
  Every new comparison needs a total order.
- **New randomness gets its own `Pcg32` stream id** (next free integer in
  `Sim`'s constructor). Never share a stream between systems; never reuse an id.
- **Every new sim-state field must be added to the state hash**
  (`World.HashInto` / `Sim.HashState`) in a fixed position.
- **Sim core stays engine- and package-agnostic**: no NuGet packages, no
  engine types, no wall clock, no I/O inside Core/, Content/ (post-load),
  Runtime/, Harness/. Presentation projects added later may depend on things;
  the sim may not.
- Content lives in `content/*.json`; behavior changes via data edits are
  preferred over code changes. `contentHash` in tool output is the provenance
  anchor — quote it when reporting balance numbers.

## Architecture map

- `Core/` — Fix64, Pcg32 (streamed RNG), Fnv1a64 (state/content hashing)
- `Content/` — JSON schema DTOs; `ContentDb` compiles + lints packs
- `Runtime/` — `Command` (replay = contentHash + seed + command log),
  `World` (flat archetype, tombstone slots), `StatSheet` (modifier algebra:
  `(base + Σadd) × Πmul`), `Sim` (tick loop, hash trace)
- `Harness/` — duel series, pairwise counter matrix, determinism verification
- `Program.cs` — CLI verbs; `--json` output is the future MCP tool seam

## Known simplifications (intentional; see README for replacement plans)

Flat archetype not ECS; tombstones not generational handles; no
collision/pathing; cumulative-only modifier stacking; tech DAG linted but not
yet driving production; factions are flat lists not base+delta patches.
Don't "fix" these in passing — each is a deliberate slice boundary.

## Roadmap order (risk-retired first)

1. Production/economy system + build-order harness scenarios
2. Godot 4 + C# shell reading interpolated sim snapshots (separate project
   referencing the sim as a library — sim stays headless)
3. MCP server wrapping harness verbs (`validate_mod`, `run_matchup`,
   `query_counter_matrix`) keyed by contentHash
4. Lockstep session layer (command-log exchange + per-tick hash desync check)
