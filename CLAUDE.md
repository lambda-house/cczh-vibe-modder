# rts-skeleton — Claude Code project memory

**Goal: an AI modding engine for Command & Conquer: Generals Zero Hour.** An agent,
using skills and templates, authors content — factions, generals, units, armies —
which is validated and measured here and then **compiled to ZH `Data/INI` and run on
the real engine**. ZH supplies the runtime and the packaging; we supply the authoring
model, the validation, and the measurement.

The division of labour, and the reason each half exists:

- **ZH is the runtime.** `~/GeneralsX/GeneralsZH` (built native arm64) executes the
  output. Packaging is already solved: loose files shadow the `.big` archives — that is
  the engine's own mod mechanism, demonstrated live parsing 39,771 lines of loose INI
  with no archive present. A `.big` writer is a convenience, never a blocker.
- **Our sim is the pre-flight.** Lint, counter matrix and skirmish measure a pack in
  seconds — what playtesting costs hours. Determinism is what makes that measurement
  reproducible and attributable to a `contentHash`.
- **The extracted model is the vocabulary.** Their `FieldParse` tables are the target
  schema, so content and knowledge transfer in both directions.

Two consequences that shape every slice:

1. **Their caps are our caps.** 128 upgrade bits (82 used, 46 free), 14 usable command
   slots, 3 weapon slots, 4 veterancy levels, 38 damage types, a closed SpecialPowerType
   enum, `NUM_GENERALS 12`. A pack valid here but over their limits produces a mod that
   will not load. `rts lint --target zh` must enforce them.
2. **Emittable ≠ simulated.** A unit needs a `CommandButton`, a numbered `CommandSet`
   slot and string labels or it is invisible in-game however correct its stats are. We
   must EMIT fields we will never simulate. Coverage therefore has two axes; conflating
   them ships packs that measure beautifully and never appear.

**Divergence is the standing risk.** Two engines now compute the same battle. Where our
model and theirs disagree — additive-in-excess bonuses, `ceil()` tick quantisation, the
two-band splash step — our numbers stop being predictions. Everything copied verbatim
from their source was for exactly this reason, and it is now testable rather than
theoretical.

**Art is deferred, not solved.** 32.9% of shipped models (2,928) are referenced by
nothing and are free to adopt, so new content is playable on existing art immediately.
That is a development accelerator and a personal-use path — **not** a distribution plan.

Deterministic, data-driven RTS sim core (C&C Generals mold). Headless sim +
content model + balancing harness + Godot inspection shell. Full context: README.md.

## Reference material (on disk, no retail assets needed)

Two measured studies justify the roadmap below; every slice cites a number from one
of them.

- `docs/ZERO-HOUR-ANATOMY.md` — the **content model**, measured from the community corpus.
- `docs/ZERO-HOUR-ASSETS.md` — the **shipped assets** and what can be replaced, measured
  from retail. Where the two disagree on a count, **retail wins** (the 104p patch adds
  content: 363 weapons vs 420, 53 armor sets vs 57, 96 sciences vs 100).
- `docs/ZERO-HOUR-MAPS.md` — the **terrain format**, decoded from all 150 shipped maps. The
  container, the eight chunks, the two scale constants and the slope at which their engine
  decides a cell is a cliff.

Sources on disk:

- `~/work/oss/CnC_Generals_Zero_Hour` — EA's GPL v3 source. **Schema authority**:
  the `FieldParse` tables enumerate every legal key of every content type.
- `~/work/oss/zh-retail/Data/INI` — retail INI, 118 files / 538,362 lines, extracted
  from `INIZH.big`. **Authoritative for planning.**
- `~/work/oss/GeneralsGamePatch/.../Data/INI` — the 104p community corpus, 581,467 lines.
- `~/GeneralsX/GeneralsZH` — retail install, **running natively on arm64**. 35 `.big`
  archives in two layers (ZH is a delta over base Generals in `ZH_Generals/`):
  26,264 files, 2.23 GB, 9,000 models, 7,891 textures.
  `~/GeneralsX/content-mode.sh stock|104p|status` toggles the loose-file overlay.
- `scratchpad/bigtool.py` — `.big` reader/extractor (`list|tree|names|extract`).

**Legal, settled: the GPL covers the engine source only.** `Data/INI`, the `.big`
archives, `generals.csf`, the maps and all art remain EA-copyrighted. No retail-derived
content pack ever ships from this project. If we want the combat-core acceptance test,
build a ZH-*shaped* pack from first principles and assert *qualitative* counter
relationships, not retail values. Do not relitigate this.

Quote measured numbers from these docs rather than reasoning from general RTS knowledge —
the corpus repeatedly contradicts intuition (terrain is gameplay-dead; bonuses are
additive-in-excess, not multiplicative; the generals are forks, not diffs; and art is
110:1 of the bytes but 0% of the simulation).

## Modding the retail game: rules measured against the RUNNING engine

Emitting new content and MODIFYING retail content are different problems with different
mechanics. Each rule below cost a crash, a hang or a silent misfire to establish, and several
contradict what EA's source implies — the GPL tree is schema authority, the running GeneralsX
build is loading authority, and **they diverge**.

1. **`Data/INI/**` is ADD-NEW-NAMES ONLY.** Redeclaring an existing block there is a fatal
   load error: the engine aborts at 29 of 42 subsystems and never reaches its main loop.
   EA's source reads as though `INI_LOAD_OVERWRITE` would patch in place — both
   `ThingFactory::parseObjectDefinition` and `ControlBar::parseCommandSetDefinition` find the
   existing block and call `initFromINI` — but both guard the duplicate with `DEBUG_CRASH`,
   which is compiled into this build. Proven with a probe that redeclared
   `AmericaWarFactoryCommandSet` using nothing but a RETAIL button.
   *Consequence: a pack cannot extend a retail faction's menu. A pack is a NEW FACTION.*
2. **`<mapdir>/map.ini` is the ONLY override channel.** `GameLogic::loadMapINI` is the sole
   caller using `INI_LOAD_CREATE_OVERRIDES`, and it runs from `startNewGame` — so overrides
   reload on every match start, and iterating costs a match restart, not a game restart.
3. **Never declare a NEW name in `map.ini`.** It gets marked as an override, and
   `LocomotorStore::reset()` (Locomotor.cpp:573) then calls `m_locomotorTemplates.erase(it)`
   WITHOUT reassigning the iterator — the loop never advances and the game hangs on match
   teardown. `Science.cpp` writes the same loop correctly. Confirmed by sampling a beachballed
   process: 448 of 813 samples in that function.
4. **Never patch a shared leaf in place; it does not merely fail, it BREAKS the leaf.**
   `WeaponStore::newOverride` copies the parent then chains new->old via
   `friend_setNextTemplate`, while the store's lookup still returns the ORIGINAL — the override
   is reachable by nothing and the weapon goes inert. Overriding a Ranger's rifle damage took
   it from 5 to ZERO. `ThingFactory::newOverride` chains the other way
   (`child->setNextOverride`), which is why OBJECT overrides work.
   **The pattern that works: new leaf in `Data/INI`, then override the Object to point at it.**
   Proven for both weapons and locomotors.
5. **A `WeaponSet`/`ArmorSet` inside an override is TOTAL, not additive.**
   `ThingFactory::newOverride` calls `setCopiedFromDefault()`, so the first set parsed clears
   every inherited one. Restate all of them or none.
6. **Modules: `AddModule` to add, `ReplaceModule` to replace** — same module type, and a NEW
   UNIQUE tag. Syntax verbatim from their shipped `Maps/MD_USA01_CINE/map.ini`.
7. **`GameData` is NOT reachable from `Data/INI` on this build.** A file under
   `Data/INI/GameData/` parses cleanly and changes nothing — probed with
   `DefaultStartingCash`, which stayed at retail 10000. That directory scan is a GeneralsX
   addition; EA's `initSubsystem` passes only `Default/GameData.ini` and `GameData.ini` with
   no dirpath. **Never conclude a load path works because the file parsed without error** —
   carry a witness field whose effect is unmissable.

Retail's own 41 `map.ini` files corroborate the shape: `Object` 272, `Buildable` 64,
`CommandSet` 27, `Science` 26, `Locomotor` 18, `Upgrade` 14, `Weapon` 6, `SpecialPower` 4,
`ParticleSystem` 4 — and **zero** `GameData`. They override locomotors but never declare a new
one there (rule 3), and touch weapons only alongside an object override (rule 4).

8. **A new faction needs its own `ControlBarScheme` or it is selectable and UNPLAYABLE.**
   `setControlBarSchemeByPlayer` matches `CBScheme->m_side` against the player's Side and
   leaves the bar unset when nothing matches; retail ships schemes for America, China, GLA and
   Observer only. We emit a DELIBERATELY MINIMAL one — retail's are 87 lines of layout, and
   restating them would copy EA's content for no simulation benefit. Note the syntax: these
   fields take NO `=` sign (`Side hellfire`, not `Side = hellfire`).

9. **A parsing object is not a WORKING object, and the gaps are found by SWEEP not by play.**
   `zhasset objectdiff` diffs every object we emit against the retail objects that share its
   mesh, and triages what only they set into behaviour / cosmetic / peer-specific. Four bugs
   running had the same shape — a field whose absence is SILENT and whose default is wrong for
   our case — and every one was found by a human noticing something in a match:

   | Field | Default | What it cost |
   |---|---|---|
   | `InitialHealth` | **0**, not MaxHealth | every unit spawned on 0 HP: no health bar, and a body that never crosses >0 -> <=0 never fires its die modules, so corpses persisted and kept clearing fog |
   | `Turret` (in Draw) | no bone | the LOGIC turret in `AIUpdateInterface` aims and fires perfectly while the gun stays welded to the hull. **Two different `Turret` concepts in different modules**; retail has 516 of the art-side one |
   | `ProductionUpdate` | absent | build buttons render and clicking does nothing |
   | `PhysicsBehavior` | absent | unit exists, cannot move or be selected |

   The sweep then found ten more in one pass — `ExperienceValue`, `ExperienceRequired`,
   `IsTrainable`, `CrusherLevel`, `CrushableLevel`, `TransportSlotCount`, `GeometryIsSmall`,
   `RadarPriority` — all now emitted and derived from content where content has the answer
   (veterancy thresholds become `ExperienceRequired`). e2e asserts zero behaviour gaps, so the
   next one is caught at build time rather than in someone's match.

   *The catalogue is measured for adopted art and DECLARED for authored art, and `artprofile`
   preserves the authored entries when it regenerates — a rebuild once wiped RTSBOX's declared
   profile and the compiler silently fell back to guessed geometry. Caught by this same gate.*

9b. **A parsing object is not a WORKING object.** Every gap below was silent — the file
   parses, the engine boots 42/42, and something simply never happens. Found by field-diffing
   our emitted objects against `AmericaWarFactory` and `AmericaTankCrusader`:

   | Missing | Symptom |
   |---|---|
   | `ProductionUpdate` | build buttons render, clicking does nothing |
   | `DefaultProductionExitUpdate` | no door to exit through |
   | `NumDoorAnimations` > 0 with no door states | queue fills and never drains |
   | `PhysicsBehavior` | unit is created but cannot move or be selected |
   | `BuildCompletion` | authored in our content, never emitted |
   | `FactoryExitWidth` | defaults to 0 |
   | `SelectPortrait` / `ButtonImage` | blank tiles in the control bar |
   | `ControlBarScheme` for the Side | faction selectable but has no command bar |

10. **ADOPTED ART HAS A SCHEMA.** "2,928 unreferenced models are free to adopt" is measured
   and true, and it is about RENDERING only. A mesh comes with a contract, and every clause
   of it fails SILENTLY — the pack lints, compiles, boots 42/42 and plays wrong:

   | Clause | Get it wrong and… |
   |---|---|
   | dimensions | units are created INSIDE the building and never seen |
   | bones (`WeaponLaunchBone`, `WeaponFireFXBone`) | shots come from the wrong place |
   | sub-objects (`WeaponMuzzleFlash`) | the flash mesh is never hidden — a permanent flame |
   | turret (`Turret` + `ControlledWeaponSlots`) | the unit never fires, with no error |
   | `AutoAcquireEnemiesWhenIdle` | the unit watches enemies walk past it |

   All four were hit adopting ONE model, `AVCrusader`. This belongs in "Divergence is the
   standing risk", not under "art is deferred": art is deferred as *authoring*, but adopting
   it is a live source of wrong behaviour today.

   *An adopted model therefore needs an ART PROFILE — geometry, bones, sub-objects, turret —
   and the profile is per-MESH, not per-unit. `zh.artRig` and `zh.turreted` are the stopgap;
   structure geometry is still hardcoded to AmericaWarFactory's numbers. The real fix is a
   catalogue of adoptable meshes with measured profiles, which `tools/zhasset` can generate.*

14. **The `.map` format is READ AND WRITTEN, and passability is DERIVED not authored.**
   `docs/ZERO-HOUR-MAPS.md` has the whole study; the four facts that shape code here:
   - **Compression is optional.** `CachedFileInputStream::open` sniffs a magic and falls
     through to raw bytes. 4 of 150 shipped maps are raw, so our writer needs no compressor.
   - **A height field is one unsigned byte per vertex**, 10 world units apart
     (`MAP_XY_FACTOR`), 0.625 units per byte (`MAP_HEIGHT_SCALE`). Total relief is therefore
     capped at 159 world units — the real constraint on importing real elevation.
   - **They have no authored passability layer at all.** `setCellCliffFlagFromHeights` calls a
     cell a cliff when its corners differ by more than `PATHFIND_CLIFF_SLOPE_LIMIT_F` = 9.8
     units, i.e. **16 height bytes**. Our blocked cells are emitted as a plateau and their
     pathfinder derives the block. A 15-byte step is a wall units walk over, silently.
   - **The border is drawn and NOT pathable.** `getMaximumPathfindExtent` returns the playable
     boundary only. A reachability check over the whole grid reports every full-width barrier
     as leaky — which is exactly what the first version of ours did.
   *The map is always RESAMPLED: our cell size is a power of two in our units, theirs is fixed
   at 10 of theirs, and the two have no common divisor. The writer preserves the world SPAN,
   because span is what a measured battle length depends on; lint reports the ratio when a
   feature could be narrower than one of their cells.*

15. **Content icons are AUTHORED; HUD furniture is borrowed on purpose.** A compiled pack
   used to point at retail button art for every object, button and upgrade
   (`SelectPortrait = SACWeaponsfact_L`), so a pack whose mesh, texture, skeleton and
   animation were all authored still could not be LOOKED at without EA's UI.
   `Data/INI/MappedImages/HandCreated` is a directory scan EA's own source admits to
   (`Image.cpp:256`), so new names there are additive with no probe needed. `rts compile` now
   writes one icon sheet plus its `MappedImage` blocks.
   **Measured: a pack on wholly authored art borrows 8 retail names, and all 8 are the
   `zh.sides` HUD** (bar backdrop, medallions, gen bar, exp bar). That line is deliberate: a
   per-unit portrait is content, an 800-pixel command bar is EA's UI design and a pack has no
   reason to reinvent it to prove a unit works. Adopting a mesh adds its own name plus its
   death FX — the price of adoption, not an unfinished edge.
   *The compiler now writes ART, which it never did before. A `MappedImage` whose texture is
   absent renders as a blank tile with no error, so both halves are emitted or neither.*
16. **The additive directory list is MEASURED FROM THE BOOT LOG, and 42 types are scanned.**
   `./run-logged.sh` captures it (see below); the list this build actually scans is:
   `AIData Armor AudioSettings Campaign ChallengeMode CommandButton CommandMap CommandSet
   ControlBarScheme Crate DamageFX DrawGroupInfo Eva FXList GameData GameLOD GameLODPresets
   InGameUI Locomotor MiscAudio Mouse Multiplayer Music Object ObjectCreationList
   ParticleSystem PlayerTemplate Rank Roads Science ShellMenuScheme SoundEffects SpecialPower
   Speech Terrain Upgrade Video Voice Water Weapon Weather WindowTransitions`.
   **`FXList` AND `ParticleSystem` are both there.** *An earlier version of this entry said
   ParticleSystem was NOT loadable, reasoned from `initSubsystem(TheParticleSystemManager,
   ..., nullptr)` in GeneralsX's source — and it was WRONG: the manager's own `init()` calls
   `loadFileDirectory` internally, which no amount of reading the call site reveals.* That is
   this file's own rule ("check the boot log's `loadDirectory` lines — not the source")
   violated by the person who wrote it. Note `GameData` is scanned and still does nothing
   (rule 11): a directory being scanned is necessary, not sufficient.
17. **The engine logs copiously and `run.sh` throws it away.** `GeneralsXZH` writes `[INI]`,
   `[CSF]`, `[SUBSYS]`, `[Skirmish]` and `[SHUTDOWN]` lines to stdout — ~12,400 of them for
   one boot. `~/GeneralsX/GeneralsZH/run-logged.sh` captures them to `logs/latest.log`
   through a pty (plain redirection block-buffers and loses the tail of a crashed run).
   *EA's own `DEBUG_LOG` layer is a separate thing and IS compiled out here — `strings` finds
   `ReleaseCrashInfo.txt` but no `DebugLogFile`. Looking for that file, not finding it, and
   concluding "this build has no logging" is the trap; GeneralsX's logging is always on.*
   Rebuilding with `-DRTS_DEBUG_LOGGING=ON` would add EA's layer, but that build stops in
   DXVK for want of `glslangValidator` and has not been needed.

**VALIDATED END TO END, IN A REAL MATCH.** A faction authored here is selectable in the
skirmish dropdown, has a working command bar, builds a unit authored here, and that unit
rolls out of the factory and is selectable and commandable. Separately, a
RETAIL unit's damage, locomotion, scale and model are all modifiable from a pack. Both halves
are emitted by `rts compile`, not hand-written.

11. **`GameData` IS NOT MODDABLE BY DATA on this build — from either channel.** Closed, not
   open. A `GameData` block under `Data/INI/GameData/` parses and does nothing; the same block
   in `map.ini` also does nothing. Both were probed with `DefaultStartingCash = 999999`, which
   stayed at retail 10000 in both cases. That is why camera zoom could not be changed: every
   `MaxCameraHeight` we set was never in play. Everything downstream checked out — the wheel
   really does drive `View::zoomOut()` -> `setHeightAboveGround(+10)` clamped by
   `m_maxCameraHeight`, `calcCameraConstraints` clamps x/y only, and `GlobalData::reset()` pops
   to the original instance an in-place patch writes to. The wall is the LOAD PATH, and it is a
   GeneralsX divergence: EA's `initSubsystem` passes no dirpath for `TheWritableGlobalData`, and
   no retail `map.ini` overrides `GameData` either. Do not spend more time on it from content.

12. **The model format is READ AND WRITTEN, verified byte-identically.** W3D is IFF-style
   chunks and the taxonomy is EA's GPL header `Libraries/Source/WWVegas/WW3D2/w3d_file.h` — engine source,
   the half the GPL covers. `zhasset w3dround` re-emits every sample byte-for-byte (the high
   bit of `size` marks a container, so sizes are recomputed, not remembered), and
   `zhasset w3dbox` AUTHORS geometry — vertices, normals, per-face normals with plane
   distances, and UVs, all computed — into a template that supplies only material state.
   **Confirmed rendering in-game.** Units are 220-245 triangles, so re-modelling is a modest
   art task; the standalone zero-EA-art route is engineering, not aspiration.
   **Topology is arbitrary**, verified in-game: a 20-sided cylinder of 122 vertices / 80
   triangles written into a 24-vertex template renders as a cylinder. That needs
   `MESH_HEADER3`'s `NumTris` (offset 40) and `NumVertices` (offset 44) rewritten AND every
   per-vertex array resized in step — `VERTICES`, `VERTEX_NORMALS`, `VERTEX_SHADE_INDICES`,
   `STAGE_TEXCOORDS` — with `TRIANGLES` indexing into them. A mismatch between any of them
   renders as nothing at all. Bounds come from the actual vertices, not the requested size,
   which differs for anything that is not a box.
   *An authored mesh DECLARES its own art profile as it is written, so the catalogue is
   measured for adopted art and declared for authored art.*
   **Textures are authored too** (`zhasset tga`): uncompressed 24-bit TGA, an 18-byte header
   then BGR triples, the one image format writable with no library. Retail is 3,496 `.dds` to
   50 `.tga`, so DDS is the house format, but TGA ships and is supported and needs no DXT
   compressor. `--texture` rewrites `TEXTURE_NAME`. A cylinder with authored geometry AND an
   authored surface renders in-game: **nothing of EA's is left in that object but the material
   flag words.**

   **UV density must be uniform, and only the render shows it.** Assigning 0..1 per surface
   gives every face the whole texture regardless of size — a cylinder's side cells came out
   3.1x wider than its cap cells. Generators now emit UVs in WORLD UNITS (arc length, real
   edge lengths) normalised by ONE global span, which also keeps every coordinate inside 0..1
   so no texture wrapping is assumed. Caught by eye from a screenshot, not by any test here;
   e2e now asserts the ratio from the emitted texcoords.
   *A grid texture rather than a flat colour was the reason it was visible at all — a solid
   colour proves the file loads and hides everything about the mapping.*

   **Smooth shading is per-VERTEX normals varying across a face, NOT shared vertices.**
   Duplicated seam vertices are fine as long as they AGREE on their normal; sharing positions
   is neither necessary nor sufficient. Curved surfaces now get analytic normals (radial on a
   cylinder wall, face-blended-with-outward at a pyramid's base), and a box stays flat because
   averaging a cube's corners rounds them into mush. Confirmed in-game.
   *This exposed a latent bug: the `TRIANGLES` chunk carries the GEOMETRIC plane normal, and
   the writer had been reusing a vertex normal there. Correct only while the two coincided
   under flat shading — the moment smooth normals made them deliberately differ, culling and
   the plane distance would have been fed a lie. It is now computed from the winding.*

13. **Skeletons, skinning and animation are AUTHORED too — the format is now closed end to
   end.** `zhasset w3dskel|w3danim` and `w3dbox --skin` write a hierarchy, bind a mesh to it
   and move it; confirmed in-game as visible motion on a fully authored object. **Nothing of
   EA's remains in it but the material flag words.**
   - **One bone per vertex, no weights.** `W3dVertInfStruct` is 8 bytes: a `uint16` bone
     index and 6 bytes of padding. There is no blend, no second influence — smooth skinning
     is not available in this mesh type. Seams therefore belong AT JOINTS, where the hard
     boundary reads as a bend; put one across a smooth surface and it shears, which is
     exactly what a rotating cylinder showed. That is the format's limit, not a bug to fix.
   - **Skinned vertices are stored in BONE-LOCAL bind-pose space**, not model space. Every
     vertex must be shifted by the inverse of its bone's rest transform before writing, or
     the mesh explodes to the pivot offsets on the first frame.
   - **An HLOD is MANDATORY for a skin, and binding is BY NAME**, never by file: the
     `HLOD_HEADER`'s `HierarchyName` must equal the skeleton's `HIERARCHY_HEADER.Name`, and
     the sub-object name must equal the mesh's `ContainerName`. Filenames play no part.
   - **Animations register as `HierarchyName + "." + Name`** — so `zh.animations` names
     `RTSMAST_SKL.RTSSPIN`, not the `.w3d` it lives in. Uncompressed `0x200` is what shipped
     assets use; the compressed families are not needed.
   - **Element counts are DIVIDED out of chunk sizes, never stated.** `VERTEX_INFLUENCES` is
     `NumVertices * 8`, a channel payload is `frames * VectorLen * 4`. A wrong struct size
     yields the wrong number of bones or frames rather than an error, so e2e asserts each
     division explicitly.
   - *The header to cite is `Libraries/Source/WWVegas/WW3D2/w3d_file.h`. `Tools/WW3D/pluglib`
     has a stale copy that stops at chunk `0x600` and has no HLOD at all — following it would
     make a skin that silently never binds.*

## Driving the running game

`tools/run-logged.sh` (installed to the game dir) and `tools/zhdrive` close the loop that
every silent bug in the catalogue above escaped through — each was found by a human noticing
something in a match.

- `zhdrive log --dirs` — the additive directory list THIS build scans, from the boot log.
- `zhdrive log --errors` — distinct error shapes, deduped by digit-normalising.
- `zhdrive shot` / `zhdrive wait <regex>` — screenshot, or block until the engine logs a line.
  Waiting on the log beats sleeping a guess: boot time varies threefold with disk cache.
- `zhdrive skirmish` — launch, skip the intro, drive into a RUNNING MATCH, unattended.
- `zhdrive ui <target>` / `zhdrive pixel x y` — click or sample in the game's own 800x600
  space. Targets are expressed there, not in screen coordinates, because that is the space
  `ControlBarScheme` authors in (`ScreenCreationRes X:800 Y:600`) and it does not move when
  the window does.

Launch with **`-quickstart`** (= `-nologo -noshellmap` + no window animation). `parseNoLogo`
sets `m_playIntro = FALSE`, which is the supported way past the intro movie; pressing `esc` at
it was always a workaround and it is what made the first drives flaky.

**Every step must be CONFIRMED, never slept through.** The first scripted drive slept fixed
intervals, fired all three clicks into the intro movie and reported "in match (probably)".
The rewrite waits on a *signal* per step — a log line where one exists
(`SkirmishGameOptionsMenu.wnd`), a pixel where none does (the intro movie is not a layout, so
`MainMenu.wnd` is already pushed while the movie still owns the keyboard) — and retries the
click pair when it did not land. **It needed that retry on its very first run.**
*Accessibility is granted to the RESPONSIBLE PROCESS: Terminal.app when Claude Code runs as
its child, the `claude` binary when it runs as a daemon. Granting the wrong one is silent.*

Four traps, each of which cost a run:
- **Focus first, every attempt.** macOS eats the first click on an unfocused window. Attempt 1
  failed on EVERY drive — deterministically, and that determinism is the tell. A stolen focus
  also corrupts PIXEL READS, which is worse than a lost click because another app's colours
  come back as data rather than as an error.
- **Park the cursor centre after every click.** An RTS scrolls whenever the pointer rests near
  an edge, and a build button at the bottom of the command bar IS in the scroll margin — so
  clicking one and then pausing pans the world out from under every later world coordinate.
- **Identify a menu by COUNTING its buttons, not by probing a point.** Main menu 6, Solo Play
  submenu 7, and they are offset — so one screen's text pixel is the next screen's border.
  Each button draws a top and bottom border, so the cyan runs come in PAIRS: divide by two.
- **`esc` does not leave the Solo Play submenu**, which has only a BACK button. A retry that
  only presses `esc` waits forever on a screen it cannot leave.

`zhdrive verify-pack` is the payoff, and `e2e.sh` gate 25 runs it behind **`ZH_PLAY=1`**
(opt-in: it needs the install, Accessibility and ~2 minutes). It asserts what only a running
match can: the portrait and the build button are SATURATED authored icons rather than the dark
panel a dangling `MappedImage` renders as, the two address DIFFERENT cells, and clicking build
CHARGES the player. Proven in both directions — deleting the emitted `MappedImages` file makes
all three fail, and the portrait reads pure black.
*The structure is found by COLOUR, never by coordinate: the start position is randomised, so
the same map put the factory at game (512,139) one run and (297,165) the next.*

**Observe needs no permission and answers every LOAD-TIME question**, which is where every
bug so far has actually lived. Act buys "click build and see" and nothing before it.
*Do not use `osascript -e 'tell application "Finder" to get bounds of window of desktop'` to
find the screen size — the usual recipe, and it HUNG this tool with no timeout. `zhdrive`
uses `system_profiler`. Coordinates are physical pixels in a screenshot and LOGICAL points to
cliclick; on this Retina panel they differ by 2x.*

## Build / verify

- Build: `dotnet build -c Release` (needs .NET 8 SDK; zero NuGet deps by design)
- Run: `dotnet bin/Release/net8.0/rts.dll <verb>` — verbs: `lint`, `duel`, `matrix`,
  `econ`, `faction`, `replay`
- **`./e2e.sh` runs the whole gate below plus the demos and the shell check.** Prefer it.
  **Add a gate by APPENDING a `gate "title"` call — never write the number.** The count is
  derived from the call sites, so two branches can each add one without touching each other's
  lines; the old literal `gate N/22` put the denominator on all 22 lines and made every
  parallel slice conflict across the whole file.
- **After ANY change under Core/, Content/, or Runtime/, run all four and treat failure as a broken build:**
  1. `rts lint`
  2. `rts replay --a crusader --b battlemaster --seed 7` → must print `DETERMINISM OK`
  3. `rts duel --a crusader --b technical --n 20 --seed 42` (smoke)
  4. `rts econ --a "technical*" --b "war_factory,crusader*" --n 20 --seed 42`
     → must print `determinism: OK` (covers the production system's replay surface)
- If `godot/` is touched, also run `./e2e.sh` — it asserts the shell reaches the
  same final state hashes as the harness. A mismatch means presentation has
  leaked into the sim; treat it as a determinism bug, not a rendering bug.

## Hard invariants — never violate, never "optimize away"

- **No `float`/`double` in sim state or sim logic.** All sim math uses `Fix64`
  (Q32.32). Doubles exist only at the content-load boundary
  (`Fix64.FromDoubleAtLoadBoundary`) and in display formatting
  (`ToDoubleForDisplay`). Introducing a float into Runtime/ is a determinism
  bug even if all tests pass on this machine.
- **System order in `Sim.Step` is part of the replay contract:** commands →
  production/economy → **garrison/capture** → **loadout/stat re-resolve** → cooldowns →
  targeting → movement → combat. The holding phase is pinned ahead of loadout because
  capture flips a structure's TEAM, and team is read by targeting (who is an enemy), by
  production (whose factory) and by the defeat test — settle ownership before anything
  asks whose it is. The loadout phase is not optional bookkeeping: a flag gained
  this tick re-selects a conditional variant, which changes weapon and armor class —
  i.e. stats. Without its own pinned phase, whether a shot used the old or new weapon
  would depend on which system ran first, intermittently. Do not
  reorder, merge, or parallelize across units without an explicit design
  decision. Within production: income accrual, then research queue, then unit
  queue, teams in ascending index — research has funding priority over units.
- **Iteration is always ascending unit index.** No dictionary/hash-order
  iteration over sim state, no LINQ ordering that isn't explicitly `Ordinal`.
  **This extends to pack loading:** when layered packs land (slice 1), a directory
  scan must be sorted `Ordinal` by path before loading. ZH's `INI::loadDirectory`
  sorts for exactly this reason — its comment reads *"This keeps things the same
  between machines in a network game."* A directory-scan loader is the most likely
  future source of hash-order nondeterminism in this codebase.
- **A faction variant is a COMPLETE clone, never a hand-listed subset of fields.**
  `UnitProto.CloneForFaction` is memberwise on purpose, so a field added to `UnitProto`
  is carried automatically. *This was a live bug:* the old object initializer copied 9 of
  20 fields, so patching nothing but a war factory's cost dropped its `KindOf` and the
  building stopped being a structure — `rts compile` emitted it with a Locomotor. `Rules`,
  conditional `Variants`, `EnergyProduction` and `BirthFlags` vanished the same way. If you
  add a mutable array field, deep-copy it there; do not revert to an explicit list.
  Note the ordering trap that made it worse: rules compile AFTER `ResolveFactions`, so the
  clone sees an empty rule array and rules must be handed to variants in the rules pass.
- **Content resolves in ONE total order, three stages, each exactly once.** `PackStack` is
  the authoritative statement of it; this is the summary.
  1. **LAYER** — packs fold in ordinal order, per-key last-wins, `null` REMOVES. Produces one
     composed DTO and knows nothing of units or factions.
  2. **DEFAULT** — the composed `defaults` block fills every unit field left unstated.
  3. **INHERIT + ROSTER** — faction `extends` flattens parents before children; within one
     faction, strictly `remove` → `add` → `modify`.

  **There is no entity-level `extends` and there will not be one.** A unit never inherits from
  a unit. Faction `modify` plus layering already express every case, and a third inheritance
  axis is the complexity this order exists to bound.

  Each stage completing before the next is what makes the awkward questions answerable:
  a `modify` is a delta on whatever the unit ends up being *after* layering, so a mod that
  retunes a unit keeps every faction's tweaks on top of the new definition. **"A pack patch
  removes what a faction modify replaced" is therefore not a merge conflict at all** — the
  removal lands in stage 1, so by stage 3 the name does not exist and every reference is an
  ordinary dangling reference, reported with the layer that took it away. The corollary that
  surprises authors: a later layer cannot *amend* a faction's `modify` block, because factions
  are keyed entries and it replaces the whole faction. Layering is for deltas BETWEEN packs;
  faction `extends` is for deltas WITHIN one.

  The two intra-faction adjacencies each buy something. **remove before add** is the DE-FORK
  idiom — drop the parent's variant, re-adopt the shared prototype; add-first would make it a
  duplicate-key error with no way to say "inherit everything except this fork". **remove
  before modify** makes the contradictory pair a deterministic error rather than a patch that
  is computed, allocated and then silently discarded.
- **EVERY hand-written merge must be TOTAL over its DTO, and all of them are checked by
  reflection on every load.** *This was a live bug of exactly the `CloneForFaction` shape:*
  `defaults` was missing from the merge's object initializer, so every multi-layer stack
  silently reverted to the compiler's built-ins. Nothing failed; the numbers were just wrong.
  `AssertMergeIsTotal` now covers `ContentPackDto`, `ZhTargetDto` and `UnitDefaultsDto` —
  all three are merged by object initializers of the same shape, and guarding only the root
  left the other two exposed. Only WRITABLE properties count: a computed one (`ZhTargetDto.Scale`)
  is derived from merged state, not state. e2e probes each type generatively — add a property,
  assert the load is refused, put it back.
  *The hazard this actually buys off is PARALLEL WORK.* Two branches each adding one field to
  `ZhTargetDto` is the likeliest way to lose one: git merges two added initializer lines
  without complaint, and a dropped line reverts that field in every layered stack, silently.
- **In a layered block, ABSENT is not the same as DEFAULT.** A non-nullable field with a C#
  initializer cannot tell "this layer omitted it" from "this layer set it to the default", so
  a mod saying nothing would overwrite the base pack's value with the compiler's. `defaults`,
  `lint`, `ranks` and `zh.worldScale` are therefore nullable, and the root fallbacks live in
  exactly one place (`UnitDefaultsDto.Root`). Any new pack-wide block must be nullable too.
- **Reference resolution is faction-scoped for variants and global for everything else.**
  `ContentDb.ResolveUnitRef` goes through the owning faction's *resolved roster* — never by
  mangling a name into `fid/ref`, because inheritance means only the roster knows which
  fork applies. A shared prototype must NOT be scoped: it is used by every faction that did
  not fork it, so one reference array cannot mean different things to each. That case is
  reported by lint. Retail proves the cost of getting this wrong: 88 bad references / 68
  broken definitions shipped, and `Prerequisites` accounts for none of them — 64 are
  references reached through a shared intermediary.
- **Schema authority is their source; LOADING behaviour is the running engine.** The two
  differ. EA's `GameEngine.cpp` passes no directory to `TheScienceStore`, yet the build we
  target logs `loadDirectory('Data\INI\Science')`. Before assuming a content type can be
  emitted additively, check the boot log's `loadDirectory` lines — not the source.
  The one type that genuinely cannot be additive is `Rank`: its blocks are NUMBERED, not
  named, so emitting a ladder overwrites retail's instead of extending it.
- **Never invent an enum value, and never confirm one by substring.** Every literal
  `ZhCompiler` emits must be copied from a retail block that demonstrably loads, and closed
  enums are checked against their C++ name table, not against a grep of the INI. Five have
  bitten us: `NO_Z_MOTION` (real: `NO_Z_MOTIVE_FORCE`), `Options = CANCELABLE`,
  `IS_PREREQUISITE`, `SpreadFormation = 32` (a BOOL), and `GARRISONABLE` — which greps as
  present only because it is a substring of `GARRISONABLE_UNTIL_DESTROYED`. Each was a hard
  load error found only by booting the engine.
- **Ties break deterministically** (e.g. targeting: `(distSq, unitIdx)`).
  Every new comparison needs a total order.
- **Terrain is CONTENT; a route is STATE.** The passability grid never changes during a
  match, so it folds into `contentHash` and never into the state hash. A unit's current
  route is the opposite: *when* it was planned decides which way it goes round an obstacle,
  so `PathGoalCell`, the cursor and the live tail of waypoints are all hashed (gated on
  `HasPassability`). Treating a path as a cache is the mistake that makes two runs diverge
  only under load, when one of them happens to replan a tick later.
- **A pathfinder is ADVICE; the step is where passability is ENFORCED.** *This was live for
  the first hour of the slice:* A* was correct, routes were sensible, and a SOLID wall with
  no gap in it changed the outcome of a battle by nothing whatsoever — because any unit whose
  search failed fell back to walking straight, and straight went through the wall. Every
  position change must be refused by terrain independently of whether anything planned it.
  Blocked movement SLIDES (full step, then x alone, then y alone, fixed order) so a unit
  brushing a wall keeps fighting instead of freezing for reasons no state hash explains.
- **World→cell is a SHIFT, never a division**, so `cellSize` must be a power of two and lint
  rejects anything else. Checked on the Fix64 *raw*, not the authored double: 0.1 looks like
  a fine number, is not representable, and would leave the grid and the mover disagreeing
  about which cell a unit is in. An arithmetic shift right also floors toward negative
  infinity, which is the rounding a centred grid needs — truncation folds -0 into +0 and puts
  a seam down the middle of every map.
- **A diagonal step needs BOTH orthogonal cells**, in the pathfinder and in the line test
  alike. Without it a unit squeezes through the corner where two blocked cells touch — a wall
  with no gap that units walk through anyway. The line test is a supercover walk for the same
  reason; Bresenham slips between diagonal neighbours.
- **Map rows are oriented to the RENDERER, not to a compass.** Row 0 is the lowest world y
  because the shell draws +y downward, as every 2D canvas does. The alternative reads as
  north-up in the JSON and appears vertically mirrored in play — a bug invisible from either
  the file or the screen alone.
- **Sight and passability are INDEPENDENT, and the pair is the model.** Water stops ground
  movement and hides nothing; a cliff does both. `HasPassability` and `HasLineOfSight` are
  therefore separate opt-in flags — a map of nothing but river changes movement and must not
  perturb a single shot. **The diagonal rule INVERTS between them:** movement needs BOTH
  orthogonal cells clear to cut a corner because a body has width, while sight needs only ONE
  because a line has none. Using the movement rule for sight makes every diagonal corner
  opaque and hands cover to positions that have not earned it.
  *Endpoints are exempt from the sight walk: a unit standing on blocking terrain — spawned
  there, or a structure whose own footprint is a plateau — must still see and be seen, or it
  is blind and invulnerable at once.*
- **An authored map is CHECKED BY A SECOND IMPLEMENTATION.** `Content/ZhMapWriter.cs` writes
  and `tools/zhasset map` reads, and the reader earns that job by decoding all 150 shipped
  maps before it grades ours. A writer checked by its own reader proves only that the two
  agree. e2e asserts the two-sided result: the same authored shapes that measure 27.4s and a
  timeout draw here come out CONNECTED and SEPARATED under their cliff rule there.
- **New randomness gets its own `Pcg32` stream id** (next free integer in
  `Sim`'s constructor). Never share a stream between systems; never reuse an id.
- **Every new sim-state field must be added to the state hash**
  (`World.HashInto` / `Sim.HashState`) in a fixed position.
- **Prototype identity is name-derived, never ordinal.** `World.HashInto` folds in
  `UnitProto.StableId` (FNV-1a64 of the id), *not* `ProtoIdx`. `ProtoIdx` is a
  runtime array index and must never reach a hash, a replay, or the wire. This is
  what makes content growth safe: adding a unit that sorts anywhere cannot change
  what an existing replay means. Renaming a unit legitimately does.
  *This was a real bug, not a hypothetical:* the earlier "append after the base
  units" rule was enforced by alphabetical luck, and `laser_crusader` silently
  renumbered `ranger`/`technical` — `technical vs ranger` moved
  `c773f31f6888b7c8` → `2cdfb299cdae6006` while the two pinned hashes stayed put,
  because they only observe units *alive in the final state*. Pinned hashes are a
  weak guard for this class of bug; `e2e.sh` now also runs a **generative** check
  (inject an unreferenced unit named to sort before and after everything, assert
  every matchup is unchanged). Keep that check — it is the one that actually holds.
- **Sim core stays engine- and package-agnostic**: no NuGet packages, no
  engine types, no wall clock, no I/O inside Core/, Content/ (post-load),
  Runtime/, Harness/. The root `NuGet.config` clears all package sources to
  enforce this; `godot/NuGet.config` re-adds the source for presentation only.
  Never relax the root one — scope an override to the consuming subtree.
- **Presentation reads `Snapshot`, writes `Sim.Enqueue`, and nothing else.**
  Snapshots are immutable, hand out doubles at the display boundary, and can
  never flow back. `Enqueue` rejects commands stamped for the current or a past
  tick, so input is always scheduled ahead — the rule lockstep needs. Do not add
  a "just this once" mutable accessor for the renderer.
- Content lives in `content/*.json`; behavior changes via data edits are
  preferred over code changes. `contentHash` in tool output is the provenance
  anchor — quote it when reporting balance numbers.

## Architecture map

- `Core/` — Fix64, Pcg32 (streamed RNG), Fnv1a64 (state/content hashing)
- `Content/` — JSON schema DTOs; `ContentDb` compiles + lints packs, and
  resolves faction inheritance (`extends`/`add`/`remove`/`modify`) into flat
  rosters plus faction-local unit variants named `faction/unit`
- `Runtime/` — `Command` (replay = contentHash + seed + command log),
  `World` (flat archetype, tombstone slots, per-team `TeamState`: money/finite
  supply pool/income, research + unit queues, researched flags — all hashed),
  `StatSheet` (modifier algebra: `(base + Σadd) × Πmul`), `Sim` (tick loop,
  production system, hash trace)
  `Snapshot` (immutable read seam for renderers; doubles, never flows back)
  `Passability` (terrain grid: one byte/cell, surface in the low 3 bits, world→cell by
  shift) + `PathFinder` (integer 8-way A*, total order on the open set, capped)
- `Harness/` — duel series, pairwise counter matrix, build-order econ
  scenarios (`RunEconSeries`; specs like `"war_factory,crusader*"`),
  determinism verification
- `Program.cs` — CLI verbs; `--json` output is the future MCP tool seam
- `godot/` — Godot 4 presentation shell, its own csproj referencing the sim.
  `SimHost` (fixed-step accumulator + snapshot pair, no Godot types) and
  `Main` (Godot Node2D: draws, HUD, input). Excluded from the sim csproj.

## Known simplifications (intentional; see README for replacement plans)

Flat archetype not ECS; tombstones not generational handles; cumulative-only modifier
stacking; economy is abstract (two team queues, income from a finite pool, cost deducted at
build start).
**Unit-unit collision is still absent and is a different thing from terrain**: units pass
through each other freely, so a chokepoint concentrates fire but never jams, and a
one-cell gap admits an army as fast as a ten-cell one. **Structures do not stamp the grid
either**, which is why "raze the wall to open the choke" is not a mechanic here — in ZH an
obstacle cell is DERIVED from the building standing on it (`CELL_OBSTACLE`, the value our
surface enum deliberately leaves a hole for).
*Struck as slices landed: layered packs (1), upgrades (6), structures and economic targets
(5), powers and the second currency (9), pathing and terrain (12). Do not re-add them.*

**Anti-pattern, never adopt: a presentation-only or partial-field variant type.** ZH's
`ObjectReskin` is restricted to ten appearance fields and cannot change Side, cost,
prerequisites, name, portrait or voice — so EA cloned 477 objects wholesale instead,
shipping 180,490 lines to express 5,385 lines of intent. **The restriction is what
created the duplication.** Our `modify` patch stays unrestricted, however tempting a
"just the visuals" variant looks once a renderer matures.
Don't "fix" these in passing — each is a deliberate slice boundary.

## Roadmap order (evidence-first; every slice cites docs/ZERO-HOUR-ANATOMY.md)

Done: production/economy + build-order scenarios; Godot shell on snapshots;
base + delta faction layering (`rts faction`); **`rts compile --target zh`** —
packs leave the harness as additive `Data/INI` the retail engine loads (9 content
types, 42/42 subsystems, 0 parse errors), with `rts lint --target zh` reporting
caps / round-trip loss / unmappable mechanics as three separate failure kinds.

0. ~~**Name-derived prototype identity**~~ — done. `UnitProto.StableId` replaced
   `ProtoIdx` in the state hash; hashes re-pinned once, deliberately; `e2e.sh` gained
   a generative renumbering guard. This was a verified live bug, not a precaution.

1. ~~**Layered content packs + `rts diff`**~~ — done. N-layer `--mod` stacking, ordinal
   sorted; `contentHash` is an ordered hash over `(ordinal, packName, version, bytesHash)`
   plus resolved content; `rts diff` emits the delta taxonomy and duplication ratio.
   A `null` value REMOVES a key, and removing a name no earlier layer declared is an error —
   the same reasoning as retail's 88 dangling references, caught one step earlier where the
   author can still see what they meant. See the total-resolution-order invariant above.
   *Rationale: this is the authoring substrate. Everything below is a sim verb, and
   every one of them would otherwise be authored by hand-editing a single monolithic
   `game.json` — which is precisely the workflow this product exists to replace. It
   also converts every later slice into a measurable diff ("author a mod, measure the
   delta against base"), which is the loop the whole agent layer assumes. Do it first
   and the rest get cheaper to validate; do it last and you pay monolithic-edit cost
   every time. ZH's `map.ini` proves the semantics (`AddModule`/`RemoveModule`/
   `ReplaceModule`, subtractive list edits, override-creates-new-instance); what it
   lacks is provenance, which is exactly what `contentHash` supplies.*

2. ~~**Schema hygiene**~~ — done. `defaults` block, load-time duration→tick quantisation
   with `ceil()`, `unitsPerPurchase`, explicit `default` on `damageVsArmor` rows.
   *ZH's `DefaultThingTemplate` is 130 lines serving all 1,949 objects; without one,
   every new field is a breaking change to every existing unit.*
3. ~~**Modifier semantics**~~ — done. Additive-in-excess op; the upgrade condition is a
   named **flag set**, not one bit; flag-keyed **conditional variant blocks** that
   re-resolve in their own pinned `Sim.Step` phase.
   *Our algebra covers 4.5% of ZH's real upgrade references; selectors cover the rest.
   1,716 of ~2,300 real condition clauses are literally `None`, so the grammar is tiny.*
4. ~~**Faction-scoped reference resolution**~~ — done. A faction-local variant resolves its
   references through its OWN faction's resolved roster (never by mangling the name into
   `fid/ref` — inheritance means only the roster knows which fork applies), falling back to
   global. Covers object prerequisites and rule spawn targets.
   *Re-measured against retail: 88 bad references across 68 broken definitions, all nine
   multiplayer generals; `patch104p` ships fixes for several, so it is a tracked defect
   class. The docs previously said 67 — right grain, off by one; quote the grain.*
   **The corpus moved the target.** `Prerequisites` contributes ZERO of retail's defects;
   64 of 88 are references reached through a SHARED INTERMEDIARY (their `ObjectCreationList`
   `Transport`/`Payload`/`ObjectNames`). Our analogue is rule effects, not prerequisites.
   **A shared prototype cannot be scoped and must not pretend to be**: it is used by every
   faction that did not fork it, so one reference array cannot mean different things to
   each. That case is LINTED, not resolved — the honest split.
5. ~~**Structures**~~ — done. A unit with speed 0, `KindOf` role flags and
   `BuildCompletion`; prerequisites are objects, so razing a factory revokes buildability.
   *Retail splits 570 buildables into 379 units / 261 structures, and a minimum viable ZH
   faction is five objects of which THREE are buildings. We have no structure concept at
   all — which is exactly why "rushes can only spawn-camp, there are no economic targets"
   appears in Known simplifications. Smaller than upgrades, and it unblocks the harness's
   biggest known gap.*
6. ~~**Upgrades as a content type**~~ — done as tech nodes granting flags, which is their
   model: an upgrade carries no effect data, it sets a bit and a keyed loadout is selected.
   Compiles to a real `Upgrade` block + `WeaponSetUpgrade`/`ArmorUpgrade`/`MaxHealthUpgrade`.
   **Their one PLAYER_UPGRADE bit per object is a reported divergence** for any unit with
   more than one variant — the same restriction that forced 268 `ConflictsWith` lines on
   retail. Ours keeps a flag *set*; only the first variant survives compilation.
7. ~~**Rules engine** `{on, when, do}` (death event first) + **spawn lists**~~ — done.
   Closed event enum (death), closed filter (required flags), closed effect enum
   (spawn|grantMoney|damageInRadius|grantFlag) with open parameters. Cascades bounded at
   4 generations and CLAMPED, never thrown — a content bug degrades a battle, it does not
   crash a sweep. Spawn placement has its own `Pcg32` stream (id 2) so adding a wreck
   cannot shift a single combat roll. Next events to add: damaged, built, timer.
8. ~~**Garrison + capturable structures**~~ — done. `garrisonCapacity` on a structure, a
   `Garrison` command, occupants strictly untargetable, and `clearsGarrison` on a weapon as
   the only way damage reaches them — through the HOST, which is ZH's model. Plus
   `CAPTURABLE` + `AutoDepositUpdate`-style deposits, in a pinned `HoldingSystem` phase
   between production and loadout because capture flips a TEAM.
   *Terrain's tactical payoff with zero geometry, and the numbers are theirs: 17 of 363
   weapons (4.7%) set `AllowAttackGarrisonedBldgs`, `ContainMax` is 10 on 204 of 236
   buildings, the derrick is 200 cash per 12s on 2000 hitpoints.*
   **`GARRISONABLE` is NOT a KindOf** — their bit table has only `NO_GARRISON`,
   `STEALTH_GARRISON`, `GARRISONABLE_UNTIL_DESTROYED`. Garrisonability is the PRESENCE OF
   THE `GarrisonContain` MODULE. Emitting the invented name was a hard load error; a grep
   missed it because it is a substring of the real one. `e2e.sh` guards it.
   *Known limit, stated not hidden:* capture is proximity-based and `World.TeamCount` is 2,
   so there is no neutral owner — a capturable building is always someone's, and attackers
   stop at weapon range and shell it rather than walk onto it.
9. ~~**Powers + science/rank second currency**, `rts science-matrix`~~ — done. Skill points
   are earned by KILLING and by nothing else; ranks convert them to purchase points;
   sciences cost points and grant team flags, which the existing variant machinery already
   consumes. Activated powers are flag-gated, on a recharge, and fire the SAME closed effect
   vocabulary the death rules use — the payoff for having kept that vocabulary closed.
   *Their whole ladder is five `Rank.ini` blocks (0/800/1500/2500/5000 needed, 1/1/1/1/3
   granted = seven points a game) against 13-20 purchasable sciences per faction, every one
   costing exactly 1. The tree's shape is entirely in its prerequisites.*
   **Their default is that skill-point value IS experience value** — not one retail object
   overrides `SkillPointValue` — so one number feeds both ladders. But the PLAYER's ladder
   must not depend on the killer carrying a veterancy track, or whole rosters never rank up.
   **`rts science-matrix` plays every legal loadout from BOTH sides** and counts the
   science-owner's wins. Not politeness: ascending unit index makes team 0 resolve first, and
   an EMPTY loadout measured 11/12 for team 0 before the swap. A 50% baseline is what makes
   the column readable — read any mirror result here as a delta, never as an absolute.
10. ~~**Spatial index (uniform grid)**~~ — done. `Runtime/SpatialGrid.cs`: counting-sort
   buckets over Fix64 raw shifts, no floats and no hash containers, rebuilt **twice** a tick.
   Two, not one, because targeting must see units produced this tick while combat must see
   positions after this tick's movement — one rebuild would be stale for one of them, and a
   stale broad phase is a wrong answer, not a slow one.
   *Measured: ~2,000 units 6.9s → 2.1s; ~4,000 units 75.9s → 7.9s against the brute path.
   Below ~800 units it is a wash, which is the honest crossover for this content.*
   **`Query` returns candidates ASCENDING and that is load-bearing**, not tidiness: splash
   applies damage in visit order, deaths feed the rule cascade, and the cascade draws from a
   `Pcg32` stream — an unsorted broad phase would silently reorder RNG draws.
   **The cell size ADAPTS to the occupied extent.** The first version clamped outliers into
   edge cells: still correct, but at 4,000 units spread over 12,000 world units everything
   past the cap piled into two rows and it degenerated to worse-than-quadratic (28.8s).
   **`--brute` disables it so equivalence is TESTABLE** — e2e asserts identical state hashes
   at three scales plus a splash case. Note `--brute` is an equivalence ORACLE, not a fair
   performance baseline: it materialises the candidate list, so it is slower than the
   pre-slice nested scan was.
11. ~~**MCP server**~~ — done. `rts mcp` speaks JSON-RPC 2.0 over newline-delimited stdio
   with ZERO dependencies (System.Text.Json is BCL), so the root `NuGet.config`'s `<clear/>`
   stays untouched. Seven tools: `put_pack`, `validate_mod`, `run_matchup`,
   `query_counter_matrix`, `compare_packs`, `list_units`, `compile_pack`.
   **`put_pack` stores by `contentHash` and returns it**, so the loop is stateless for the
   agent — upload once, then reference by hash. Two agents authoring byte-identical content
   collide on the same entry, which is correct.
   **Every result carries `contentHash`.** A balance number without it is not reproducible,
   and reproducibility is the entire reason the core is deterministic.
   **Errors are repair instructions.** A bad prototype id returns "no prototype 'x'.
   Available: ..." rather than a `KeyNotFoundException` — the same principle as the harness
   reporting WHY a build queue stalled instead of showing a flat draw.
   *stdout is the protocol channel: diagnostics go to stderr, and one stray `Console.WriteLine`
   desyncs the transport.*
12. ~~**Passability grid**~~ — done. One byte/cell with the surface in the low three bits,
   a drawn `map` block, integer 8-way A*, and locomotor **surface masks** so the same river
   is a wall to infantry, a road to a hovercraft and nothing at all to aircraft.
   *Measured: the same two armies on the same seed decide in 17.4s across open ground and
   27.4s through one gate; a solid barrier turns the match into a timeout draw. Cost is
   ~1.7x tick time at 2,000 units (2.0s → 3.5s) and nothing at all for a pack with no map.*
   **It did NOT re-baseline the pinned hashes, and this entry used to say it must.** That was
   written before opt-in-by-content became the house discipline across five slices. Gating on
   CONSEQUENCE — a map exists *and* some unit cannot cross some cell — means an inert map is
   bit-identical to no map, which is both true and much more useful than a re-baseline: every
   pinned replay and both generative guards survive. Prefer this over re-baselining whenever
   the feature can be made a genuine no-op.
13. ~~**Elevation as a boolean LOS gate only**~~ — done. Cliff and Impassable break a sight
    line; water and rubble do not. Derived from the surface, never authored separately — the
    two blocking surfaces are exactly the ones the map writer emits as a PLATEAU, so what
    stands up out of the ground is what stops a shot, in both engines.
    *Measured with WATER as the control, which is what makes it a test of sight rather than of
    terrain: two maps of identical shape, a two-cell wall thinner than every gun in the pack,
    decide in **28.4s when the wall only blocks movement and 22.2s when it also blocks
    sight**. Before this slice both measured the same, because both armies simply shot across.*
    **The opaque wall is FASTER, which was not the expected direction.** A transparent wall
    halts both armies at it in a long attritional exchange; an opaque one denies acquisition,
    so they keep moving to the gate and settle it in one concentrated fight. Cover changes
    what a chokepoint MEANS, not merely how long it takes to walk around.
    **COVER DOES NOT TRANSFER, and it is the reverse of what this slice assumed.** ZH HAS the
    check — `Weapon::isClearFiringLineOfSightTerrain` — and its single call site in
    `TurretAI.cpp` is **commented out**, with EA's own note that some weapons (Tomahawk) must
    fire beyond LOS. The only live consumer is `Pathfinder::isAttackViewBlockedByObstacle`,
    which uses terrain LOS to choose where to STAND, and skips it entirely for
    `KINDOF_IMMOBILE` ("we can't move around it"). `isWithinAttackRange` is pure distance, so
    TurretAI's comment that it "checks terrain los now" is stale. Verified identical in
    GeneralsX's own tree — the source of the running build. So a wall that gives COVER here
    gives only a DETOUR there, `ZhLint` reports it per map, and a balance measurement that
    leans on cover does not transfer.
    **CONFIRMED IN A REAL MATCH**, once slice 16 made the test constructible. A map with a
    two-cell cliff wall beside the player start and a neutral Crusader placed 72 world units
    beyond it — inside the hellhound's 80 — with the plateau squarely between. Force-fire
    across the wall: the target's health bar went 96px pure green -> 62px yellow -> gone, and
    the tank was left as a scorch mark. **Our unit never moved.** It shot through a cliff that
    its own pathfinder will not cross, from a standstill, and killed what was behind it.
    *The order matters: an ordinary right-click on a NEUTRAL object is a move order, not an
    attack, and the first attempt looked like LOS blocking when it was simply no order at all.
    Ctrl+click force-fire is the one that tests anything.*

## The extracted reference model (`tools/zhasset`)

The extraction exists so an agent can reason over ZH's content and derive new content from it,
rather than re-deriving the corpus each time. Three commands carry the model; `reference/` is
their output and is gitignored — ship the extractor, never the extract.

- `catalogue` — the resolved corpus: 15 factions, 2,102 objects, 363 weapons, 96 sciences,
  79 powers. Answers *what is in the game*.
- **`dossier <object>`** — one object COMPLETE, with its asset closure. Answers *how do I make
  another one of these*. Full definition, every `ModelConditionState` with its model and
  animation, the textures named INSIDE each `.w3d`, icons resolved through both hops, sounds,
  and the death FX/OCL entry points.
  *`AmericaWarFactory` is 86 draw states — 36 of them construction, 9 of them door animations —
  across 36 models and 155 distinct textures. **Building assembly is not a separate asset**; it
  is `ACTIVELY_BEING_CONSTRUCTED` / `PARTIALLY_CONSTRUCTED` states, and a summary reporting only
  the default model misses the whole build-up.*
- **`techtree [faction]`** — the graph, joined across five files none of which names it:
  `PlayerTemplate` roots, `Object` command sets and `Prerequisites`, `CommandSet` slots,
  `CommandButton` verbs, `Science`/`Rank`. Every playable faction resolves to 4 tiers.
  **The verb is the edge, and there are more than the obvious one:** `UNIT_BUILD` (259) makes
  units, **`DOZER_CONSTRUCT` (192) makes STRUCTURES** — a tree built from `UNIT_BUILD` alone
  stops dead at the dozer — **`PURCHASE_SCIENCE` (82) IS the experience tree**, and
  `OBJECT_UPGRADE` is per-object where `PLAYER_UPGRADE` is per-player. Some lines carry a
  trailing space, so the verb is normalised before comparing.
  *The two trees are ONE graph: an `Object`'s `Prerequisites` can require a Science and a
  `SPECIAL_POWER` button can require one, so superweapons and the promotion ladder are reached
  from different roots of the same structure. Measured: 5 ranks at 0/800/1500/2500/5000 grant
  7 purchase points a game against 14-24 reachable sciences per faction.*

- **`nearest --like <object>`** / `nearest --role … --cost …` — the clone entry point, and the
  first step of the authoring loop: pick the nearest existing thing, take everything it is made
  of, change some, emit it.
  **Substitutability is ROLE, not cost and not stats.** A 900-cost tank and a 900-cost jet share
  a number and nothing else, while a Crusader and a Battlemaster are the same thing under
  different flags. So role is a Jaccard overlap on `KindOf` weighted 3x against everything else,
  with the entries that say nothing about role removed — `PRELOAD`, `SCORE`, `SELECTABLE`,
  `CAN_CAST_REFLECTIONS` are on almost everything and leaving them in inflates every object's
  similarity to every other. Numeric axes compare as RATIOS, not differences: 100 vs 200 damage
  is the same distance as 1000 vs 2000.
  *Distances are reported PER AXIS, because an agent picking a template needs to know WHY —
  "same role, 18% cheaper, identical range" is actionable and "score 0.026" is not.*
  *`CINE_` cutscene clones are excluded by default: they are byte-identical copies, they rank
  first every time, and cloning a clone inherits nothing the original lacks.*
  Sanity: `--like AmericaTankCrusader` returns Paladin (its own upgrade) then Battlemaster
  (China's equivalent); a spec for a cheap fast anti-infantry China vehicle returns the Gattling
  Tank, which is precisely what that unit is.

## Roadmap: the ASSET MODEL

The sim roadmap above is finished bar the lockstep layer. What is left is art, and it has its
own order. The governing line is already measured and does not move: **a pack on wholly
authored art borrows 8 retail names, and all 8 are the `zh.sides` HUD.** Content is ours; UI
furniture is borrowed on purpose.

14. ~~**A pack must CARRY its art, and lint must prove every reference resolves.**~~ — done.
    `zh.art` lists the files a pack ships and the compiler copies them into the output, routed
    by extension (`.w3d` to `Art/W3D`, images to `Art/Textures`). `AssetIndex` reads the `.big`
    tables of contents directly plus the loose `Art/` tree — **25,346 assets on this install**
    — and lint refuses any `zh.models` name that resolves to neither an installed asset nor
    one the pack ships.
    *A MAPPING is not a MODEL: the old check only asked whether a `zh.models` entry existed.
    A typo, or an authored mesh not yet built, produced a unit that moves, shoots, dies and
    cannot be seen or clicked, with no error from the engine — the `desertA` shape.*
    **Absent is not failure.** With no install the index is unusable and the check SKIPS; a
    check that cannot run must never look like a check that failed. Only names are read from
    the archives, never bytes.
15. ~~**Derive art profiles FROM THE MESH.**~~ — **STRUCK, on measurement. It cannot be done,
    and `zhasset artvalidate` is the standing proof.** The idea was that the W3D reader could
    supply geometry from vertex bounds and the rig from the hierarchy, unblocking the 2,928
    adoptable meshes that have no measured profile. Derived profiles were compared against the
    536 meshes that DO have one:

    | Derived from the mesh | Median | Within ±20% |
    |---|---|---|
    | `majorRadius` | ×1.02 | 61% |
    | `minorRadius` | ×1.10 | 51% |
    | `height` | **×1.51** | **26%** |

    **Gameplay geometry is a designer-chosen COLLISION size, not a property of the art.** The
    silhouette includes barrels, wings and antennae the footprint excludes — AVLeopard's mesh
    spans 21.9 half-units because of its gun, against a declared `majorRadius` of 15. Unbiased
    on average and wrong by more than a fifth for a third of meshes, which is precisely the
    "units created INSIDE the building and never seen" bug, mechanised.
    *The catastrophic direction is not a clean signal either: 8.9% of retail objects declare a
    radius under 30% of their own mesh half-span, so "much smaller than the art" cannot even
    be a warning without firing on retail content.*
    *Rig names fare better but still fail: the named part is present in the mesh 93% of the
    time for `turretBone` and `muzzleFlash`, 77% for `launchBone`, 65% for `fireFXBone` — and
    presence is not IDENTIFICATION. Several bones are candidates and only the INI says which
    one the weapon fires from.*
    **Consequence for the plan: the free-art pool cannot be made safe by inference.** There are
    two safe paths and inventing a third is what this measurement rules out — adopt a mesh
    retail USES, which has a measured profile, or AUTHOR one, which declares its profile as it
    is written. "2,928 models are free to adopt" remains true about rendering and stays false
    about behaviour.
16. ~~**Place objects in the emitted `.map`.**~~ — done. `map.objects` places a template at a
    position in OUR world units; the writer converts and emits it into `ObjectsList` beside
    the waypoints. Confirmed in a real match: a placed retail Crusader appeared exactly where
    the map put it.
    **The owner is the trap.** `GameLogic` resolves it with `PlayerList::validateTeam`, whose
    miss path is a `DEBUG_CRASH` *before* it falls back to neutral — and DEBUG_CRASH is
    compiled into this build, same as rule 1. Only `"team"`, the neutral side's default, is
    safe from a compiled map: a skirmish opponent's name exists in the LOBBY, not in the map's
    `SidesList`, so naming one is the crash rather than the fallback.
    *This is what finally settled the LOS question below.*
17. **Author an `FXList` and a `ParticleSystem`.** (M) *Both directories are scanned — measured
    from the boot log, after this file asserted the opposite from source and was wrong. Today
    an adopted mesh inherits a peer's death FX and a fully AUTHORED one dies silently and
    invisibly, which is the last place adopted art is structurally required.*
18. **Audio.** (L) *The largest untouched category: not one line is emitted and every unit is
    silent. `SoundEffects`, `Speech`, `Voice` and `MiscAudio` are all in the 42 scanned dirs.
    It is also 0% of the simulation, so it changes no measurement — which is why it is here
    and not higher. Split it the way icons were split: weapon and death sounds are content and
    get authored; EVA and UI chrome stay borrowed.*
19. **Terrain themes.** (M) *A map's SHAPE is ours and its SURFACE is retail's. Authoring a
    tile sheet plus `Terrain` blocks closes that. Note 12 of retail's own 291 blocks name a
    texture that ships in no archive — lint should report those too.*

**Deliberately out, on evidence:** a DDS writer (retail is 3,496 `.dds` to 50 `.tga`, but TGA
ships, is supported and needs no DXT compressor); a `.big` writer (loose files shadow archives
— that IS the engine's mod mechanism, and no retail-derived pack ever ships from here anyway);
a modelling suite (units are 220-245 triangles — if primitives stop being enough, a small OBJ
importer is the cheap answer).

Both design decisions this roadmap owed an answer to are now ANSWERED:
**(a)** ~~flag changes re-select loadouts~~ — `LoadoutSystem` runs between
production and cooldowns; see the system-order invariant above.
**(b)** ~~one total resolution order~~ — see the invariant below. There turned out to be
THREE mechanisms, not four: an entity-level `extends` was never built, and deliberately
will not be.

Deliberately out, on evidence: terrain types as content (ZH's are inert — 291 blocks,
one call site, a flag nothing sets); art/FX/audio; prerequisite OR-expressions; slope-
modulated speed; elevation damage/range/cover; chokepoints as a content type.

Lockstep session layer stays last, unchanged.
