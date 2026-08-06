using System.Text.Json;
using RtsSkeleton.Core;
using RtsSkeleton.Runtime;

namespace RtsSkeleton.Content;

public sealed class WeaponDef
{
    public required string Id;
    public int DamageTypeIdx;
    public Fix64 Damage;
    public Fix64 Range;
    public Fix64 RangeSq;
    public Fix64 AcquireRangeSq;
    public int CooldownTicks;
    public Fix64 Spread;
}

public sealed class VetTrackDef
{
    public required string Id;
    public int[] Thresholds = Array.Empty<int>();
    public Modifier[][] RankBundles = Array.Empty<Modifier[]>();
}

public sealed class UnitProto
{
    public required string Id;
    public required string FactionId;
    public int Cost;
    public string[] Prerequisites = Array.Empty<string>();
    public int ArmorClassIdx;
    public int WeaponIdx;
    public int VetTrackIdx = -1;
    /// <summary>Base stats indexed by <see cref="Stat"/>. Speed already per-tick.</summary>
    public Fix64[] BaseStats = new Fix64[StatResolver.StatCount];
}

/// <summary>
/// Compiled, validated content. Loading is the only place doubles are converted
/// to fixed-point; everything downstream is deterministic by construction.
/// ContentHash fingerprints the source bytes so every sim result is attributable
/// to an exact content bundle — the provenance anchor for the AI balancing loop.
/// </summary>
public sealed class ContentDb
{
    public const int TicksPerSecond = 30;
    private const int AcquireRangeMultiplier = 4;

    public required string PackName;
    public ulong ContentHash;

    public string[] DamageTypes = Array.Empty<string>();
    public string[] ArmorClasses = Array.Empty<string>();
    /// <summary>[damageType, armorClass] -> multiplier. The counter-matrix seed.</summary>
    public Fix64[,] DamageVsArmor = new Fix64[0, 0];

    public WeaponDef[] Weapons = Array.Empty<WeaponDef>();
    public VetTrackDef[] VetTracks = Array.Empty<VetTrackDef>();
    public UnitProto[] Units = Array.Empty<UnitProto>();
    public Dictionary<string, int> UnitIndexById = new();
    public Dictionary<string, string[]> Factions = new();
    public Dictionary<string, TechNodeDto> TechNodes = new();
    public LintConfigDto LintConfig = new();

    public static ContentDb Load(string path, out List<string> errors, out List<string> warnings)
    {
        errors = new List<string>();
        warnings = new List<string>();

        byte[] bytes = File.ReadAllBytes(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var dto = JsonSerializer.Deserialize<ContentPackDto>(bytes, options)
                  ?? throw new InvalidDataException("Empty content pack");

        var db = new ContentDb
        {
            PackName = dto.Meta.Name,
            ContentHash = Fnv1a64.HashBytes(bytes),
            LintConfig = dto.Lint,
        };

        // --- Vocabulary tables --------------------------------------------------
        db.DamageTypes = dto.DamageTypes.ToArray();
        db.ArmorClasses = dto.ArmorClasses.ToArray();
        var dtIdx = IndexOf(db.DamageTypes);
        var acIdx = IndexOf(db.ArmorClasses);

        db.DamageVsArmor = new Fix64[db.DamageTypes.Length, db.ArmorClasses.Length];
        for (int d = 0; d < db.DamageTypes.Length; d++)
        for (int a = 0; a < db.ArmorClasses.Length; a++)
            db.DamageVsArmor[d, a] = Fix64.One;

        foreach (var (dt, row) in dto.DamageVsArmor)
        {
            if (!dtIdx.TryGetValue(dt, out int d))
            {
                errors.Add($"damageVsArmor: unknown damage type '{dt}'");
                continue;
            }
            foreach (var (ac, mult) in row)
            {
                if (!acIdx.TryGetValue(ac, out int a))
                {
                    errors.Add($"damageVsArmor[{dt}]: unknown armor class '{ac}'");
                    continue;
                }
                db.DamageVsArmor[d, a] = Fix64.FromDoubleAtLoadBoundary(mult);
            }
        }

        // --- Weapons ------------------------------------------------------------
        var weaponIdx = new Dictionary<string, int>();
        var weapons = new List<WeaponDef>();
        foreach (var (id, w) in dto.Weapons)
        {
            if (!dtIdx.TryGetValue(w.DamageType, out int d))
            {
                errors.Add($"weapon '{id}': unknown damage type '{w.DamageType}'");
                d = 0;
            }
            if (w.CooldownTicks <= 0) errors.Add($"weapon '{id}': cooldownTicks must be > 0");
            var range = Fix64.FromDoubleAtLoadBoundary(w.Range);
            var acquire = range * AcquireRangeMultiplier;
            weaponIdx[id] = weapons.Count;
            weapons.Add(new WeaponDef
            {
                Id = id,
                DamageTypeIdx = d,
                Damage = Fix64.FromDoubleAtLoadBoundary(w.Damage),
                Range = range,
                RangeSq = range * range,
                AcquireRangeSq = acquire * acquire,
                CooldownTicks = Math.Max(1, w.CooldownTicks),
                Spread = Fix64.FromDoubleAtLoadBoundary(w.Spread),
            });
        }
        db.Weapons = weapons.ToArray();

        // --- Veterancy tracks ---------------------------------------------------
        var trackIdx = new Dictionary<string, int>();
        var tracks = new List<VetTrackDef>();
        foreach (var (id, t) in dto.VeterancyTracks)
        {
            if (t.Thresholds.Count != t.Ranks.Count)
                errors.Add($"veterancy '{id}': thresholds ({t.Thresholds.Count}) and ranks ({t.Ranks.Count}) length mismatch");
            for (int i = 1; i < t.Thresholds.Count; i++)
                if (t.Thresholds[i] <= t.Thresholds[i - 1])
                    errors.Add($"veterancy '{id}': thresholds must be strictly increasing");

            var bundles = new List<Modifier[]>();
            foreach (var rank in t.Ranks)
            {
                var mods = new List<Modifier>();
                foreach (var m in rank)
                {
                    if (!Enum.TryParse<Stat>(m.Stat, ignoreCase: true, out var stat))
                    {
                        errors.Add($"veterancy '{id}': unknown stat '{m.Stat}'");
                        continue;
                    }
                    if (!Enum.TryParse<ModOp>(m.Op, ignoreCase: true, out var op))
                    {
                        errors.Add($"veterancy '{id}': unknown op '{m.Op}' (expected Add|Mul)");
                        continue;
                    }
                    mods.Add(new Modifier(stat, op, Fix64.FromDoubleAtLoadBoundary(m.Value)));
                }
                bundles.Add(mods.ToArray());
            }
            trackIdx[id] = tracks.Count;
            tracks.Add(new VetTrackDef { Id = id, Thresholds = t.Thresholds.ToArray(), RankBundles = bundles.ToArray() });
        }
        db.VetTracks = tracks.ToArray();

        // --- Tech DAG -----------------------------------------------------------
        db.TechNodes = dto.Tech.Nodes;
        foreach (var (id, node) in dto.Tech.Nodes)
            foreach (var req in node.Requires)
                if (!dto.Tech.Nodes.ContainsKey(req))
                    errors.Add($"tech '{id}': unknown prerequisite '{req}'");
        CheckAcyclic(dto.Tech.Nodes, errors);

        // --- Units --------------------------------------------------------------
        var units = new List<UnitProto>();
        foreach (var (id, u) in dto.Units.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var c = u.Components;
            if (c.Health is null) { errors.Add($"unit '{id}': missing Health component"); continue; }
            if (c.Mobile is null) { errors.Add($"unit '{id}': missing Mobile component"); continue; }
            if (c.WeaponBearer is null) { errors.Add($"unit '{id}': missing WeaponBearer component"); continue; }

            if (!acIdx.TryGetValue(c.Health.ArmorClass, out int armor))
            {
                errors.Add($"unit '{id}': unknown armor class '{c.Health.ArmorClass}'");
                armor = 0;
            }
            if (!weaponIdx.TryGetValue(c.WeaponBearer.Weapon, out int wpn))
            {
                errors.Add($"unit '{id}': unknown weapon '{c.WeaponBearer.Weapon}'");
                continue;
            }
            int vet = -1;
            if (c.VeterancyCarrier is not null && !trackIdx.TryGetValue(c.VeterancyCarrier.Track, out vet))
            {
                errors.Add($"unit '{id}': unknown veterancy track '{c.VeterancyCarrier.Track}'");
                vet = -1;
            }
            foreach (var req in u.Prerequisites)
                if (!dto.Tech.Nodes.ContainsKey(req))
                    errors.Add($"unit '{id}': unknown tech prerequisite '{req}'");
            if (u.Cost <= 0) errors.Add($"unit '{id}': cost must be > 0");

            var proto = new UnitProto
            {
                Id = id,
                FactionId = u.Faction,
                Cost = u.Cost,
                Prerequisites = u.Prerequisites.ToArray(),
                ArmorClassIdx = armor,
                WeaponIdx = wpn,
                VetTrackIdx = vet,
            };
            var w = db.Weapons[wpn];
            proto.BaseStats[(int)Stat.MaxHp] = Fix64.FromDoubleAtLoadBoundary(c.Health.Max);
            proto.BaseStats[(int)Stat.Speed] = Fix64.FromDoubleAtLoadBoundary(c.Mobile.Speed) / Fix64.FromInt(TicksPerSecond);
            proto.BaseStats[(int)Stat.Damage] = w.Damage;
            proto.BaseStats[(int)Stat.CooldownScale] = Fix64.One;
            proto.BaseStats[(int)Stat.Range] = w.Range;
            proto.BaseStats[(int)Stat.ArmorFactor] = Fix64.One;

            db.UnitIndexById[id] = units.Count;
            units.Add(proto);
        }
        db.Units = units.ToArray();

        // --- Factions -----------------------------------------------------------
        foreach (var (fid, list) in dto.Factions)
        {
            foreach (var uid in list)
                if (!db.UnitIndexById.ContainsKey(uid))
                    errors.Add($"faction '{fid}': unknown unit '{uid}'");
            db.Factions[fid] = list.ToArray();
        }
        foreach (var u in db.Units)
            if (!dto.Factions.ContainsKey(u.FactionId))
                warnings.Add($"unit '{u.Id}': faction '{u.FactionId}' not declared in factions table");

        // --- Coarse balance lint: raw DPS per 1000 cost -------------------------
        var band = db.LintConfig.DpsPer1000CostBand;
        double lo = band.Count > 0 ? band[0] : 0, hi = band.Count > 1 ? band[1] : double.MaxValue;
        foreach (var u in db.Units)
        {
            var w = db.Weapons[u.WeaponIdx];
            double dps = w.Damage.ToDoubleForDisplay() * TicksPerSecond / w.CooldownTicks;
            double per1000 = dps * 1000.0 / u.Cost;
            if (per1000 < lo || per1000 > hi)
                warnings.Add($"unit '{u.Id}': {per1000:0.#} DPS/1000 cost outside band [{lo}, {hi}]");
        }

        return db;
    }

    private static Dictionary<string, int> IndexOf(string[] items)
    {
        var d = new Dictionary<string, int>();
        for (int i = 0; i < items.Length; i++) d[items[i]] = i;
        return d;
    }

    /// <summary>Kahn's algorithm; any leftover node is on a cycle.</summary>
    private static void CheckAcyclic(Dictionary<string, TechNodeDto> nodes, List<string> errors)
    {
        var indegree = nodes.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        foreach (var (_, node) in nodes)
            foreach (var req in node.Requires)
                if (indegree.ContainsKey(req)) { /* edge req -> node */ }
        foreach (var (id, node) in nodes)
            foreach (var req in node.Requires)
                if (nodes.ContainsKey(req)) indegree[id]++;

        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));
        int visited = 0;
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            visited++;
            foreach (var (id, node) in nodes)
                if (node.Requires.Contains(cur) && --indegree[id] == 0)
                    queue.Enqueue(id);
        }
        if (visited != nodes.Count)
            errors.Add("tech: prerequisite graph contains a cycle");
    }

    /// <summary>Tech gating helper: is a unit producible given a set of researched nodes?</summary>
    public bool CanProduce(UnitProto proto, IReadOnlySet<string> researched)
        => proto.Prerequisites.All(researched.Contains);
}
