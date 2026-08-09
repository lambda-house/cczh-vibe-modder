using System.Text;
using System.Text.Json;
using RtsSkeleton.Core;

namespace RtsSkeleton.Content;

/// <summary>
/// Layered content packs. A mod is a patch over a base pack, not a fork of it.
///
/// Loading is N-layer last-wins over an ORDERED list of packs. Zero Hour proves the
/// shape — it ships Default/X.ini, then X.ini, then a per-scenario map.ini, and its
/// loader sorts directory entries with the comment "This keeps things the same between
/// machines in a network game". What it lacks is provenance: a ZH sidecar declares no
/// base, no version and no hash, so multiplayer must ship the file over the wire and
/// compare a whole-corpus CRC that can say two clients differ but never what differs.
/// <see cref="Compose"/> supplies exactly that missing piece.
///
/// Merge rules, in one place because they are the whole contract:
///   - vocabularies (damageTypes, armorClasses) UNION, base order preserved, new names
///     appended. Never reordered — position is identity for the damageVsArmor matrix.
///   - keyed tables (units, weapons, factions, tech nodes, veterancy tracks) merge by
///     key; a later layer redefining a key REPLACES that entry wholesale.
///   - damageVsArmor merges per (damageType, armorClass) CELL, so a mod can retune one
///     matchup without restating the row.
///   - meta/lint: last layer wins.
/// </summary>
public static class PackStack
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public sealed class Layer
    {
        public required string Path;
        public required string Name;
        public required int Version;
        public required ulong BytesHash;
        public required ContentPackDto Dto;
    }

    /// <summary>
    /// Read each pack in order and fold it onto the accumulator. Returns the composed
    /// DTO plus the layer manifest, which is what makes a result attributable.
    /// </summary>
    public static (ContentPackDto Dto, List<Layer> Layers) Compose(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw new InvalidDataException("no content pack given");

        var layers = new List<Layer>();
        ContentPackDto? acc = null;

        foreach (var path in paths)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var dto = JsonSerializer.Deserialize<ContentPackDto>(bytes, JsonOpts)
                      ?? throw new InvalidDataException($"{path}: empty content pack");

            layers.Add(new Layer
            {
                Path = path,
                Name = dto.Meta.Name,
                Version = dto.Meta.Version,
                BytesHash = Fnv1a64.HashBytes(bytes),
                Dto = dto,
            });

            acc = acc is null ? dto : Merge(acc, dto);
        }

        return (acc!, layers);
    }

    /// <summary>
    /// Provenance anchor for a stack. Folds each layer's (ordinal, name, version, bytes)
    /// in load order, so reordering two mods or bumping one byte both change the hash —
    /// and the hash of a single-layer stack still depends only on that pack's bytes.
    /// </summary>
    public static ulong StackHash(IReadOnlyList<Layer> layers)
    {
        var h = Fnv1a64.Create();
        for (int i = 0; i < layers.Count; i++)
        {
            h.AddInt32(i);
            h.AddBytes(Encoding.UTF8.GetBytes(layers[i].Name));
            h.AddInt32(layers[i].Version);
            h.AddUInt64(layers[i].BytesHash);
        }
        return h.Value;
    }

    private static ContentPackDto Merge(ContentPackDto b, ContentPackDto over)
    {
        var m = new ContentPackDto
        {
            Meta = over.Meta,
            Lint = over.Lint,
            DamageTypes = UnionOrdered(b.DamageTypes, over.DamageTypes),
            ArmorClasses = UnionOrdered(b.ArmorClasses, over.ArmorClasses),
            Weapons = MergeKeyed(b.Weapons, over.Weapons),
            VeterancyTracks = MergeKeyed(b.VeterancyTracks, over.VeterancyTracks),
            Units = MergeKeyed(b.Units, over.Units),
            Factions = MergeKeyed(b.Factions, over.Factions),
            Tech = new TechDto { Nodes = MergeKeyed(b.Tech.Nodes, over.Tech.Nodes) },
            Sciences = MergeKeyed(b.Sciences, over.Sciences),
            Powers = MergeKeyed(b.Powers, over.Powers),
            // Ranks are an ORDERED ladder, not a keyed table: a later pack replaces the whole
            // ladder or inherits it whole. Merging them per index would let a mod that only
            // wants a cheaper rank 2 silently reorder the thresholds.
            Ranks = over.Ranks.Count > 0 ? over.Ranks : b.Ranks,
            DamageVsArmor = MergeMatrix(b.DamageVsArmor, over.DamageVsArmor),
        };
        return m;
    }

    /// <summary>Base order first, then names the overlay introduces. Never reorders.</summary>
    private static List<string> UnionOrdered(List<string> b, List<string> over)
    {
        var result = new List<string>(b);
        foreach (var s in over)
            if (!result.Contains(s, StringComparer.Ordinal))
                result.Add(s);
        return result;
    }

    private static Dictionary<string, T> MergeKeyed<T>(Dictionary<string, T> b, Dictionary<string, T> over)
    {
        var result = new Dictionary<string, T>(b, StringComparer.Ordinal);
        foreach (var (k, v) in over) result[k] = v;   // whole-entry replace
        return result;
    }

    private static Dictionary<string, Dictionary<string, double>> MergeMatrix(
        Dictionary<string, Dictionary<string, double>> b,
        Dictionary<string, Dictionary<string, double>> over)
    {
        var result = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var (dt, row) in b) result[dt] = new Dictionary<string, double>(row, StringComparer.Ordinal);
        foreach (var (dt, row) in over)
        {
            if (!result.TryGetValue(dt, out var into))
                result[dt] = into = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (ac, v) in row) into[ac] = v;   // per-cell
        }
        return result;
    }
}
