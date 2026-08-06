using RtsSkeleton.Content;
using RtsSkeleton.Core;

namespace RtsSkeleton.Runtime;

public sealed class SimResult
{
    public int WinnerTeam = -1;   // -1 = draw / tick cap
    public int Ticks;
    public int SurvivorsTeam0;
    public int SurvivorsTeam1;
    public ulong FinalHash;
    public ulong CommandLogHash;
    public List<(int Tick, ulong Hash)> HashTrace = new();
}

/// <summary>
/// The headless deterministic core. Consumes (content, seed, command log),
/// advances fixed 30 Hz logic ticks in a hard-coded system order, and emits a
/// state-hash trace. No rendering, no wall clock, no floats, no engine types —
/// this exact class is what a Godot shell reads snapshots from, what a lockstep
/// session steps, and what the balancing harness runs at N× realtime.
///
/// System order per tick (order is part of the determinism contract):
///   commands → cooldowns → targeting → movement → combat/deaths/veterancy → hash
/// </summary>
public sealed class Sim
{
    private const int HashTraceInterval = 300; // every 10 seconds of game time

    public readonly World World;
    private readonly List<Command> _log;
    private int _nextCommand;
    private readonly Pcg32 _rngCombat;
    public int Tick { get; private set; }

    public Sim(ContentDb content, ulong seed, List<Command> commandLog)
    {
        World = new World(content);
        _log = commandLog
            .OrderBy(c => c.Tick)
            .ThenBy(c => c.Seq)
            .ToList();
        _rngCombat = new Pcg32(seed, streamId: 1);
        // Future streams (crates, AI jitter, weather) get their own ids so adding
        // one never shifts another system's draw sequence.
    }

    public SimResult Run(int maxTicks)
    {
        var result = new SimResult { CommandLogHash = Command.HashLog(_log) };

        while (Tick < maxTicks)
        {
            Step();

            if (Tick % HashTraceInterval == 0)
                result.HashTrace.Add((Tick, HashState()));

            int a = World.AliveCount(0);
            int b = World.AliveCount(1);
            if (a == 0 || b == 0)
            {
                result.WinnerTeam = a == 0 && b == 0 ? -1 : (a > 0 ? 0 : 1);
                break;
            }
        }

        result.Ticks = Tick;
        result.SurvivorsTeam0 = World.AliveCount(0);
        result.SurvivorsTeam1 = World.AliveCount(1);
        result.FinalHash = HashState();
        return result;
    }

    public void Step()
    {
        Tick++;
        ApplyCommands();
        TickCooldowns();
        TargetingSystem();
        MovementSystem();
        CombatSystem();
    }

    // --- Systems ---------------------------------------------------------------

    private void ApplyCommands()
    {
        while (_nextCommand < _log.Count && _log[_nextCommand].Tick <= Tick)
        {
            var c = _log[_nextCommand++];
            switch (c.Kind)
            {
                case CommandKind.Spawn:
                    World.Spawn(c.A, c.B, c.X, c.Y);
                    break;
                case CommandKind.RallyTeam:
                    for (int i = 0; i < World.UnitCount; i++)
                    {
                        ref var u = ref World.Units[i];
                        if (!u.Alive || u.Team != c.A) continue;
                        u.RallyX = c.X;
                        u.RallyY = c.Y;
                        u.HasRally = true;
                    }
                    break;
            }
        }
    }

    private void TickCooldowns()
    {
        for (int i = 0; i < World.UnitCount; i++)
        {
            ref var u = ref World.Units[i];
            if (u.Alive && u.CooldownRemaining > 0) u.CooldownRemaining--;
        }
    }

    /// <summary>
    /// Keep current target while alive and inside acquire radius; otherwise pick the
    /// nearest live enemy by (squared distance, unit index) — the index tie-break is
    /// what keeps target choice deterministic when distances are equal.
    /// </summary>
    private void TargetingSystem()
    {
        for (int i = 0; i < World.UnitCount; i++)
        {
            ref var u = ref World.Units[i];
            if (!u.Alive) continue;
            var weapon = World.Content.Weapons[World.Content.Units[u.ProtoIdx].WeaponIdx];

            if (u.TargetIdx >= 0)
            {
                ref readonly var t = ref World.Units[u.TargetIdx];
                if (t.Alive && DistSq(in u, in t) <= weapon.AcquireRangeSq) continue;
                u.TargetIdx = -1;
            }

            Fix64 bestDist = Fix64.MaxValue;
            int best = -1;
            for (int j = 0; j < World.UnitCount; j++)
            {
                if (j == i) continue;
                ref readonly var e = ref World.Units[j];
                if (!e.Alive || e.Team == u.Team) continue;
                Fix64 d = DistSq(in u, in e);
                if (d <= weapon.AcquireRangeSq && d < bestDist)
                {
                    bestDist = d;
                    best = j;
                }
            }
            u.TargetIdx = best;
        }
    }

    /// <summary>
    /// Close to weapon range on the current target, otherwise advance to rally.
    /// No collision or steering in the skeleton — units pass through each other.
    /// Flow-field / RVO pathing is a presentation-era feature slotted here later.
    /// </summary>
    private void MovementSystem()
    {
        for (int i = 0; i < World.UnitCount; i++)
        {
            ref var u = ref World.Units[i];
            if (!u.Alive) continue;
            Fix64 speed = World.Resolved(i, Stat.Speed);

            if (u.TargetIdx >= 0)
            {
                ref readonly var t = ref World.Units[u.TargetIdx];
                Fix64 range = World.Resolved(i, Stat.Range);
                if (DistSq(in u, in t) > range * range)
                    StepToward(ref u, t.X, t.Y, speed);
            }
            else if (u.HasRally)
            {
                Fix64 dx = u.RallyX - u.X, dy = u.RallyY - u.Y;
                Fix64 dsq = dx * dx + dy * dy;
                if (dsq <= Fix64.Half * Fix64.Half) u.HasRally = false;
                else StepToward(ref u, u.RallyX, u.RallyY, speed);
            }
        }
    }

    private static void StepToward(ref UnitState u, Fix64 tx, Fix64 ty, Fix64 speed)
    {
        Fix64 dx = tx - u.X, dy = ty - u.Y;
        Fix64 len = Fix64.Sqrt(dx * dx + dy * dy);
        if (len <= speed)
        {
            u.X = tx;
            u.Y = ty;
            return;
        }
        u.X += dx * speed / len;
        u.Y += dy * speed / len;
    }

    /// <summary>
    /// Fire when off cooldown and in range. Damage pipeline:
    ///   resolvedDamage(attacker) × typeVsArmor × spread × armorFactor(victim).
    /// Kills award XP = victim cost / 10 to the attacker; rank-ups re-resolve the
    /// attacker's stat sheet through the modifier algebra. Immediate kill marking
    /// inside the deterministic scan keeps ordering unambiguous (a dead unit can
    /// still have fired earlier in the same tick — same rule as Generals).
    /// </summary>
    private void CombatSystem()
    {
        var content = World.Content;
        for (int i = 0; i < World.UnitCount; i++)
        {
            ref var u = ref World.Units[i];
            if (!u.Alive || u.CooldownRemaining > 0 || u.TargetIdx < 0) continue;
            ref var t = ref World.Units[u.TargetIdx];
            if (!t.Alive) continue;

            Fix64 range = World.Resolved(i, Stat.Range);
            if (DistSq(in u, in t) > range * range) continue;

            var proto = content.Units[u.ProtoIdx];
            var weapon = content.Weapons[proto.WeaponIdx];

            Fix64 spreadMult = Fix64.One;
            if (weapon.Spread > Fix64.Zero)
            {
                // uniform in [1 - spread, 1 + spread)
                Fix64 frac = _rngCombat.NextFraction01();
                spreadMult = Fix64.One - weapon.Spread + Fix64.Two * weapon.Spread * frac;
            }

            Fix64 typeMult = content.DamageVsArmor[weapon.DamageTypeIdx, content.Units[t.ProtoIdx].ArmorClassIdx];
            Fix64 dmg = World.Resolved(i, Stat.Damage) * typeMult * spreadMult * World.Resolved(u.TargetIdx, Stat.ArmorFactor);

            t.Hp -= dmg;
            u.CooldownRemaining = (World.Resolved(i, Stat.CooldownScale) * weapon.CooldownTicks).CeilToInt();

            if (t.Hp <= Fix64.Zero)
            {
                t.Alive = false;
                AwardKillXp(i, content.Units[t.ProtoIdx].Cost);
            }
        }
    }

    private void AwardKillXp(int attackerIdx, int victimCost)
    {
        ref var a = ref World.Units[attackerIdx];
        var proto = World.Content.Units[a.ProtoIdx];
        if (proto.VetTrackIdx < 0) return;
        a.Xp += victimCost / 10;

        var track = World.Content.VetTracks[proto.VetTrackIdx];
        int newRank = a.Rank;
        while (newRank < track.Thresholds.Length && a.Xp >= track.Thresholds[newRank])
            newRank++;
        if (newRank != a.Rank)
        {
            a.Rank = newRank;
            World.RecomputeStats(attackerIdx);
        }
    }

    // --- Helpers ---------------------------------------------------------------

    private static Fix64 DistSq(in UnitState a, in UnitState b)
    {
        Fix64 dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    public ulong HashState()
    {
        var h = Fnv1a64.Create();
        h.AddInt32(Tick);
        h.AddUInt64(_rngCombat.StateForHash);
        World.HashInto(ref h);
        return h.Value;
    }
}
