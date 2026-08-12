# Roadmap — the way forward

Only what is open. Everything shipped, and the measurement that justified it, is in
`docs/HISTORY.md`.

## Open work

### Lockstep session layer  (L)

Always intended to be last. The determinism it needs is already there and gated: replay is
`(contentHash, seed, command log)`, `Sim.Enqueue` refuses a command stamped for the current or
a past tick, and every pinned hash is verified on each build.

## Owed, not open

Debts from finished work. Each is small; none blocks anything.

- **Nothing this project emits can be HEARD.** Not a defect in the pack: the arm64 build carries
  no wav decoder at all, so audio is verified against the schema and by `zhasset audio` and its
  playback is not claimed. It becomes checkable the day GeneralsX lands its "Phase 2" audio, and
  the check is already written.
- **The authored explosion has never been SEEN rendering.** It is verified three ways — enums
  checked against the C++ name tables, our own reader walking the emitted `FXList` through to
  its texture, the engine loading both files — but not photographed. Five attempts failed, each
  derailed by a genuine bug the attempt uncovered (a stale-pack faction, `GeometryIsSmall`, two
  false-refusing guards). **Do not attempt it again by micro-ing units through the UI.** The
  route that would work is a scripted scenario where a unit dies on a timer; that is a slice,
  not the two-minute check it was repeatedly called.
- **`CLAUDE.md` and the skills can still drift.** Nothing enforces that a lesson lands in the
  right place. The rule to apply by hand: does this constrain ALL work (`CLAUDE.md`), only this
  task (a skill), or only justify a past decision (`docs/`)?

## Standing risk

**Divergence.** Two engines compute the same battle, and where the models differ our numbers
stop being predictions. `rts lint --target zh` reports the known set per pack, and
`zh-authoring` lists them — spread, veterancy composition, one upgrade bit per object, cover,
the numbered rank ladder. The ones that bite are the ones nobody has found yet, so anything
copied verbatim from their source was copied for exactly this reason.
