using RtsSkeleton.Content;
using RtsSkeleton.Core;

namespace RtsSkeleton.Runtime;

/// <summary>
/// One unit as presentation sees it. Doubles, not Fix64: this is the display
/// boundary, the same category of conversion as <see cref="Fix64.ToDoubleForDisplay"/>.
/// Nothing here may ever flow back into the sim — a renderer interpolating these
/// values cannot perturb determinism, which is the whole point of the seam.
/// </summary>
public readonly struct UnitSnapshot
{
    public readonly int Id;          // unit index; stable across snapshots (tombstones, no reuse)
    public readonly int ProtoIdx;
    public readonly int Team;
    public readonly double X;
    public readonly double Y;
    public readonly double HpFraction;
    public readonly int Rank;
    public readonly bool Firing;     // fired within the last few ticks — muzzle-flash hint
    public readonly int TargetId;    // unit index being engaged, -1 for none

    public UnitSnapshot(int id, int protoIdx, int team, double x, double y, double hpFraction, int rank, bool firing, int targetId)
    {
        Id = id;
        ProtoIdx = protoIdx;
        Team = team;
        X = x;
        Y = y;
        HpFraction = hpFraction;
        Rank = rank;
        Firing = firing;
        TargetId = targetId;
    }
}

public readonly struct TeamSnapshot
{
    public readonly double Money;
    public readonly double PoolRemaining;
    public readonly int QueuedUnits;
    public readonly int QueuedResearch;
    /// <summary>Build progress of the queue head in [0,1]; 0 when nothing has started.</summary>
    public readonly double BuildProgress;
    public readonly int AliveUnits;

    public TeamSnapshot(double money, double pool, int queuedUnits, int queuedResearch, double buildProgress, int aliveUnits)
    {
        Money = money;
        PoolRemaining = pool;
        QueuedUnits = queuedUnits;
        QueuedResearch = queuedResearch;
        BuildProgress = buildProgress;
        AliveUnits = aliveUnits;
    }
}

/// <summary>
/// Immutable read-only view of the world at one tick — the only thing a renderer
/// is allowed to consume. A shell keeps the previous and current snapshot and
/// interpolates between them at frame rate while the sim advances in fixed 30 Hz
/// steps; that decoupling is what lets presentation run at any frame rate without
/// touching the lockstep-safe tick loop.
/// </summary>
public sealed class Snapshot
{
    public int Tick;
    public UnitSnapshot[] Units = Array.Empty<UnitSnapshot>();
    public TeamSnapshot[] Teams = Array.Empty<TeamSnapshot>();
    public int WinnerTeam = -2;   // -2 = still running, -1 = draw, else team index

    /// <summary>
    /// Fills <paramref name="into"/> from the world, reusing its arrays when the
    /// capacity allows. Snapshotting allocates nothing in steady state, so a shell
    /// can capture every tick without generating GC pressure mid-battle.
    /// </summary>
    public static void Capture(World w, int tick, int winnerTeam, Snapshot into)
    {
        into.Tick = tick;
        into.WinnerTeam = winnerTeam;

        int alive = 0;
        for (int i = 0; i < w.UnitCount; i++)
            if (w.Units[i].Alive) alive++;

        if (into.Units.Length < alive) into.Units = new UnitSnapshot[Math.Max(alive, into.Units.Length * 2)];
        var units = into.Units;

        int n = 0;
        for (int i = 0; i < w.UnitCount; i++)
        {
            ref readonly var u = ref w.Units[i];
            if (!u.Alive) continue;
            Fix64 maxHp = w.Resolved(i, Stat.MaxHp);
            double frac = maxHp > Fix64.Zero ? (u.Hp / maxHp).ToDoubleForDisplay() : 0.0;
            var weapon = w.Content.Weapons[w.Content.Units[u.ProtoIdx].WeaponIdx];
            bool firing = u.CooldownRemaining > weapon.CooldownTicks - 4;
            units[n++] = new UnitSnapshot(i, u.ProtoIdx, u.Team,
                u.X.ToDoubleForDisplay(), u.Y.ToDoubleForDisplay(),
                Math.Clamp(frac, 0.0, 1.0), u.Rank, firing, u.TargetIdx);
        }
        into.UnitCount = n;

        if (into.Teams.Length != World.TeamCount) into.Teams = new TeamSnapshot[World.TeamCount];
        for (int t = 0; t < World.TeamCount; t++)
        {
            var ts = w.Teams[t];
            double progress = 0.0;
            if (ts.UnitQueue.Count > 0 && ts.UnitQueue[0].Started)
            {
                int total = w.Content.Units[ts.UnitQueue[0].Idx].BuildTicks;
                progress = total > 0 ? 1.0 - (double)ts.UnitQueue[0].TicksRemaining / total : 0.0;
            }
            into.Teams[t] = new TeamSnapshot(
                ts.Money.ToDoubleForDisplay(), ts.PoolRemaining.ToDoubleForDisplay(),
                ts.UnitQueue.Count, ts.ResearchQueue.Count, progress, w.AliveCount(t));
        }
    }

    /// <summary>Live entries in <see cref="Units"/>; the array itself may be longer.</summary>
    public int UnitCount;
}
