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
    public int TargetIdx;      // -1 = none
    public int Xp;
    public int Rank;
    public Fix64 RallyX;
    public Fix64 RallyY;
    public bool HasRally;
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

    public readonly ContentDb Content;
    public readonly UnitState[] Units = new UnitState[MaxUnits];
    public readonly Fix64[] ResolvedStats = new Fix64[MaxUnits * StatResolver.StatCount];
    public int UnitCount;

    public World(ContentDb content) => Content = content;

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
        StatResolver.Resolve(
            proto.BaseStats,
            mods,
            ResolvedStats.AsSpan(unitIdx * StatResolver.StatCount, StatResolver.StatCount));
    }

    public int AliveCount(int team)
    {
        int n = 0;
        for (int i = 0; i < UnitCount; i++)
            if (Units[i].Alive && Units[i].Team == team) n++;
        return n;
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
            h.AddInt32(u.ProtoIdx);
            h.AddInt32(u.Team);
            h.AddInt64(u.X.Raw);
            h.AddInt64(u.Y.Raw);
            h.AddInt64(u.Hp.Raw);
            h.AddInt32(u.CooldownRemaining);
            h.AddInt32(u.TargetIdx);
            h.AddInt32(u.Xp);
            h.AddInt32(u.Rank);
        }
    }
}
