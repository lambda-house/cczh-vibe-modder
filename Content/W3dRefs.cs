namespace RtsSkeleton.Content;

/// <summary>
/// What a `.w3d` file ASKS FOR, read out of the binary.
///
/// <para><b>Why the lint has to open the file.</b> A mesh's texture references live inside the
/// model, not in INI: <c>W3D_CHUNK_TEXTURE_NAME</c> (0x32) holds a bare filename that the
/// engine resolves at load. So a pack can carry a mesh it authored, reference nothing but that
/// mesh from its INI, and still depend on EA's art — because the mesh itself names a retail
/// texture. Nothing in the INI layer can see that, and in game it renders perfectly right up
/// until the file is missing, at which point the object appears untextured with no error.</para>
///
/// <para>Deliberately a READER only, and a shallow one: chunk headers and one leaf type. The
/// full parser lives in <c>tools/zhasset</c>, which is where authoring belongs. Duplicating it
/// here would be two implementations of the same thing with no second opinion to show for
/// it — this one exists because the lint cannot shell out to Python.</para>
/// </summary>
public static class W3dRefs
{
    private const uint TextureName = 0x0032;
    private const uint MeshHeader3 = 0x001F;

    /// <summary>
    /// Sub-object names the model declares, read out of each <c>MESH_HEADER3</c>.
    ///
    /// <para><b>The name is the interface.</b> Two engine features are switched on by nothing
    /// but a mesh's name — <c>W3DTankDraw</c> finds a scrolling belt with
    /// <c>_strnicmp(meshName,"TREADS",6)</c>, and <c>W3DAssetManager</c> recolours a submesh to
    /// the player's colour on <c>_strnicmp(meshName,"HOUSECOLOR",10)</c>. Neither is declared in
    /// INI, so the only way for the compiler to know whether a model can use them is to open
    /// the model and look. Emitting the INI half without the mesh half gives a tank whose
    /// treads never move and whose colour never changes, silently and in a clean boot.</para>
    /// </summary>
    public static List<string> MeshNames(string path)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TryRead(path, out var buf)) return found;
        Walk(buf, 0, buf.Length, 0, MeshHeader3, found, seen);
        return found;
    }

    /// <summary>
    /// Texture filenames the model names, in file order, de-duplicated case-insensitively.
    /// An unreadable or truncated file yields an empty list rather than throwing: the lint's
    /// job is to report what a pack depends on, and a malformed mesh is a different check's
    /// problem.
    /// </summary>
    public static List<string> Textures(string path)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TryRead(path, out var buf)) return found;
        Walk(buf, 0, buf.Length, 0, TextureName, found, seen);
        return found;
    }

    private static bool TryRead(string path, out byte[] buf)
    {
        try { buf = File.ReadAllBytes(path); return true; }
        catch (IOException) { buf = []; return false; }
        catch (UnauthorizedAccessException) { buf = []; return false; }
    }

    private static void Walk(byte[] b, int off, int end, int depth, uint want,
                             List<string> found, HashSet<string> seen)
    {
        // A malformed file can nest arbitrarily; bound it rather than risk a stack
        // overflow on input the lint does not control.
        if (depth > 16) return;
        while (off + 8 <= end)
        {
            uint type = BitConverter.ToUInt32(b, off);
            uint raw = BitConverter.ToUInt32(b, off + 4);
            // The HIGH BIT of the size marks a container, and the size excludes the
            // 8-byte header. Reading it as a plain length is the single mistake that
            // turns this walk into garbage.
            int size = (int)(raw & 0x7FFFFFFF);
            bool container = (raw & 0x80000000) != 0;
            int body = off + 8;
            if (size < 0 || body + size > end) return;      // truncated: stop, do not throw

            if (container)
            {
                Walk(b, body, body + size, depth + 1, want, found, seen);
            }
            else if (type == want && size > 0)
            {
                // MESH_HEADER3 carries a fixed-width NUL-padded MeshName at offset 8, 16 bytes
                // wide; TEXTURE_NAME is a NUL-terminated string filling the chunk. Same
                // extraction either way once the window is right.
                int at = want == MeshHeader3 ? body + 8 : body;
                int cap = want == MeshHeader3 ? Math.Min(16, body + size - at) : size;
                if (cap <= 0) { off = body + size; continue; }
                int len = 0;
                while (len < cap && b[at + len] != 0) len++;
                string name = System.Text.Encoding.Latin1.GetString(b, at, len).Trim();
                if (name.Length > 0 && seen.Add(name)) found.Add(name);
            }
            off = body + size;
        }
    }

}
