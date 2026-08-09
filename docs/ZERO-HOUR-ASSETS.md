# Zero Hour assets: what exists, and what can be replaced

A map of the **retail** Zero Hour asset set as installed, and a plan for what can be
customised. Companion to `ZERO-HOUR-ANATOMY.md` — that one measures the *content model*
from the community corpus; this one measures the *shipped assets* from retail and asks
what an author (human or agent) can actually change.

Sources, all local: the 35 `.big` archives at `~/GeneralsX/GeneralsZH/`, retail INI
extracted to `~/work/oss/zh-retail/`, and EA's GPL engine source as schema authority.
Archive reader: `scratchpad/bigtool.py` (format from `Win32BIGFileSystem.cpp:104-155`).

> **Legal, decided:** EA's GPL v3 release covers the **engine source only**. `Data/INI`,
> the `.big` archives, `generals.csf`, the 116 maps and all art remain EA-copyrighted.
> Everything here is inventory and planning. **No retail-derived content pack ever ships
> from this project** — see §6.

---

## 1. The inventory (corrected)

The ZH archives are a **delta over base Generals**. Both layers are installed; counting
only the ZH layer undercounts the art by roughly half.

| | Archives | Files | Size |
|---|---|---|---|
| Zero Hour layer | 20 | ~12,500 | ~0.95 GB |
| Base Generals (`ZH_Generals/`) | 15 | ~13,700 | ~1.28 GB |
| **Union** | **35** | **26,264** | **2.23 GB** |

**9,000 `.w3d` models · 7,891 textures · 8,642 audio files · 382 terrain tiles ·
158 `.wnd` layouts · 251 maps · 549,372 lines of INI.**

Content is **~18 MB of text** against **~1.94 GB of binary art** — a **110:1 byte ratio**.

### Where the effort actually went

- **64.0%** of every code line in `Data/INI/Object/*.ini` sits inside a `Draw` module;
  **68.4%** for the 570 buildable objects (221,792 code lines, only 70,000 non-Draw).
- **97.0%** of general-prefixed content is duplication: 477 cloned objects ship
  **180,490 lines** to express **5,385 lines** of intent. Per-prefix delta ranges from
  `Tank_` at 1.4% to `Demo_` at 6.5%.
- **20.3%** of shipped models (1,807 / 66.1 MB) and **13.9%** of textures (974 / 52.2 MB)
  are reachable from nothing at all.

### Retail vs. the community corpus

Retail differs from the 104p figures in `ZERO-HOUR-ANATOMY.md`. **Trust these for
planning:** 1,868 Objects (+235 `ObjectReskin`), 570 buildable (379 units / 261
structures), 363 weapons, 53 armor sets, 182 locomotors, 82 upgrades, 79 special powers,
96 sciences, 247 distinct terrain types, 15 PlayerTemplates.

**13 factions are `PlayableSide=Yes` but only 12 are selectable** — Boss is locked out by
`ChallengeMode.ini:278` (`StartsEnabled = no`) and has no `SkirmishBuildList`, so it can
never be an AI opponent.

---

## 2. The replaceability matrix

Three categories: **pure-data** (edit text), **data+art** (text plus a binary asset),
**needs-cpp** (engine change).

| What | Category | Verdict | Cost |
|---|---|---|---|
| Unit stats — cost, health, vision, veterancy | pure-data | **easy** | 1 file, 1–10 lines |
| Weapons / armor / locomotors / damageFX | pure-data | **easy** | 2 files, 20–40 lines |
| Which model an object uses (`Model=`) | pure-data | **easy** | 1 line |
| Per-scenario overrides (`map.ini`) | pure-data | **easy** | 1 file, 3–45 lines |
| Terrain type reusing a tile | pure-data | **easy** | 5 lines |
| All display text (`.str` beats `.csf`) | pure-data | **easy** | ~840 labels for a skirmish TC |
| Menu/shell restyling | pure-data | **easy** | scriptable across 1,670 windows |
| New unit reusing existing art | pure-data | **easy** | 4 files, ~60 lines, **zero art** |
| Unit skin **keeping the texture filename** | data+art | **easy** | 1 `.dds`, zero INI |
| Cameo / portrait / UI sprite | data+art | **easy** | 8 lines + 1 image |
| Upgrades / sciences / powers | pure-data | moderate | 8–15 lines each |
| 4th faction as a sub-general | pure-data | moderate | 6–9 files, **zero art** |
| Command-bar theme per faction | data+art | moderate | ~88 lines + ~30 sprites |
| New terrain theme | data+art | moderate | ~25 TGAs (~5 MB) |
| New playable map | data+art | moderate | 1 `.map` + 1 preview |
| Audio — voices, weapons, EVA | data+art | moderate | ~37 takes per unit |
| New unit with new art | data+art | moderate | vehicle ~2 `.w3d`; **infantry ~18–24** |
| New structure | data+art | **hard** | 650–1,200 lines + ~1.9 MB art |
| 4th **base** side | data+art | **hard** | ~100 models + ~200 textures + voice bank |
| Unit skin under a **new** texture filename | needs-cpp | **hard** | binary `.w3d` edit or engine patch |
| More than 14 command-bar buttons | needs-cpp | **blocked** | `MAX_COMMANDS_PER_SET = 18` |
| New EVA event / audio trigger / Draw module / INI keyword / KindOf | needs-cpp | **blocked** | enum + name table + rebuild |

### The two findings that move the boundary

**Texture binding lives inside the `.w3d` binary.** The texture name is a NUL-terminated
string in `W3D_CHUNK_TEXTURE_NAME (0x32)`; `W3DModelDraw`'s ConditionState parse table
holds only `m_modelName` and bone names. So **"reskin a unit" is pure data only if the
replacement `.dds` keeps the shipped filename.** Renaming it is a binary edit. Resolution
rates once both archive layers are considered: INI→model **98.7%**, model→texture **99.9%**.

**Trap:** `housecolor2.tga` is shared by **1,832 models** — repainting it recolours the
entire army.

**Everything fails soft.** A missing string renders literally as `MISSING: 'label'`,
missing audio is silence, a missing MappedImage draws nothing, a missing ControlBarScheme
silently falls back to the Observer HUD, an over-budget terrain atlas renders the wrong
tile. Retail itself ships **95 dangling model refs, 18 dangling atlas refs, 12 dangling
terrain textures, 8 dangling string labels, 10 dangling map object names and 3 `Side`
mis-attributions**. Generated content can boot 90% broken and look fine.

### Ceilings already near saturation

`UPGRADE_MAX_COUNT = 128` with 82 used (**46 free**) · terrain atlas **780/784 slots** on
one retail map · 14 usable command-bar slots · 3 weapon slots · 4 veterancy levels ·
53 distinct `UnitSpecificSounds` keys against ~52 hardcoded · `NUM_GENERALS 12`.

---

## 3. Recipes

**Retune a unit** — drop 3–10 lines into `<MapDir>/map.ini`. Zero base modification, zero
assets. Retail uses this form 297 times across 33 map override files. This is the fastest
test loop in the engine and should be the default for any experiment.

**Reskin a unit, free path** — drop one `.dds` at the exact path the `.w3d` already names.
**Zero INI files touched.** If it doesn't change, the filename is wrong; dump the model's
strings to find the real one.

**Add a buildable unit** — minimum 4 files (~60 lines): the faction Object INI, a
`CommandButton` (`Command=UNIT_BUILD`), a numbered slot in the producing structure's
`CommandSet`, and 3 labels in `Generals.str`. **Zero binary assets** if you reuse an
existing model and cameo.

**Add a 4th faction** (as a sub-general) — 6–9 files, **zero art**. `Side` is a free-form
string (`INI::parseAsciiString`, `ThingTemplate.cpp:140`); nothing validates it. A minimum
viable faction is ~3,240 lines; a full general costs 17k–22k lines of which only **1.4–6.5%
is real intent**.

**Author a map programmatically** — *demonstrated*: a parser read all **116/116** retail
maps with zero failures, and a writer emitted a structurally complete new map (367,932
bytes, all 8 chunks) that re-parses clean. Compression is optional — `CompressionManager`
passes through anything that isn't `EAR\0`. `BlendTileData` is the only genuinely hard
chunk. Preview `.tga` is exactly 65,580 bytes in all 109 retail previews.

**New terrain theme** — ~25 uncompressed TGAs, imageType 2 (never RLE, never DDS —
`WorldHeightMap.cpp:1340-1380` parses the header itself), k×64 square with k ≤ 10. Maps
store texture-class *names*, so retexturing every shipped map is a `Terrain.ini` edit with
**no `.map` touched**.

---

## 4. Total conversion budget

Target: 3 factions, 60 units, 30 structures, 3 terrain themes, 10 maps, at retail fidelity.

**Binary art — ~2,400 assets, 450–600 MB.** ~1,160 `.w3d` (40 vehicles × 2; 20 infantry ×
18, one file per animation; 30 structures × 24 for the construction/damage/rubble/garrison
ladder — that matrix, not the base mesh, is the cost) · ~1,110 `.dds` · ~240 cameos ·
75 terrain TGAs · 1,500–2,200 `.wav` · 10–15 music tracks · 10 maps.

**Authored text — ~58,000 lines.** Objects ~38,700 · command buttons/sets 2,800 · combat
tables 2,660 · FX/OCL/particles ~3,000 · MappedImages 1,920 · audio events 4,000 ·
strings ~2,500.

Retail spent **549,372 INI lines** for the same job — a **9.5× tax**, almost all of it the
180,490 clone lines.

**Effort split: art is 90–95% of the cost and 100% of the schedule risk.** ~1,160 models at
0.5–2 days each is 3–9 person-years of modelling; the 58,000 lines of INI is 3–6
person-months.

> **The number that matters:** an AI agent working inside Zero Hour can author exactly the
> ~58,000 lines of text — **5–10% of a total conversion**. The other 90–95% is a binary
> asset pipeline it cannot touch. That is the hard ceiling on "an agent authors a ZH-like
> game" inside the real engine.

---

## 5. What this means for our platform

**The thesis, quantified.** 110:1 art-to-data bytes; 64.0% of object INI is Draw modules;
97.0% of general content is duplication. Our entire content pack is **141 lines / 5.8 KB**
with zero binary assets. Inside ZH an agent can author 5–10% of a game. On our platform
that 5–10% *is* the whole product.

**Our content debt is now measurable.** Retail's median buildable object is 245 code lines,
of which a median **120 are non-Draw** — and that residual is tightly bounded (p10 83,
p90 172), unlike art which runs 50 to 1,150+. Our unit prototype is ~10 lines. **We cover
roughly 8–12% of the residual concept surface.** That percentage is the honest roadmap KPI.

**Structures are the missing content type, not upgrades.** Retail splits 379 units / 261
structures, and a minimum viable faction is five objects of which **three are buildings** —
a CommandCenter, a cash drop-off, one factory. We have no structure concept, and
`CLAUDE.md` already names the consequence: *"rushes can only spawn-camp, there are no
economic targets."* A structure is a unit with speed 0, a role flag and a build site
instead of a queue — a smaller slice than upgrades that unblocks the harness's biggest gap.

**Do not copy `ObjectReskin`.** It is restricted to ten appearance-only fields
(`ThingTemplate.cpp:259-277`) and cannot change Side, cost, prerequisites, name, portrait
or voice — so EA cloned 477 objects wholesale instead. **The restriction is what creates
the duplication.** Our `modify` stays unrestricted; never add a presentation-only variant
type, however tempting once a renderer matures.

**Steal the layering model.** `Default/X.ini` + `X.ini` + `map.ini` is a working three-layer
pack system, and `INI::loadDirectory` sorts directory contents with the comment *"This keeps
things the same between machines in a network game"* — our ascending-iteration rule,
independently discovered. Spec packs as **N-layer last-wins, sorted Ordinal by path**, not a
two-file base+mod split.

**Fresh evidence for faction-scoped references.** Beyond the 67 defects already recorded,
retail ships three `Side` mis-attributions: `LaserGeneral.ini:5450` carries
`Side = AmericaSuperWeaponGeneral`; `InfantryGeneral.ini:2355` carries
`Side = ChinaTankGeneral`; `GLAVehicle.ini:6380` carries `Side = GLAToxinGeneral`.

---

## 6. Open question 4, closed

*"Do we import retail ZH as a reference content pack?"* — **No. Never.**

The GPL covers the engine, not the data. The 53×363 damage table and the core roster are
EA-authored content; a pack derived from them is a derivative work regardless of format.

The acceptance test is still worth having, so build it the legitimate way: a ZH-*shaped*
pack from first principles, named as an homage, asserting that `rts matrix` reproduces the
**qualitative** counter relationships — cannon loses to infantry, HMG beats infantry, rifle
does nothing to armour — rather than exact retail values.
