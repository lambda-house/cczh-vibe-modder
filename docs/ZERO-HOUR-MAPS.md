# Zero Hour maps, measured

Third study alongside `ZERO-HOUR-ANATOMY.md` (the content model) and `ZERO-HOUR-ASSETS.md`
(the shipped art). Same rule: every number here is measured from source on disk or decoded
from the shipped corpus, and the extractor ships while the extract does not.

**Corpus: 150 maps** from `MapsZH.big` (116) and `maps.big` (55, minus overlaps).
`tools/zhasset map scan` decodes **150/150**.

## Container

A map is `Maps/<Name>/<Name>.map`, EA's `DataChunk` format
(`Common/System/DataChunk.cpp`) — not the W3D container, though the family resemblance is
close enough to mislead.

```
'CkMp'  int32 symbolCount  { uint8 len, char[len] name, uint32 id } * count
then repeating:  uint32 id | uint16 version | int32 size | payload
```

Chunks nest, but **nesting is not visible in the bytes**. W3D marks a container with the high
bit of its size; here the size is honest and a chunk is a container only because the parser
registered for it registers further parsers. `ObjectsList` contains `Object`; `SidesList`
contains `PlayerScriptsList`.

Two consequences for a writer:

- **Sizes are back-patched**, never predicted — the same discipline as W3D.
- **Dict keys share the chunk symbol table.** A dict entry key is `(tocId << 8) | type`, so
  the table cannot be written until the body is, and a key you never used is not in it. This
  is why `DataChunkOutput` writes chunks to a temp file and prepends the table at close.

### Compression is optional

`CachedFileInputStream::open` (`DataChunk.cpp:53`) sniffs a 4-byte magic and falls straight
through to the raw bytes when nothing matches. Measured across the corpus:

| Storage | Maps |
|---|---|
| `EAR\0` refpack | 145 |
| raw `CkMp` | 4 |
| `ZL5\0` zlib | 1 |

Compressed forms are `magic + uint32 uncompressedSize + stream`. **Writing raw is legal and
shipped**, which is why our writer has no compressor.

## The eight chunks

All eight appear in all 150 maps. Versions are what the corpus actually contains.

| Chunk | Ver | What it is |
|---|---|---|
| `HeightMapData` | 4 | the height field |
| `BlendTileData` | 6, 7, 8 | terrain texture indices — **88% of the bytes** |
| `WorldInfo` | 1 | a dict: `mapName`, `weather`, `compression` |
| `SidesList` | 3 | players, build lists, teams, scripts |
| `ObjectsList` | 3 | every placed object **and every waypoint** |
| `PolygonTriggers` | 3, 4 | trigger areas; water areas are these |
| `GlobalLighting` | 3 | **exactly 872 bytes in all 150** |
| `WaypointsList` | 1 | waypoint *links*, not waypoints |

### HeightMapData is a grayscale image

```
int32 width | int32 height | int32 borderSize
int32 numBoundaries | (int32 x, int32 y) * numBoundaries
int32 dataSize | uint8[dataSize]
```

**One unsigned byte per vertex.** `ParseHeightMapData` throws `ERROR_CORRUPT_FILE_FORMAT`
unless `dataSize == width * height`, so there is no padding to get wrong.

Two constants fix the scale (`Common/MapObject.h:60`):

| Constant | Value | Meaning |
|---|---|---|
| `MAP_XY_FACTOR` | 10.0 | world units between height samples |
| `MAP_HEIGHT_SCALE` | 10/16 = 0.625 | world units per height **byte** |

So **total relief is capped at 255 × 0.625 = 159.4 world units** — about sixteen tank
lengths. That is the real constraint on importing real-world elevation, not resolution.

`borderSize` is a non-playable margin on every side; `boundaries[0]` is the playable extent in
cells. **World coordinates start at the playable corner**: objects in `Tournament Desert` run
x 29..1987 inside a 0..2000 playable box, on a 270×320 grid with a 35-cell border.

### The border is not walkable, and it looks like it is

`W3DTerrainLogic::getMaximumPathfindExtent` returns `0 .. boundaries[i] * MAP_XY_FACTOR`, and
`Pathfinder::newMap` allocates cells for exactly that rectangle. The border is flat, drawn,
and unreachable. A reachability check that ignores this reports every full-width barrier as
leaky — which is precisely what the first version of `zhasset map verify` did.

### BlendTileData is mostly ceremony

```
int32 len (== width*height)
int16[len] * 4          base tile, blend, extraBlend, cliffInfo
uint8[height * ((width+7)/8)]   per-cell cliff bits
int32 numBitmapTiles | int32 numBlendedTiles | int32 numCliffInfo
int32 numTextureClasses
  per class: int32 firstTile, numTiles, width, legacy; asciiString name
int32 numEdgeTiles | int32 numEdgeTextureClasses | per class: ... name
  for i in 1..numBlendedTiles-1:  blend records
  for i in 1..numCliffInfo-1:     cliff records
```

Both trailing loops run **from 1**, so `numBlendedTiles = numCliffInfo = 1` makes them empty
and the chunk simply ends. That is the format's own degenerate case, not a shortcut.

**Terrain textures are indirect.** The class name is a `TerrainType`, resolved through
`TheTerrainTypes` to a TGA under `Art/Terrain`, which `readTexClass` splits into a `width²`
tile sheet. Our anatomy study calls `Terrain.ini` inert — true for *gameplay*: 291 blocks, one
call site, a `RestrictConstruction` flag nothing sets. **This is that call site.** Inert is
not unused, and an unknown name draws nothing at all with no error anywhere.

### GlobalLighting: arithmetic that confirms the read

`int32 timeOfDay`, then for each of four times of day one terrain light, three object lights
and two more terrain lights at nine reals each, then a `uint32` shadow colour:

```
4 + 4 * 54 * 4 + 4 = 872
```

That agreeing with the observed size in all 150 maps is the check that the layout was read
correctly — and it is why the values we write can be ours. Only the *shape* had to come from
anywhere else.

## What makes a map skirmish-playable

- **`Player_1_Start` … `Player_N_Start` waypoints, contiguous from 1.** `MapUtil.cpp:334`
  counts upward and **stops at the first gap**; the player count is derived, never declared.
- A waypoint is an `ObjectsList` entry with an empty template name and a `waypointID` /
  `waypointName` in its dict.
- **`MapCache` builds itself** on boot (`MapCache::updateCache`), CRCs the file and caches it.
  No hand-written `MapCache.ini`.
- **No string-table entry needed.** `WorldInfo.mapName` is a *lookup tag*; when it is absent
  or unresolvable the display name falls back to the filename.
- `PlayerScriptsList` inside `SidesList` is **not optional** — the parser calls
  `file.parse(NULL)` and throws `ERROR_CORRUPT_FILE_FORMAT` on false. An empty script list is
  a chunk with nothing in it, not an absent chunk.

## Passability is derived, not authored

ZH has **no authored passability layer**. `setCellCliffFlagFromHeights` compares the four
corners of each cell and calls it `CELL_CLIFF` when they differ by more than
`PATHFIND_CLIFF_SLOPE_LIMIT_F`:

| | Value |
|---|---|
| Slope limit | **9.8 world units** across one 10-unit cell |
| In height bytes | 9.8 / 0.625 = **15.68 → a step of 16 blocks** |

This is the entire bridge between our grid and theirs: a blocked cell is emitted as a
**plateau** and their pathfinder derives the block. A step of 15 is a wall units stroll over,
and nothing in either engine says so.

Note what the cliff flag actually marks: the **rim** of a plateau, not its top. Our 48×48
chokepoint emits 2,820 raised vertices and only 362 cliff cells — the mesa top is flat, and
unreachable because its edge is cliff all the way round.

## What we emit, and what does not transfer

`rts compile --target zh` writes `Maps/<pack>_map/<pack>_map.map` when the pack authored a
`map` block, and nothing at all when it did not.

| Our surface | Becomes |
|---|---|
| `Clear` | ground at height 32 |
| `Cliff`, `Impassable` | plateau at height 64 — a 32-byte step, 2× their limit |
| `Water` | **open ground**, reported as divergence — needs a `PolygonTrigger` with `isWater` |
| `Rubble` | **open ground**, reported as divergence — no height analogue |

**The map is resampled, always.** Our cell size is a power of two in our units; theirs is
fixed at 10 of theirs. The two have no common divisor, so the writer preserves the world
**span** rather than the cell count — span is what a measured battle length depends on.
`ZhLint` reports the ratio when one authored cell is under one of their cells, because a
feature narrower than that can silently vanish in the resample.

## Real-world terrain

- **Elevation: yes, nearly directly.** A DEM is a grayscale raster and so is `HeightMapData`.
  Resample to a 10-unit grid, quantise at 0.625 units per step. The binding constraint is the
  159-unit vertical range, not horizontal resolution — at 10 units per vertex a 2 km map is
  200 vertices, roughly SRTM's 30 m posting.
- **Aerial photography: not directly.** There is no per-cell UV and no unique texture space —
  the renderer indexes a tile sheet. Either classify the photo per cell into existing
  `TerrainType`s, or author a tile sheet (`zhasset tga` writes the format). Either way it is a
  *palette* derived from the place, not a photograph of it.

The honest summary: a real place's **landform** transfers faithfully, its **appearance** only
impressionistically.

## Tools

```
zhasset map scan <dir>     decode a corpus; chunk histogram and versions
zhasset map read <file>    one map: chunks, heightmap, terrain classes, waypoints
zhasset map verify <file>  assertions a WRITTEN map must satisfy
    --expect-cliff         terrain steep enough that THEIR pathfinder blocks it
    --expect-connected     the start spots are mutually reachable
    --expect-separated     the start spots are not
```

`verify` is deliberately a **second implementation**: `Content/ZhMapWriter.cs` writes and
`tools/zhasset` reads, and the reader earns the job by decoding all 150 shipped maps before it
is allowed to grade ours. A writer checked by its own reader proves only that the two agree.
