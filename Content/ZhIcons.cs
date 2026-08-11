using System.Text;

namespace RtsSkeleton.Content;

/// <summary>
/// Author the control-bar icon sheet and the <c>MappedImage</c> blocks that address it.
///
/// <para><b>Why this exists.</b> A compiled pack used to reference retail's own button art by
/// name — <c>SelectPortrait = SACWeaponsfact_L</c> — for every object, every command button
/// and every piece of HUD chrome. That works, costs nothing and is what every mod does, but it
/// means a pack whose mesh, texture, skeleton and animation are all authored still cannot be
/// looked at without EA's UI. Icons were the largest remaining block of borrowed names.</para>
///
/// <para><b>The load path is a directory scan, and it is one of the few EA's own source
/// admits to.</b> <c>Image.cpp:256</c> calls
/// <c>ini.loadDirectory("Data\\INI\\MappedImages\\HandCreated", TRUE, INI_LOAD_OVERWRITE, NULL)</c>,
/// so a new file of new names there is additive in the ordinary way. No probe needed.</para>
///
/// <para><b>One sheet, many rectangles</b> — retail's shape, for retail's reason.
/// <c>SAUserInterface512.tga</c> is a single 512x512 page holding hundreds of images, each a
/// <c>Coords</c> rectangle into it. One file per icon would work and would also cost a texture
/// bind per button.</para>
///
/// <para>The sheet is written by the COMPILER rather than by <c>zhasset</c>, which is a
/// deliberate change of policy: until now <c>rts compile</c> emitted INI and no art at all, so
/// a pack's models and textures were copied into the install by hand and nothing checked that
/// the files a pack referenced existed. An emitted pack should be complete.</para>
/// </summary>
public static class ZhIcons
{
    /// <summary>Edge of one cell in pixels. 64 is comfortably above the 60x48 that retail's own
    /// button images use, so an icon is never upscaled into blur.</summary>
    public const int CellPixels = 64;

    /// <summary>
    /// A sheet grows as the compiler claims cells and is laid out once at the end. It has to
    /// work that way round: some image names are not knowable up front — an upgrade's is
    /// derived from the FLAG that gates it during emission, not from any content id — and a
    /// pre-declared list would silently miss them.
    /// </summary>
    public sealed class Sheet
    {
        public required string TextureName;
        public readonly List<string> Images = new();
        private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

        /// <summary>Cells per side once laid out. Smallest square that holds the claims: a
        /// square keeps the Coords arithmetic identical in both axes.</summary>
        public int Cells
        {
            get { int c = 1; while (c * c < Math.Max(1, Images.Count)) c++; return c; }
        }

        public int Pixels => Cells * CellPixels;

        /// <summary>Reserve a cell and return the name, so call sites read
        /// <c>ButtonImage = {icons.Claim(...)}</c> and cannot reference an unrendered image.
        /// Assignment is by call order, and call order is the compiler's ordinal iteration
        /// over already-sorted content, so the same pack always produces the same sheet.</summary>
        public string Claim(string name)
        {
            if (!_byName.ContainsKey(name)) { _byName[name] = Images.Count; Images.Add(name); }
            return name;
        }

        public byte[] Tga() => Render(this);
    }

    public static Sheet Begin(string textureName) => new() { TextureName = textureName };

    /// <summary>
    /// Uncompressed 24-bit TGA: an 18-byte header then BGR triples, bottom-up. The one image
    /// format writable with no library at all. Retail is 3,496 <c>.dds</c> to 50 <c>.tga</c>,
    /// so DDS is the house format — but TGA ships, is supported, and needs no DXT compressor.
    ///
    /// <para>Cells get distinct hues by golden-ratio stepping so that ADJACENT indices are far
    /// apart in colour. That is diagnostic, not decoration: two buttons rendering the same
    /// colour means two <c>MappedImage</c> entries point at one cell, which is otherwise a
    /// silent mistake. The darker inset border makes an off-by-one rectangle read as a sliver
    /// of the neighbour rather than as nothing at all.</para>
    /// </summary>
    private static byte[] Render(Sheet s)
    {
        int w = s.Pixels, h = s.Pixels;
        var px = new byte[w * h * 4];

        void Put(int x, int y, int r, int g, int b)
        {
            // TGA's origin is bottom-left, so row 0 of the file is the BOTTOM of the image.
            // Writing top-down here and forgetting that is how a sheet comes out vertically
            // mirrored, which looks like a Coords bug and is not. Measured against a retail
            // UI page (SAUserInterface512_005.tga): descriptor 0x08, bottom-up, same as ours.
            int o = ((h - 1 - y) * w + x) * 4;
            px[o] = (byte)b; px[o + 1] = (byte)g; px[o + 2] = (byte)r; px[o + 3] = 255;
        }

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Put(x, y, 18, 20, 26);

        int cells = s.Cells;
        for (int idx = 0; idx < s.Images.Count; idx++)
        {
            int cx = idx % cells, cy = idx / cells;
            var (r, g, b) = Hue((idx * 0.6180339887) % 1.0);
            int dr = (int)(r * 0.35), dg = (int)(g * 0.35), db = (int)(b * 0.35);
            for (int y = 0; y < CellPixels; y++)
                for (int x = 0; x < CellPixels; x++)
                {
                    bool edge = x < 3 || y < 3 || x >= CellPixels - 3 || y >= CellPixels - 3;
                    Put(cx * CellPixels + x, cy * CellPixels + y,
                        edge ? dr : r, edge ? dg : g, edge ? db : b);
                }
        }

        // 32-bit BGRA with 8 alpha bits, because that is what EVERY retail UI page is
        // (measured: SAUserInterface512_005.tga is type 2, 32bpp, descriptor 0x08). A control
        // bar image is composited with alpha; a 24-bit one has no alpha channel to composite.
        var tga = new byte[18 + px.Length];
        tga[2] = 2;                                   // uncompressed true-colour
        BitConverter.GetBytes((ushort)w).CopyTo(tga, 12);
        BitConverter.GetBytes((ushort)h).CopyTo(tga, 14);
        tga[16] = 32;                                 // bits per pixel
        tga[17] = 0x08;                               // 8 alpha bits, origin bottom-left
        px.CopyTo(tga, 18);
        return tga;
    }

    private static (int R, int G, int B) Hue(double hue)
    {
        int i = (int)(hue * 6);
        double f = hue * 6 - i;
        int v = 235, p = 60, q = (int)(235 - 175 * f), t = (int)(60 + 175 * f);
        return (i % 6) switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t),
            3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
    }

    /// <summary>
    /// The <c>MappedImage</c> blocks. Syntax copied from a retail block that demonstrably
    /// loads (<c>SAUserInterface512.INI</c>), including <c>Status = NONE</c>, which is not
    /// optional decoration — it is a parsed field of the block.
    /// </summary>
    public static void WriteIni(StringBuilder sb, Sheet s)
    {
        int cells = s.Cells;
        for (int idx = 0; idx < s.Images.Count; idx++)
        {
            string name = s.Images[idx];
            int cx = idx % cells, cy = idx / cells;
            int left = cx * CellPixels, top = cy * CellPixels;
            sb.AppendLine($"MappedImage {name}");
            sb.AppendLine($"  Texture = {s.TextureName}.tga");
            sb.AppendLine($"  TextureWidth = {s.Pixels}");
            sb.AppendLine($"  TextureHeight = {s.Pixels}");
            sb.AppendLine($"  Coords = Left:{left} Top:{top} " +
                          $"Right:{left + CellPixels} Bottom:{top + CellPixels}");
            sb.AppendLine("  Status = NONE");
            sb.AppendLine("End").AppendLine();
        }
    }
}
