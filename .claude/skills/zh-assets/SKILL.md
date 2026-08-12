---
name: zh-assets
description: Query the Command & Conquer Generals Zero Hour asset set — archives, models, textures, maps, audio, how INI references art — AND author new art: build a .w3d from nothing, model it parametrically through Blender, and render it to look at. Use when asked what an asset is, where something lives, how big the art burden is, to inspect/extract from the .big archives, or to create or view a model, texture or recipe.
---

# The Zero Hour asset set

**Shapes and joins: `docs/ZERO-HOUR-MODEL.md`.** It is the contract for what these commands
emit, so rely on it rather than re-deriving the corpus.

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

zhasset dossier <object>       # ONE OBJECT + its whole asset closure (start here)
zhasset fx <name>              # an FXList/ParticleSystem/OCL expanded to textures + meshes
zhasset map read|scan|verify   # the .map terrain format, read and checked
zhasset w3d <file>             # one model: meshes, motion, textures
zhasset artprofile             # the contract an adoptable mesh carries
zhasset artvalidate            # proof that contract CANNOT be derived from the mesh
```

**The art pipeline — read a model, and write one back:**

```
zhasset gltf <f.w3d> --out x.glb        # EXPORT so it can be LOOKED AT
zhasset w3dfrom --gltf x.glb --out y.w3d  # BUILD one back, every chunk authored
zhasset model --recipe r.json --out y.w3d # a parametric recipe, via Blender's kernel

zhasset w3dround <f>           # parse and re-emit; byte-identity proves the reader
zhasset w3dbox|w3dskel|w3danim # older direct generators (w3dbox needs a retail --template)
zhasset tga --out t.tga        # a texture from nothing: 18-byte header, then BGR
```

`w3dfrom` is the one that makes "authored" literally true: `w3dbox --template` copies
MATERIAL_INFO, SHADERS, VERTEX_MATERIALS and the MATERIAL_PASS scaffolding out of a **retail
mesh**, so what it produces is our geometry in their container. `w3dfrom` writes every chunk
from spec.

Generated output lands in `reference/` and is **gitignored** — see Licence below.

## Authoring a model: the numbers that decide everything

Model to the **measured** budget, not to taste:

| | measured |
|---|---|
| triangles | median **169**, p75 298, p90 619, p99 1,015 (3,794 models) |
| textures | **256²** is 44% of 3,748; then 128², 64², 512² |
| a real building | GLA barracks 110×51×88 at 565 tris; war factory 100×58×122 at 684 |
| a real vehicle | AVLeopard 44×17×15 at 245 tris, in **10 named sub-objects** |

Three consequences that are not obvious:

- **Text-to-3D generation is the wrong tool.** It emits 50k–500k triangles of organic
  surface; retopologising that to 169 is harder than authoring 169. And the engine consumes
  **structure**, not surface — a turret must be its own named sub-object to rotate, house
  colour its own submesh to be tinted.
- **Spend triangles where they can be SEEN.** The Mangal's road wheels were 55% of the model
  and completely invisible — a wheel 3 units thick inside a track box 5 units thick. Retail
  reaches the same conclusion from the other end: flat tread strips, with links and wheels in
  a scrolling texture.
- **Sub-object NAMES are load-bearing.** An INI Draw module shows and hides a piece by name,
  and the engine finds a turret the same way. `w3dfrom` carries recipe names through; a
  merged single mesh cannot animate however good it looks.

### Names the engine reads, and the INI half each one needs

Naming a part is not documentation — it is the switch. Full contract and the four silent
failure modes: **`zh-authoring` → `references/engine-rules.md` rule 20.** Summary:

| name the part | and emit | to get |
|---|---|---|
| `TREADSL` / `TREADSR` | `Draw = W3DTankDraw` + `TreadAnimationRate` (non-zero!) | a scrolling belt |
| `HOUSECOLOR<nn>` | `OkToChangeModelColor = Yes` | the player's colour on that submesh |
| bones `TREADFX01`/`02` | `TrackMarks = <sheet>.tga` | mud sized to the real track width |
| `TURRET`, exactly | *(derived)* an AI Turret block and a Draw bone | a **360°** gun |

A tread must also carry the `LINEAR_OFFSET` vertex-material mapper (`0x00040000`) plus a
`VERTEX_MAPPER_ARGS0` chunk, or the engine's scan skips it. `zhasset model` does this from
the name; do not hand-write it.

**Do not name a part `TURRET` unless it should spin through 360°** — ZH has no arc field
anywhere, so a limited-traverse gun does not exist. Model a casemate by naming the part
something else (`CASEMATE`) and slowing the hull's `TurnRate` to the traverse you want.

**Bones need no geometry, and most bones have none.** NVGattTank carries **29 pivots for 12
submeshes**; the 17 spare ones are `FIREPOINT01..08` (flames on a wreck), `SMOKE01..03`,
`MUZZLE01`, `TREADFX01..04`. Declare them with `"bones": [{"name":…, "at":[x,y,z]}]`.

## Recipes (`Content/models/*.json`)

`tools/zhblender.py` runs **inside Blender**, never under system Python. Shapes: `box`,
`cylinder`, `cone`, `wedge`, `ridge`, `dome`. Per part: `size`, `at` (centre of the box),
`rot`, `taper`, `bevel`, `cuts` (boolean), `array`, `mirror`. Each part becomes one named
W3D sub-object.

- **`ridge` is not `taper`.** A ridge collapses the top to a LINE; taper scales both non-axis
  dimensions equally and so makes a pyramid. A pitched roof is an entire silhouette on its own.
- **`cuts` are LOCAL to the part**, because the part is still at the origin when they are
  applied — it has to be, or a bevel width scales with wherever the part happens to stand.
- **`mirror` is about the MODEL's centreline**, and therefore runs after placement.
- **Over budget REPORTS and names the heaviest parts.** It does not decimate unless
  `"decimate": true`, because collapse decimation eats exactly the bevel loops that made the
  thing read as built, while the count lands neatly on target.

## Textures: tiling material, packed atlas, and the one that must be neither

Three modes, and picking wrong is not a quality question but a correctness one.

- **A tiling MATERIAL** is the default and the measured norm: **61.9% of the 3,790 retail
  meshes with texcoords have UVs outside 0..1**, so the engine wraps. Cube projection at a
  fixed world size gives uniform texel density by construction.
- **A packed ATLAS** (`"atlas": true`) gives each part its own rectangle, so a generator can
  paint the roof *as* a roof. It is also what makes the AO bake possible — two parts sharing
  a texel cannot each bake their own shadow into it.
- **A SCROLLING sheet can be neither.** `W3DTankDraw` animates a belt by adding to the mesh's
  U offset, so a tread packed into a sub-rectangle scrolls straight into its neighbour's.
  Retail splits exactly here: `NVGattTank.tga` for the body, `NVTreads.tga` for the two
  belts. `zhasset model` excludes a `TREADS*` part from the pack automatically.

Four rules that each cost a rebuild:

- **COUNTS IN A PAINTER ARE PER TILE, NOT PER MODEL.** Cube projection repeats the sheet;
  at `uvScale` 12 a 48-long belt samples it four times, so `"wheels": 6` arrived in game as
  twenty-four tiny ovals. Divide by the repeat count before you author.
- **A part that leaves the atlas also leaves the AO bake**, so it must carry its own tone.
  A belt tucked under an overhang rendered as the brightest thing on the vehicle otherwise.
- **Every feature must be a FRACTION of its pitch.** A 1-px gap is 11% of a 9-px link and 3%
  of a 32-px one, so coarsening a pattern to make it readable makes its edges finer instead.
- **A moving texture must be legible at the speed it moves**, which is much coarser than a
  good static surface. Thirty-two fine links read as a stationary hatch; twelve read as a
  track. This is a different criterion from "looks right in a still".
- **Don't paint what would slide.** Road wheels were correct on a static belt and wrong the
  moment it scrolled — a painted wheel travels down the hull. Links are the only thing on a
  real track that moves.
- **A track-mark sheet is 32-bit and V-symmetric.** `_PresetAlphaShader` composites it, so
  alpha *is* the mark; U runs across the ribbon (two bands, bare ground between) and V
  alternates 0/1 **every other quad**, so anything not mirror-symmetric in V flickers between
  two orientations as the vehicle drives.

## Looking at it

**There is no substitute for rendering and looking.** Structural checks — counts, byte-identical
round-trips, even Blender's own importer — all pass happily on a model whose parts are in the
wrong place. Three transform bugs in one session were caught this way and by nothing else.

```
blender --background --python-expr "...import_scene.gltf(filepath=...)..."
```

- the render engine enum is **`BLENDER_EEVEE`**, not `BLENDER_EEVEE_NEXT`
- `read_factory_settings(use_empty=True)` first, or the default cube is in the shot
- frame from the union of `obj.matrix_world @ Vector(c) for c in obj.bound_box`
- **macOS Quick Look does NOT open `.glb`** — `qlmanage` hangs. Blender or a browser.
- `bpy.ops.object.transform_apply` acts on the **selection**, not the active object, and
  reports success either way

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
