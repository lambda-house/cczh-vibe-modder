using RtsSkeleton.Content;
using RtsSkeleton.Core;

namespace RtsSkeleton.Runtime;

/// <summary>
/// Per-unit runtime state. The skeleton uses one flat archetype (every unit has
/// health, mobility, a weapon, optional veterancy) constructed from the
/// component-shaped content. The full build replaces this with ECS storage;
/// the content format already supports that, so it's a loader/storage refactor,
/// not a data migration.
/// </summary>
public struct UnitState
{
    public bool Alive;
    public int ProtoIdx;
    public int Team;
    public Fix64 X;
    public Fix64 Y;
    public Fix64 Hp;
    public int CooldownRemaining;
    /// <summary>
    /// Shots left before a long reload. -1 means "not yet initialised from the prototype";
    /// 0 with a clipped weapon means reloading. Sim state, so it is hashed.
    /// </summary>
    public int ClipRemaining;
    /// <summary>Flags carried by this unit (birth flags plus anything gained in play).</summary>
    public ulong Flags;
    /// <summary>Index into the prototype's Variants, or -1 for the base loadout.</summary>
    public int VariantIdx;
    public int TargetIdx;      // -1 = none
    public int Xp;
    public int Rank;
    public Fix64 RallyX;
    public Fix64 RallyY;
    public bool HasRally;

    /// <summary>
    /// Index of the structure this unit is garrisoned inside, or -1. An occupant keeps
    /// firing, from the host's position, and cannot be targeted at all — not by direct fire,
    /// not by splash. Damage reaches it only as spill from a ClearsGarrison weapon striking
    /// the HOST, which is ZH's own model. That asymmetry IS the mechanic.
    /// </summary>
    public int GarrisonHost;

    /// <summary>Ticks of uncontested enemy adjacency accrued against this structure.</summary>
    public int CaptureProgress;
    /// <summary>Team currently capturing, or -1. Progress resets when the claimant changes.</summary>
    public int CaptureBy;
    /// <summary>Ticks until this structure next pays its owner. Counts down.</summary>
    public int DepositCountdown;
}

/// <summary>One item of team production/research work. Idx is a proto index in the
/// unit queue and a tech index in the research queue. Cost is deducted when the
/// entry reaches the queue head and starts; until then it is pure intent.</summary>
public struct QueueEntry
{
    public int Idx;
    public bool Started;
    public int TicksRemaining;
}

/// <summary>
/// Per-team macro state: money, finite supply pool, income, production spawn point,
/// default rally for produced units, researched tech flags, and two queues
/// (research and unit production) that run in parallel. All of it is sim state
/// and all of it is hashed.
/// </summary>
public sealed class TeamState
{
    public Fix64 Money;
    public Fix64 IncomePerTick;
    /// <summary>Undrawn income remaining; a finite economy is what makes defeat decidable.</summary>
    public Fix64 PoolRemaining;
    public Fix64 SpawnX;
    public Fix64 SpawnY;
    public Fix64 RallyX;
    public Fix64 RallyY;
    public bool HasRally;
    public int ProducedCount;
    /// <summary>Team-scope flags — ZH's PLAYER upgrades. Researched tech grants one.</summary>
    public ulong Flags;

    /// <summary>
    /// The SECOND currency. Skill points are earned by KILLING and by nothing else — no
    /// economy converts into them — which is what makes them a different resource rather
    /// than a slower kind of money. ZH's own default is that a unit's skill-point value IS
    /// its experience value: not one retail object overrides SkillPointValue, so the number
    /// that ranks a unit up is the same number that ranks its commander up.
    /// </summary>
    public int SkillPoints;
    /// <summary>Commander rank, 0 before the first threshold. Grants purchase points.</summary>
    public int CommanderRank;
    /// <summary>Unspent science purchase points. ZH grants seven across a whole game.</summary>
    public int PurchasePoints;
    public readonly bool[] SciencesOwned;
    /// <summary>Ticks until each power is ready. 0 = ready.</summary>
    public readonly int[] PowerCooldown;

    public readonly bool[] Researched;
    public readonly List<QueueEntry> ResearchQueue = new();
    public readonly List<QueueEntry> UnitQueue = new();

    public TeamState(int techCount, int scienceCount, int powerCount)
    {
        Researched = new bool[techCount];
        SciencesOwned = new bool[scienceCount];
        PowerCooldown = new int[powerCount];
    }
}

/// <summary>
/// Deterministic world container. Rules that matter:
/// - iteration is always by ascending unit index;
/// - dead units become tombstones (no index reuse in the skeleton; a freelist with
///   generation-tagged handles replaces this later);
/// - resolved stats live in a flat Fix64 array recomputed only on modifier change.
/// </summary>
public sealed class World
{
    public const int MaxUnits = 4096;
    public const int TeamCount = 2;

    public readonly ContentDb Content;
    public readonly UnitState[] Units = new UnitState[MaxUnits];
    public readonly Fix64[] ResolvedStats = new Fix64[MaxUnits * StatResolver.StatCount];
    public int UnitCount;
    public readonly TeamState[] Teams;

    /// <summary>
    /// Waypoints a unit may hold before it must ask again. A cap, not a limit on route
    /// length: a longer route is walked in instalments, which also means a unit notices
    /// the world moved while it was walking instead of following a stale plan to the end.
    /// </summary>
    public const int PathCapacity = 24;
    /// <summary>Ticks a unit must wait before recomputing a route. Chasing a moving target
    /// changes the goal cell most ticks, and one A* per unit per tick is a search per unit
    /// per tick — this is the difference between a feature and a stall.</summary>
    public const int RepathInterval = 8;

    // Path state. Allocated ONLY when the pack has impassable terrain, because 4,096 units
    // times 24 waypoints is 400KB that a pack with no map would never read.
    public readonly int[] PathCells;
    public readonly int[] PathLen;
    public readonly int[] PathCursor;
    /// <summary>Goal cell the current route was computed for, or -1 for no route.</summary>
    public readonly int[] PathGoalCell;
    public readonly int[] RepathCooldown;
    public readonly Fix64 CheapestUnitCost;

    public World(ContentDb content)
    {
        Content = content;
        Teams = new TeamState[TeamCount];
        for (int t = 0; t < TeamCount; t++)
            Teams[t] = new TeamState(content.Tech.Length, content.Sciences.Length, content.Powers.Length);
        Fix64 cheapest = Fix64.MaxValue;
        foreach (var u in content.Units) cheapest = Fix64.Min(cheapest, Fix64.FromInt(u.Cost));
        CheapestUnitCost = cheapest;

        int paths = content.HasPassability ? MaxUnits : 0;
        PathCells = new int[paths * PathCapacity];
        PathLen = new int[paths];
        PathCursor = new int[paths];
        PathGoalCell = new int[paths];
        RepathCooldown = new int[paths];
    }

    public int Spawn(int protoIdx, int team, Fix64 x, Fix64 y)
    {
        if (UnitCount >= MaxUnits) throw new InvalidOperationException("Unit capacity exceeded");
        int idx = UnitCount++;
        var proto = Content.Units[protoIdx];
        ref var u = ref Units[idx];
        u = default;
        u.Alive = true;
        u.ProtoIdx = protoIdx;
        u.Team = team;
        u.X = x;
        u.Y = y;
        u.TargetIdx = -1;
        u.ClipRemaining = -1;      // uninitialised; filled from the prototype on first shot
        u.Flags = proto.BirthFlags;
        u.VariantIdx = -1;
        u.GarrisonHost = -1;
        u.CaptureBy = -1;
        u.DepositCountdown = proto.DepositTicks;   // a fresh building pays after one full period
        if (PathLen.Length > 0)
        {
            // Slots are reused as the array fills; a fresh body must not inherit the route of
            // whoever held the index before it.
            PathLen[idx] = 0;
            PathCursor[idx] = 0;
            PathGoalCell[idx] = -1;
            RepathCooldown[idx] = 0;
        }
        SelectLoadout(idx);        // before RecomputeStats: the variant can change stats
        RecomputeStats(idx);
        u.Hp = Resolved(idx, Stat.MaxHp);
        return idx;
    }

    public Fix64 Resolved(int unitIdx, Stat stat)
        => ResolvedStats[unitIdx * StatResolver.StatCount + (int)stat];

    /// <summary>
    /// Re-resolves the unit's stat sheet from prototype base + cumulative veterancy
    /// bundles up to current rank. Called on spawn and on rank change only.
    /// </summary>
    public void RecomputeStats(int unitIdx)
    {
        ref var u = ref Units[unitIdx];
        var proto = Content.Units[u.ProtoIdx];
        IEnumerable<Modifier> mods = Enumerable.Empty<Modifier>();
        if (proto.VetTrackIdx >= 0 && u.Rank > 0)
        {
            var track = Content.VetTracks[proto.VetTrackIdx];
            mods = track.RankBundles.Take(u.Rank).SelectMany(b => b);
        }
        // Veterancy first, then the active variant — so an upgrade's bonus composes with a
        // rank bonus through one algebra rather than two competing ones.
        if (u.VariantIdx >= 0)
            mods = mods.Concat(proto.Variants[u.VariantIdx].Modifiers);

        StatResolver.Resolve(
            proto.BaseStats,
            mods,
            ResolvedStats.AsSpan(unitIdx * StatResolver.StatCount, StatResolver.StatCount));
    }

    /// <summary>
    /// Pick the active conditional loadout: first variant whose required flags are all
    /// present in (unit flags | team flags). Returns true if the selection CHANGED, so the
    /// caller knows whether a stat re-resolve is actually needed.
    /// </summary>
    public bool SelectLoadout(int unitIdx)
    {
        ref var u = ref Units[unitIdx];
        var proto = Content.Units[u.ProtoIdx];
        if (proto.Variants.Length == 0) return false;

        ulong effective = u.Flags | Teams[u.Team].Flags;
        int chosen = -1;
        for (int v = 0; v < proto.Variants.Length; v++)
            if ((proto.Variants[v].Required & ~effective) == 0) { chosen = v; break; }

        if (chosen == u.VariantIdx) return false;
        u.VariantIdx = chosen;
        return true;
    }

    /// <summary>Weapon in force right now: the active variant's, else the prototype's.</summary>
    public int EffectiveWeaponIdx(int unitIdx)
    {
        ref readonly var u = ref Units[unitIdx];
        var proto = Content.Units[u.ProtoIdx];
        if (u.VariantIdx >= 0 && proto.Variants[u.VariantIdx].WeaponIdx >= 0)
            return proto.Variants[u.VariantIdx].WeaponIdx;
        return proto.WeaponIdx;
    }

    /// <summary>Armor class in force right now.</summary>
    public int EffectiveArmorIdx(int unitIdx)
    {
        ref readonly var u = ref Units[unitIdx];
        var proto = Content.Units[u.ProtoIdx];
        if (u.VariantIdx >= 0 && proto.Variants[u.VariantIdx].ArmorClassIdx >= 0)
            return proto.Variants[u.VariantIdx].ArmorClassIdx;
        return proto.ArmorClassIdx;
    }

    public int AliveCount(int team)
    {
        int n = 0;
        for (int i = 0; i < UnitCount; i++)
            if (Units[i].Alive && Units[i].Team == team) n++;
        return n;
    }

    /// <summary>
    /// Live count of a team's units carrying a KindOf role. Ascending index, no early exit
    /// beyond the match itself, so the answer never depends on iteration order.
    /// </summary>
    public int AliveWithKind(int team, string flag)
    {
        int bit = Runtime.KindOf.BitOf(flag);
        if (bit < 0) return 0;
        uint mask = 1u << bit;
        int n = 0;
        for (int i = 0; i < UnitCount; i++)
        {
            ref readonly var u = ref Units[i];
            if (u.Alive && u.Team == team && (Content.Units[u.ProtoIdx].KindMask & mask) != 0) n++;
        }
        return n;
    }

    public int AliveOfProto(int team, int protoIdx)
    {
        int n = 0;
        for (int i = 0; i < UnitCount; i++)
            if (Units[i].Alive && Units[i].Team == team && Units[i].ProtoIdx == protoIdx) n++;
        return n;
    }

    /// <summary>
    /// Credit a team for a kill and promote it if that crosses a rank threshold.
    ///
    /// Deliberately independent of the KILLER's veterancy track: a unit with no track still
    /// earns its commander skill points, because the currency belongs to the player and not
    /// to the unit. Tying them together was the obvious shortcut and it would have made
    /// whole rosters unable to rank up.
    /// </summary>
    public void AwardSkillPoints(int team, int amount)
    {
        if ((uint)team >= TeamCount || amount <= 0 || Content.Ranks.Length == 0) return;
        var ts = Teams[team];
        ts.SkillPoints += amount;
        while (ts.CommanderRank < Content.Ranks.Length
               && ts.SkillPoints >= Content.Ranks[ts.CommanderRank].SkillPointsNeeded)
        {
            ts.PurchasePoints += Content.Ranks[ts.CommanderRank].PurchasePointsGranted;
            ts.CommanderRank++;
        }
    }

    /// <summary>
    /// Buy a science. Every failure path is a silent no-op for the same reason garrison is:
    /// an inadmissible command must do NOTHING, identically on every machine, rather than
    /// throw on one of them.
    /// </summary>
    public bool TryPurchaseScience(int team, int scienceIdx)
    {
        if ((uint)team >= TeamCount || (uint)scienceIdx >= (uint)Content.Sciences.Length) return false;
        var ts = Teams[team];
        var sc = Content.Sciences[scienceIdx];
        if (ts.SciencesOwned[scienceIdx]) return false;
        if (ts.PurchasePoints < sc.Cost) return false;
        if (ts.CommanderRank < sc.RequiresRank) return false;
        foreach (int req in sc.RequiresIdx)
            if (!ts.SciencesOwned[req]) return false;

        ts.PurchasePoints -= sc.Cost;
        ts.SciencesOwned[scienceIdx] = true;
        ts.Flags |= sc.GrantsFlags;   // the seam to conditional variants, already built
        return true;
    }

    /// <summary>Live occupants of a garrisonable structure. Ascending index, like everything.</summary>
    public int GarrisonCount(int hostIdx)
    {
        int n = 0;
        for (int i = 0; i < UnitCount; i++)
            if (Units[i].Alive && Units[i].GarrisonHost == hostIdx) n++;
        return n;
    }

    /// <summary>
    /// Move a unit into a structure. Rejected — silently, as a no-op — when the host is not
    /// garrisonable, is full, belongs to another team, or either party is dead. A command
    /// that cannot be honoured must not desync: it must do NOTHING, identically everywhere.
    /// </summary>
    public bool TryGarrison(int unitIdx, int hostIdx)
    {
        if (unitIdx < 0 || hostIdx < 0 || unitIdx >= UnitCount || hostIdx >= UnitCount) return false;
        ref var u = ref Units[unitIdx];
        ref var host = ref Units[hostIdx];
        if (!u.Alive || !host.Alive || u.Team != host.Team) return false;
        if (u.GarrisonHost >= 0) return false;
        // A structure cannot take cover in another structure. Beyond being nonsense, it is
        // what keeps the clear-building damage in ApplyDamage from recursing: with no
        // structure ever an occupant, an A-inside-B-inside-A cycle cannot be built.
        if (Content.Units[u.ProtoIdx].IsStructure) return false;
        var hp = Content.Units[host.ProtoIdx];
        if (hp.GarrisonCapacity <= 0) return false;
        if (GarrisonCount(hostIdx) >= hp.GarrisonCapacity) return false;
        u.GarrisonHost = hostIdx;
        // Occupants fire from the building, so they take its position. Without this the
        // occupant would keep shooting from wherever it was standing when it entered.
        u.X = host.X;
        u.Y = host.Y;
        return true;
    }

    /// <summary>Turn every occupant out of a host, e.g. because the host died.</summary>
    public void EvictGarrison(int hostIdx)
    {
        for (int i = 0; i < UnitCount; i++)
            if (Units[i].GarrisonHost == hostIdx) Units[i].GarrisonHost = -1;
    }

    /// <summary>
    /// Net power for a team: sum of EnergyProduction over live things. ZH's whole power
    /// economy is this one integer, and going negative is a brownout.
    /// </summary>
    public int PowerBalance(int team)
    {
        int p = 0;
        for (int i = 0; i < UnitCount; i++)
        {
            ref readonly var u = ref Units[i];
            if (u.Alive && u.Team == team) p += Content.Units[u.ProtoIdx].EnergyProduction;
        }
        return p;
    }

    /// <summary>
    /// Out of the game: no live units, nothing being built, and (money + undrawn
    /// pool) can never cover the next unit — the queue head if one is waiting,
    /// otherwise the cheapest prototype. With no economy set up this reduces to
    /// AliveCount == 0, so pure-combat duels keep their original semantics.
    /// A tech-deadlocked queue head that stays affordable never resolves; those
    /// games end at the tick cap as draws.
    /// </summary>
    /// <summary>
    /// Why a team's unit queue is not progressing, or null if it is fine.
    ///
    /// This exists because a stalled queue is INVISIBLE in aggregate results: the team just
    /// quietly builds nothing, net worth stays flat (it counts money and army together), and
    /// the run reads as a boring draw. That cost three separate debugging rounds during
    /// development — each time a build order referenced a unit whose prerequisite it never
    /// satisfied. A harness that cannot say "your order never built anything, here is why"
    /// makes every experiment above it untrustworthy.
    /// </summary>
    public string? QueueStallReason(int team)
    {
        var ts = Teams[team];
        if (ts.UnitQueue.Count == 0) return null;
        var head = ts.UnitQueue[0];
        if (head.Started) return null;
        var p = Content.Units[head.Idx];

        foreach (int t in p.PrereqTechIdx)
            if (!ts.Researched[t])
                return $"'{p.Id}' needs tech '{Content.Tech[t].Id}', never researched";

        foreach (int o in p.PrereqObjectIdx)
            if (AliveOfProto(team, o) == 0)
                return $"'{p.Id}' needs a live '{Content.Units[o].Id}'";

        if (Content.HasFactories && !p.IsStructure && AliveWithKind(team, Runtime.KindOf.Factory) == 0)
            return $"'{p.Id}' needs a live factory (KindOf {Runtime.KindOf.Factory})";

        if (Content.HasPower && PowerBalance(team) < 0)
            return $"'{p.Id}' blocked by brownout (power {PowerBalance(team)})";

        if (p.MaxSimultaneousOfType > 0 && AliveOfProto(team, p.SelfIdx) >= p.MaxSimultaneousOfType)
            return $"'{p.Id}' at its cap of {p.MaxSimultaneousOfType}";

        if (ts.Money < Fix64.FromInt(p.Cost))
            return $"'{p.Id}' costs {p.Cost}, team has {ts.Money.ToDoubleForDisplay():0}";

        return null;
    }

    public bool TeamDefeated(int team)
    {
        if (AliveCount(team) > 0) return false;
        var ts = Teams[team];

        // Affordability is not the only way to be finished. Once a pack has factories, a
        // team with no live factory can never start a unit again no matter how rich it is —
        // so money alone would keep a dead team "alive" until the tick cap and turn a
        // decisive win into a draw. This is the rule that makes a factory worth killing.
        if (Content.HasFactories && AliveWithKind(team, Runtime.KindOf.Factory) == 0)
        {
            // Unless it can still place a structure — a rebuild is a real comeback path.
            bool canRebuild = false;
            foreach (var e in ts.UnitQueue)
            {
                var p = Content.Units[e.Idx];
                if (p.IsStructure && (e.Started || ts.Money + ts.PoolRemaining >= Fix64.FromInt(p.Cost)))
                { canRebuild = true; break; }
            }
            if (!canRebuild) return true;
        }

        if (ts.UnitQueue.Count > 0)
        {
            if (ts.UnitQueue[0].Started) return false;
            return ts.Money + ts.PoolRemaining < Fix64.FromInt(Content.Units[ts.UnitQueue[0].Idx].Cost);
        }
        return ts.Money + ts.PoolRemaining < CheapestUnitCost;
    }

    /// <summary>Canonical fingerprint of all sim-relevant unit state.</summary>
    public void HashInto(ref Fnv1a64 h)
    {
        h.AddInt32(UnitCount);
        for (int i = 0; i < UnitCount; i++)
        {
            ref readonly var u = ref Units[i];
            h.AddBool(u.Alive);
            if (!u.Alive) continue;
            // Identity, not table position. ProtoIdx stays a runtime array index; folding
            // it in would make a replay's meaning depend on where a prototype happens to
            // sort, so adding an unrelated unit could silently rewrite an old replay.
            h.AddUInt64(Content.Units[u.ProtoIdx].StableId);
            h.AddInt32(u.Team);
            h.AddInt64(u.X.Raw);
            h.AddInt64(u.Y.Raw);
            h.AddInt64(u.Hp.Raw);
            h.AddInt32(u.CooldownRemaining);
            // Appended AFTER CooldownRemaining, never inserted mid-record: field position in
            // the hash is part of the replay contract. -1 for every unit in a pack with no
            // clipped weapons, so this costs existing packs nothing.
            h.AddInt32(u.ClipRemaining);
            // Flag state is hashed only when the pack can produce it. In a pack with no
            // flags these are provably constant (0 and -1) for every unit for all time, so
            // omitting them loses no information — and it keeps pins that predate this
            // slice comparable, which is worth more than uniformity. e2e asserts the other
            // half of the claim: with flags in play, the hash DOES respond to them.
            if (Content.HasFlags)
            {
                h.AddUInt64(u.Flags);
                h.AddInt32(u.VariantIdx);
            }
            h.AddInt32(u.TargetIdx);
            h.AddInt32(u.Xp);
            h.AddInt32(u.Rank);
            // Same opt-in discipline as the flag fields above, and for the same reason: in a
            // pack with no garrisonable building these are provably -1/0/0 for every unit for
            // all time, so omitting them loses nothing and keeps every pin that predates this
            // slice comparable. Appended last, never inserted: position is the contract.
            if (Content.HasGarrison) h.AddInt32(u.GarrisonHost);
            if (Content.HasCapture)
            {
                h.AddInt32(u.CaptureProgress);
                h.AddInt32(u.CaptureBy);
                h.AddInt32(u.DepositCountdown);
            }
            // A route is sim state, not a cache: WHEN it was computed decides which way the
            // unit walks around an obstacle, so two runs that plan at different moments
            // diverge. Only the live tail is folded — the waypoints already consumed cannot
            // affect anything again — and the whole block is gated on the pack actually
            // having terrain to path around, exactly like the flag and garrison fields.
            if (Content.HasPassability)
            {
                h.AddInt32(PathGoalCell[i]);
                h.AddInt32(PathCursor[i]);
                h.AddInt32(PathLen[i]);
                for (int w = PathCursor[i]; w < PathLen[i]; w++)
                    h.AddInt32(PathCells[i * PathCapacity + w]);
                h.AddInt32(RepathCooldown[i]);
            }
        }

        for (int t = 0; t < TeamCount; t++)
        {
            var ts = Teams[t];
            h.AddInt64(ts.Money.Raw);
            h.AddInt64(ts.IncomePerTick.Raw);
            h.AddInt64(ts.PoolRemaining.Raw);
            // Gated like every currency before it: a pack with no rank ladder and no science
            // has these provably zero forever, so omitting them keeps older pins comparable.
            if (Content.HasSciences)
            {
                h.AddInt32(ts.SkillPoints);
                h.AddInt32(ts.CommanderRank);
                h.AddInt32(ts.PurchasePoints);
                for (int i = 0; i < ts.SciencesOwned.Length; i++) h.AddBool(ts.SciencesOwned[i]);
            }
            if (Content.HasPowers)
                for (int i = 0; i < ts.PowerCooldown.Length; i++) h.AddInt32(ts.PowerCooldown[i]);
            h.AddInt64(ts.SpawnX.Raw);
            h.AddInt64(ts.SpawnY.Raw);
            h.AddInt64(ts.RallyX.Raw);
            h.AddInt64(ts.RallyY.Raw);
            h.AddBool(ts.HasRally);
            h.AddInt32(ts.ProducedCount);
            if (Content.HasFlags) h.AddUInt64(ts.Flags);
            foreach (bool r in ts.Researched) h.AddBool(r);
            HashQueue(ref h, ts.ResearchQueue);
            HashQueue(ref h, ts.UnitQueue);
        }
    }

    private static void HashQueue(ref Fnv1a64 h, List<QueueEntry> q)
    {
        h.AddInt32(q.Count);
        foreach (var e in q)
        {
            h.AddInt32(e.Idx);
            h.AddBool(e.Started);
            h.AddInt32(e.TicksRemaining);
        }
    }
}
