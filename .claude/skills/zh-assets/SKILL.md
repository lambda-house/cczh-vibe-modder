---
name: zh-assets
description: Explain and query the Command & Conquer Generals Zero Hour asset set — archives, models, textures, maps, audio, and how INI content references art. Use when asked what an asset is, where something lives, how big the art burden is, or to inspect/extract from the .big archives.
---

# The Zero Hour asset set

Retail ZH on this machine is **35 `.big` archives in two layers**, and the layering trips
people up: the ZH archives are a **delta over base Generals**, which lives in
`~/GeneralsX/GeneralsZH/ZH_Generals/`. Counting only the top level undercounts the art by
roughly half — a mistake worth not repeating.

**26,264 files · 2.23 GB · 9,000 `.w3d` models · 7,891 textures · 8,642 audio files ·
251 maps · 549,372 lines of INI.**

## The one ratio that matters

Content is **~18 MB of text**; art is **~1.94 GB of binary**. A **110:1 byte ratio**.
And **64% of every code line inside `Data/INI/Object/*.ini` sits in a `Draw` module** —
rising to 68% for buildable objects. When someone says "ZH modding is hard," this is why:
the text is tractable, the art is not.

Consequence for anyone planning work here: an agent can author the ~58,000 lines of text a
total conversion needs — **5–10% of the job**. The other 90–95% is a binary asset pipeline.

## Tool

`tools/zhasset` (no dependencies, deterministic, re-runnable):

```
zhasset archives list          # both layers, file counts and sizes
zhasset archives tree          # composition by extension
zhasset archives names --archive <path> --glob '*.w3d'
zhasset archives extract --archive <path> --dest <dir> [--glob ...]
zhasset deps                   # INI -> art resolution, both directions
zhasset show <name>            # explain one object/weapon/faction/terrain type
```

Generated output lands in `reference/` and is **gitignored** — see Licence below.

## What lives where

| Archive | Holds |
|---|---|
| `W3DZH.big` + `ZH_Generals/W3D.big` | models, `.w3d` |
| `TexturesZH.big` + `ZH_Generals/Textures.big` | `.dds` and `.tga` |
| `TerrainZH.big` + `ZH_Generals/Terrain.big` | terrain tiles (mostly in the **base** layer) |
| `INIZH.big` | all content, 118 `.ini` files |
| `MapsZH.big` | `.map`, per-map `map.ini`, preview `.tga` |
| `WindowZH.big` | `.wnd` UI layouts (plain text, hand-editable) |
| `ShadersZH.big` | the 5 `.vso`/`.pso` without which terrain cannot draw |

## How INI reaches art — and the trap

Resolution is high: **98.9% of the 6,041 model references resolve**, and **32.9% of
shipped models are orphans** referenced by nothing.

The trap: **a model's textures are named inside the `.w3d` binary**
(`W3D_CHUNK_TEXTURE_NAME`, chunk `0x32`), not in INI. So:

- Replacing a texture **while keeping its filename** is pure data — drop the `.dds` in, zero
  INI edits.
- Replacing it **under a new name** is a binary edit or an engine change. There is no data path.

And `housecolor2.tga` is shared by **1,832 models** — repainting it recolours the whole army.

## Everything fails soft

A missing string renders literally as `MISSING: 'label'`, missing audio is silence, a missing
image draws nothing, a missing ControlBarScheme silently falls back to the Observer HUD.
Retail itself ships 95 dangling model refs and 8 dangling string labels. **Generated content
can boot 90% broken and look fine** — so never treat "it launched" as validation.

## Licence — read before writing anything to the repo

EA's GPL v3 release covers the **engine source only**. `Data/INI`, the `.big` archives,
`generals.csf`, the maps and all art remain **EA-copyrighted**.

- The extractors in `tools/` may be committed.
- Their output may **not**. `reference/` is gitignored.
- No retail-derived content pack ever ships from this project. This is settled; see
  `docs/ZERO-HOUR-ASSETS.md` §6.
