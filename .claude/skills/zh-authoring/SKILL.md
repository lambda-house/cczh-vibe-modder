---
name: zh-authoring
description: Recipes for customising Zero Hour content — retune a unit, reskin one, add a unit, add a faction, author a map, build a terrain theme — with exact files, costs and verification steps. Use when asked how to change or create ZH content, or what a customisation would cost.
---

# Customising Zero Hour

Every recipe below states what it touches, what binary art it needs, and **how you would
know it worked** — because in this engine everything fails soft, so "it launched" proves
nothing.

Categories: **pure-data** (edit text) · **data+art** (text plus a binary asset) ·
**needs-cpp** (engine change).

## Start here: the loop

Do not hand-write a unit from a blank file. Clone the nearest existing one — the corpus has
2,102 objects and one of them is almost always 90% of what you want.

```
zhasset nearest --like AmericaTankCrusader        # or --role VEHICLE,CAN_ATTACK --cost 900 --side China
zhasset dossier <the winner>                      # EVERYTHING it is made of, transitively
zhasset techtree <faction>                        # where it would sit, what would gate it
  ... edit ...
rts compile --target zh --out <dir>               # emit Data/INI + the art the pack ships
zhasset audio <dir>                               # grade the emitted waves and AudioEvents
```

`nearest` ranks by **role, not cost** — a 900-cost tank and a 900-cost jet share a number and
nothing else. It reports per-axis distances so you can see *why* something matched.

`dossier` is the one that saves the most time: it resolves every `ModelConditionState`, the
textures named **inside** each `.w3d`, both hops of the icon lookup, the sounds, and the death
effects expanded into their particle systems and debris meshes. Full shapes in
`docs/ZERO-HOUR-MODEL.md`.

**Two things the model will not tell you, because they were measured and found untrue:**
- **Art profiles cannot be derived from a mesh.** Gameplay geometry is a designer-chosen
  collision size; derived `height` is within 20% for only 26% of meshes. So adopt a mesh retail
  **uses** (it has a measured profile) or **author** one (it declares its own). The 2,928
  unreferenced models are free about rendering and not about behaviour.
- **Cover does not exist in ZH.** Firing line-of-sight is checked nowhere — confirmed in a match
  by killing a target through a cliff. Never tune balance against terrain cover.
- **Audio cannot be heard on this build, from any input.** GeneralsX's `OpenALAudioManager`
  allocates sources and never buffers a sample. Emission is real and additively loaded; playback
  is not. Verify with `zhasset audio` and a clean boot, and never claim more than that.

## Deeper references

- **`references/engine-rules.md`** — the 19 rules measured against the RUNNING engine. Which
  channels are additive and which are fatal, why `map.ini` is the only override channel, which
  shared leaves BREAK when patched in place, and what a new faction must emit to be playable.
  Read this before emitting anything into a retail install.
- **`references/driving.md`** — launching with logging, and driving the client into a live
  match to check a change by observation rather than by hope.

## Silent failures that reach a match

Every one of these compiles, loads, boots 42/42 and then does nothing. No error, no log line.
They were each found by someone noticing something in a match, which is the slowest detector
there is — check them before you look anywhere else.

| Symptom | Cause |
|---|---|
| unit is invisible but behaves | **`GeometryIsSmall = Yes` on a radius > ~20.** Retail's small objects have median radius 10; only 3 of 237 exceed 30. Derive the flag from the radius, never from unit-vs-structure |
| unit is invisible, nothing behaves | the model name resolves to no file — `zh.models` having an entry is not the same as the `.w3d` existing |
| map renders BLACK | the `TerrainType` names a texture that ships in no archive. **12 of retail's own 291 blocks dangle this way**, `desertA` among them. Naming a real block is not sufficient |
| terrain tile ignored | `countTiles` wants uncompressed true-colour, 24–32 bpp, at least one whole 64×64 tile, at `Art/Terrain/` — not `Art/Textures/` |
| blank command-bar tiles | a `MappedImage` whose texture is missing. Both halves ship or neither |
| units spawn INSIDE a building | geometry smaller than the mesh. It cannot be derived — adopt a mesh retail *uses*, or author one |
| authored unit dies invisibly | no death FX. An adopted mesh inherits its peer's; an authored one inherits nothing |
| faction plays the wrong pack's units | **two packs installed at once.** Sides are pack-prefixed so they coexist rather than collide, the lobby lists several factions, and a match takes whichever is default. Uninstall by `MANIFEST.txt` before installing |
| build buttons render, click does nothing | missing `ProductionUpdate`, or a door count with no door states |
| a sound event resolves and is silent | its wave is not at `Data/Audio/Sounds/<name>.wav`. An `AudioEvent` never names a path — the engine composes it from `AudioSettings.ini`, so a `Sounds` entry IS the base filename |
| a misspelt field does nothing, quietly | an unknown KEY is dropped without a word (a bad enum VALUE, by contrast, kills the process). A clean boot is no evidence a field name is right — check it with `zhasset objectdiff` |

## What does NOT transfer — measure here, and it will differ there

These all compile, load and play, and then behave differently from the numbers you tuned
against. `rts lint --target zh` reports them per pack; know them before you tune, not after.

| Ours | Theirs | Consequence |
|---|---|---|
| `spread` as a damage multiplier per shot | `ScatterRadius` — the projectile geometrically misses | not interchangeable; compiled damage is the un-spread mean |
| veterancy bonuses composed with `Mul` | situational bonuses compose **additively-in-excess** | stacked bonuses resolve LOWER there. Use the `Excess` op to match |
| a flag SET per unit | **one `PLAYER_UPGRADE` bit per object** | only the first conditional variant survives. The same restriction forced 268 `ConflictsWith` lines on retail |
| terrain blocks line of sight | firing LOS is checked **nowhere** | a wall that gives COVER here gives only a DETOUR there. Never tune balance on cover |
| a rank ladder in content | `Rank` blocks are NUMBERED, not named | emitting yours REPLACES retail's rather than extending it — the one type that cannot be additive |
| `grantMoney` on death | nearest is `CreateCrateDie`, which the KILLER collects | different beneficiary, so it is not emitted at all |

## Emit-time literals that are hard load errors

Closed C++ name tables, not free strings — an unknown value aborts the boot. **Check the name
table, never a grep of the INI**: `SCORCHMARK` and `SMUDGE` are legal `ParticleSystem` values
that appear in no shipped file, and `GARRISONABLE` greps as present only because it is a
substring of `GARRISONABLE_UNTIL_DESTROYED`.

- **`GARRISONABLE` is NOT a KindOf.** The bit table has only `NO_GARRISON`, `STEALTH_GARRISON`,
  `GARRISONABLE_UNTIL_DESTROYED`. Garrisonability is the PRESENCE OF the `GarrisonContain`
  module. Emitting the invented name was a hard load error.
- `ParticleSystem` `Priority` / `Shader` / `Type`, `Terrain` `Class`, `Locomotor`
  `ZAxisBehavior` (`NO_Z_MOTIVE_FORCE`, not `NO_Z_MOTION`), `DamageType`, `SpecialPowerType`.
- A map object's **owner** must be a team that exists. `PlayerList::validateTeam` guards the
  miss with `DEBUG_CRASH` *before* falling back to neutral, and DEBUG_CRASH is compiled into
  this build. Only `"team"` — the neutral side's default — is safe from a compiled map; a
  skirmish opponent's name exists in the LOBBY, not in the map's `SidesList`.

## Authoring FX is cheap, and here is the measurement

**1,087 particle systems in the whole game draw on 81 distinct textures** — small additive
sprites, reused everywhere. A from-scratch set is a modest art task, not a pipeline.

- An ADDITIVE sprite's "empty" is **BLACK, not transparent**. Additive blending ignores alpha,
  so the ordinary compositing instinct leaves a visible square. Squared falloff, because linear
  reads as a flat disc with a hard edge.
- **Two files, not one:** `ParticleSystem` blocks go to `Data/INI/ParticleSystem/` and the
  `FXList` to `Data/INI/FXList/` — the directories each manager actually scans.
- An adopted mesh inherits its peer's death FX and an authored one inherits nothing, so ours is
  the FALLBACK, never the override: `OCL_CrusaderTurret` throws THAT tank's turret.

## Driving the game to check

`tools/zhdrive skirmish` launches and plays into a live match; `verify-pack` asserts icons
resolve and the build button charges the player. Launch with **`-quickstart`** — it sets
`m_playIntro = FALSE`, and pressing `esc` at the intro is what made early runs flaky.

Three traps, all measured: the **cursor must be parked centre** after every click or the RTS
edge-scrolls the camera out from under your coordinates; the **start position is randomised**,
so find things by appearance rather than by coordinate; and `esc` **leaves none** of the shell's
three wedged states, so relaunch instead of clicking harder.

## Retune a unit — pure-data, easy

Drop 3–10 lines into `<MapDir>/map.ini`. Zero base modification; retail itself does this
**297 times across 33 map override files**. Loose files beat archives, so no repacking.

**This is the fastest test loop in the engine and should be the default for any experiment.**

Verify: the cameo tooltip shows the new cost, and a 1v1 resolves differently.

## Reskin a unit — two paths, very different costs

**Free path (pure-data):** drop one `.dds` at the exact path the `.w3d` already names.
**Zero INI files touched.** Median texture is 43 KB.

**Blocked path (needs-cpp):** using a *new* filename. The texture binding is a
NUL-terminated string inside the model binary (`W3D_CHUNK_TEXTURE_NAME`); no INI key
overrides it.

Verify: the unit visibly changes with no INI edit. If it doesn't, your filename is wrong —
`zhasset dossier <object>` lists the exact texture names read from inside the `.w3d`, per model
condition state, so there is no need to guess or grep strings.

⚠️ `housecolor2.tga` is shared by 1,832 models. Repainting it recolours the entire army.

## Add a buildable unit — pure-data, easy

**Minimum 4 files, ~60 lines, zero binary assets** if you reuse an existing model and cameo:

1. `Object` block in the faction's Object INI
2. `CommandButton` with `Command = UNIT_BUILD`
3. a numbered slot in the producing structure's `CommandSet` (only **14 usable slots**)
4. 3 labels in `Data/Generals.str` (plain text; release builds prefer it over the binary `.csf`)

Verify in three stages, each failing in a different place: the button appears (CommandSet
wiring) → it is clickable (prerequisites) → the unit spawns (Object block).

## Add a faction — pure-data, moderate

Cheapest real faction is a **sub-general of an existing base side**: 6–9 files, **zero art**.
`Side` is a free string nothing validates.

Minimum viable faction ≈ **3,240 lines / 6 objects**, and note **three of the five core
objects are buildings** — a command centre, a cash drop-off, one factory. A full retail-parity
general costs 17k–22k lines, of which only **1.4–6.5% is real intent**.

Files: `PlayerTemplate.ini`, one `Object/<X>General.ini`, `CommandSet.ini`,
`CommandButton.ini`, `ControlBarScheme.ini`, `Default/AIData.ini` (a `SkirmishBuildList`, or
the AI cannot play it), plus 2 string labels.

Verify: appears in the skirmish dropdown → starts with a command centre → can build →
the AI can play it.

## Author a map programmatically — data+art, moderate

Demonstrated working: a parser read **all 116 retail maps with zero failures**, and a writer
emitted a structurally complete new map (367,932 bytes, 8 chunks) that re-parses clean.

Emit the `CkMp` container directly: symbol table then `(u32 id, u16 version, i32 size)` chunk
headers. **Compression is optional** — the engine passes through anything that isn't `EAR\0`.
`BlendTileData` is the only genuinely hard chunk. The preview `.tga` is exactly **65,580
bytes** in all 109 retail previews.

Verify: `MapCache.ini` regenerates and derives the player count from your `Player_N_Start`
waypoints.

## New terrain theme — data+art, moderate

~25 uncompressed TGAs, **imageType 2 (never RLE, never DDS** — the engine parses the TGA
header itself), k×64 square with k ≤ 10. Maps store texture-class *names*, so retexturing
every shipped map is a `Terrain.ini` edit with **no `.map` touched**.

Hard budget: the terrain atlas is 784 slots and one retail map already uses 780.

## Total conversion — the honest number

~2,400 binary assets (450–600 MB) against **~58,000 lines of text**. Art is **90–95% of the
cost and 100% of the schedule risk**: ~1,160 models at 0.5–2 days each is 3–9 person-years.

Retail spent 549,372 INI lines on the same job — a **9.5× tax**, almost entirely the 180,490
clone lines.

## Ceilings you will hit

`UPGRADE_MAX_COUNT = 128` (82 used, **46 free**) · terrain atlas 784 slots (780 used on one
retail map) · 14 command-bar slots · 3 weapon slots · 4 veterancy levels · `NUM_GENERALS 12`.
A retail-parity total conversion has almost no headroom.

## Licence

Retail `Data/INI`, the archives and all art are EA-copyrighted; only the engine source is
GPL. Extractors may be committed, **their output may not**, and no retail-derived content
pack ever ships from this project.
