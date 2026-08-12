# Rules measured against the RUNNING engine

Every rule here cost a crash, a hang or a silent misfire to establish, and several contradict
what EA's source implies. **The GPL tree is schema authority; the running GeneralsX build is
loading authority; they diverge.**

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

8b. **A faction's SIDE is a global name and must be PACK-PREFIXED, like everything else.**
   Objects, weapons and armor were prefixed from the start; the faction name, its `Side` and
   its `ControlBarScheme` were not. Install two packs that each define a faction called
   `hellfire` and you get duplicate `PlayerTemplate`s, duplicate schemes, and one Side that
   three files disagree about — the survivor points at ONE pack's objects and the other's are
   unreachable. **Four packs accumulated in a test install and a match silently played the
   wrong pack's units**, which cost an hour chasing a model override that was never broken.
   e2e compiles two packs and asserts they share no faction, side or control bar.

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
   | `GeometryIsSmall` | hardcoded `Yes` for anything mobile | **a 40-radius unit renders as NOTHING.** No error, no log line; the object exists and is simply never drawn. It looked like placement was broken — the same mesh was fine as a STRUCTURE, and a 20-radius mesh was fine as a unit. Retail's own practice, measured over 237 `IsSmall=Yes` objects: median radius 10, 90th percentile 15, only 3 above 30. Derive it from the radius, never from the unit/structure split |

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

18. **An unknown KEY is silently ignored; an invalid enum VALUE is fatal. They are not the
   same failure and the difference decides what "it booted" proves.** Measured with two
   probes on the same build, one after the other:
   - `VoiceSelekt = ...` (a misspelt Object key) — the file loads, the boot completes, the
     game reaches the shell, and **nothing appears in the log**. The key is dropped.
   - `Priority = URGENT` in an `AudioEvent` — the process **dies**, and the log says
     `[INI] ERROR in load('Data/INI/SoundEffects/demo.ini') - exception caught`.

   The mechanism is the parse table: an unrecognised field name hits a `DEBUG_CRASH` that is
   compiled out of a release build and then falls through to skip, whereas `parseIndexList`
   and `parseBitString32` **throw** on a name that is not in their C++ table. So the five
   enum literals that have cost us boots (`NO_Z_MOTION`, `CANCELABLE`, `IS_PREREQUISITE`,
   `SpreadFormation`, `GARRISONABLE`) were always going to be loud, and a mistyped key never
   will be. *Consequence for verification: a clean boot IS evidence that every enum value in
   an emitted file is legal, and is NO evidence at all that the keys are. Keys must be
   sourced from a retail block that uses them and checked by `zhasset objectdiff`; do not
   spend a boot trying to validate a field name.*

19. **Audio: the channel is open, the format needs no encoder, and this build cannot make a
   sound.** All three parts are load-bearing.
   - **Emitting is additive and works.** `Data/INI/SoundEffects/` is in the scanned 42
     (rule 16), alongside `Voice`, `Speech`, `MiscAudio`, `Eva` and `Music`. A pack file
     there loads next to retail's — the log reports `filesRead=2` and parses every block.
   - **The path is COMPUTED, not declared.** An `AudioEvent` never names a file.
     `AudioEventRTS::generateFilenamePrefix` composes
     `{AudioRoot}\{SoundsFolder}\{name}.{SoundsExtension}` out of `AudioSettings.ini` —
     `Data\Audio\Sounds\<name>.wav` as shipped. The entries in a `Sounds`, `Attack` or
     `Decay` line **are** base filenames. Put the wave anywhere else and the event parses,
     resolves and is silent. Localisation is a preference, not a requirement:
     `adjustForLocalization` tries `Sounds\{Language}\` first and keeps the unlocalised path
     when that misses, so flat output is correct even for `voice` events.
   - **`Type` and `Control` are `parseBitString32` — flag SETS, space-separated**
     (`Type = world shrouded everyone`). Only `Priority` is a single value. Reading either as
     a scalar keeps the first bit and quietly drops the rest.
   - **No encoder is needed.** Census of all 8,638 shipped audio files (1,049.6 MB, 47% of
     the install): 8,582 `.wav` and 56 `.mp3`, in 8 wave formats — **5,346 plain PCM
     (`wFormatTag = 1`) against 3,236 IMA ADPCM (17)**, and the largest single bucket, 5,157
     files, is **mono / 22,050 Hz / 16-bit PCM**. The most typical format is also the one a
     44-byte RIFF header produces. Run `zhasset audio --census` to re-measure.
   - **AND YET: this build has no decoder.** `OpenALAudioManager` (GeneralsX replaced Miles
     with OpenAL on SDL3) creates a context and allocates sources, then never calls
     `alBufferData` — there is no RIFF parsing anywhere in the tree, `addAudioEvent` forwards
     to a queue nothing drains, and `update()` is a documented "Phase 2" no-op.
     **Nothing you emit can be heard here, from any input.** Do not spend a session trying;
     verify audio with `zhasset audio <packdir>` and a clean boot, and say plainly that
     playback is unverified. This is the same shape as rule 11 (`GameData` parses and does
     nothing) — the file being read is necessary and nowhere near sufficient.
