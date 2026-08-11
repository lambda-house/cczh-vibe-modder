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
