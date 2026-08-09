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
    public readonly bool[] Researched;
    public readonly List<QueueEntry> ResearchQueue = new();
    public readonly List<QueueEntry> UnitQueue = new();

    public TeamState(int techCount) => Researched = new bool[techCount];
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
    public readonly Fix64 CheapestUnitCost;

    public World(ContentDb content)
    {
        Content = content;
        Teams = new TeamState[TeamCount];
        for (int t = 0; t < TeamCount; t++) Teams[t] = new TeamState(content.Tech.Length);
        Fix64 cheapest = Fix64.MaxValue;
        foreach (var u in content.Units) cheapest = Fix64.Min(cheapest, Fix64.FromInt(u.Cost));
        CheapestUnitCost = cheapest;
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
        }

        for (int t = 0; t < TeamCount; t++)
        {
            var ts = Teams[t];
            h.AddInt64(ts.Money.Raw);
            h.AddInt64(ts.IncomePerTick.Raw);
            h.AddInt64(ts.PoolRemaining.Raw);
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
