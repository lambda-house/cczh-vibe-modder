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
        byte[] buf;
        try { buf = File.ReadAllBytes(path); }
        catch (IOException) { return found; }
        catch (UnauthorizedAccessException) { return found; }

        Walk(buf, 0, buf.Length, 0);
        return found;

        void Walk(byte[] b, int off, int end, int depth)
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
                    Walk(b, body, body + size, depth + 1);
                }
                else if (type == TextureName && size > 0)
                {
                    int len = 0;
                    while (len < size && b[body + len] != 0) len++;
                    string name = System.Text.Encoding.Latin1.GetString(b, body, len).Trim();
                    if (name.Length > 0 && seen.Add(name)) found.Add(name);
                }
                off = body + size;
            }
        }
    }
}
