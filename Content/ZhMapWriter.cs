using System.Text;
using RtsSkeleton.Core;
using RtsSkeleton.Runtime;

namespace RtsSkeleton.Content;

/// <summary>
/// Write a Zero Hour <c>.map</c> from our authored passability grid.
///
/// <para>This is the second format we author rather than adopt, and it closes the loop slice
/// 12 opened: a chokepoint measured here can now be PLAYED there. Until this existed, every
/// terrain number the harness produced was taken on a map the retail engine had never seen —
/// <c>ZhLint</c> reported the whole map as divergence, which was honest and useless.</para>
///
/// <para><b>The format is EA's <c>DataChunk</c>, and it is theirs to document, ours to
/// write.</b> A 4-byte <c>CkMp</c> tag, a symbol table mapping chunk names to integer ids,
/// then <c>(id, version, size, payload)</c> records that nest. Same shape as W3D and the same
/// trap: sizes are back-patched after the payload is known, never predicted.</para>
///
/// <para><b>Compression is OPTIONAL</b> and we do not do it.
/// <c>CachedFileInputStream::open</c> sniffs for an <c>EAR\0</c> refpack header and falls
/// straight through to the raw bytes when there is none. Measured, not assumed: of 150
/// shipped maps, several store raw. A refpack encoder would buy disk and nothing else.</para>
///
/// <para><b>The bridge to their pathfinder is a SLOPE, not a flag.</b> ZH has no authored
/// passability layer at all — <c>setCellCliffFlagFromHeights</c> walks the height field and
/// calls a cell <c>CELL_CLIFF</c> when the corners of one cell differ by more than
/// <c>PATHFIND_CLIFF_SLOPE_LIMIT_F</c> = 9.8 world units. At <c>MAP_HEIGHT_SCALE</c> = 0.625
/// units per byte that is 15.68 height bytes, so a step of 16 is the threshold and anything
/// less is walkable ground however much it looks like a wall. Our blocked cells are therefore
/// emitted as a PLATEAU and their engine derives the block itself. That is the whole
/// correspondence, and it is a number we can be wrong about — which is the point.</para>
/// </summary>
public static class ZhMapWriter
{
    /// <summary>Their <c>MAP_XY_FACTOR</c>: world units between height samples.</summary>
    public const double CellSize = 10.0;

    /// <summary>Their <c>MAP_HEIGHT_SCALE</c> = MAP_XY_FACTOR/16: world units per height byte.</summary>
    public const double HeightScale = CellSize / 16.0;

    /// <summary>
    /// Height-byte step at which THEY call a cell a cliff: ceil(9.8 / 0.625) = 16. Emitted
    /// walls clear it with margin, because a wall that is one byte short of the threshold is
    /// a wall units stroll over and nothing in either engine says so.
    /// </summary>
    public const int CliffStepBytes = 16;

    /// <summary>Height byte for open ground. Not 0: a plateau needs headroom above and the
    /// engine reads 0 as the floor, so anything below ground would clamp rather than dip.</summary>
    public const byte GroundHeight = 32;

    /// <summary>Plateau height for a blocked cell. Twice the cliff threshold, so the slope is
    /// unambiguous at every corner of the boundary cell rather than only at the steepest.</summary>
    public const byte WallHeight = GroundHeight + 2 * CliffStepBytes;

    /// <summary>
    /// Non-playable margin in cells, on every side. Retail's own maps run 35; ours are small
    /// and a wide border is mostly wasted bytes, but it cannot be zero — the border is what
    /// the camera pulls back into at the map edge.
    /// </summary>
    public const int Border = 8;


    /// <summary>Their <c>TILE_PIXEL_EXTENT</c>: one terrain tile is 64x64 pixels, and
    /// <c>countTiles</c> derives the tile count by dividing the image by it.</summary>
    public const int TilePixels = 64;

    /// <summary>
    /// A terrain tile the map can paint itself with, so a pack's ground is ITS OWN.
    ///
    /// <para>Until this, a map's SHAPE was authored and its SURFACE was retail's — the writer
    /// named a `TerrainType` like `SandMediumType2` and inherited EA's texture. This is the
    /// last borrowed thing in an emitted map.</para>
    ///
    /// <para><c>countTiles</c> is picky in ways that fail as a BLACK MAP rather than an error:
    /// the image must be uncompressed true-colour (TGA type 2), 24 or 32 bits, and at least
    /// 64x64 — under that it yields zero tiles and <c>readTexClass</c> simply returns. One tile
    /// is all the map asks for, since <c>BlendTileData</c> declares <c>numTiles = 1</c>.</para>
    ///
    /// <para>The pattern is a mottled grain rather than a flat colour, for the same reason the
    /// mesh test used a grid: a flat fill proves the file loaded and hides everything about how
    /// it is mapped. Tiling artefacts are the thing worth being able to see.</para>
    /// </summary>
    public static byte[] TerrainTile(int seed = 12345)
    {
        int n = TilePixels;
        var px = new byte[n * n * 3];
        // A tiny deterministic PRNG rather than System.Random: the emitted bytes must be
        // identical on every machine, or two people compiling one pack get different packs.
        uint st = (uint)seed | 1u;
        uint Next() { st ^= st << 13; st ^= st >> 17; st ^= st << 5; return st; }
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int grain = (int)(Next() % 24) - 12;
                // A faint diagonal weave so rotation and tiling are both visible.
                int weave = ((x + y) % 16 < 8) ? 5 : -5;
                int r = Math.Clamp(168 + grain + weave, 0, 255);
                int g = Math.Clamp(146 + grain + weave, 0, 255);
                int b = Math.Clamp(104 + grain, 0, 255);
                int o = ((n - 1 - y) * n + x) * 3;          // TGA rows run bottom-up
                px[o] = (byte)b; px[o + 1] = (byte)g; px[o + 2] = (byte)r;
            }

        var tga = new byte[18 + px.Length];
        tga[2] = 2;                                          // uncompressed true-colour
        BitConverter.GetBytes((ushort)n).CopyTo(tga, 12);
        BitConverter.GetBytes((ushort)n).CopyTo(tga, 14);
        tga[16] = 24;
        px.CopyTo(tga, 18);
        return tga;
    }

    public sealed class Result
    {
        public byte[] Bytes = Array.Empty<byte>();
        public int Width, Height, PlayableWidth, PlayableHeight;
        public int BlockedCells, Objects;
        public List<string> Notes = new();
        public List<(string Name, double X, double Y)> Waypoints = new();
    }

    /// <summary>
    /// Build a map from a resolved pack. <paramref name="starts"/> are player start positions
    /// in OUR world units, in player order; the engine derives the map's player count by
    /// counting <c>Player_N_Start</c> waypoints upward from 1 until one is missing, so they
    /// must be contiguous and there must be at least two for a skirmish map.
    /// </summary>
    public static Result Write(PassabilityGrid grid, double worldScale, string terrainType,
                               string mapNameTag, IReadOnlyList<(Fix64 X, Fix64 Y)> starts,
                               IReadOnlyList<MapObjectDto>? objects = null)
    {
        var res = new Result();

        // ---- Resolution ---------------------------------------------------------------
        // Our cell size is a power of two in OUR units; theirs is fixed at 10 of theirs.
        // Those two grids have no common divisor, so this RESAMPLES rather than mapping cell
        // to cell — the thing that must be preserved is the world SPAN, because span is what
        // a measured battle length depends on. Matching cell counts instead would silently
        // resize the battlefield by the ratio of the two cell sizes.
        double ourSpanX = grid.Width * grid.CellSize.ToDoubleForDisplay();
        double ourSpanY = grid.Height * grid.CellSize.ToDoubleForDisplay();
        int pw = Math.Max(4, (int)Math.Round(ourSpanX * worldScale / CellSize));
        int ph = Math.Max(4, (int)Math.Round(ourSpanY * worldScale / CellSize));

        res.PlayableWidth = pw;
        res.PlayableHeight = ph;
        res.Width = pw + 2 * Border;
        res.Height = ph + 2 * Border;

        // ---- Height field ---------------------------------------------------------------
        // One unsigned byte per VERTEX; the parser throws ERROR_CORRUPT_FILE_FORMAT unless
        // dataSize == width*height exactly, so there is no padding to get wrong.
        var heights = new byte[res.Width * res.Height];
        Array.Fill(heights, GroundHeight);

        for (int vy = 0; vy < ph; vy++)
        {
            for (int vx = 0; vx < pw; vx++)
            {
                // Vertex centre in our world units, then straight to a cell index. Sampling
                // at the CENTRE rather than the corner keeps a one-cell wall one cell wide
                // instead of smearing it across the two vertices that bracket it.
                double ox = (vx + 0.5) / pw * ourSpanX;
                double oy = (vy + 0.5) / ph * ourSpanY;
                int cx = Math.Clamp((int)(ox / grid.CellSize.ToDoubleForDisplay()), 0, grid.Width - 1);
                int cy = Math.Clamp((int)(oy / grid.CellSize.ToDoubleForDisplay()), 0, grid.Height - 1);

                var surface = grid.At(cy * grid.Width + cx);
                // Only the GROUND-blocking surfaces transfer. Water and rubble need a water
                // PolygonTrigger and a rubble pass respectively, neither of which this slice
                // writes; they are reported, not silently flattened into something else.
                if (surface is Surface.Cliff or Surface.Impassable)
                {
                    heights[(vy + Border) * res.Width + (vx + Border)] = WallHeight;
                    res.BlockedCells++;
                }
            }
        }

        // ---- Chunks ----------------------------------------------------------------------
        var toc = new Toc();
        var body = new MemoryStream();
        var w = new ChunkWriter(body, toc);

        w.Open("HeightMapData", 4);
        w.I32(res.Width);
        w.I32(res.Height);
        w.I32(Border);
        w.I32(1);                       // one boundary rectangle
        w.I32(pw); w.I32(ph);
        w.I32(heights.Length);
        w.Raw(heights);
        w.Close();

        WriteBlendTiles(w, res.Width, res.Height, terrainType);

        w.Open("WorldInfo", 1);
        // A dict, and the keys are ordinary TOC symbols. mapName is a STRING TABLE TAG, not a
        // display name: MapCache falls back to the filename when it is absent or unresolvable,
        // which is exactly what we want — a map needs no entry in a string table we do not own.
        w.Dict(d =>
        {
            d.Str("mapName", mapNameTag);
            d.Int("weather", 0);
            d.Int("compression", 0);
        });
        w.Close();

        WriteSides(w, starts.Count);

        // ---- ObjectsList: the start waypoints ---------------------------------------------
        // A waypoint IS an object with a waypointID in its dict and an empty template name.
        // MapUtil counts Player_N_Start upward from 1 and STOPS at the first gap, so the
        // numbering is contiguous by construction rather than by convention.
        w.Open("ObjectsList", 3);
        int waypointId = 1;
        double zhSpanX = pw * CellSize, zhSpanY = ph * CellSize;
        for (int i = 0; i < starts.Count; i++)
        {
            // Our world is centred on the origin; theirs starts at the playable corner.
            double zx = starts[i].X.ToDoubleForDisplay() * worldScale + zhSpanX / 2.0;
            double zy = starts[i].Y.ToDoubleForDisplay() * worldScale + zhSpanY / 2.0;
            zx = Math.Clamp(zx, CellSize, zhSpanX - CellSize);
            zy = Math.Clamp(zy, CellSize, zhSpanY - CellSize);
            string name = $"Player_{i + 1}_Start";
            WriteWaypoint(w, name, waypointId++, zx, zy);
            res.Waypoints.Add((name, zx, zy));
        }
        WriteWaypoint(w, "InitialCameraPosition", waypointId, zhSpanX / 2.0, zhSpanY / 2.0);
        res.Waypoints.Add(("InitialCameraPosition", zhSpanX / 2.0, zhSpanY / 2.0));

        // Placed objects. Same chunk as the waypoints and the same world frame: ours is
        // centred on the origin, theirs starts at the playable corner.
        foreach (var o in objects ?? Array.Empty<MapObjectDto>())
        {
            double zx = Math.Clamp(o.X * worldScale + zhSpanX / 2.0, 0, zhSpanX);
            double zy = Math.Clamp(o.Y * worldScale + zhSpanY / 2.0, 0, zhSpanY);
            WriteObject(w, o, zx, zy);
            res.Objects++;
        }
        w.Close();

        w.Open("PolygonTriggers", 4);
        w.I32(0);
        w.Close();

        WriteLighting(w);

        w.Open("WaypointsList", 1);
        w.I32(0);                        // waypoint LINKS, not waypoints; ours are unlinked
        w.Close();

        // ---- Assemble: symbol table first, then the chunk stream ---------------------------
        // The TOC has to be written after the body because ids are allocated as chunks open —
        // the same reason DataChunkOutput writes its chunks to a temp file and prepends the
        // table at close.
        var outMs = new MemoryStream();
        toc.WriteTo(outMs);
        body.Position = 0;
        body.CopyTo(outMs);
        res.Bytes = outMs.ToArray();
        return res;
    }

    /// <summary>
    /// The tile layer. 88% of a retail map's bytes and almost none of its meaning: four
    /// int16 index planes and a cliff bitfield, then the texture-class table.
    ///
    /// <para>Everything here is at its degenerate value on purpose. <c>numBlendedTiles</c> and
    /// <c>numCliffInfo</c> are 1, and both of the record loops that follow run from index 1 to
    /// the count — so at 1 they read nothing at all and the chunk simply ends. That is not a
    /// shortcut around an unfinished reader; it is the format's own empty case.</para>
    ///
    /// <para>The texture class name is a <c>TerrainType</c>, resolved through
    /// <c>TheTerrainTypes</c> to a tile sheet under <c>Art/Terrain</c>. Our docs call
    /// <c>Terrain.ini</c> inert, and for GAMEPLAY it is — a <c>RestrictConstruction</c> flag
    /// nothing sets. This is that "exactly one call site": inert is not the same as unused,
    /// and a map cannot be drawn without naming one.</para>
    /// </summary>
    private static void WriteBlendTiles(ChunkWriter w, int width, int height, string terrainType)
    {
        int n = width * height;
        w.Open("BlendTileData", 8);
        w.I32(n);
        for (int plane = 0; plane < 4; plane++)      // tile, blend, extraBlend, cliffInfo
            w.Raw(new byte[n * 2]);                  // index 0 everywhere = the base tile
        w.Raw(new byte[height * ((width + 7) / 8)]); // per-cell cliff bits, all clear

        w.I32(1);   // numBitmapTiles   — one 64x64 tile out of the class's sheet
        w.I32(1);   // numBlendedTiles  — 1 means the blend record loop is empty
        w.I32(1);   // numCliffInfo     — likewise for cliff records

        w.I32(1);                 // one texture class
        w.I32(0);                 //   firstTile
        w.I32(1);                 //   numTiles
        w.I32(1);                 //   width in tiles
        w.I32(0);                 //   legacy isGDF field, read and discarded
        w.Str(terrainType);

        w.I32(0);   // numEdgeTiles
        w.I32(0);   // numEdgeTextureClasses
        w.Close();
    }

    /// <summary>
    /// Players and their build lists. A skirmish map still needs the neutral side and the
    /// per-slot skirmish sides, because <c>validateSides</c> runs over whatever it finds.
    ///
    /// <para>The nested <c>PlayerScriptsList</c> is NOT optional decoration: the parser calls
    /// <c>file.parse(NULL)</c> and THROWS <c>ERROR_CORRUPT_FILE_FORMAT</c> if it comes back
    /// false. An empty script list is a chunk with no scripts in it, not an absent chunk.</para>
    /// </summary>
    private static void WriteSides(ChunkWriter w, int playerCount)
    {
        w.Open("SidesList", 3);
        w.I32(1);                                  // one side: neutral
        w.Dict(d =>
        {
            d.Str("playerName", "");
            d.Bool("playerIsHuman", false);
            d.Wide("playerDisplayName", "Neutral");
            d.Str("playerFaction", "FactionCivilian");
            d.Str("playerAllies", "");
            d.Str("playerEnemies", "");
        });
        w.I32(0);                                  // that side's build list is empty
        w.I32(0);                                  // no teams

        w.Open("PlayerScriptsList", 1);
        w.Close();
        w.Close();
    }

    /// <summary>
    /// A placed object. Unlike a waypoint this becomes a REAL object at load, which is why the
    /// owner matters: <c>GameLogic</c> looks it up with <c>PlayerList::validateTeam</c>, whose
    /// miss path is a <c>DEBUG_CRASH</c> before it falls back — and DEBUG_CRASH is compiled
    /// into this build.
    /// </summary>
    private static void WriteObject(ChunkWriter w, MapObjectDto o, double x, double y)
    {
        w.Open("Object", 3);
        w.F32(x); w.F32(y); w.F32(0);
        w.F32(o.Angle * Math.PI / 180.0);      // their field is radians
        w.I32(0);
        w.Str(o.Template);
        w.Dict(d =>
        {
            d.Str("originalOwner", string.IsNullOrWhiteSpace(o.Owner) ? "team" : o.Owner);
            d.Str("uniqueID", $"{o.Template}_{(int)x}_{(int)y}");
            d.Int("objectInitialHealth", 100);
            d.Bool("objectEnabled", true);
        });
        w.Close();
    }

    private static void WriteWaypoint(ChunkWriter w, string name, int id, double x, double y)
    {
        w.Open("Object", 3);
        w.F32(x); w.F32(y); w.F32(0);
        w.F32(0);                 // angle
        w.I32(0);                 // flags
        w.Str("");                // no thing template — a waypoint is a bare marker
        w.Dict(d =>
        {
            d.Int("waypointID", id);
            d.Str("waypointName", name);
            d.Str("uniqueID", name);
            d.Bool("objectSelectable", false);
        });
        w.Close();
    }

    /// <summary>
    /// Lighting: <c>int32 timeOfDay</c>, then for each of four times of day one terrain light,
    /// three object lights and two more terrain lights — 54 reals each — and a shadow colour.
    /// 4 + 4*54*4 + 4 = <b>872 bytes, which is the size of this chunk in all 149 shipped maps
    /// we can decode</b>. That arithmetic agreeing with the corpus is the check that the
    /// layout was read correctly, and it is why the values below can be ours rather than
    /// copied: only the SHAPE had to come from anywhere else.
    /// </summary>
    private static void WriteLighting(ChunkWriter w)
    {
        w.Open("GlobalLighting", 3);
        w.I32(1);                                   // TIME_OF_DAY_AFTERNOON
        for (int tod = 0; tod < 4; tod++)
        {
            void Light(float amb, float dif, float lx, float ly, float lz)
            {
                w.F32(amb); w.F32(amb); w.F32(amb);
                w.F32(dif); w.F32(dif); w.F32(dif);
                w.F32(lx); w.F32(ly); w.F32(lz);
            }
            Light(0.4f, 0.7f, -0.5f, -0.4f, -0.75f);   // terrain light 0
            Light(0.4f, 0.7f, -0.5f, -0.4f, -0.75f);   // object light 0
            Light(0, 0, 0, 0, -1);                      // object lights 1..2, off
            Light(0, 0, 0, 0, -1);
            Light(0, 0, 0, 0, -1);                      // terrain lights 1..2, off
            Light(0, 0, 0, 0, -1);
        }
        w.I32(unchecked((int)0xFF404040));          // shadow colour
        w.Close();
    }

    // --- DataChunk primitives -------------------------------------------------------------

    /// <summary>
    /// The symbol table. Ids are allocated in first-use order starting at 1, matching
    /// <c>DataChunkTableOfContents::allocateID</c> — <c>m_nextID</c> is initialised to 1, and
    /// id 0 is therefore never a valid chunk.
    /// </summary>
    private sealed class Toc
    {
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly List<string> _order = new();

        public int Id(string name)
        {
            if (_ids.TryGetValue(name, out int id)) return id;
            id = _ids.Count + 1;
            _ids[name] = id;
            _order.Add(name);
            return id;
        }

        public void WriteTo(Stream s)
        {
            s.Write("CkMp"u8);
            WriteI32(s, _order.Count);
            foreach (var name in _order)
            {
                var bytes = Encoding.ASCII.GetBytes(name);
                s.WriteByte((byte)bytes.Length);
                s.Write(bytes);
                WriteI32(s, _ids[name]);
            }
        }

        private static void WriteI32(Stream s, int v) => s.Write(BitConverter.GetBytes(v));
    }

    private sealed class ChunkWriter
    {
        private readonly MemoryStream _s;
        private readonly Toc _toc;
        private readonly Stack<long> _sizeSlots = new();

        public ChunkWriter(MemoryStream s, Toc toc) { _s = s; _toc = toc; }

        public void Open(string name, ushort version)
        {
            I32(_toc.Id(name));
            _s.Write(BitConverter.GetBytes(version));
            _sizeSlots.Push(_s.Position);
            I32(0);                       // back-patched by Close
        }

        /// <summary>Back-patch the size. Predicting it instead is the bug that makes a chunk
        /// file read as garbage from the first wrong byte onward — same lesson as W3D.</summary>
        public void Close()
        {
            long slot = _sizeSlots.Pop();
            long end = _s.Position;
            _s.Position = slot;
            I32((int)(end - slot - 4));
            _s.Position = end;
        }

        public void I32(int v) => _s.Write(BitConverter.GetBytes(v));
        public void F32(double v) => _s.Write(BitConverter.GetBytes((float)v));
        public void Raw(byte[] b) => _s.Write(b);

        public void Str(string v)
        {
            var b = Encoding.ASCII.GetBytes(v);
            _s.Write(BitConverter.GetBytes((ushort)b.Length));
            _s.Write(b);
        }

        public void Dict(Action<DictWriter> build)
        {
            var dw = new DictWriter(_toc);
            build(dw);
            _s.Write(BitConverter.GetBytes((ushort)dw.Count));
            dw.CopyTo(_s);
        }
    }

    /// <summary>
    /// A dict entry key is <c>(tocId &lt;&lt; 8) | type</c>, so dict KEYS share the chunk
    /// symbol table — which is why the table must be built while the body is written and
    /// cannot be declared up front.
    /// </summary>
    private sealed class DictWriter
    {
        private readonly Toc _toc;
        private readonly MemoryStream _ms = new();
        public int Count { get; private set; }

        public DictWriter(Toc toc) { _toc = toc; }

        private void Key(string name, int type)
        {
            _ms.Write(BitConverter.GetBytes((_toc.Id(name) << 8) | type));
            Count++;
        }

        public void Bool(string k, bool v) { Key(k, 0); _ms.WriteByte((byte)(v ? 1 : 0)); }
        public void Int(string k, int v) { Key(k, 1); _ms.Write(BitConverter.GetBytes(v)); }
        public void Real(string k, double v) { Key(k, 2); _ms.Write(BitConverter.GetBytes((float)v)); }

        public void Str(string k, string v)
        {
            Key(k, 3);
            var b = Encoding.ASCII.GetBytes(v);
            _ms.Write(BitConverter.GetBytes((ushort)b.Length));
            _ms.Write(b);
        }

        public void Wide(string k, string v)
        {
            Key(k, 4);
            _ms.Write(BitConverter.GetBytes((ushort)v.Length));   // LENGTH IN CHARS, not bytes
            _ms.Write(Encoding.Unicode.GetBytes(v));
        }

        public void CopyTo(Stream s) { _ms.Position = 0; _ms.CopyTo(s); }
    }
}
