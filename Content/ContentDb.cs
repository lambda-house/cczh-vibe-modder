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

    /// <summary>Splash bands, squared at load so the tick loop never takes a square root.</summary>
    public Fix64 PrimaryRadiusSq;
    public Fix64 SecondaryDamage;
    public Fix64 SecondaryRadiusSq;
    public Fix64 MinRangeSq;
    /// <summary>0 = unlimited shots (no clip).</summary>
    public int ClipSize;
    public int ClipReloadTicks;
    public bool HasSplash => PrimaryRadiusSq > Fix64.Zero || SecondaryRadiusSq > Fix64.Zero;
}

public sealed class VetTrackDef
{
    public required string Id;
    public int[] Thresholds = Array.Empty<int>();
    public Modifier[][] RankBundles = Array.Empty<Modifier[]>();
}

public sealed class TechNodeDef
{
    public required string Id;
    public int Cost;
    public int ResearchTicks;
    public int[] RequiresIdx = Array.Empty<int>();
}

/// <summary>
/// A faction after inheritance is resolved: the flattened roster, plus the chain it
/// came from. <see cref="OwnUnitIdx"/> holds the effective prototype for each roster
/// entry — a faction-local variant where the faction patched it, the shared base
/// prototype where it didn't.
/// </summary>
public sealed class FactionDef
{
    public required string Id;
    public string? Parent;
    /// <summary>Roster entry ids as authored (e.g. "crusader"), ordinal-sorted.</summary>
    public string[] RosterIds = Array.Empty<string>();
    /// <summary>Effective prototype index per roster entry, parallel to RosterIds.</summary>
    public int[] OwnUnitIdx = Array.Empty<int>();
    /// <summary>Roster entries this faction patched, for diff reporting.</summary>
    public string[] PatchedIds = Array.Empty<string>();
    public string[] AddedIds = Array.Empty<string>();
    public string[] RemovedIds = Array.Empty<string>();

    /// <summary>Prototype index of the starting structure, or -1 if the faction is not startable.</summary>
    public int StartingBuildingIdx = -1;
    /// <summary>Prototype indices of units in play at t=0, in authored order.</summary>
    public int[] StartingUnitIdx = Array.Empty<int>();
    /// <summary>Faction-specific starting cash, or -1 to take the scenario default.</summary>
    public int StartMoney = -1;
    /// <summary>A faction is playable when something can produce for it from turn one.</summary>
    public bool IsStartable => StartingBuildingIdx >= 0 || StartingUnitIdx.Length > 0;
}

public sealed class UnitProto
{
    public required string Id;
    public required string FactionId;
    /// <summary>
    /// Position-independent identity: FNV-1a64 over the UTF-8 id. This — never
    /// <c>ProtoIdx</c> — is what <see cref="Runtime.World.HashInto"/> folds in, so a
    /// replay means the same thing no matter where a prototype lands in the table.
    /// Growing content therefore cannot renumber an existing prototype's meaning;
    /// only renaming one can, which is the honest semantics.
    /// </summary>
    public ulong StableId;
    /// <summary>Roster id this came from; equals Id for base units, the unpatched id
    /// for faction-local variants (so "usa_laser/crusader" reports "crusader").</summary>
    public string RosterId = "";
    /// <summary>True when produced by a faction patch rather than authored directly.</summary>
    public bool IsVariant;
    public int Cost;
    public int BuildTicks;
    /// <summary>Bodies produced per purchase. See <see cref="UnitDto.UnitsPerPurchase"/>.</summary>
    public int UnitsPerPurchase = 1;
    /// <summary>Every declared KindOf name, ordinal-sorted. Open set; lint closes it.</summary>
    public string[] KindOf = Array.Empty<string>();
    /// <summary>Dense mask over <see cref="Runtime.KindOf.Known"/> for the roles the sim tests.</summary>
    public uint KindMask;
    public BuildCompletion BuildCompletion = BuildCompletion.AppearsAtRallyPoint;
    public int EnergyProduction;
    public int MaxSimultaneousOfType;          // 0 = uncapped
    /// <summary>This proto's own index in <see cref="ContentDb.Units"/>. Runtime detail only.</summary>
    public int SelfIdx = -1;
    public bool Is(string flag) => (KindMask & (1u << Runtime.KindOf.BitOf(flag))) != 0;
    public bool IsStructure => Is(Runtime.KindOf.Structure);
    public string[] Prerequisites = Array.Empty<string>();
    public int[] PrereqTechIdx = Array.Empty<int>();
    /// <summary>
    /// Prototype indices that must be ALIVE for this to be buildable — ZH's
    /// `Prerequisites { Object = ... }`. Resolved after the unit table is complete,
    /// because a prerequisite may name a unit declared later in the pack.
    /// </summary>
    public int[] PrereqObjectIdx = Array.Empty<int>();
    /// <summary>Flags this prototype is born with.</summary>
    public ulong BirthFlags;
    /// <summary>Conditional loadouts in authored order; first match wins.</summary>
    public LoadoutVariant[] Variants = Array.Empty<LoadoutVariant>();
    /// <summary>Event rules in authored order. All matching rules fire, in order.</summary>
    public RuleDef[] Rules = Array.Empty<RuleDef>();
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
    /// <summary>Reserved armor-class key in a damageVsArmor row: fills every unnamed cell.</summary>
    public const string DefaultCellKey = "default";

    /// <summary>Square a load-boundary double into fixed-point. Radii are compared squared.</summary>
    private static Fix64 Sq(double v)
    {
        var f = Fix64.FromDoubleAtLoadBoundary(v);
        return f * f;
    }

    /// <summary>Compile a modifier list, reporting unknown stats/ops against a context label.</summary>
    private static Modifier[] ParseModifiers(List<ModifierDto> src, string what, List<string> errors)
    {
        var mods = new List<Modifier>();
        foreach (var m in src)
        {
            if (!Enum.TryParse<Stat>(m.Stat, ignoreCase: true, out var stat))
            { errors.Add($"{what}: unknown stat '{m.Stat}'"); continue; }
            if (!Enum.TryParse<ModOp>(m.Op, ignoreCase: true, out var op))
            { errors.Add($"{what}: unknown op '{m.Op}' (expected Add|Mul|Excess)"); continue; }
            mods.Add(new Modifier(stat, op, Fix64.FromDoubleAtLoadBoundary(m.Value)));
        }
        return mods.ToArray();
    }

    /// <summary>
    /// Position-independent prototype identity: FNV-1a64 over the UTF-8 id.
    /// Ordinal identity is what breaks replays when content grows — Zero Hour shipped
    /// exactly this bug (faction choice travels the wire as an index into
    /// PlayerTemplateStore, so inserting a template renumbers every later faction),
    /// while its Science store is name-keyed and reorders freely. We take the
    /// name-keyed side everywhere.
    /// </summary>
    public static ulong StableIdOf(string id) =>
        Fnv1a64.HashBytes(System.Text.Encoding.UTF8.GetBytes(id));

    public required string PackName;
    public ulong ContentHash;
    /// <summary>The ordered pack stack this db was composed from. Base first.</summary>
    public List<PackStack.Layer> Layers = new();
    /// <summary>True once any prototype is an FS_FACTORY; gates factory-dependent production.</summary>
    public bool HasFactories;
    /// <summary>True once any prototype declares EnergyProduction; gates the brownout rule.</summary>
    public bool HasPower;
    /// <summary>
    /// True once any content mentions a flag. Gates BOTH the loadout phase and the hashing
    /// of flag state, so a pack that never uses flags is bit-identical to one compiled
    /// before flags existed. Omitting an always-zero field from the hash loses no
    /// information — it cannot vary — and it preserves the regression value of pins that
    /// predate this slice.
    /// </summary>
    public bool HasFlags;
    /// <summary>Pack-wide flag vocabulary, ordinal-sorted. Bit position = index.</summary>
    public FlagTable Flags = new(Array.Empty<string>());
    /// <summary>True once any prototype declares a rule; gates the death-rule phase.</summary>
    public bool HasRules;

    public string[] DamageTypes = Array.Empty<string>();
    public string[] ArmorClasses = Array.Empty<string>();
    /// <summary>[damageType, armorClass] -> multiplier. The counter-matrix seed.</summary>
    public Fix64[,] DamageVsArmor = new Fix64[0, 0];

    public WeaponDef[] Weapons = Array.Empty<WeaponDef>();
    public VetTrackDef[] VetTracks = Array.Empty<VetTrackDef>();
    public UnitProto[] Units = Array.Empty<UnitProto>();
    public Dictionary<string, int> UnitIndexById = new();
    public Dictionary<string, FactionDef> Factions = new();
    /// <summary>Faction ids in resolution order (parents before children).</summary>
    public string[] FactionOrder = Array.Empty<string>();
    public Dictionary<string, TechNodeDto> TechNodes = new();
    /// <summary>Ordinal-sorted dense tech table; indices are what sim state stores.</summary>
    public TechNodeDef[] Tech = Array.Empty<TechNodeDef>();
    public Dictionary<string, int> TechIndexById = new();
    public LintConfigDto LintConfig = new();

    public static ContentDb Load(string path, out List<string> errors, out List<string> warnings)
        => Load(new[] { path }, out errors, out warnings);

    /// <summary>
    /// The ZH target block, composed across the same stack so a mod can supply model and
    /// damage-type mappings for the units it adds without restating the base pack's.
    /// </summary>
    public static ZhTargetDto LoadZhTarget(IReadOnlyList<string> paths)
    {
        var acc = new ZhTargetDto();
        foreach (var layer in PackStack.Compose(paths).Layers)
        {
            var z = layer.Dto.Zh;
            foreach (var (k, v) in z.DamageTypes) acc.DamageTypes[k] = v;
            foreach (var (k, v) in z.Models) acc.Models[k] = v;
            foreach (var (k, v) in z.DrawModules) acc.DrawModules[k] = v;
            if (z.WorldScale != 16.0) acc.WorldScale = z.WorldScale;
        }
        return acc;
    }

    /// <summary>
    /// Load an ordered stack of packs: base first, mods after, last-wins.
    /// See <see cref="PackStack"/> for the merge contract.
    /// </summary>
    public static ContentDb Load(IReadOnlyList<string> paths, out List<string> errors, out List<string> warnings)
    {
        errors = new List<string>();
        warnings = new List<string>();

        var (dto, layers) = PackStack.Compose(paths);

        var db = new ContentDb
        {
            PackName = dto.Meta.Name,
            // Provenance over the whole STACK, not just the last file: reordering two
            // mods changes it, and so does one byte in any layer. This is a deliberate
            // one-time change of the contentHash function — a single-layer stack no
            // longer equals the raw bytes hash (game.json moved c961a94f5e2d4a3a ->
            // 54e18480c04a7dc8). State hashes are unaffected; only provenance labels move.
            ContentHash = PackStack.StackHash(layers),
            Layers = layers,
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
            // "default" is a declared field, applied BEFORE the explicit cells regardless
            // of where it appears in the row. ZH's positional `Armor = DEFAULT n%` blast-fill
            // is order-sensitive; ours cannot be.
            if (row.TryGetValue(DefaultCellKey, out double fill))
                for (int a = 0; a < db.ArmorClasses.Length; a++)
                    db.DamageVsArmor[d, a] = Fix64.FromDoubleAtLoadBoundary(fill);

            foreach (var (ac, mult) in row)
            {
                if (string.Equals(ac, DefaultCellKey, StringComparison.Ordinal)) continue;
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
                PrimaryRadiusSq = Sq(w.PrimaryDamageRadius),
                SecondaryDamage = Fix64.FromDoubleAtLoadBoundary(w.SecondaryDamage),
                SecondaryRadiusSq = Sq(w.SecondaryDamageRadius),
                MinRangeSq = Sq(w.MinimumAttackRange),
                ClipSize = Math.Max(0, w.ClipSize),
                ClipReloadTicks = w.ClipReloadSeconds is double crs
                    ? (int)Math.Ceiling(crs * TicksPerSecond)
                    : Math.Max(0, w.ClipReloadTicks),
            });

            // ZH asserts secondaryRadius >= primaryRadius (Weapon.cpp:1301) because the
            // bands are tested outermost-last; an inverted pair silently makes the
            // secondary band unreachable rather than erroring.
            if (w.SecondaryDamageRadius > 0 && w.SecondaryDamageRadius < w.PrimaryDamageRadius)
                errors.Add($"weapon '{id}': secondaryDamageRadius ({w.SecondaryDamageRadius}) " +
                           $"must be >= primaryDamageRadius ({w.PrimaryDamageRadius})");
            if (w.MinimumAttackRange > 0 && w.MinimumAttackRange >= w.Range)
                errors.Add($"weapon '{id}': minimumAttackRange ({w.MinimumAttackRange}) " +
                           $"must be < range ({w.Range}) or the weapon can never fire");
            if (w.ClipSize > 0 && w.ClipReloadTicks <= 0 && w.ClipReloadSeconds is null)
                warnings.Add($"weapon '{id}': clipSize {w.ClipSize} with no clipReload; " +
                             $"the clip empties once and never refills");
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
        var techIds = dto.Tech.Nodes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < techIds.Length; i++) db.TechIndexById[techIds[i]] = i;
        db.Tech = new TechNodeDef[techIds.Length];
        for (int i = 0; i < techIds.Length; i++)
        {
            var id = techIds[i];
            var node = dto.Tech.Nodes[id];
            foreach (var req in node.Requires)
                if (!dto.Tech.Nodes.ContainsKey(req))
                    errors.Add($"tech '{id}': unknown prerequisite '{req}'");
            if (node.Cost < 0) errors.Add($"tech '{id}': cost must be >= 0");
            if (node.ResearchTicks <= 0) errors.Add($"tech '{id}': researchTicks must be > 0");
            db.Tech[i] = new TechNodeDef
            {
                Id = id,
                Cost = node.Cost,
                ResearchTicks = Math.Max(1, node.ResearchTicks),
                RequiresIdx = node.Requires.Where(db.TechIndexById.ContainsKey)
                                           .Select(r => db.TechIndexById[r]).ToArray(),
            };
        }
        CheckAcyclic(dto.Tech.Nodes, errors);

        // --- Flag vocabulary ------------------------------------------------------
        // Collected from every mention anywhere before compiling units, so a variant may
        // require a flag that only some later unit grants.
        var flagNames = new List<string>();
        foreach (var (_, u) in dto.Units)
        {
            if (u.Flags is not null) flagNames.AddRange(u.Flags);
            if (u.Variants is not null)
                foreach (var v in u.Variants) flagNames.AddRange(v.WhenFlags);
        }
        // A researched tech grants a same-named flag, which is what lets a conditional
        // variant key off research without a second mechanism.
        flagNames.AddRange(dto.Tech.Nodes.Keys);
        db.Flags = new FlagTable(flagNames);
        db.HasFlags = flagNames.Count > 0
                      && dto.Units.Values.Any(u => (u.Flags?.Count ?? 0) > 0
                                                   || (u.Variants?.Count ?? 0) > 0);
        if (db.Flags.Count > FlagTable.MaxFlags)
            errors.Add($"flag vocabulary is {db.Flags.Count}; the mask holds {FlagTable.MaxFlags}");

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
            // Defaults fill anything the unit does not state, so adding a schema field
            // later is not a breaking change to every pack in existence.
            var ud = dto.Defaults.Unit;
            var prereqs = u.Prerequisites ?? ud.Prerequisites;
            int cost = u.Cost ?? ud.Cost;
            int perPurchase = u.UnitsPerPurchase ?? ud.UnitsPerPurchase;

            // Durations quantise to whole ticks with ceil(), once, at the load boundary —
            // the same discipline as Fix64.FromDoubleAtLoadBoundary. ZH does this too
            // (INI.cpp:1717 ceils ms->frames at parse time), which is why its DragonTank
            // flame weapon reads 250 DPS in the file and delivers 150: 40ms ceils to 2
            // frames. Authored seconds must never silently mean a fractional tick.
            int buildTicks = u.BuildSeconds is double secs
                ? (int)Math.Ceiling(secs * TicksPerSecond)
                : (u.BuildTicks ?? ud.BuildTicks);

            // A prerequisite names EITHER a tech node OR another unit — and the unit form is
            // how ZH really models it (`Prerequisites { Object = AmericaWarFactory }`, 624 such
            // rows in retail). That is what gives a building meaning: you need the war factory
            // STANDING, not a research flag that can never be taken away.
            foreach (var req in prereqs)
                if (!dto.Tech.Nodes.ContainsKey(req) && !dto.Units.ContainsKey(req))
                    errors.Add($"unit '{id}': prerequisite '{req}' is neither a tech node nor a unit");
            if (cost <= 0) errors.Add($"unit '{id}': cost must be > 0");
            if (buildTicks <= 0) errors.Add($"unit '{id}': buildTicks must be > 0");
            if (perPurchase <= 0) errors.Add($"unit '{id}': unitsPerPurchase must be > 0");
            if (u.BuildSeconds is double s2 && Math.Abs(s2 * TicksPerSecond - buildTicks) > 1e-9)
                warnings.Add($"unit '{id}': buildSeconds {s2} is not a whole tick at {TicksPerSecond}Hz; " +
                             $"rounded up to {buildTicks} ticks ({(double)buildTicks / TicksPerSecond:0.###}s)");

            var proto = new UnitProto
            {
                Id = id,
                StableId = StableIdOf(id),
                FactionId = u.Faction,
                Cost = cost,
                BuildTicks = Math.Max(1, buildTicks),
                UnitsPerPurchase = perPurchase,
                Prerequisites = prereqs.ToArray(),
                PrereqTechIdx = prereqs.Where(db.TechIndexById.ContainsKey)
                                       .Select(r => db.TechIndexById[r]).ToArray(),
                ArmorClassIdx = armor,
                WeaponIdx = wpn,
                VetTrackIdx = vet,
            };

            // KindOf: ordinal-sorted so the stored order never depends on authoring order.
            var kinds = (u.KindOf ?? new List<string>())
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(k => k, StringComparer.Ordinal).ToArray();
            proto.KindOf = kinds;
            foreach (var k in kinds)
            {
                int bit = Runtime.KindOf.BitOf(k);
                if (bit < 0) { warnings.Add($"unit '{id}': unknown kindOf '{k}' (declared, never consulted)"); continue; }
                proto.KindMask |= 1u << bit;
            }

            string bc = (u.BuildCompletion ?? "").ToUpperInvariant();
            if (bc.Length == 0 || bc == "APPEARS_AT_RALLY_POINT")
                proto.BuildCompletion = BuildCompletion.AppearsAtRallyPoint;
            else if (bc == "PLACED_BY_PLAYER")
                proto.BuildCompletion = BuildCompletion.PlacedByPlayer;
            else
                errors.Add($"unit '{id}': unknown buildCompletion '{u.BuildCompletion}' " +
                           $"(expected APPEARS_AT_RALLY_POINT or PLACED_BY_PLAYER)");

            proto.BirthFlags = db.Flags.MaskOf(u.Flags ?? new List<string>());

            if (u.Variants is not null && u.Variants.Count > 0)
            {
                var vs = new List<LoadoutVariant>();
                foreach (var v in u.Variants)
                {
                    if (v.WhenFlags.Count == 0)
                        warnings.Add($"unit '{id}': variant with no whenFlags always matches; " +
                                     $"anything after it is unreachable");
                    foreach (var f in v.WhenFlags)
                        if (!db.Flags.TryBit(f, out _))
                            errors.Add($"unit '{id}': variant requires unknown flag '{f}'");

                    int vw = -1;
                    if (v.Weapon is not null && !weaponIdx.TryGetValue(v.Weapon, out vw))
                    { errors.Add($"unit '{id}': variant names unknown weapon '{v.Weapon}'"); vw = -1; }
                    int va = -1;
                    if (v.ArmorClass is not null && !acIdx.TryGetValue(v.ArmorClass, out va))
                    { errors.Add($"unit '{id}': variant names unknown armor class '{v.ArmorClass}'"); va = -1; }

                    vs.Add(new LoadoutVariant
                    {
                        Required = db.Flags.MaskOf(v.WhenFlags),
                        WeaponIdx = vw,
                        ArmorClassIdx = va,
                        Modifiers = ParseModifiers(v.Modifiers, $"unit '{id}' variant", errors),
                    });
                }
                proto.Variants = vs.ToArray();
            }

            proto.EnergyProduction = u.EnergyProduction ?? 0;
            proto.MaxSimultaneousOfType = u.MaxSimultaneousOfType ?? 0;

            // A structure that could walk would be a content bug the sim cannot express.
            if (proto.IsStructure && c.Mobile.Speed != 0)
                errors.Add($"unit '{id}': STRUCTURE must have speed 0 (got {c.Mobile.Speed})");
            if (proto.BuildCompletion == BuildCompletion.PlacedByPlayer && !proto.IsStructure)
                warnings.Add($"unit '{id}': PLACED_BY_PLAYER without KindOf STRUCTURE");
            var w = db.Weapons[wpn];
            proto.BaseStats[(int)Stat.MaxHp] = Fix64.FromDoubleAtLoadBoundary(c.Health.Max);
            proto.BaseStats[(int)Stat.Speed] = Fix64.FromDoubleAtLoadBoundary(c.Mobile.Speed) / Fix64.FromInt(TicksPerSecond);
            proto.BaseStats[(int)Stat.Damage] = w.Damage;
            proto.BaseStats[(int)Stat.CooldownScale] = Fix64.One;
            proto.BaseStats[(int)Stat.Range] = w.Range;
            proto.BaseStats[(int)Stat.ArmorFactor] = Fix64.One;

            proto.RosterId = id;
            db.UnitIndexById[id] = units.Count;
            units.Add(proto);
        }

        // --- Factions: resolve inheritance, materialize variants -----------------
        // Base prototypes keep their indices; variants are appended in a deterministic
        // order (faction resolution order, then roster order) so adding a general never
        // renumbers an existing unit and never perturbs an existing replay.
        ResolveFactions(db, dto, units, errors, warnings);
        db.Units = units.ToArray();
        for (int i = 0; i < db.Units.Length; i++) db.Units[i].SelfIdx = i;

        // Object prerequisites resolve here, once every prototype exists — forward
        // references are legal, so declaration order in the pack does not matter.
        foreach (var p in db.Units)
            p.PrereqObjectIdx = p.Prerequisites
                                 .Where(r => db.UnitIndexById.ContainsKey(r))
                                 .Select(r => db.UnitIndexById[r])
                                 .OrderBy(i => i)                 // ascending, always
                                 .ToArray();

        // Rules likewise: an effect may spawn a prototype declared later in the pack, and a
        // wreck spawning a wreck is a legitimate (bounded) cascade.
        foreach (var (id, u) in dto.Units)
        {
            if (u.Rules is null || u.Rules.Count == 0) continue;
            if (!db.UnitIndexById.TryGetValue(id, out int owner)) continue;

            var compiled = new List<RuleDef>();
            foreach (var r in u.Rules)
            {
                if (!Enum.TryParse<RuleEvent>(r.On, ignoreCase: true, out var ev))
                {
                    errors.Add($"unit '{id}': unknown rule event '{r.On}' (expected: death)");
                    continue;
                }
                foreach (var f in r.WhenFlags)
                    if (!db.Flags.TryBit(f, out _))
                        errors.Add($"unit '{id}': rule requires unknown flag '{f}'");

                var effects = new List<EffectDef>();
                foreach (var e in r.Do)
                {
                    if (!Enum.TryParse<EffectKind>(e.Kind, ignoreCase: true, out var kind))
                    {
                        errors.Add($"unit '{id}': unknown effect '{e.Kind}' " +
                                   $"(expected: spawn|grantMoney|damageInRadius|grantFlag)");
                        continue;
                    }
                    var def = new EffectDef { Kind = kind, Count = Math.Max(1, e.Count),
                                              Amount = e.Amount, TeamScope = e.TeamScope,
                                              Spread = Fix64.FromDoubleAtLoadBoundary(e.Spread),
                                              RadiusSq = Sq(e.Radius) };

                    if (kind == EffectKind.Spawn)
                    {
                        if (e.Proto is null || !db.UnitIndexById.TryGetValue(e.Proto, out int pi))
                            errors.Add($"unit '{id}': spawn names unknown prototype '{e.Proto}'");
                        else def.ProtoIdx = pi;
                    }
                    if (kind == EffectKind.DamageInRadius)
                    {
                        if (e.Weapon is null || !db.Weapons.Select(w => w.Id).Contains(e.Weapon, StringComparer.Ordinal))
                            errors.Add($"unit '{id}': damageInRadius names unknown weapon '{e.Weapon}'");
                        else def.WeaponIdx = Array.FindIndex(db.Weapons, w => w.Id == e.Weapon);
                        if (e.Radius <= 0)
                            errors.Add($"unit '{id}': damageInRadius needs a radius > 0");
                    }
                    if (kind == EffectKind.GrantFlag)
                    {
                        if (e.Flag is null || !db.Flags.TryBit(e.Flag, out int fb))
                            errors.Add($"unit '{id}': grantFlag names unknown flag '{e.Flag}'");
                        else def.FlagBit = fb;
                    }
                    if (kind == EffectKind.GrantMoney && e.Amount == 0)
                        warnings.Add($"unit '{id}': grantMoney with amount 0 does nothing");

                    effects.Add(def);
                }

                compiled.Add(new RuleDef
                {
                    On = ev,
                    RequiredFlags = db.Flags.MaskOf(r.WhenFlags),
                    Effects = effects.ToArray(),
                });
            }
            db.Units[owner].Rules = compiled.ToArray();
        }
        db.HasRules = db.Units.Any(p => p.Rules.Length > 0);
        // Structural gates are opt-in per pack: a pack that declares no factory keeps the
        // pre-structures semantics exactly, so adding the feature moved no existing hash.
        db.HasFactories = db.Units.Any(u => u.Is(Runtime.KindOf.Factory));
        db.HasPower = db.Units.Any(u => u.EnergyProduction != 0);

        foreach (var u in db.Units)
            if (!u.IsVariant && !dto.Factions.ContainsKey(u.FactionId))
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

    /// <summary>
    /// Flattens the faction inheritance chain. Parents resolve before children, so a
    /// child patches an already-flattened roster and grandchildren compose naturally.
    /// A cycle or a missing parent leaves the faction unresolved and reports an error
    /// rather than looping.
    /// </summary>
    private static void ResolveFactions(ContentDb db, ContentPackDto dto, List<UnitProto> units, List<string> errors, List<string> warnings)
    {
        var order = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=unvisited 1=visiting 2=done

        void Visit(string id)
        {
            if (state.TryGetValue(id, out int s))
            {
                if (s == 1) errors.Add($"faction '{id}': inheritance cycle");
                return;
            }
            state[id] = 1;
            var parent = dto.Factions[id].Extends;
            if (!string.IsNullOrEmpty(parent))
            {
                if (!dto.Factions.ContainsKey(parent))
                    errors.Add($"faction '{id}': unknown parent faction '{parent}'");
                else Visit(parent);
            }
            state[id] = 2;
            order.Add(id);
        }

        foreach (var id in dto.Factions.Keys.OrderBy(k => k, StringComparer.Ordinal)) Visit(id);
        db.FactionOrder = order.ToArray();

        foreach (var fid in order)
        {
            var f = dto.Factions[fid];
            var parentId = string.IsNullOrEmpty(f.Extends) ? null : f.Extends;

            // Roster entry -> effective prototype index, seeded from the parent.
            var roster = new Dictionary<string, int>(StringComparer.Ordinal);
            if (parentId is not null && db.Factions.TryGetValue(parentId, out var parentDef))
                for (int i = 0; i < parentDef.RosterIds.Length; i++)
                    roster[parentDef.RosterIds[i]] = parentDef.OwnUnitIdx[i];
            else
                foreach (var uid in f.Units)
                {
                    if (!db.UnitIndexById.TryGetValue(uid, out int idx))
                    { errors.Add($"faction '{fid}': unknown unit '{uid}'"); continue; }
                    roster[uid] = idx;
                }

            foreach (var uid in f.Remove.OrderBy(x => x, StringComparer.Ordinal))
                if (!roster.Remove(uid))
                    errors.Add($"faction '{fid}': remove '{uid}' is not in the inherited roster");

            foreach (var uid in f.Add.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!db.UnitIndexById.TryGetValue(uid, out int idx))
                { errors.Add($"faction '{fid}': add references unknown unit '{uid}'"); continue; }
                if (!roster.TryAdd(uid, idx))
                    errors.Add($"faction '{fid}': add '{uid}' is already in the roster");
            }

            var patched = new List<string>();
            foreach (var (uid, patch) in f.Modify.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!roster.TryGetValue(uid, out int baseIdx))
                { errors.Add($"faction '{fid}': modify '{uid}' is not in the roster"); continue; }
                if (patch.Cost is <= 0) errors.Add($"faction '{fid}': modify '{uid}' cost must be > 0");
                if (patch.BuildTicks is <= 0) errors.Add($"faction '{fid}': modify '{uid}' buildTicks must be > 0");

                var mods = new List<Modifier>();
                foreach (var m in patch.Modifiers)
                {
                    if (!Enum.TryParse<Stat>(m.Stat, ignoreCase: true, out var stat))
                    { errors.Add($"faction '{fid}': modify '{uid}' unknown stat '{m.Stat}'"); continue; }
                    if (!Enum.TryParse<ModOp>(m.Op, ignoreCase: true, out var op))
                    { errors.Add($"faction '{fid}': modify '{uid}' unknown op '{m.Op}' (expected Add|Mul)"); continue; }
                    mods.Add(new Modifier(stat, op, Fix64.FromDoubleAtLoadBoundary(m.Value)));
                }

                var b = units[baseIdx];
                var variant = new UnitProto
                {
                    Id = $"{fid}/{uid}",
                    StableId = StableIdOf($"{fid}/{uid}"),
                    FactionId = fid,
                    RosterId = uid,
                    IsVariant = true,
                    Cost = patch.Cost ?? b.Cost,
                    BuildTicks = patch.BuildTicks ?? b.BuildTicks,
                    Prerequisites = b.Prerequisites,
                    PrereqTechIdx = b.PrereqTechIdx,
                    ArmorClassIdx = b.ArmorClassIdx,
                    WeaponIdx = b.WeaponIdx,
                    VetTrackIdx = b.VetTrackIdx,
                };
                // Bake the patch into base stats once, at load. Veterancy then layers on
                // top through the same algebra at runtime — one mechanism, not two.
                StatResolver.Resolve(b.BaseStats, mods, variant.BaseStats);

                roster[uid] = units.Count;
                db.UnitIndexById[variant.Id] = units.Count;
                units.Add(variant);
                patched.Add(uid);
            }

            var ids = roster.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

            // Startability inherits down the chain: a general that only patches stats still
            // starts the way its parent does. Resolution prefers the faction's OWN roster
            // entry, so `usa_laser` starting a "command_center" gets its own variant of it
            // if it has one — the faction-scoped reference rule, applied here rather than
            // left for a later slice to retrofit.
            var parent = parentId is not null && db.Factions.TryGetValue(parentId, out var pf) ? pf : null;

            int startBuilding = -1;
            string? sb = f.StartingBuilding ?? (parent is not null && parent.StartingBuildingIdx >= 0
                                                ? units[parent.StartingBuildingIdx].RosterId : null);
            if (sb is not null)
            {
                if (roster.TryGetValue(sb, out int own)) startBuilding = own;
                else if (db.UnitIndexById.TryGetValue(sb, out int glob)) startBuilding = glob;
                else errors.Add($"faction '{fid}': startingBuilding '{sb}' is not a unit");

                if (startBuilding >= 0 && !units[startBuilding].IsStructure)
                    warnings.Add($"faction '{fid}': startingBuilding '{sb}' is not a STRUCTURE");
            }

            var startUnits = new List<int>();
            foreach (var su in f.StartingUnits ?? parent?.StartingUnitIdx.Select(i => units[i].RosterId).ToList()
                                                 ?? new List<string>())
            {
                if (roster.TryGetValue(su, out int own)) startUnits.Add(own);
                else if (db.UnitIndexById.TryGetValue(su, out int glob)) startUnits.Add(glob);
                else errors.Add($"faction '{fid}': startingUnit '{su}' is not a unit");
            }

            db.Factions[fid] = new FactionDef
            {
                Id = fid,
                Parent = parentId,
                RosterIds = ids,
                OwnUnitIdx = ids.Select(k => roster[k]).ToArray(),
                PatchedIds = patched.ToArray(),
                AddedIds = f.Add.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                RemovedIds = f.Remove.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                StartingBuildingIdx = startBuilding,
                StartingUnitIdx = startUnits.ToArray(),
                StartMoney = f.StartMoney ?? parent?.StartMoney ?? -1,
            };
        }
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
