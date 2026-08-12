# Roadmap — the way forward

Only what is open. Everything shipped, and the measurement that justified it, is in
`docs/HISTORY.md`.

## Open work

### The art pipeline  (L)

Tracked in Linear as **A faction from nothing** — one building, one unit, zero borrowed bytes,
every asset authored and viewable. The asset roadmap proved a pack can CARRY art; this is about
AUTHORING it and being able to SEE it.

Landed: glTF export (`zhasset gltf`), a from-nothing W3D writer (`zhasset w3dfrom` — every
chunk from spec, so no retail template), and a parametric recipe driven through Blender's
geometry kernel (`zhasset model` + `tools/zhblender.py`).

Open: textures on the meshes, skinning and motion, the contact sheet that previews a whole
pack, and the zero-borrow lint that would make the claim enforceable rather than asserted.

**Nothing authored this way has been loaded by the engine yet.** A clean boot validates every
literal in a file and no field name, so it still wants a witness in a real match — which is
blocked on macOS TCC granting Accessibility and Screen Recording to the session's host app.

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
- **The authored explosion has never been SEEN rendering *in the engine*.** Note what changed
  and what did not: `zhasset gltf` now renders authored *geometry* outside the game, which is
  how three transform bugs were caught, but an `FXList` is the engine's own particle system and
  no external renderer can stand in for it. This debt is unmoved. It is verified three ways —
  enums
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
