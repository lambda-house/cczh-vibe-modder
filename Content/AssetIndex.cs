using System.Text;

namespace RtsSkeleton.Content;

/// <summary>
/// Which art assets EXIST in the target install — the `.big` archives plus the loose tree that
/// shadows them.
///
/// <para><b>Why this has to exist.</b> A pack names a mesh with <c>zh.models</c>, and until now
/// the only check was that a MAPPING existed. Whether the file did was nobody's job. Name a
/// model that is not there and the engine renders nothing, logs nothing, and the unit is
/// invisible while behaving perfectly — it moves, shoots, dies and cannot be seen or clicked.
/// That is the same failure class as <c>desertA</c>, the TerrainType whose texture ships in no
/// archive, and that one reached a real match before anyone noticed.</para>
///
/// <para>The index reads `.big` tables of contents directly rather than going through a
/// generated file. The format is four fields and a name list — engine packaging, not EA
/// content — and reading it live means the check is available whenever the game is, with
/// nothing to regenerate and nothing to go stale. Names only: no bytes are extracted, and
/// nothing from the archives is ever written out.</para>
///
/// <para><b>Absent is not a failure.</b> With no install (CI, a fresh clone) the index is
/// empty and <see cref="IsUsable"/> is false, and callers must skip the check rather than
/// report every reference as broken. The same discipline as <c>ArtProfiles</c>.</para>
/// </summary>
public sealed class AssetIndex
{
    /// <summary>Leaf names, upper-cased, without extension: "AVLEOPARD", "RTSBOX".</summary>
    private readonly HashSet<string> _stems = new(StringComparer.Ordinal);
    private readonly HashSet<string> _full = new(StringComparer.Ordinal);

    public int Count => _full.Count;

    /// <summary>False when there is no install to check against — the caller must then skip,
    /// not fail. A check that cannot run must never look like a check that failed.</summary>
    public bool IsUsable => _full.Count > 0;

    /// <summary>
    /// Is a name present? Matched on the STEM, because that is how the engine references art:
    /// an INI says <c>Model = AVLeopard</c> and the loader finds <c>AVLeopard.w3d</c> wherever
    /// it lives, case-insensitively, in an archive or loose.
    /// </summary>
    public bool Has(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _stems.Contains(Stem(name));
    }

    private static string Stem(string path)
    {
        string s = path.Replace('\\', '/');
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        int dot = s.LastIndexOf('.');
        if (dot > 0) s = s[..dot];
        return s.ToUpperInvariant();
    }

    /// <summary>
    /// Scan an install: every `.big` in the root and in the base-game subdirectory, then the
    /// loose <c>Art/</c> tree that shadows them.
    /// </summary>
    public static AssetIndex Load(string? gameDir, IEnumerable<string>? alsoProvided = null)
    {
        var idx = new AssetIndex();

        foreach (var name in alsoProvided ?? Array.Empty<string>())
            idx.Add(name);

        if (gameDir is not null && Directory.Exists(gameDir))
        {
            // ZH is a delta over base Generals, so the archives live in two layers. Missing
            // either half would report half of retail's art as absent.
            foreach (var dir in new[] { gameDir, Path.Combine(gameDir, "ZH_Generals") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var big in Directory.EnumerateFiles(dir, "*.big").OrderBy(p => p, StringComparer.Ordinal))
                    try { idx.ReadBig(big); } catch { /* a truncated archive is not our error */ }
            }

            string art = Path.Combine(gameDir, "Art");
            if (Directory.Exists(art))
                foreach (var f in Directory.EnumerateFiles(art, "*", SearchOption.AllDirectories))
                    idx.Add(f);
        }

        return idx;
    }

    private void Add(string path)
    {
        _full.Add(path);
        _stems.Add(Stem(path));
    }

    /// <summary>
    /// A BIGF table of contents. Big-endian counts and offsets in a little-endian era, which
    /// is the one thing to get wrong here; names are NUL-terminated and backslash-separated.
    /// </summary>
    private void ReadBig(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (new string(br.ReadChars(4)) != "BIGF") return;
        br.ReadUInt32();                                   // archive size, stored unswapped
        uint count = BE(br.ReadUInt32());
        br.ReadUInt32();                                   // offset of the first file
        fs.Position = 0x10;

        var name = new StringBuilder(128);
        for (uint i = 0; i < count; i++)
        {
            br.ReadUInt32(); br.ReadUInt32();              // offset, size — we want names only
            name.Clear();
            for (byte b = br.ReadByte(); b != 0; b = br.ReadByte()) name.Append((char)b);
            Add(name.ToString());
        }
    }

    private static uint BE(uint v) =>
        (v >> 24) | ((v >> 8) & 0x0000FF00) | ((v << 8) & 0x00FF0000) | (v << 24);
}
