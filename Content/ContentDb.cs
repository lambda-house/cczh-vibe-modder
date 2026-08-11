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
    public Fix64 AcquireRange;
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
    /// <summary>Outer splash radius (linear), for the spatial broad phase.</summary>
    public Fix64 SplashRadius;
    /// <summary>Reaches infantry inside a garrisoned building. ZH: AllowAttackGarrisonedBldgs.</summary>
    public bool ClearsGarrison;
    public bool HasSplash => PrimaryRadiusSq > Fix64.Zero || SecondaryRadiusSq > Fix64.Zero;
}

public sealed class VetTrackDef
{
    public required string Id;
    public int[] Thresholds = Array.Empty<int>();
    public Modifier[][] RankBundles = Array.Empty<Modifier[]>();
}

public sealed class RankDef
{
    public int SkillPointsNeeded;
    public int PurchasePointsGranted;
}

public sealed class ScienceDef
{
    public required string Id;
    public int Cost;
    public int[] RequiresIdx = Array.Empty<int>();
    public int RequiresRank;
    public ulong GrantsFlags;
}

public sealed class PowerDef
{
    public required string Id;
    public ulong RequiresFlags;
    public int RechargeTicks;
    public EffectDef[] Effects = Array.Empty<EffectDef>();
}

/// <summary>A validated modification to a unit that already exists in the target game.</summary>
public sealed class OverrideDef
{
    public required string Id;
    public required string Object;
    /// <summary>Fully-expanded target names: the base object plus any fork prefixes.</summary>
    public string[] Targets = Array.Empty<string>();
    public int WeaponIdx = -1;
    public Fix64 Speed = Fix64.Zero;          // Zero = not set; lint rejects an authored 0
    public double Scale;
    public string? Model;
    public string? ModelReplacesTag;
    public string? IdleAnimation;
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
    /// <summary>Terrain this unit crosses. ZH's Locomotor.Surfaces; ground alone by default.
    /// A mask that includes Air bypasses the grid entirely rather than needing flyable
    /// cells — their model, and the reason an air unit needs no terrain authored for it.</summary>
    public Runtime.SurfaceMask Surfaces = Runtime.SurfaceMask.Ground;
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
    /// <summary>Occupants held. 0 = not garrisonable. ZH: GarrisonContain.ContainMax.</summary>
    public int GarrisonCapacity;
    /// <summary>Ticks of uncontested enemy adjacency needed to flip ownership.</summary>
    public int CaptureTicks;
    /// <summary>Cash paid to the owner every DepositTicks. ZH: AutoDepositUpdate.</summary>
    public int DepositAmount;
    public int DepositTicks;
    /// <summary>Base stats indexed by <see cref="Stat"/>. Speed already per-tick.</summary>
    public Fix64[] BaseStats = new Fix64[StatResolver.StatCount];

    /// <summary>
    /// A faction patch produces a COMPLETE copy that then has the patch applied — never a
    /// hand-listed subset of fields. That distinction is not stylistic: this was a real bug.
    /// The old field-by-field initializer copied 9 of 20 fields, so patching nothing but a
    /// war factory's cost dropped its <c>KindOf</c> and it stopped being a structure —
    /// <c>rts compile</c> emitted the building with a Locomotor. Rules, conditional variants,
    /// energy production and birth flags vanished the same way.
    ///
    /// Memberwise so that ADDING a field to this class carries it automatically. A field
    /// added to the type but forgotten here is precisely the failure being designed out, so
    /// deep-copy the mutable arrays below rather than reverting to an explicit list.
    /// </summary>
    public UnitProto CloneForFaction(string fid, string rosterId)
    {
        var c = (UnitProto)MemberwiseClone();
        c.Id = $"{fid}/{rosterId}";
        c.StableId = ContentDb.StableIdOf(c.Id);
        c.FactionId = fid;
        c.RosterId = rosterId;
        c.IsVariant = true;
        c.SelfIdx = -1;                       // reassigned once the table is complete
        c.BaseStats = (Fix64[])BaseStats.Clone();   // shared array would alias the base unit
        return c;
    }
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

    /// <summary>
    /// Resolve a unit reference as the prototype holding it would see it: a faction-local
    /// variant looks in its own faction's roster first, everything else resolves globally.
    /// Returns -1 when the name is not a unit at all (prerequisites may name a tech node).
    /// </summary>
    public static int ResolveUnitRef(ContentDb db, UnitProto from, string name)
    {
        if (from.IsVariant && db.RosterOf.TryGetValue(from.FactionId, out var roster)
            && roster.TryGetValue(name, out int own)) return own;
        return db.UnitIndexById.TryGetValue(name, out int glob) ? glob : -1;
    }

    public required string PackName;
    public ulong ContentHash;
    /// <summary>The ordered pack stack this db was composed from. Base first.</summary>
    public List<PackStack.Layer> Layers = new();
    /// <summary>True once any prototype is an FS_FACTORY; gates factory-dependent production.</summary>
    public bool HasFactories;
    /// <summary>True once any prototype declares EnergyProduction; gates the brownout rule.</summary>
    public bool HasPower;
    /// <summary>Any garrisonable structure. Gates the occupancy fields into the state hash.</summary>
    public bool HasGarrison;
    /// <summary>Any capturable or paying structure. Gates capture progress into the hash.</summary>
    public bool HasCapture;
    /// <summary>Any rank ladder or science. Gates the second currency into the state hash.</summary>
    public bool HasSciences;
    /// <summary>Any activated power. Gates per-team recharge state into the hash.</summary>
    public bool HasPowers;

    /// <summary>
    /// Authored terrain, or null when the pack declares no map. CONTENT, not sim state: it
    /// never changes during a match, so it folds into <c>contentHash</c> and never into the
    /// state hash.
    /// </summary>
    public Runtime.PassabilityGrid? Map;

    /// <summary>
    /// True once the map contains a cell some unit cannot cross. Gates pathing and the path
    /// fields in the state hash, on the same opt-in-by-content discipline as every feature
    /// before it: a pack with no map moves in a straight line and hashes exactly as it did
    /// before this slice existed. An all-clear map is not a feature, it is a no-op, and it
    /// would be dishonest for it to move a pinned replay.
    /// </summary>
    public bool HasPassability;

    public RankDef[] Ranks = Array.Empty<RankDef>();
    public ScienceDef[] Sciences = Array.Empty<ScienceDef>();
    public Dictionary<string, int> ScienceIndexById = new(StringComparer.Ordinal);
    public PowerDef[] Powers = Array.Empty<PowerDef>();
    public Dictionary<string, int> PowerIndexById = new(StringComparer.Ordinal);
    public OverrideDef[] Overrides = Array.Empty<OverrideDef>();
    /// <summary>Any override. Gates map.ini emission; overrides never affect the sim.</summary>
    public bool HasOverrides;
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
    /// <summary>Per-faction resolved roster: faction id -> (roster id -> prototype index).
    /// The scope a faction-local variant resolves its references through.</summary>
    public Dictionary<string, Dictionary<string, int>> RosterOf = new(StringComparer.Ordinal);
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
    /// Why a referenced name does not exist, when the answer is "a later layer removed it".
    /// Appended to the ordinary "unknown x" errors, because an error that names the cause is a
    /// repair instruction and one that does not is a scavenger hunt — the same principle the
    /// MCP surface follows when it answers a bad prototype id with the list of good ones.
    /// Empty when the name was simply never declared, which reads correctly either way.
    /// </summary>
    private static string Why(Dictionary<string, string> removedBy, string table, string id)
        => PackStack.WhyRemoved(removedBy, table, id) is string w ? $" — removed by {w}" : "";

    /// <summary>
    /// The ZH target block, composed across the same stack so a mod can supply model and
    /// damage-type mappings for the units it adds without restating the base pack's.
    /// One line, because the composition lives in <see cref="PackStack"/> with every other
    /// field. It used to be a second fold here — two implementations of one contract.
    /// </summary>
    public static ZhTargetDto LoadZhTarget(IReadOnlyList<string> paths)
        => PackStack.Compose(paths).Dto.Zh;

    /// <summary>
    /// Load an ordered stack of packs: base first, mods after, last-wins.
    /// See <see cref="PackStack"/> for the merge contract.
    /// </summary>
    public static ContentDb Load(IReadOnlyList<string> paths, out List<string> errors, out List<string> warnings)
    {
        errors = new List<string>();
        warnings = new List<string>();

        // STAGE 1 of the resolution order: fold the layers into one DTO. Everything below
        // reads a composed pack and never a layer, which is what makes the order total.
        // PackStack is the authoritative statement of all three stages.
        var composed = PackStack.Compose(paths);
        var (dto, layers) = (composed.Dto, composed.Layers);
        errors.AddRange(composed.Diagnostics);
        var removedBy = composed.RemovedBy;

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
            LintConfig = dto.Lint ?? new LintConfigDto(),
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
        // ORDINAL, like everything else. Unsorted, the weapon table's order came from JSON
        // DOCUMENT ORDER, so moving two weapons in a file renumbered every WeaponIdx after
        // them — the same class of bug as the old ordinal prototype identity, and the reason
        // units are sorted a few lines down. Sim state never folds a weapon index, so this
        // moves no replay hash; it makes the table content-derived instead of file-derived.
        foreach (var (id, w) in dto.Weapons.OrderBy(kv => kv.Key, StringComparer.Ordinal))
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
                AcquireRange = acquire,
                AcquireRangeSq = acquire * acquire,
                CooldownTicks = Math.Max(1, w.CooldownTicks),
                Spread = Fix64.FromDoubleAtLoadBoundary(w.Spread),
                PrimaryRadiusSq = Sq(w.PrimaryDamageRadius),
                SecondaryDamage = Fix64.FromDoubleAtLoadBoundary(w.SecondaryDamage),
                SecondaryRadiusSq = Sq(w.SecondaryDamageRadius),
                MinRangeSq = Sq(w.MinimumAttackRange),
                // Linear radii for the spatial broad phase. Derived from the SAME authored
                // doubles at the load boundary, never by taking a square root of the squared
                // value in the sim — a sqrt in Runtime/ would be a determinism bug.
                SplashRadius = Fix64.FromDoubleAtLoadBoundary(
                    Math.Max(w.PrimaryDamageRadius, w.SecondaryDamageRadius)),
                ClipSize = Math.Max(0, w.ClipSize),
                ClipReloadTicks = w.ClipReloadSeconds is double crs
                    ? (int)Math.Ceiling(crs * TicksPerSecond)
                    : Math.Max(0, w.ClipReloadTicks),
                ClearsGarrison = w.ClearsGarrison,
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
        foreach (var (id, t) in dto.VeterancyTracks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
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
        // Sciences grant flags, and powers are gated on one, so both contribute vocabulary.
        foreach (var sc in dto.Sciences.Values)
            if (sc.GrantsFlags is not null) flagNames.AddRange(sc.GrantsFlags);
        foreach (var pw in dto.Powers.Values)
            if (pw.RequiresFlag.Length > 0) flagNames.Add(pw.RequiresFlag);
        db.Flags = new FlagTable(flagNames);
        db.HasFlags = flagNames.Count > 0
                      && (dto.Units.Values.Any(u => (u.Flags?.Count ?? 0) > 0
                                                    || (u.Variants?.Count ?? 0) > 0)
                          || dto.Sciences.Count > 0 || dto.Powers.Count > 0);
        if (db.Flags.Count > FlagTable.MaxFlags)
            errors.Add($"flag vocabulary is {db.Flags.Count}; the mask holds {FlagTable.MaxFlags}");

        // --- Units --------------------------------------------------------------
        //
        // STAGE 2 of the resolution order: the composed `defaults` block fills every unit
        // field left unstated, so adding a schema field later is not a breaking change to
        // every pack in existence. Resolved ONCE, here, against the compiler's own root
        // defaults — a field null at both levels has exactly one answer and it is written
        // down in UnitDefaultsDto.Root rather than scattered through this loop.
        var rootDefaults = UnitDefaultsDto.Root;
        var packDefaults = dto.Defaults?.Unit;
        var ud = new UnitDefaultsDto
        {
            Cost = packDefaults?.Cost ?? rootDefaults.Cost,
            BuildTicks = packDefaults?.BuildTicks ?? rootDefaults.BuildTicks,
            UnitsPerPurchase = packDefaults?.UnitsPerPurchase ?? rootDefaults.UnitsPerPurchase,
            Prerequisites = packDefaults?.Prerequisites ?? rootDefaults.Prerequisites,
        };

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
                errors.Add($"unit '{id}': unknown weapon '{c.WeaponBearer.Weapon}'"
                           + Why(removedBy, "weapons", c.WeaponBearer.Weapon));
                continue;
            }
            int vet = -1;
            if (c.VeterancyCarrier is not null && !trackIdx.TryGetValue(c.VeterancyCarrier.Track, out vet))
            {
                errors.Add($"unit '{id}': unknown veterancy track '{c.VeterancyCarrier.Track}'");
                vet = -1;
            }
            var prereqs = u.Prerequisites ?? ud.Prerequisites!;
            int cost = u.Cost ?? ud.Cost!.Value;
            int perPurchase = u.UnitsPerPurchase ?? ud.UnitsPerPurchase!.Value;

            // Durations quantise to whole ticks with ceil(), once, at the load boundary —
            // the same discipline as Fix64.FromDoubleAtLoadBoundary. ZH does this too
            // (INI.cpp:1717 ceils ms->frames at parse time), which is why its DragonTank
            // flame weapon reads 250 DPS in the file and delivers 150: 40ms ceils to 2
            // frames. Authored seconds must never silently mean a fractional tick.
            int buildTicks = u.BuildSeconds is double secs
                ? (int)Math.Ceiling(secs * TicksPerSecond)
                : (u.BuildTicks ?? ud.BuildTicks!.Value);

            // A prerequisite names EITHER a tech node OR another unit — and the unit form is
            // how ZH really models it (`Prerequisites { Object = AmericaWarFactory }`, 624 such
            // rows in retail). That is what gives a building meaning: you need the war factory
            // STANDING, not a research flag that can never be taken away.
            foreach (var req in prereqs)
                if (!dto.Tech.Nodes.ContainsKey(req) && !dto.Units.ContainsKey(req))
                    errors.Add($"unit '{id}': prerequisite '{req}' is neither a tech node nor a unit"
                               + Why(removedBy, "units", req) + Why(removedBy, "tech.nodes", req));
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

            // Surfaces: ground alone unless declared, which is what 1,741 of retail's
            // locomotors say. An unknown name is an error, not a silently ignored word — the
            // enum is closed on their side too, and a typo that means "cannot move" is the
            // hardest kind of bug to see from a state hash.
            if (c.Mobile.Surfaces is { Count: > 0 } surf)
            {
                var mask = Runtime.SurfaceMask.None;
                foreach (var name in surf.OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (Enum.TryParse<Runtime.SurfaceMask>(name, ignoreCase: true, out var bit)
                        && bit != Runtime.SurfaceMask.None)
                        mask |= bit;
                    else
                        errors.Add($"unit '{id}': unknown surface '{name}' " +
                                   "(expected ground|water|cliff|air|rubble)");
                }
                proto.Surfaces = mask;
                if (mask == Runtime.SurfaceMask.None)
                    errors.Add($"unit '{id}': surfaces resolved to nothing; it could never move");
            }

            proto.EnergyProduction = u.EnergyProduction ?? 0;
            proto.MaxSimultaneousOfType = u.MaxSimultaneousOfType ?? 0;
            proto.GarrisonCapacity = Math.Max(0, u.GarrisonCapacity ?? 0);
            proto.CaptureTicks = u.CaptureSeconds is double cs
                ? (int)Math.Ceiling(cs * TicksPerSecond)
                : Math.Max(0, u.CaptureTicks ?? 0);
            proto.DepositAmount = Math.Max(0, u.DepositAmount ?? 0);
            proto.DepositTicks = u.DepositSeconds is double ds
                ? (int)Math.Ceiling(ds * TicksPerSecond)
                : Math.Max(0, u.DepositTicks ?? 0);

            // Garrisonability is the capacity itself — no paired role flag, because ZH has
            // none: it is the presence of the GarrisonContain module that makes a building
            // garrisonable. Declaring "GARRISONABLE" in kindOf would be an invented enum
            // value and a hard load error on their side.
            if (proto.GarrisonCapacity > 0 && !proto.IsStructure)
                errors.Add($"unit '{id}': garrisonCapacity is only meaningful on a STRUCTURE");

            bool capturable = proto.Is(Runtime.KindOf.Capturable);
            if (capturable && proto.CaptureTicks <= 0)
                errors.Add($"unit '{id}': KindOf CAPTURABLE needs captureTicks/captureSeconds > 0");
            if (!capturable && proto.CaptureTicks > 0)
                errors.Add($"unit '{id}': captureTicks without KindOf CAPTURABLE");
            if (proto.DepositAmount > 0 && proto.DepositTicks <= 0)
                errors.Add($"unit '{id}': depositAmount {proto.DepositAmount} with no deposit interval");
            if (proto.DepositTicks > 0 && proto.DepositAmount <= 0)
                warnings.Add($"unit '{id}': deposit interval set but depositAmount is 0 — pays nothing");

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
        ResolveFactions(db, dto, units, removedBy, errors, warnings);
        db.Units = units.ToArray();
        for (int i = 0; i < db.Units.Length; i++) db.Units[i].SelfIdx = i;

        // Faction-scoped reference resolution.
        //
        // A reference inside a faction-local variant must prefer that faction's OWN roster
        // entry over the global prototype of the same name. Measured from retail: 88 bad
        // references across 68 broken definitions ship in Zero Hour because their generals
        // are forks and a fork kept pointing at the base — the Laser General's Paradrop
        // delivers the plain Ranger, not the Laser Ranger. patch104p carries fixes for
        // several of them, so this is a tracked defect class, not a theoretical one.
        //
        // Resolution goes through the faction's RESOLVED ROSTER, never by mangling the name
        // into "fid/ref". Inheritance is why: a general that does not patch the war factory
        // still inherits its PARENT's patched one, and only the roster knows that.
        //
        // The scoping stops at variants, and that limit is the honest one. A base prototype
        // is shared by every faction that did not fork it, so its reference array cannot be
        // faction-dependent — exactly the position EA's shared SUPERWEAPON_Paradrop1 OCL is
        // in. Lint reports that case instead of pretending to resolve it.
        var rosterOf = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var fid in db.FactionOrder)
        {
            var f = db.Factions[fid];
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < f.RosterIds.Length; i++) map[f.RosterIds[i]] = f.OwnUnitIdx[i];
            rosterOf[fid] = map;
        }
        db.RosterOf = rosterOf;

        // Object prerequisites resolve here, once every prototype exists — forward
        // references are legal, so declaration order in the pack does not matter.
        foreach (var p in db.Units)
            p.PrereqObjectIdx = p.Prerequisites
                                 .Where(r => ResolveUnitRef(db, p, r) >= 0)
                                 .Select(r => ResolveUnitRef(db, p, r))
                                 .Distinct()
                                 .OrderBy(i => i)                 // ascending, always
                                 .ToArray();

        // Rules likewise: an effect may spawn a prototype declared later in the pack, and a
        // wreck spawning a wreck is a legitimate (bounded) cascade.
        // Sorted so lint MESSAGES come out in a stable order too: a report whose lines shuffle
        // between runs cannot be diffed, and diffing reports is how a pack gets reviewed.
        foreach (var (id, u) in dto.Units.OrderBy(kv => kv.Key, StringComparer.Ordinal))
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

                var effects = CompileEffects(db, $"unit '{id}'", r.Do, errors, warnings);

                compiled.Add(new RuleDef
                {
                    On = ev,
                    RequiredFlags = db.Flags.MaskOf(r.WhenFlags),
                    Effects = effects,
                });
            }
            db.Units[owner].Rules = compiled.ToArray();

            // Rules compile AFTER faction resolution — a spawn may name a prototype declared
            // later — so the variant clone above copied an empty rule array. Hand each
            // variant its own copy here, with spawn targets re-resolved through the owning
            // faction's roster. Without this a patched suicide truck stops exploding, and a
            // patched scrap tank leaves the BASE faction's wreck: our exact analogue of the
            // shared-OCL defect, where 64 of retail's 88 bad references live.
            foreach (var v in db.Units)
            {
                if (!v.IsVariant || !string.Equals(v.RosterId, id, StringComparison.Ordinal)) continue;
                v.Rules = compiled.Select(rule => new RuleDef
                {
                    On = rule.On,
                    RequiredFlags = rule.RequiredFlags,
                    Effects = rule.Effects.Select(e => e.Kind != EffectKind.Spawn || e.ProtoIdx < 0
                        ? e
                        : new EffectDef
                        {
                            Kind = e.Kind, Count = e.Count, Spread = e.Spread, Amount = e.Amount,
                            WeaponIdx = e.WeaponIdx, RadiusSq = e.RadiusSq, FlagBit = e.FlagBit,
                            TeamScope = e.TeamScope,
                            ProtoIdx = ResolveUnitRef(db, v, db.Units[e.ProtoIdx].RosterId),
                        }).ToArray(),
                }).ToArray();
            }
        }
        db.HasRules = db.Units.Any(p => p.Rules.Length > 0);
        // Structural gates are opt-in per pack: a pack that declares no factory keeps the
        // pre-structures semantics exactly, so adding the feature moved no existing hash.
        db.HasFactories = db.Units.Any(u => u.Is(Runtime.KindOf.Factory));
        db.HasPower = db.Units.Any(u => u.EnergyProduction != 0);
        // Opt-in by content, like every gate before it: a pack with no garrisonable and no
        // capturable building hashes exactly as it did before this slice existed.
        db.HasGarrison = db.Units.Any(u => u.GarrisonCapacity > 0);
        db.HasCapture = db.Units.Any(u => u.CaptureTicks > 0 || u.DepositAmount > 0);
        db.Map = CompileMap(dto.Map, errors, warnings);
        // Opt-in by CONSEQUENCE, not by presence. A map every unit can cross everywhere
        // changes no movement, so it must change no hash — otherwise "I added terrain and
        // all my replays broke" would be true even when the terrain does nothing.
        db.HasPassability = db.Map is not null && db.Units.Any(u =>
        {
            if ((u.Surfaces & Runtime.SurfaceMask.Air) != 0) return false;
            for (int c = 0; c < db.Map.CellCount; c++)
                if ((Runtime.PassabilityGrid.Required(db.Map.At(c)) & u.Surfaces) == 0) return true;
            return (Runtime.PassabilityGrid.Required(db.Map.Outside) & u.Surfaces) == 0;
        });

        // --- Second currency: ranks, sciences, powers ---------------------------------
        // Skill points are earned by KILLING, never by economy — that is the whole point of
        // a second currency, and it is why this is not just another tech tree. ZH's default
        // ladder is 0/800/1500/2500/5000 needed and 1/1/1/1/3 granted: seven points for an
        // entire game, against 13-20 purchasable sciences per faction.
        var ranks = new List<RankDef>();
        foreach (var rk in dto.Ranks ?? new List<RankDto>())
            ranks.Add(new RankDef
            {
                SkillPointsNeeded = Math.Max(0, rk.SkillPointsNeeded),
                PurchasePointsGranted = Math.Max(0, rk.PurchasePointsGranted),
            });
        db.Ranks = ranks.ToArray();
        for (int i = 1; i < db.Ranks.Length; i++)
            if (db.Ranks[i].SkillPointsNeeded <= db.Ranks[i - 1].SkillPointsNeeded)
                errors.Add($"ranks: rank {i + 1} needs more skill points than rank {i} " +
                           $"({db.Ranks[i].SkillPointsNeeded} <= {db.Ranks[i - 1].SkillPointsNeeded})");

        var scienceIds = dto.Sciences.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < scienceIds.Length; i++) db.ScienceIndexById[scienceIds[i]] = i;
        var sciences = new List<ScienceDef>();
        foreach (var id in scienceIds)
        {
            var sc = dto.Sciences[id];
            foreach (var req in sc.Requires ?? new List<string>())
                if (!dto.Sciences.ContainsKey(req))
                    errors.Add($"science '{id}': unknown prerequisite science '{req}'");
            if (sc.Cost <= 0) errors.Add($"science '{id}': cost must be > 0");
            if (sc.RequiresRank > db.Ranks.Length)
                errors.Add($"science '{id}': requiresRank {sc.RequiresRank} but only {db.Ranks.Length} ranks exist");

            // Prerequisites resolve against the FULL index, which is already built above, so
            // a science may name one declared later in the file.
            var reqIdx = new List<int>();
            foreach (var rq in sc.Requires ?? new List<string>())
                if (db.ScienceIndexById.TryGetValue(rq, out int ri)) reqIdx.Add(ri);
            reqIdx.Sort();

            sciences.Add(new ScienceDef
            {
                Id = id,
                Cost = sc.Cost,
                RequiresRank = Math.Max(0, sc.RequiresRank),
                RequiresIdx = reqIdx.ToArray(),
                GrantsFlags = db.Flags.MaskOf(sc.GrantsFlags ?? new List<string>()),
            });
        }
        db.Sciences = sciences.ToArray();
        // A cycle would make a science unbuyable forever rather than erroring at runtime.
        for (int i = 0; i < db.Sciences.Length; i++)
            if (db.Sciences[i].RequiresIdx.Contains(i))
                errors.Add($"science '{db.Sciences[i].Id}': requires itself");

        var powerIds = dto.Powers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < powerIds.Length; i++) db.PowerIndexById[powerIds[i]] = i;
        var powers = new List<PowerDef>();
        foreach (var id in powerIds)
        {
            var pw = dto.Powers[id];
            int recharge = pw.RechargeSeconds is double rs
                ? (int)Math.Ceiling(rs * TicksPerSecond)
                : pw.RechargeTicks;
            if (recharge <= 0) errors.Add($"power '{id}': needs a recharge > 0");
            if (pw.RequiresFlag.Length > 0 && !db.Flags.TryBit(pw.RequiresFlag, out _))
                errors.Add($"power '{id}': unknown flag '{pw.RequiresFlag}'");
            if (pw.Do.Count == 0) errors.Add($"power '{id}': has no effects");
            powers.Add(new PowerDef
            {
                Id = id,
                RequiresFlags = pw.RequiresFlag.Length == 0
                    ? 0UL : db.Flags.MaskOf(new List<string> { pw.RequiresFlag }),
                RechargeTicks = Math.Max(1, recharge),
                Effects = CompileEffects(db, $"power '{id}'", pw.Do, errors, warnings),
            });
        }
        db.Powers = powers.ToArray();

        // --- Overrides ------------------------------------------------------------------
        // These NEVER touch the simulation: they describe edits to units that exist only in
        // the target game, which our sim has no model of. They exist to be COMPILED. That is
        // the "emittable is not simulated" axis taken to its limit, and it is why nothing
        // here enters the content hash's view of the battle.
        var ovIds = dto.Overrides.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var ovs = new List<OverrideDef>();
        foreach (var id in ovIds)
        {
            var o = dto.Overrides[id];
            if (o.Object.Length == 0) { errors.Add($"override '{id}': needs an 'object' to modify"); continue; }

            var targets = new List<string> { o.Object };
            bool all = string.Equals(o.Scope, "all", StringComparison.OrdinalIgnoreCase);
            if (!all && !string.Equals(o.Scope, "base", StringComparison.OrdinalIgnoreCase))
                errors.Add($"override '{id}': scope must be 'base' or 'all', got '{o.Scope}'");
            if (all)
            {
                // Fan out across the target's forks. The author must NAME them, because only
                // they know the target game's prefixes — and silently guessing is the failure
                // this field exists to prevent.
                if ((o.Forks?.Count ?? 0) == 0)
                    errors.Add($"override '{id}': scope 'all' needs 'forks' (e.g. [\"SupW_\",\"Lazr_\",\"AirF_\"])");
                foreach (var pre in (o.Forks ?? new List<string>()).OrderBy(x => x, StringComparer.Ordinal))
                    targets.Add(pre + o.Object);
            }

            int wi = -1;
            if (o.Weapon is not null)
            {
                wi = Array.FindIndex(db.Weapons, w => string.Equals(w.Id, o.Weapon, StringComparison.Ordinal));
                if (wi < 0) errors.Add($"override '{id}': unknown weapon '{o.Weapon}' — it must be one of OURS, " +
                                       $"because an override repoints the object at a NEW leaf rather than " +
                                       $"patching the target's own weapon in place");
            }
            if (o.Model is not null && string.IsNullOrEmpty(o.ModelReplacesTag))
                errors.Add($"override '{id}': 'model' needs 'modelReplacesTag' — their ReplaceModule must name " +
                           $"the module it replaces, and guessing the tag is a hard load error");
            if (o.Scale is <= 0) errors.Add($"override '{id}': scale must be > 0");
            if (o.Speed is <= 0) errors.Add($"override '{id}': speed must be > 0");

            ovs.Add(new OverrideDef
            {
                Id = id,
                Object = o.Object,
                Targets = targets.ToArray(),
                WeaponIdx = wi,
                Speed = o.Speed is double sp ? Fix64.FromDoubleAtLoadBoundary(sp) : Fix64.Zero,
                Scale = o.Scale ?? 0,
                Model = o.Model,
                ModelReplacesTag = o.ModelReplacesTag,
                IdleAnimation = o.IdleAnimation,
            });
        }
        db.Overrides = ovs.ToArray();
        db.HasOverrides = db.Overrides.Length > 0;

        db.HasSciences = db.Ranks.Length > 0 || db.Sciences.Length > 0;
        db.HasPowers = db.Powers.Length > 0;

        foreach (var u in db.Units)
            if (!u.IsVariant && !dto.Factions.ContainsKey(u.FactionId))
                warnings.Add($"unit '{u.Id}': faction '{u.FactionId}' not declared in factions table");

        // --- Faction-scoped reference lint --------------------------------------
        //
        // Resolution above fixes the case it CAN fix: a variant belongs to one faction, so
        // its references are scoped to that faction's roster. The case it cannot fix is the
        // one that actually ships broken in Zero Hour — a SHARED prototype, used unforked by
        // several factions, referencing something that one of them HAS forked. A single
        // reference array cannot mean different things to different factions.
        //
        // Retail is the proof: 88 bad references across 68 definitions, and the largest
        // cluster is exactly this shape — one shared Paradrop list delivering the base
        // Ranger to all three USA generals that forked it. There is no resolution rule that
        // rescues that; the author has to fork the referrer too. So we report it.
        foreach (var fid in db.FactionOrder)
        {
            var f = db.Factions[fid];
            var forked = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < f.RosterIds.Length; i++)
                if (db.Units[f.OwnUnitIdx[i]].IsVariant) forked.Add(f.RosterIds[i]);
            if (forked.Count == 0) continue;

            for (int i = 0; i < f.RosterIds.Length; i++)
            {
                var p = db.Units[f.OwnUnitIdx[i]];
                if (p.IsVariant) continue;              // scoped already; nothing to warn about

                foreach (var req in p.Prerequisites.Where(forked.Contains))
                    warnings.Add($"faction '{fid}': shared unit '{p.Id}' requires '{req}', but this " +
                                 $"faction patched '{req}' into '{fid}/{req}'. The shared prototype is used " +
                                 $"by other factions too, so its requirement still names the base object and " +
                                 $"this faction's own '{req}' will not satisfy it. Patch '{p.RosterId}' here too.");

                foreach (var e in p.Rules.SelectMany(rule => rule.Effects))
                    if (e.Kind == EffectKind.Spawn && e.ProtoIdx >= 0 && forked.Contains(db.Units[e.ProtoIdx].RosterId))
                        warnings.Add($"faction '{fid}': shared unit '{p.Id}' spawns " +
                                     $"'{db.Units[e.ProtoIdx].RosterId}', but this faction patched it. The " +
                                     $"spawn is on the shared prototype, so this faction gets the base one. " +
                                     $"Patch '{p.RosterId}' here too.");
            }
        }

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
    /// Turn drawn rows into a grid. Every failure here is an ERROR rather than a best guess:
    /// a map is geometry, and a mis-sized row or an unlisted character silently shifts every
    /// cell after it — the map still loads, the walls are simply in the wrong place, and
    /// nothing but a screenshot would ever tell you.
    /// </summary>
    private static Runtime.PassabilityGrid? CompileMap(MapDto? m, List<string> errors, List<string> warnings)
    {
        if (m is null) return null;
        if (m.Rows.Count == 0) { errors.Add("map: no rows"); return null; }

        // Power of two, so world -> cell is a shift. Checked on the RAW, because the check
        // has to hold for the value the sim will actually use, not for the double it was
        // typed as: 0.1 is a fine-looking number that is not representable and would leave
        // the grid and the movement code disagreeing about which cell a unit is in.
        long cellRaw = Fix64.FromDoubleAtLoadBoundary(m.CellSize).Raw;
        if (cellRaw <= 0 || (cellRaw & (cellRaw - 1)) != 0)
        {
            errors.Add($"map: cellSize {m.CellSize} is not a power of two " +
                       "(world -> cell must be an exact shift; try 0.25, 0.5, 1, 2, 4)");
            return null;
        }
        int shift = System.Numerics.BitOperations.TrailingZeroCount((ulong)cellRaw);
        if (shift < 1)
        {
            errors.Add($"map: cellSize {m.CellSize} is below the smallest representable cell");
            return null;
        }

        var legend = new Dictionary<char, Runtime.Surface>();
        foreach (var (k, v) in m.Legend.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (k.Length != 1) { errors.Add($"map: legend key '{k}' must be a single character"); continue; }
            if (!Enum.TryParse<Runtime.Surface>(v, ignoreCase: true, out var surf))
            { errors.Add($"map: legend '{k}' names unknown surface '{v}' " +
                         "(expected clear|water|cliff|rubble|impassable)"); continue; }
            legend[k[0]] = surf;
        }

        if (!Enum.TryParse<Runtime.Surface>(m.Outside, ignoreCase: true, out var outside))
        {
            errors.Add($"map: outside names unknown surface '{m.Outside}'");
            outside = Runtime.Surface.Clear;
        }

        int width = m.Rows[0].Length, height = m.Rows.Count;
        if (width == 0) { errors.Add("map: row 0 is empty"); return null; }

        var cells = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            var row = m.Rows[y];
            if (row.Length != width)
            {
                errors.Add($"map: row {y} is {row.Length} cells, row 0 is {width}; " +
                           "a ragged map shifts every cell after the short row");
                return null;
            }
            for (int x = 0; x < width; x++)
            {
                if (!legend.TryGetValue(row[x], out var surf))
                {
                    errors.Add($"map: row {y} column {x} uses '{row[x]}', which the legend does not define");
                    return null;
                }
                cells[y * width + x] = (byte)surf;
            }
        }

        var unused = legend.Keys.Where(c => !m.Rows.Any(r => r.Contains(c)))
                                .OrderBy(c => c).ToArray();
        if (unused.Length > 0)
            warnings.Add($"map: legend defines {string.Join(", ", unused.Select(c => $"'{c}'"))} " +
                         "but the map never uses them");

        return new Runtime.PassabilityGrid(width, height, shift, outside, cells);
    }

    /// <summary>
    /// STAGE 3 of the resolution order (see <see cref="PackStack"/>): faction inheritance,
    /// then the roster operations. Parents resolve before children, so a child patches an
    /// already-flattened roster and grandchildren compose naturally. A cycle or a missing
    /// parent leaves the faction unresolved and reports an error rather than looping.
    ///
    /// <para><b>Within one faction the order is fixed: remove → add → modify.</b> It is not
    /// arbitrary, and each adjacency buys something:</para>
    /// <list type="bullet">
    ///   <item><b>remove before add</b> makes <c>remove X</c> + <c>add X</c> the DE-FORK
    ///     idiom: drop the parent's faction-local variant of X and re-adopt the shared
    ///     prototype. Under add-first it would instead be a duplicate-key error, and there
    ///     would be no way to say "inherit everything except this one fork".</item>
    ///   <item><b>remove before modify</b> makes <c>remove X</c> + <c>modify X</c> a
    ///     deterministic ERROR rather than a silent no-op. Under modify-first the patch would
    ///     be computed, a variant allocated, and then thrown away — work whose only trace is
    ///     a unit that is mysteriously absent.</item>
    /// </list>
    ///
    /// <para>Each op iterates in Ordinal key order, so a faction's variants land in the unit
    /// array at positions that do not depend on JSON key order. Identity is name-derived
    /// (<c>StableId</c>), so this affects allocation order only — never a hash.</para>
    /// </summary>
    private static void ResolveFactions(ContentDb db, ContentPackDto dto, List<UnitProto> units,
                                        Dictionary<string, string> removedBy,
                                        List<string> errors, List<string> warnings)
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
                    errors.Add($"faction '{id}': unknown parent faction '{parent}'" + Why(removedBy, "factions", parent));
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
                    { errors.Add($"faction '{fid}': unknown unit '{uid}'" + Why(removedBy, "units", uid)); continue; }
                    roster[uid] = idx;
                }

            foreach (var uid in f.Remove.OrderBy(x => x, StringComparer.Ordinal))
                if (!roster.Remove(uid))
                    errors.Add($"faction '{fid}': remove '{uid}' is not in the inherited roster"
                               + Why(removedBy, "units", uid));

            foreach (var uid in f.Add.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!db.UnitIndexById.TryGetValue(uid, out int idx))
                { errors.Add($"faction '{fid}': add references unknown unit '{uid}'" + Why(removedBy, "units", uid)); continue; }
                if (!roster.TryAdd(uid, idx))
                    errors.Add($"faction '{fid}': add '{uid}' is already in the roster");
            }

            var patched = new List<string>();
            foreach (var (uid, patch) in f.Modify.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!roster.TryGetValue(uid, out int baseIdx))
                { errors.Add($"faction '{fid}': modify '{uid}' is not in the roster"
                                  + Why(removedBy, "units", uid)); continue; }
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
                var variant = b.CloneForFaction(fid, uid);
                variant.Cost = patch.Cost ?? b.Cost;
                variant.BuildTicks = patch.BuildTicks ?? b.BuildTicks;
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
                else errors.Add($"faction '{fid}': startingBuilding '{sb}' is not a unit"
                                + Why(removedBy, "units", sb));

                if (startBuilding >= 0 && !units[startBuilding].IsStructure)
                    warnings.Add($"faction '{fid}': startingBuilding '{sb}' is not a STRUCTURE");
            }

            var startUnits = new List<int>();
            foreach (var su in f.StartingUnits ?? parent?.StartingUnitIdx.Select(i => units[i].RosterId).ToList()
                                                 ?? new List<string>())
            {
                if (roster.TryGetValue(su, out int own)) startUnits.Add(own);
                else if (db.UnitIndexById.TryGetValue(su, out int glob)) startUnits.Add(glob);
                else errors.Add($"faction '{fid}': startingUnit '{su}' is not a unit"
                                + Why(removedBy, "units", su));
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

    /// <summary>
    /// Compile the closed effect vocabulary. Shared by death rules and by activated powers,
    /// which is the point of keeping the vocabulary closed and the parameters open: a new
    /// TRIGGER costs nothing, because every trigger fires the same four effects.
    /// <paramref name="ctx"/> is only for messages, e.g. "unit 'salvager'" or "power 'a10'".
    /// </summary>
    private static EffectDef[] CompileEffects(ContentDb db, string ctx, List<EffectDto> dos,
                                              List<string> errors, List<string> warnings)
    {
        var effects = new List<EffectDef>();
        foreach (var e in dos)
        {
            if (!Enum.TryParse<EffectKind>(e.Kind, ignoreCase: true, out var kind))
            {
                errors.Add($"{ctx}: unknown effect '{e.Kind}' " +
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
                    errors.Add($"{ctx}: spawn names unknown prototype '{e.Proto}'");
                else def.ProtoIdx = pi;
            }
            if (kind == EffectKind.DamageInRadius)
            {
                if (e.Weapon is null || !db.Weapons.Select(w => w.Id).Contains(e.Weapon, StringComparer.Ordinal))
                    errors.Add($"{ctx}: damageInRadius names unknown weapon '{e.Weapon}'");
                else def.WeaponIdx = Array.FindIndex(db.Weapons, w => w.Id == e.Weapon);
                if (e.Radius <= 0)
                    errors.Add($"{ctx}: damageInRadius needs a radius > 0");
            }
            if (kind == EffectKind.GrantFlag)
            {
                if (e.Flag is null || !db.Flags.TryBit(e.Flag, out int fb))
                    errors.Add($"{ctx}: grantFlag names unknown flag '{e.Flag}'");
                else def.FlagBit = fb;
            }
            if (kind == EffectKind.GrantMoney && e.Amount == 0)
                warnings.Add($"{ctx}: grantMoney with amount 0 does nothing");

            effects.Add(def);
        }
        return effects.ToArray();
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
