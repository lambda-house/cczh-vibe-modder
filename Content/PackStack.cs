using System.Reflection;
using System.Text;
using System.Text.Json;
using RtsSkeleton.Core;

namespace RtsSkeleton.Content;

/// <summary>
/// Layered content packs — <b>Stage 1 of the one resolution order</b>. A mod is a patch over
/// a base pack, not a fork of it.
///
/// <para><b>THE TOTAL RESOLUTION ORDER.</b> Three mechanisms compose content, and they run in
/// this order, each exactly once, each completing before the next begins:</para>
/// <list type="number">
///   <item><b>LAYER</b> (here) — packs fold in ordinal order; per-key last-wins; a null value
///     REMOVES the key. Produces one composed DTO. Knows nothing of units or factions.</item>
///   <item><b>DEFAULT</b> (<see cref="ContentDb"/>) — the composed <c>defaults</c> block fills
///     every unit field left unstated. Produces the shared prototype set.</item>
///   <item><b>INHERIT + ROSTER</b> (<c>ContentDb.ResolveFactions</c>) — faction <c>extends</c>
///     flattens parents before children; within one faction, strictly
///     <c>remove</c> → <c>add</c> → <c>modify</c>. Produces faction-local variants.</item>
/// </list>
///
/// <para>There is deliberately NO entity-level <c>extends</c>: a unit never inherits from
/// another unit. Faction <c>modify</c> plus layering already express every case, and a third
/// inheritance axis is exactly the complexity this order exists to bound.</para>
///
/// <para><b>The consequences worth knowing before you author against it:</b></para>
/// <list type="bullet">
///   <item>A later layer cannot AMEND a faction's <c>modify</c> block — factions are keyed
///     entries, so it replaces the whole faction. Deltas <i>within</i> a layer are what
///     faction <c>extends</c> is for; layering is for deltas <i>between</i> packs.</item>
///   <item>A <c>modify</c> is a delta on whatever the unit ends up being after Stage 1. So a
///     mod that retunes a unit keeps every faction's tweaks, applied on top of the new
///     definition — the useful direction, and the reason Stage 1 precedes Stage 3.</item>
///   <item>A removal lands in Stage 1, so by Stage 3 the name simply does not exist and every
///     reference to it is an error naming the layer that removed it. "Pack patch removes what
///     a faction modify replaced" is therefore not a merge conflict at all; it is a dangling
///     reference, reported like any other.</item>
/// </list>
///
/// <para>Zero Hour proves the layering shape — it ships Default/X.ini, then X.ini, then a
/// per-scenario map.ini, and its loader sorts directory entries with the comment "This keeps
/// things the same between machines in a network game". What it lacks is provenance: a ZH
/// sidecar declares no base, no version and no hash, so multiplayer must ship the file over
/// the wire and compare a whole-corpus CRC that can say two clients differ but never what
/// differs. <see cref="StackHash"/> supplies exactly that missing piece.</para>
///
/// <para><b>Merge rules, in one place because they are the whole contract:</b></para>
/// <list type="bullet">
///   <item>vocabularies (damageTypes, armorClasses) UNION, base order preserved, new names
///     appended. Never reordered and never removed — position is identity for the
///     damageVsArmor matrix, so dropping one would renumber every cell after it.</item>
///   <item>keyed tables (units, weapons, factions, tech nodes, veterancy tracks, sciences,
///     powers, overrides) merge by key; a later layer redefining a key REPLACES that entry
///     wholesale, and a null value REMOVES it.</item>
///   <item>damageVsArmor merges per (damageType, armorClass) CELL, so a mod can retune one
///     matchup without restating the row. A null row removes the row.</item>
///   <item>defaults merge per FIELD; zh sub-tables per key; zh.turreted unions.</item>
///   <item>meta, lint, ranks: whole-block last-wins, but only from a layer that STATES them.
///     Absent is not the same as default — see <see cref="DefaultsDto"/>.</item>
/// </list>
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
    /// The composed stack. <see cref="Diagnostics"/> are load errors raised by Stage 1 itself
    /// — removing a name no earlier layer declared is the only way to earn one.
    /// <see cref="RemovedBy"/> is provenance, so a later stage's "unknown unit 'x'" can say
    /// WHICH layer took it away instead of leaving the author to guess.
    /// </summary>
    public sealed class Composed
    {
        public required ContentPackDto Dto;
        public required List<Layer> Layers;
        public required List<string> Diagnostics;
        public required Dictionary<string, string> RemovedBy;
    }

    /// <summary>
    /// Read each pack in order and fold it onto the accumulator. Returns the composed
    /// DTO plus the layer manifest, which is what makes a result attributable.
    /// </summary>
    public static Composed Compose(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw new InvalidDataException("no content pack given");
        AssertMergeIsTotal();

        var layers = new List<Layer>();
        var diagnostics = new List<string>();
        var removedBy = new Dictionary<string, string>(StringComparer.Ordinal);

        // The accumulator starts EMPTY rather than at layer 0, so the first pack goes through
        // exactly the same merge as every later one. No special case means no divergence
        // between "what a base pack means" and "what a mod means" — and it is what makes a
        // removal in layer 0 a reportable error rather than a silent nothing.
        var acc = new ContentPackDto();

        foreach (var path in paths)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var dto = JsonSerializer.Deserialize<ContentPackDto>(bytes, JsonOpts)
                      ?? throw new InvalidDataException($"{path}: empty content pack");

            var layer = new Layer
            {
                Path = path,
                Name = dto.Meta.Name,
                Version = dto.Meta.Version,
                BytesHash = Fnv1a64.HashBytes(bytes),
                Dto = dto,
            };
            layers.Add(layer);

            acc = Merge(acc, dto, $"layer {layers.Count} ('{layer.Name}')", diagnostics, removedBy);
        }

        return new Composed { Dto = acc, Layers = layers, Diagnostics = diagnostics, RemovedBy = removedBy };
    }

    /// <summary>
    /// Why a name is missing, if a layer took it away. Key is "<c>table:id</c>", e.g.
    /// "units:crusader". Used to turn "unknown unit" into a repair instruction.
    /// </summary>
    public static string? WhyRemoved(Dictionary<string, string> removedBy, string table, string id)
        => removedBy.TryGetValue($"{table}:{id}", out var why) ? why : null;

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

    // --- Totality --------------------------------------------------------------------
    //
    // A merge that silently skips a field is the same bug UnitProto.CloneForFaction had: an
    // object initializer listing 9 of 20 fields, where the 11 it forgot vanished without a
    // word. It was live for months. `defaults` was already lost the same way here — every
    // multi-layer stack quietly reverted to the compiler's built-ins.
    //
    // So the merge declares what it handles and the declaration is checked against the type.
    // Adding a property to ContentPackDto without merging it now fails at load, loudly,
    // rather than at some later balance number that is quietly wrong.

    private static readonly string[] MergedProperties =
    {
        nameof(ContentPackDto.Meta),
        nameof(ContentPackDto.Defaults),
        nameof(ContentPackDto.DamageTypes),
        nameof(ContentPackDto.ArmorClasses),
        nameof(ContentPackDto.DamageVsArmor),
        nameof(ContentPackDto.Weapons),
        nameof(ContentPackDto.VeterancyTracks),
        nameof(ContentPackDto.Tech),
        nameof(ContentPackDto.Ranks),
        nameof(ContentPackDto.Sciences),
        nameof(ContentPackDto.Powers),
        nameof(ContentPackDto.Overrides),
        nameof(ContentPackDto.Units),
        nameof(ContentPackDto.Factions),
        nameof(ContentPackDto.Zh),
        nameof(ContentPackDto.Lint),
    };

    /// <summary>
    /// Every property of <see cref="ContentPackDto"/> must be named by <see cref="Merge"/>.
    /// Cheap enough to run on every load, which is the point — a test that has to be
    /// remembered is a test that is not run.
    /// </summary>
    public static void AssertMergeIsTotal()
    {
        var actual = typeof(ContentPackDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        var handled = MergedProperties.ToHashSet(StringComparer.Ordinal);

        var unmerged = actual.Except(handled).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var stale = handled.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (unmerged.Length == 0 && stale.Length == 0) return;

        var msg = new StringBuilder("PackStack.Merge is not total over ContentPackDto.");
        if (unmerged.Length > 0)
            msg.Append($" Unmerged, so it would be silently dropped from every layered stack: {string.Join(", ", unmerged)}.");
        if (stale.Length > 0)
            msg.Append($" Declared but no longer on the type: {string.Join(", ", stale)}.");
        throw new InvalidOperationException(msg.ToString());
    }

    // --- Merge -----------------------------------------------------------------------

    private static ContentPackDto Merge(ContentPackDto b, ContentPackDto over, string layer,
                                        List<string> diag, Dictionary<string, string> removedBy)
    {
        var m = new ContentPackDto
        {
            // The stack is named for its top pack, and lint/defaults/ranks inherit unless the
            // layer actually states them. Absent != default: see DefaultsDto.
            Meta = over.Meta,
            Lint = over.Lint ?? b.Lint,
            Ranks = over.Ranks ?? b.Ranks,
            Defaults = MergeDefaults(b.Defaults, over.Defaults),

            DamageTypes = UnionOrdered(b.DamageTypes, over.DamageTypes),
            ArmorClasses = UnionOrdered(b.ArmorClasses, over.ArmorClasses),

            Weapons = MergeKeyed(b.Weapons, over.Weapons, "weapons", layer, diag, removedBy),
            VeterancyTracks = MergeKeyed(b.VeterancyTracks, over.VeterancyTracks, "veterancyTracks", layer, diag, removedBy),
            Units = MergeKeyed(b.Units, over.Units, "units", layer, diag, removedBy),
            Factions = MergeKeyed(b.Factions, over.Factions, "factions", layer, diag, removedBy),
            Sciences = MergeKeyed(b.Sciences, over.Sciences, "sciences", layer, diag, removedBy),
            Powers = MergeKeyed(b.Powers, over.Powers, "powers", layer, diag, removedBy),
            Overrides = MergeKeyed(b.Overrides, over.Overrides, "overrides", layer, diag, removedBy),
            Tech = new TechDto
            {
                Nodes = MergeKeyed(b.Tech.Nodes, over.Tech.Nodes, "tech.nodes", layer, diag, removedBy),
            },

            DamageVsArmor = MergeMatrix(b.DamageVsArmor, over.DamageVsArmor, layer, diag, removedBy),
            Zh = MergeZh(b.Zh, over.Zh),
        };
        return m;
    }

    /// <summary>Per FIELD, not per block: a mod that wants a cheaper default cost should not
    /// have to restate the build time and the prerequisite list to keep them.</summary>
    private static DefaultsDto? MergeDefaults(DefaultsDto? b, DefaultsDto? over)
    {
        if (b is null) return over;
        if (over is null) return b;
        var (bu, ou) = (b.Unit, over.Unit);
        if (bu is null) return new DefaultsDto { Unit = ou };
        if (ou is null) return new DefaultsDto { Unit = bu };
        return new DefaultsDto
        {
            Unit = new UnitDefaultsDto
            {
                Cost = ou.Cost ?? bu.Cost,
                BuildTicks = ou.BuildTicks ?? bu.BuildTicks,
                UnitsPerPurchase = ou.UnitsPerPurchase ?? bu.UnitsPerPurchase,
                Prerequisites = ou.Prerequisites ?? bu.Prerequisites,
            },
        };
    }

    /// <summary>
    /// ZH target data is keyed by OUR ids, so it merges exactly like the keyed tables above.
    /// It used to be folded by a second, near-identical loop in ContentDb.LoadZhTarget — two
    /// implementations of one contract, which is how they drift.
    /// </summary>
    private static ZhTargetDto MergeZh(ZhTargetDto b, ZhTargetDto over) => new()
    {
        DamageTypes = LastWins(b.DamageTypes, over.DamageTypes),
        Models = LastWins(b.Models, over.Models),
        DrawModules = LastWins(b.DrawModules, over.DrawModules),
        Sides = LastWins(b.Sides, over.Sides),
        ArtRig = LastWins(b.ArtRig, over.ArtRig),
        Animations = LastWins(b.Animations, over.Animations),
        Turreted = UnionOrdered(b.Turreted, over.Turreted),
        WorldScale = over.WorldScale ?? b.WorldScale,
    };

    private static Dictionary<string, string> LastWins(Dictionary<string, string> b, Dictionary<string, string> over)
    {
        var r = new Dictionary<string, string>(b, StringComparer.Ordinal);
        foreach (var (k, v) in over) r[k] = v;
        return r;
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

    /// <summary>
    /// Whole-entry replace, and <c>"id": null</c> REMOVES.
    ///
    /// Removing what is not there is an ERROR, never a silent no-op. Retail shipped 88 dangling
    /// references across 68 broken definitions precisely because a name that resolves to
    /// nothing costs nothing to write; a removal that matches nothing is the same typo one step
    /// earlier, and it is the step where the author can still see what they meant.
    /// </summary>
    private static Dictionary<string, T> MergeKeyed<T>(
        Dictionary<string, T> b, Dictionary<string, T> over, string table, string layer,
        List<string> diag, Dictionary<string, string> removedBy) where T : class
    {
        var result = new Dictionary<string, T>(b, StringComparer.Ordinal);
        foreach (var k in over.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var v = over[k];
            if (v is not null) { result[k] = v; removedBy.Remove($"{table}:{k}"); continue; }

            if (!result.Remove(k))
                diag.Add($"{layer}: {table} removes '{k}', which no earlier layer declared");
            else
                removedBy[$"{table}:{k}"] = layer;
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, double>> MergeMatrix(
        Dictionary<string, Dictionary<string, double>> b,
        Dictionary<string, Dictionary<string, double>> over,
        string layer, List<string> diag, Dictionary<string, string> removedBy)
    {
        var result = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var (dt, row) in b) result[dt] = new Dictionary<string, double>(row, StringComparer.Ordinal);

        foreach (var dt in over.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var row = over[dt];
            if (row is null)
            {
                if (!result.Remove(dt))
                    diag.Add($"{layer}: damageVsArmor removes row '{dt}', which no earlier layer declared");
                else
                    removedBy[$"damageVsArmor:{dt}"] = layer;
                continue;
            }
            if (!result.TryGetValue(dt, out var into))
                result[dt] = into = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (ac, v) in row) into[ac] = v;   // per-cell
        }
        return result;
    }
}
