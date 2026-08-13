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

zhasset preview --recipe r.json --out sheet.png   # 4 VIEWS IN ONE IMAGE, ~6s. START HERE.
zhasset w3dgrade <f.w3d>       # grade it with OpenSAGE's INDEPENDENT reader, and diff
zhasset w3dround <f>           # parse and re-emit; byte-identity proves the reader
zhasset w3dbox|w3dskel|w3danim # older direct generators (w3dbox needs a retail --template)
zhasset tga --out t.tga        # a texture from nothing: 18-byte header, then BGR
```

**`w3dgrade` is the only check on the mesh writer that is not downstream of our own code.**
`zhasset w3d` reads with the chunk table that wrote the file, `w3dround` compares us to
ourselves, and the glTF round-trip goes out through our exporter before Blender sees anything —
three checks, one opinion. OpenSAGE's plugin was written by other people from the same shipped
files. It earns the right to grade ours by reading RETAIL first, exactly as `zhasset map` does.

```
git clone https://github.com/OpenSAGE/OpenSAGE.BlenderPlugin ~/work/oss/OpenSAGE.BlenderPlugin
cd ~/work/oss/OpenSAGE.BlenderPlugin && git submodule update --init --depth 1
```

The submodule step is **not** optional and its absence lies: the addon imports its updater
package at module scope, so a plain clone reports a *Blender-version incompatibility*.

`w3dfrom` is the one that makes "authored" literally true: `w3dbox --template` copies
MATERIAL_INFO, SHADERS, VERTEX_MATERIALS and the MATERIAL_PASS scaffolding out of a **retail
mesh**, so what it produces is our geometry in their container. `w3dfrom` writes every chunk
from spec.

Generated output lands in `reference/` and is **gitignored** — see Licence below.

## How an asset is actually built

One recipe produces every file a unit needs. Nothing downstream is hand-made.

```
Content/models/<x>.json          the ONLY source. Declarative: parts, textures, bones.
        |
        |  zhasset model / models          (zhblender.py runs INSIDE Blender)
        v
  Blender kernel   prims -> taper -> bevel -> boolean cuts -> array -> mirror
                   -> cube_project -> pack_atlas -> Cycles AO bake
        |
        v  glTF (.glb)
        |
        |  zhasset w3dfrom                 every chunk authored; no template, no retail bytes
        v
  <X>.W3D  +  <x>.tga sheets  +  <X>_icon.tga  +  art-profiles.json
        |
        |  listed in the pack's zh.art
        v
  rts compile --target zh            Object/Locomotor/FXList/... INI + Art/W3D + Art/Textures
        v
  rsync into ~/GeneralsX/GeneralsZH
```

**Check it in this order. Each step is cheaper than the next and catches different things.**

| | catches | cost |
|---|---|---|
| `zhasset preview --recipe … --out sheet.png` | shape, proportion, every texture-space bug | **6 s** |
| `zhasset w3dgrade <f.w3d>` | format errors, by an INDEPENDENT reader | ~20 s |
| `./e2e.sh` | the contracts: names, mappers, INI halves, zero-borrow | minutes |
| `rts lint --no-borrow <faction>` | art that resolves into the installed game | seconds |
| boot the game | **only** what the ENGINE thinks | ~2 min |

**The game is the authority on the engine and a poor judge of shape.** It shows one angle at
distance after a two-minute round trip. Never package before the sheet looks right.

## Authoring a model: the numbers that decide everything

Model to the **measured** budget, not to taste:

| | measured |
|---|---|
| triangles | median **169**, p75 298, p90 619, p99 1,015 (3,794 models) |
| textures | **256²** is 44% of 3,748; then 128², 64², 512² |
| a real building | GLA barracks 110×51×88 at 565 tris; war factory 100×58×122 at 684 |
| a real vehicle | AVLeopard 44×17×15 at 245 tris, in **10 named sub-objects** |

- **Text-to-3D generation is the wrong tool here.** It emits 50k–500k triangles of organic
  surface; retopologising to 169 is harder than authoring 169. And the engine consumes
  **structure**, not surface. (It is the right tool for *reference*, and for organic subjects —
  infantry — where this approach genuinely runs out.)
- **PROPORTION BEFORE DETAIL.** Every silhouette correction in this project came from the SIDE
  view, and no amount of surface work fixes a wrong ratio. Get length:height right first.
- **Spend triangles only on what changes the SILHOUETTE.** A hole in the outline cannot be
  faked; a rib, a wheel, a window, a pipe run, a rivet can. Corollary: a detail at a *different
  height* from its neighbours is geometry, because a flat plate at constant z cannot say that —
  which is why a drive sprocket is modelled and road wheels are painted.

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
`VERTEX_MAPPER_ARGS0` chunk, or the engine's scan skips it. `zhasset model` does this from the
name; do not hand-write it.

**Do not name a part `TURRET` unless it should spin through 360°** — ZH has no arc field, so a
limited-traverse gun does not exist. Model a casemate by naming the part something else and
slowing the hull's `TurnRate` to the traverse you want.

**Bones need no geometry, and most bones have none.** NVGattTank carries **29 pivots for 12
submeshes**; the spares are `FIREPOINT01..08`, `SMOKE01..03`, `MUZZLE01`, `TREADFX01..04`.
Declare them with `"bones": [{"name":…, "at":[x,y,z]}]`.

## Recipes (`Content/models/*.json`)

`tools/zhblender.py` runs **inside Blender**, never under system Python. Shapes: `box`,
`cylinder`, `cone`, `wedge`, `ridge`, `dome`. Per part: `size`, `at` (centre), `rot`, `taper`,
`bevel`, `cuts` (boolean), `array`, `mirror`, `uvScale`, `texture`, `paint`, `noAtlas`.

- **`ridge` is not `taper`.** A ridge collapses the top to a LINE (`ridgeWidth` widens the
  crown); taper scales the other two axes and makes a truncated pyramid. **A ridge is a PRISM,
  so its end face is SOLID** — that gable will stand in front of anything you cut behind it.
- **`taper` takes `[sx, sy]`** for one axis only. A single factor scales both and splays a
  track into a funnel; a tank's belt stretches in LENGTH over its height and not in width.
- **`cuts` are LOCAL to the part**, so a cut's z moves when the part's centre does.
- **`mirror` is about the MODEL's centreline**, and therefore runs after placement. `"xy"`
  gives four copies from one authored part — front/rear and left/right.
- **Over budget REPORTS and names the heaviest parts.** It does not decimate unless
  `"decimate": true`: collapse decimation eats exactly the bevel loops that made the thing read
  as built, while the count lands neatly on target.

## Textures: tiling material, packed atlas, and the one that must be neither

Three modes. Picking wrong is a correctness question, not a quality one.

- **A tiling MATERIAL** is the default and the measured norm: **61.9% of the 3,790 retail meshes
  with texcoords have UVs outside 0..1**, so the engine wraps. Cube projection at a fixed world
  size gives uniform texel density by construction.
- **A packed ATLAS** (`"atlas": true`) gives each part its own rectangle, so a generator can
  paint the roof *as* a roof. It is also what makes the AO bake possible — two parts sharing a
  texel cannot each bake their own shadow.
- **A SCROLLING sheet can be neither.** `W3DTankDraw` animates a belt by adding to its U
  offset, so a tread packed into a sub-rectangle scrolls into its neighbour's. Retail splits
  exactly here: `NVGattTank.tga` for the body, `NVTreads.tga` for the belts.

Painters available: `tread`, `wheels`, `hatch`, `ribbed`, `pipes`, `windows`, `trackmark`.
A painter on a PART selects an atlas region's generator; on a TEXTURE spec it fills a standalone
tiling sheet. **Both places exist and they are not interchangeable** — a `paint` on a part whose
texture is standalone silently falls through to the plain panel generator.

### The rules that each cost a rebuild

- **COUNTS IN A PAINTER ARE PER TILE, NOT PER MODEL.** Cube projection repeats the sheet; at
  `uvScale` 12 a 48-long belt samples it four times, so `"wheels": 6` arrived as twenty-four.
- **PHASE IS NOT FREE TO ASSUME.** Cube projection decides where U=0 falls on a part. A feature
  centred mid-tile lands on the tile SEAM if that origin is half a tile out — which cut a wheel
  in half at each end of the plate and added one the recipe never asked for. Hence `phase`.
- **AN ATLAS REGION IS A PART'S WHOLE UV BOX, NOT ITS IMPORTANT FACE.** Faces are placed by
  WORLD position, so a thin plate standing 17 units off the centreline puts its big faces at
  V=z/scale and its edges at V=y/scale — a box spanning 1.25 of V to hold a face needing 0.5,
  with that face landing in the bottom third of its own region. Symptom: a region correctly
  packed, correctly painted, and sampling its own empty band. Fix with `"noAtlas": true`, then
  size the part so **height == uvScale and centre == uvScale/2**, which lands V on exactly 0..1.
- **BOTH HALVES OF OPTING OUT MUST AGREE.** `noAtlas` leaves the packing *and* the texture
  assignment. Leaving one behind points a tiling part at the atlas — the wheels rendered as
  blocks for exactly this reason, being a slice of whatever regions lay under their UVs.
- **EVERY FEATURE MUST BE A FRACTION OF ITS PITCH.** A 1-px gap is 11% of a 9-px link and 3% of
  a 32-px one, so coarsening a pattern to make it readable makes its edges *finer* instead.
- **A MOVING TEXTURE MUST BE LEGIBLE AT THE SPEED IT MOVES** — far coarser than a good static
  surface. Thirty-two fine links read as a stationary hatch; twelve read as a track.
- **DON'T PAINT WHAT WOULD SLIDE.** Road wheels were correct on a static belt and wrong the
  moment it scrolled. They became their own static SURFACE, not a different feature of the same
  one.
- **A PART THAT LEAVES THE ATLAS LEAVES THE AO BAKE**, so it must carry its own tone — a belt
  under an overhang otherwise renders as the brightest thing on the vehicle.
- **ROUND IS CARRIED BY THE CONTRAST STEP AT THE RIM**, not by the mask. Wheels with a gap
  brighter than the tyre read as a row of squares whatever shape the maths draws.
- **A TRACK-MARK SHEET IS 32-BIT AND V-SYMMETRIC.** `_PresetAlphaShader` composites it, so alpha
  *is* the mark; U runs across the ribbon and V alternates 0/1 **every other quad**.
- **AN ADDITIVE SPRITE AND AN ALPHA SPRITE ARE DIFFERENT IMAGES.** Additive encodes falloff as
  brightness on BLACK (adding zero hides the square edge); ALPHA composites by alpha and
  multiplies by the system's colour, so that same black is *opaque* and renders as a dark box.

### Fidelity, in the order it pays

1. **AO baked from the real geometry.** The single largest gap between our sheets and retail's.
2. **A region that knows what it is** — the roof painted as a roof, the belt as a belt.
3. **A repeating human-scale element.** For a building this is WINDOWS, and it is the difference
   between "inhabited structure" and "grey box" — dark glass, a lit sill, a shadowed reveal, a
   few panes lit so the grid does not read as a texture. Small and many, not few and large.
4. **Grime streaks anchored to the panel seams**, because that is where water runs. The cheapest
   fidelity available on a large flat wall.
5. Colour and panel noise. Least important; retail sheets are *not* more colourful than ours.

Two calibration traps: **strength tuned on a flat surface overpowers a curved one** (a dome's
form is its falloff, and meridians as strong as that make a striped bowl), and **anything
multiplied by the AO bake needs roughly double the authored strength**, because the bake
flattens exactly the low-frequency variation streaks add.

## Looking at it — and the discipline that goes with it

```
zhasset preview --recipe Content/models/x.json --out sheet.png     # 4 views, ~6 s
```

Three-quarter is what the game draws; **SIDE is where proportion lives**; **FRONT is where an
opening either reads as a hole or does not**; the low **gear** pass is where texture-space bugs
surface, being the part small enough to be wrong without the silhouette changing.

**LOOK AT THE THING YOU JUST CLAIMED TO FIX.** In one session three fixes were reported as done
and were not, and in every case the change itself was correct — something else was covering it:

- the front opening was widened twice while a ROOF whose eaves overlapped the frame covered the
  top of it, so no widening could ever show;
- the doorway slab was placed where the portico's own solid rear already was (buried), then in a
  recess so deep no light reached it — **a detail light cannot reach renders black whatever is
  in it, so a door must be SHALLOW**;
- the wheel plate was excluded from the atlas pack but still pointed at the atlas texture.

A structural check cannot see any of these. Counts matched, the round-trip was byte-identical,
and an independent reader agreed — on every one.

Blender gotchas that cost time and will again:

- the render engine enum is **`BLENDER_EEVEE`**, not `BLENDER_EEVEE_NEXT`
- `read_factory_settings(use_empty=True)` first, or the default cube is in the shot
- `bpy.ops.object.transform_apply` acts on the **selection**, not the active object
- `bpy.data.materials.new()` treats the name as a HINT and appends `.001` on a collision — two
  parts naming one texture produced a mesh pointing at a file nobody wrote
- **macOS Quick Look does NOT open `.glb`** — `qlmanage` hangs. Blender or a browser.

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
