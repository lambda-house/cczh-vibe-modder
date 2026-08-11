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
///   commands → production/economy → garrison/capture → loadout/stat re-resolve →
///   cooldowns → targeting → movement → combat/deaths/veterancy → hash
/// Production sits right after commands so an enqueue issued this tick can start
/// building this tick, and a unit finished this tick acts (cooldown/targeting/
/// movement/combat) this tick — the same treatment command-spawned units get.
/// The loadout phase sits between production and cooldowns because a flag gained this
/// tick (research completing, an upgrade landing) re-selects the conditional loadout,
/// which changes weapon and armor class — i.e. STATS. Resolving it in its own pinned
/// phase is what stops damage depending on which system happened to run first.
/// The holding phase sits ahead of it for the same class of reason: capture flips a
/// structure's TEAM, which targeting, production and the defeat test all read. Settle
/// ownership once, before anything asks whose it is.
/// </summary>
public sealed class Sim
{
    private const int HashTraceInterval = 300; // every 10 seconds of game time

    public readonly World World;
    private readonly List<Command> _log;
    private int _nextCommand;
    private readonly Pcg32 _rngCombat;
    private readonly Pcg32 _rngSpawn;
    /// <summary>
    /// Deaths raised this tick, drained by <see cref="DeathRuleSystem"/>. A list rather
    /// than immediate dispatch so that combat's iteration stays a clean single pass and
    /// cascades are bounded in one place.
    /// </summary>
    private readonly List<PendingDeath> _deaths = new();

    /// <summary>
    /// Broad phase over live positions, rebuilt once per tick. A pure accelerator: it must
    /// never change an answer. <see cref="UseSpatialIndex"/> turns it off so the harness can
    /// prove that by running both and comparing state hashes.
    /// </summary>
    private readonly SpatialGrid _grid = new();
    private int[] _candidates = new int[256];

    /// <summary>
    /// Terrain pathfinder, or null when the pack declares no impassable terrain. Its scratch
    /// arrays are sized to the map, so it is owned per-Sim rather than shared — two sims in
    /// one process (the harness runs many) must not write each other's search.
    /// </summary>
    private readonly PathFinder? _path;

    /// <summary>
    /// Off = every radius query is the original full scan. Exists ONLY so equivalence is
    /// testable; both paths must produce bit-identical state, and e2e asserts it.
    /// </summary>
    public bool UseSpatialIndex = true;

    /// <summary>
    /// How many generations of death-triggered deaths may chain. ZH has NO bound here — a
    /// SlowDeathBehavior spawns an OCL whose objects carry their own death rules, and
    /// nothing caps the cascade. We clamp instead of failing: a content bug should degrade
    /// a battle, not crash the harness mid-sweep. Clamping is hash-safe because the bound
    /// is a constant, so every machine clamps at the same generation.
    /// </summary>
    private const int MaxDeathCascadeDepth = 4;

    public int Tick { get; private set; }

    /// <summary>
    /// -2 while the battle is running, -1 for a draw, else the winning team.
    /// Updated by <see cref="Step"/> so an interactive shell driving the sim one
    /// tick at a time applies exactly the rule the batch harness does.
    /// </summary>
    public int Outcome { get; private set; } = Running;
    public const int Running = -2;

    public Sim(ContentDb content, ulong seed, List<Command> commandLog)
    {
        World = new World(content);
        _log = commandLog
            .OrderBy(c => c.Tick)
            .ThenBy(c => c.Seq)
            .ToList();
        _rngCombat = new Pcg32(seed, streamId: 1);
        _rngSpawn = new Pcg32(seed, streamId: 2);
        // Pathing draws no randomness at all and therefore claims no stream id. A route is a
        // pure function of (start, goal, grid, mask) with a total order on the open set —
        // if it needed a coin flip it would not be reproducible.
        _path = content.HasPassability && content.Map is not null ? new PathFinder(content.Map) : null;
        // Future streams (crates, AI jitter, weather) get their own ids so adding
        // one never shifts another system's draw sequence. Stream 2 is death-rule
        // spawn placement: it must not share with combat, or adding a wreck would
        // shift every damage roll that follows it.
    }

    /// <summary>
    /// Append a command from outside the pre-built log (interactive shells now,
    /// lockstep peers later). Commands must be stamped for a future tick so that
    /// state never depends on arrival timing — only on the (tick, seq) order the
    /// log records. The grown log remains the complete replay of the session.
    /// </summary>
    public void Enqueue(in Command c)
    {
        if (c.Tick <= Tick)
            throw new ArgumentException($"command for tick {c.Tick} but sim is at {Tick}; commands must be scheduled ahead");
        int i = _log.Count;
        while (i > _nextCommand && (_log[i - 1].Tick > c.Tick
                                    || (_log[i - 1].Tick == c.Tick && _log[i - 1].Seq > c.Seq)))
            i--;
        _log.Insert(i, c);
    }

    public SimResult Run(int maxTicks)
    {
        var result = new SimResult { CommandLogHash = Command.HashLog(_log) };

        while (Tick < maxTicks)
        {
            Step();

            if (Tick % HashTraceInterval == 0)
                result.HashTrace.Add((Tick, HashState()));

            if (Outcome != Running)
            {
                result.WinnerTeam = Outcome;
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
        ProductionSystem();
        HoldingSystem();
        LoadoutSystem();
        TickCooldowns();
        // TWO rebuilds, not one, because the two consumers need different instants and a
        // broad phase that is even slightly stale is a wrong answer rather than a slow one.
        // Targeting must see units PRODUCED this tick — a unit finished this tick acts this
        // tick — and combat must see positions AFTER this tick's movement. One rebuild would
        // have to be stale for one of them. Two are still O(n) against the O(n^2) they replace.
        if (UseSpatialIndex) _grid.Rebuild(World);
        TargetingSystem();
        MovementSystem();
        if (UseSpatialIndex) _grid.Rebuild(World);
        CombatSystem();
        DeathRuleSystem();

        bool d0 = World.TeamDefeated(0);
        bool d1 = World.TeamDefeated(1);
        if (d0 || d1) Outcome = d0 && d1 ? -1 : (d0 ? 1 : 0);
    }

    /// <summary>Captures the current tick into <paramref name="into"/> for a renderer.</summary>
    public void CaptureSnapshot(Snapshot into) => Snapshot.Capture(World, Tick, Outcome, into);

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
                case CommandKind.Garrison:
                    // Return value deliberately ignored: an inadmissible garrison is a no-op,
                    // not an error. Every peer computes the same no-op.
                    World.TryGarrison(c.A, c.B);
                    break;
                case CommandKind.PurchaseScience:
                    World.TryPurchaseScience(c.A, c.B);
                    break;
                case CommandKind.FirePower:
                    FirePower(c.A, c.B, c.X, c.Y);
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
                    if ((uint)c.A < World.TeamCount)
                    {
                        var ts = World.Teams[c.A];
                        ts.RallyX = c.X;
                        ts.RallyY = c.Y;
                        ts.HasRally = true;
                    }
                    break;
                case CommandKind.SetupEconomy:
                    if ((uint)c.A < World.TeamCount)
                    {
                        var ts = World.Teams[c.A];
                        ts.Money = c.X;
                        ts.IncomePerTick = c.Y;
                        ts.PoolRemaining = Fix64.FromInt(c.B);
                    }
                    break;
                case CommandKind.SetSpawn:
                    if ((uint)c.A < World.TeamCount)
                    {
                        var ts = World.Teams[c.A];
                        ts.SpawnX = c.X;
                        ts.SpawnY = c.Y;
                    }
                    break;
                case CommandKind.ProduceUnit:
                    if ((uint)c.A < World.TeamCount && (uint)c.B < World.Content.Units.Length)
                        World.Teams[c.A].UnitQueue.Add(new QueueEntry { Idx = c.B });
                    break;
                case CommandKind.ResearchTech:
                    if ((uint)c.A < World.TeamCount && (uint)c.B < World.Content.Tech.Length)
                        World.Teams[c.A].ResearchQueue.Add(new QueueEntry { Idx = c.B });
                    break;
            }
        }
    }

    /// <summary>
    /// Economy and production, per team in ascending team index:
    /// income accrual (bounded by the remaining supply pool), then the research
    /// queue, then the unit queue — so a tech completing this tick unlocks a unit
    /// start this tick, and research has funding priority over units. Queue heads
    /// deduct their cost when they start and stall (blocking the queue) until
    /// prerequisites are researched and funds suffice; a finished head completes
    /// and the next entry may start in the same tick, so a build order issued
    /// entirely at tick 0 is paced by money and build times alone.
    /// </summary>
    private void ProductionSystem()
    {
        for (int team = 0; team < World.TeamCount; team++)
        {
            var ts = World.Teams[team];

            if (ts.PoolRemaining > Fix64.Zero)
            {
                Fix64 accrue = Fix64.Min(ts.IncomePerTick, ts.PoolRemaining);
                ts.Money += accrue;
                ts.PoolRemaining -= accrue;
            }

            TickResearchQueue(ts);
            TickUnitQueue(team, ts);
        }
    }

    private void TickResearchQueue(TeamState ts)
    {
        var q = ts.ResearchQueue;
        if (q.Count > 0 && q[0].Started)
        {
            var head = q[0];
            head.TicksRemaining--;
            if (head.TicksRemaining == 0)
            {
                ts.Researched[head.Idx] = true;
                q.RemoveAt(0);
            }
            else q[0] = head;
        }
        while (q.Count > 0 && !q[0].Started)
        {
            var head = q[0];
            if (ts.Researched[head.Idx]) { q.RemoveAt(0); continue; } // duplicate: free skip
            var node = World.Content.Tech[head.Idx];
            if (!PrereqsMet(ts, node.RequiresIdx) || ts.Money < Fix64.FromInt(node.Cost)) break;
            ts.Money -= Fix64.FromInt(node.Cost);
            head.Started = true;
            head.TicksRemaining = node.ResearchTicks;
            q[0] = head;
            break;
        }
    }

    private void TickUnitQueue(int team, TeamState ts)
    {
        var q = ts.UnitQueue;
        if (q.Count > 0 && q[0].Started)
        {
            var head = q[0];
            head.TicksRemaining--;
            if (head.TicksRemaining == 0)
            {
                q.RemoveAt(0);
                SpawnProduced(team, ts, head.Idx);
            }
            else q[0] = head;
        }
        if (q.Count > 0 && !q[0].Started)
        {
            var head = q[0];
            var proto = World.Content.Units[head.Idx];
            if (PrereqsMet(ts, proto.PrereqTechIdx)
                && ObjectPrereqsMet(team, proto)
                && CanProduce(team, proto)
                && ts.Money >= Fix64.FromInt(proto.Cost))
            {
                ts.Money -= Fix64.FromInt(proto.Cost);
                head.Started = true;
                head.TicksRemaining = proto.BuildTicks;
                q[0] = head;
            }
        }
    }

    /// <summary>
    /// Structural gates on starting a purchase, checked in a fixed order so the reason a
    /// queue stalls is deterministic.
    ///
    /// This is where structures stop being decoration: a team with no live factory cannot
    /// start a unit, so the factory is a target worth killing. CLAUDE.md records the gap this
    /// closes — "rushes can only spawn-camp, there are no economic targets".
    /// </summary>
    private bool CanProduce(int team, UnitProto proto)
    {
        // Opt-in by content: a pack that declares no factory at all keeps the original
        // semantics exactly, so every existing pack and every pinned hash is untouched.
        // The gate switches on the moment a pack introduces its first FS_FACTORY.
        if (World.Content.HasFactories
            && !proto.IsStructure
            && World.AliveWithKind(team, KindOf.Factory) == 0)
            return false;

        // Brownout: a net-negative grid halts production. One integer, exactly as ZH does it.
        // Same opt-in rule — no pack that ignores power can be affected by power.
        if (World.Content.HasPower && World.PowerBalance(team) < 0) return false;

        if (proto.MaxSimultaneousOfType > 0
            && World.AliveOfProto(team, proto.SelfIdx) >= proto.MaxSimultaneousOfType)
            return false;

        return true;
    }

    private void SpawnProduced(int team, TeamState ts, int protoIdx)
    {
        // One purchase can yield several bodies (ZH: Redguard 2, Stinger Site 3). Ascending
        // loop, each body taking the next stagger slot, so the order is replay-stable.
        int n = World.Content.Units[protoIdx].UnitsPerPurchase;
        for (int i = 0; i < n; i++) SpawnOneProduced(team, ts, protoIdx);
    }

    private void SpawnOneProduced(int team, TeamState ts, int protoIdx)
    {
        // Deterministic exit stagger so successive units don't stack on one point.
        Fix64 y = ts.SpawnY + Fix64.FromInt(((ts.ProducedCount % 7) - 3) * 2);
        ts.ProducedCount++;
        int idx = World.Spawn(protoIdx, team, ts.SpawnX, y);
        if (ts.HasRally)
        {
            ref var u = ref World.Units[idx];
            u.RallyX = ts.RallyX;
            u.RallyY = ts.RallyY;
            u.HasRally = true;
        }
    }

    private static bool PrereqsMet(TeamState ts, int[] reqs)
    {
        foreach (int r in reqs)
            if (!ts.Researched[r]) return false;
        return true;
    }

    /// <summary>
    /// Every object prerequisite must be alive on this team RIGHT NOW. Unlike a researched
    /// tech flag, this is revocable: level the war factory and its units stop being buildable.
    /// That revocability is the whole point — it is what turns a building into a target.
    /// </summary>
    private bool ObjectPrereqsMet(int team, UnitProto proto)
    {
        foreach (int idx in proto.PrereqObjectIdx)
            if (World.AliveOfProto(team, idx) == 0) return false;
        return true;
    }

    /// <summary>
    /// Re-select conditional loadouts and re-resolve stats for anything whose flags changed.
    ///
    /// This phase exists because of a specific ordering hazard, recorded in CLAUDE.md before
    /// the slice was written: a flag change re-selects the loadout, which changes weapon and
    /// armor class, which are STATS. Previously stats were recomputed only on spawn and on
    /// rank change. Without a pinned phase, whether a shot used the old or new weapon would
    /// depend on which system happened to run first — order-dependent damage, and
    /// intermittent, because it depends on what fired that tick.
    ///
    /// It sits AFTER production (research completing this tick grants its flag this tick)
    /// and BEFORE cooldowns/targeting/combat (so everything downstream sees one consistent
    /// loadout for the whole tick). Ascending unit index, as ever.
    /// </summary>
    /// <summary>
    /// Occupancy and ownership: eviction, capture progress, and what a held building pays.
    ///
    /// Its own pinned phase between production and loadout, for the same reason the loadout
    /// phase exists. Capture flips a structure's TEAM, and team is read by targeting (who is
    /// an enemy), by production (whose factory this is) and by the defeat test. If ownership
    /// changed anywhere later, whether a shot this tick counted as friendly fire would depend
    /// on which system happened to run first. Resolve it once, up front, before anything reads
    /// a team.
    ///
    /// Deliberately NOT randomised: capture is a countdown, not a roll, so this phase adds no
    /// Pcg32 stream. Adding garrison therefore cannot shift a single existing combat roll.
    /// </summary>
    private void HoldingSystem()
    {
        var content = World.Content;
        if (!content.HasGarrison && !content.HasCapture) return;

        for (int i = 0; i < World.UnitCount; i++)
        {
            ref var u = ref World.Units[i];

            // A dead host turns its occupants out rather than killing them. ZH's rule is the
            // harsher one — the building collapses on them — but ours keeps the counter play
            // legible: flame the building, the infantry spill out and are shootable.
            if (!u.Alive)
            {
                if (content.HasGarrison) World.EvictGarrison(i);
                continue;
            }

            // An occupant whose host died in an earlier phase this tick is already out.
            if (u.GarrisonHost >= 0 && !World.Units[u.GarrisonHost].Alive) u.GarrisonHost = -1;

            var proto = content.Units[u.ProtoIdx];

            // --- capture ------------------------------------------------------------
            // A structure is claimed by the nearest live enemy standing on it, and only when
            // no unit of the owning team is there to contest. Ascending index breaks the tie,
            // as everywhere else.
            if (proto.CaptureTicks > 0)
            {
                int claimant = -1;
                bool contested = false;
                for (int j = 0; j < World.UnitCount; j++)
                {
                    ref readonly var c = ref World.Units[j];
                    if (!c.Alive || j == i || c.GarrisonHost >= 0) continue;
                    if (content.Units[c.ProtoIdx].IsStructure) continue;
                    if (DistSq(in u, in c) > CaptureRadiusSq) continue;
                    if (c.Team == u.Team) { contested = true; break; }
                    if (claimant < 0) claimant = c.Team;
                }

                if (contested || claimant < 0)
                {
                    u.CaptureProgress = 0;
                    u.CaptureBy = -1;
                }
                else
                {
                    if (u.CaptureBy != claimant) { u.CaptureBy = claimant; u.CaptureProgress = 0; }
                    if (++u.CaptureProgress >= proto.CaptureTicks)
                    {
                        u.Team = claimant;
                        u.CaptureProgress = 0;
                        u.CaptureBy = -1;
                        // Occupants belonged to the previous owner; they do not change sides.
                        if (content.HasGarrison) World.EvictGarrison(i);
                    }
                }
            }

            // --- deposit ------------------------------------------------------------
            // ZH's AutoDepositUpdate: a flat sum on a fixed period, paid to whoever owns the
            // building now. Money is credited directly and does NOT draw down the supply
            // pool — a captured derrick is new income, which is exactly why it is worth
            // taking rather than a faster way to spend a shared pool.
            if (proto.DepositAmount > 0 && proto.DepositTicks > 0 && (uint)u.Team < World.TeamCount)
            {
                if (--u.DepositCountdown <= 0)
                {
                    u.DepositCountdown = proto.DepositTicks;
                    World.Teams[u.Team].Money += Fix64.FromInt(proto.DepositAmount);
                }
            }
        }
    }

    /// <summary>
    /// How close a unit must be to claim a structure: 2 world units, squared. Not arbitrary —
    /// formation spacing in every scenario here is 3 units, so a radius under 1.5 can never
    /// be reached by anything but the one unit that spawned on the spot. It is also the right
    /// order for a building: the compiler scales our world by 16 for ZH, where retail
    /// structure footprints run 13-40, i.e. roughly 1-2.5 of our units.
    /// </summary>
    private static readonly Fix64 CaptureRadiusSq = Fix64.FromInt(4);

    private void LoadoutSystem()
    {
        if (!World.Content.HasFlags) return;

        // Researched tech grants a same-named team flag. Doing this here rather than at
        // research completion keeps the grant and its consequences in one phase.
        for (int team = 0; team < World.TeamCount; team++)
        {
            var ts = World.Teams[team];
            for (int t = 0; t < ts.Researched.Length; t++)
            {
                if (!ts.Researched[t]) continue;
                if (World.Content.Flags.TryBit(World.Content.Tech[t].Id, out int bit))
                    ts.Flags |= 1UL << bit;
            }
        }

        for (int i = 0; i < World.UnitCount; i++)
        {
            if (!World.Units[i].Alive) continue;
            if (World.SelectLoadout(i)) World.RecomputeStats(i);
        }
    }

    private void TickCooldowns()
    {
        for (int i = 0; i < World.UnitCount; i++)
        {
            ref var u = ref World.Units[i];
            if (u.Alive && u.CooldownRemaining > 0) u.CooldownRemaining--;
        }
        // Power recharge shares this phase with weapon cooldown because it is the same kind
        // of clock, and teams tick in ascending index like everything else.
        if (!World.Content.HasPowers) return;
        for (int t = 0; t < World.TeamCount; t++)
        {
            var cd = World.Teams[t].PowerCooldown;
            for (int p = 0; p < cd.Length; p++) if (cd[p] > 0) cd[p]--;
        }
    }

    /// <summary>
    /// Keep current target while alive and inside acquire radius; otherwise pick the
    /// nearest live enemy by (squared distance, unit index) — the index tie-break is
    /// what keeps target choice deterministic when distances are equal.
    /// </summary>
    /// <summary>
    /// Can a see b? True whenever the pack has no sight-blocking terrain, which is what keeps
    /// this feature free for every pack that does not use it — and keeps every pinned replay
    /// bit-identical.
    /// </summary>
    private bool CanSee(in UnitState a, in UnitState b)
    {
        if (!World.Content.HasLineOfSight) return true;
        var grid = World.Content.Map;
        return grid is null || grid.HasLineOfSight(a.X, a.Y, b.X, b.Y);
    }

    private void TargetingSystem()
    {
        for (int i = 0; i < World.UnitCount; i++)
        {
            ref var u = ref World.Units[i];
            if (!u.Alive) continue;
            // Effective weapon: a variant that swaps the gun also changes acquire range,
            // so targeting must agree with combat about which weapon is in force.
            var weapon = World.Content.Weapons[World.EffectiveWeaponIdx(i)];

            // An occupant is never a target in its own right — you shoot the BUILDING. That
            // is what AllowAttackGarrisonedBldgs means in ZH: the weapon is allowed to engage
            // a garrisoned structure, and the damage reaches the people inside (which is why
            // ImmuneToClearBuildingAttacks has to exist as an opt-out). Ordinary weapons hit
            // the building and the occupants are untouched; the 17 weapons of 363 that clear
            // — flame, toxin, flashbang, sniper, C4 — hit both. See ApplyDamage.
            if (u.TargetIdx >= 0)
            {
                ref readonly var t = ref World.Units[u.TargetIdx];
                if (t.Alive && t.GarrisonHost < 0 && DistSq(in u, in t) <= weapon.AcquireRangeSq
                    && CanSee(in u, in t)) continue;
                u.TargetIdx = -1;
            }

            Fix64 bestDist = Fix64.MaxValue;
            int best = -1;
            int cand = Candidates(u.X, u.Y, weapon.AcquireRange);
            for (int c = 0; c < cand; c++)
            {
                int j = _candidates[c];
                if (j == i) continue;
                ref readonly var e = ref World.Units[j];
                if (!e.Alive || e.Team == u.Team) continue;
                if (e.GarrisonHost >= 0) continue;
                Fix64 d = DistSq(in u, in e);
                // EXPLICIT total order on (distSq, unitIdx). The ascending scan plus a strict
                // `<` already produced exactly this, so writing it out moves no hash — but it
                // is what lets a broad phase visit candidates in any order it likes without
                // changing which target is chosen.
                if (d > weapon.AcquireRangeSq) continue;
                // LOS is tested LAST, after range and team: it is the only test here that
                // walks the grid, so every candidate a cheap test can reject should already
                // be gone.
                if (!CanSee(in u, in e)) continue;
                if (d < bestDist || (d == bestDist && j < best) || best < 0)
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
    /// No collision or steering — units pass through each other; only TERRAIN stops them.
    /// </summary>
    private void MovementSystem()
    {
        for (int i = 0; i < World.UnitCount; i++)
        {
            ref var u = ref World.Units[i];
            if (!u.Alive) continue;
            // An occupant holds the building's position and does not chase. Letting it walk
            // out to close range would quietly undo the immunity it just gained.
            if (u.GarrisonHost >= 0) continue;
            if (World.RepathCooldown.Length > 0 && World.RepathCooldown[i] > 0)
                World.RepathCooldown[i]--;
            Fix64 speed = World.Resolved(i, Stat.Speed);

            if (u.TargetIdx >= 0)
            {
                ref readonly var t = ref World.Units[u.TargetIdx];
                Fix64 range = World.Resolved(i, Stat.Range);
                if (DistSq(in u, in t) > range * range)
                    Advance(i, ref u, t.X, t.Y, speed);
            }
            else if (u.HasRally)
            {
                Fix64 dx = u.RallyX - u.X, dy = u.RallyY - u.Y;
                Fix64 dsq = dx * dx + dy * dy;
                if (dsq <= Fix64.Half * Fix64.Half) u.HasRally = false;
                else Advance(i, ref u, u.RallyX, u.RallyY, speed);
            }
        }
    }

    /// <summary>
    /// One step toward a world goal, around terrain if terrain is in the way.
    ///
    /// <para>The three cases are ordered by how much they cost, and the cheapest one is also
    /// what makes the whole feature opt-in: with no impassable terrain the direct line is
    /// always clear, so this is the same straight step the sim took before pathing existed
    /// and every pinned hash is untouched. It is not a fast path bolted onto a pathfinder —
    /// it is the pathfinder declining to run when there is nothing to path around.</para>
    /// </summary>
    private void Advance(int i, ref UnitState u, Fix64 gx, Fix64 gy, Fix64 speed)
    {
        var mask = World.Content.Units[u.ProtoIdx].Surfaces;
        var grid = World.Content.Map;
        if (grid is null || !World.Content.HasPassability)
        {
            StepToward(ref u, gx, gy, speed, mask);
            return;
        }

        // Air does not consult the grid at all. That is their model, not a shortcut: an
        // AIR locomotor is exempt rather than universally permitted, so a map needs no
        // flyable cells authored into it for aircraft to work.
        if ((mask & SurfaceMask.Air) != 0 || grid.LineWalkable(u.X, u.Y, gx, gy, mask))
        {
            InvalidatePath(i);
            StepToward(ref u, gx, gy, speed, mask, lineKnownClear: true);
            return;
        }

        int goalCell = CellOf(grid, gx, gy);
        if (World.PathGoalCell[i] != goalCell || World.PathCursor[i] >= World.PathLen[i])
        {
            // A route is only recomputed on a cadence. Chasing a moving body changes the
            // goal cell on most ticks, and one search per unit per tick is what turns a
            // 2,000-unit battle from seconds into minutes. Between recomputes the unit walks
            // the plan it has, which is also what a real one would do.
            if (World.RepathCooldown[i] > 0 && World.PathCursor[i] < World.PathLen[i])
            {
                FollowPath(i, ref u, gx, gy, speed, mask);
                return;
            }
            Repath(i, ref u, grid, mask, gx, gy, goalCell);
        }
        FollowPath(i, ref u, gx, gy, speed, mask);
    }

    private static int CellOf(PassabilityGrid g, Fix64 x, Fix64 y)
    {
        int cx = g.CellX(x), cy = g.CellY(y);
        return g.InBounds(cx, cy) ? cy * g.Width + cx : -1;
    }

    private void InvalidatePath(int i)
    {
        World.PathLen[i] = 0;
        World.PathCursor[i] = 0;
        World.PathGoalCell[i] = -1;
    }

    private void Repath(int i, ref UnitState u, PassabilityGrid grid, SurfaceMask mask,
                        Fix64 gx, Fix64 gy, int goalCell)
    {
        int written = _path!.FindPath(grid.CellX(u.X), grid.CellY(u.Y),
                                      grid.CellX(gx), grid.CellY(gy), mask,
                                      World.PathCells, i * World.PathCapacity, World.PathCapacity);
        World.PathLen[i] = written;
        World.PathCursor[i] = 0;
        World.PathGoalCell[i] = written > 0 ? goalCell : -1;
        World.RepathCooldown[i] = World.RepathInterval;
    }

    /// <summary>
    /// Walk the stored route. A unit that has no route still moves — straight at the goal,
    /// wall or no wall. That is deliberate: an unreachable goal is a CONTENT bug (a walled-in
    /// spawn, an island with no bridge), and the same rule applies as to the death-rule
    /// cascade — it degrades one unit's movement, it does not freeze a sweep and it does not
    /// throw. A unit pressed against a wall is visible; a unit that vanished from the
    /// simulation's attention is not.
    /// </summary>
    private void FollowPath(int i, ref UnitState u, Fix64 gx, Fix64 gy, Fix64 speed, SurfaceMask mask)
    {
        if (World.PathCursor[i] >= World.PathLen[i])
        {
            StepToward(ref u, gx, gy, speed, mask);
            return;
        }

        var grid = World.Content.Map!;
        grid.CellCentre(World.PathCells[i * World.PathCapacity + World.PathCursor[i]],
                        out Fix64 wx, out Fix64 wy);
        Fix64 dx = wx - u.X, dy = wy - u.Y;
        // Arrive within half a cell rather than exactly: a waypoint is the CENTRE of a cell
        // the unit only has to enter, and demanding the exact point makes a unit whose speed
        // overshoots it circle the centre forever.
        Fix64 arrive = grid.CellSize * Fix64.Half;
        if (dx * dx + dy * dy <= arrive * arrive)
        {
            World.PathCursor[i]++;
            if (World.PathCursor[i] >= World.PathLen[i])
            {
                StepToward(ref u, gx, gy, speed, mask);
                return;
            }
            grid.CellCentre(World.PathCells[i * World.PathCapacity + World.PathCursor[i]],
                            out wx, out wy);
        }
        StepToward(ref u, wx, wy, speed, mask);
    }

    /// <summary>
    /// One step of at most <paramref name="speed"/>, refused where terrain refuses it.
    ///
    /// <para><b>The refusal is the load-bearing half, not the pathfinder.</b> A route is
    /// advice; without a check at the step a unit whose search failed — walled-in spawn,
    /// island with no bridge, cap exhausted — simply walks through the wall, and the whole
    /// feature is decorative. That was the first version, and a SOLID barrier with no gap in
    /// it changed the outcome of a battle by nothing at all.</para>
    ///
    /// <para>Blocked movement SLIDES rather than stopping: full step, then x alone, then y
    /// alone, in that fixed order. A unit that halts dead against a wall it is merely brushing
    /// looks broken and, worse, stops contributing to a battle for reasons no state hash
    /// explains. Sliding is also what makes the no-route fallback honest — the unit presses
    /// along the barrier looking for a way round instead of pretending it is not there.</para>
    /// </summary>
    /// <param name="lineKnownClear">The caller already proved the whole segment to
    /// (<paramref name="tx"/>, <paramref name="ty"/>) walkable. A step along it lands on that
    /// segment, and the cells a sub-segment touches are a subset of the cells the whole one
    /// does, so re-walking them is pure waste — and it is the COMMON case, since the direct
    /// line is clear for most units on most ticks even on a map full of walls.</param>
    private void StepToward(ref UnitState u, Fix64 tx, Fix64 ty, Fix64 speed, SurfaceMask mask,
                            bool lineKnownClear = false)
    {
        Fix64 dx = tx - u.X, dy = ty - u.Y;
        Fix64 len = Fix64.Sqrt(dx * dx + dy * dy);
        Fix64 nx, ny;
        if (len <= speed) { nx = tx; ny = ty; }
        else { nx = u.X + dx * speed / len; ny = u.Y + dy * speed / len; }

        var grid = World.Content.Map;
        if (lineKnownClear || grid is null || !World.Content.HasPassability
            || (mask & SurfaceMask.Air) != 0)
        {
            u.X = nx;
            u.Y = ny;
            return;
        }

        if (grid.LineWalkable(u.X, u.Y, nx, ny, mask)) { u.X = nx; u.Y = ny; }
        else if (grid.LineWalkable(u.X, u.Y, nx, u.Y, mask)) u.X = nx;
        else if (grid.LineWalkable(u.X, u.Y, u.X, ny, mask)) u.Y = ny;
        // Wedged into a corner: no move. Deliberately not an error — terrain that traps a
        // unit is a content bug for lint to find, not a reason to abort a sweep.
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
            Fix64 dsq = DistSq(in u, in t);
            if (dsq > range * range) continue;
            // Re-tested at fire time, not merely at acquisition. Targeting runs before
            // movement, so by the time the shot goes off either party may have stepped behind
            // the wall — and a unit that keeps firing through cover it has already lost is
            // precisely the bug this slice exists to prevent.
            if (!CanSee(in u, in t)) continue;

            var proto = content.Units[u.ProtoIdx];
            // Effective, not prototype: a conditional variant may have swapped the weapon.
            var weapon = content.Weapons[World.EffectiveWeaponIdx(i)];

            // Artillery's defining weakness: too close and it cannot depress.
            if (weapon.MinRangeSq > Fix64.Zero && dsq < weapon.MinRangeSq) continue;

            // Clips. -1 is the uninitialised sentinel, so a unit's first shot fills the
            // magazine from its prototype rather than needing a spawn-time hook.
            if (weapon.ClipSize > 0)
            {
                if (u.ClipRemaining < 0) u.ClipRemaining = weapon.ClipSize;
                if (u.ClipRemaining == 0)
                {
                    // Empty: pay the long reload once, then refill.
                    u.CooldownRemaining = Math.Max(1, weapon.ClipReloadTicks);
                    u.ClipRemaining = weapon.ClipSize;
                    continue;
                }
                u.ClipRemaining--;
            }

            Fix64 spreadMult = Fix64.One;
            if (weapon.Spread > Fix64.Zero)
            {
                // uniform in [1 - spread, 1 + spread)
                Fix64 frac = _rngCombat.NextFraction01();
                spreadMult = Fix64.One - weapon.Spread + Fix64.Two * weapon.Spread * frac;
            }

            Fix64 baseDmg = World.Resolved(i, Stat.Damage) * spreadMult;
            u.CooldownRemaining = (World.Resolved(i, Stat.CooldownScale) * weapon.CooldownTicks).CeilToInt();

            if (!weapon.HasSplash)
            {
                ApplyDamage(i, u.TargetIdx, baseDmg, weapon);
            }
            else
            {
                // Two-band step function centred on the TARGET, no falloff — ZH's model
                // exactly (Weapon.cpp:1462). Ascending index, and the victim list is
                // resolved against positions as they were before this shot, so ordering
                // inside one blast cannot depend on who died first.
                Fix64 secondary = weapon.SecondaryDamage * spreadMult;
                Fix64 cx = t.X, cy = t.Y;
                int blast = Candidates(cx, cy, weapon.SplashRadius);
                for (int b = 0; b < blast; b++)
                {
                    int v = _candidates[b];
                    ref readonly var victim = ref World.Units[v];
                    if (!victim.Alive) continue;
                    // Splash never reaches an occupant directly either, or a blast beside a
                    // held building would kill people a direct shot cannot touch. A clearing
                    // weapon still gets them, through the host, in ApplyDamage.
                    if (victim.GarrisonHost >= 0) continue;
                    Fix64 dx = victim.X - cx, dy = victim.Y - cy;
                    Fix64 d2 = dx * dx + dy * dy;

                    if (d2 <= weapon.PrimaryRadiusSq) ApplyDamage(i, v, baseDmg, weapon);
                    else if (d2 <= weapon.SecondaryRadiusSq && secondary > Fix64.Zero)
                        ApplyDamage(i, v, secondary, weapon);
                }
            }
        }
    }

    /// <summary>
    /// Apply one damage instance: armor-class multiplier, the victim's own armor factor,
    /// then death and XP. Splash routes every victim through here, so a blast and a single
    /// shot resolve damage by identical rules — friendly fire included, deliberately: ZH's
    /// RadiusDamageAffects defaults to hitting everything, and pretending otherwise would
    /// make splash strictly better than single-target.
    /// </summary>
    private void ApplyDamage(int attackerIdx, int victimIdx, Fix64 amount, WeaponDef weapon)
    {
        ref var victim = ref World.Units[victimIdx];
        if (!victim.Alive) return;

        var content = World.Content;
        // Effective armor class too — an upgrade that swaps armor must change what the
        // damage table looks up, which is precisely how ZH's ArmorUpgrade works.
        Fix64 typeMult = content.DamageVsArmor[weapon.DamageTypeIdx, World.EffectiveArmorIdx(victimIdx)];
        victim.Hp -= amount * typeMult * World.Resolved(victimIdx, Stat.ArmorFactor);

        if (victim.Hp <= Fix64.Zero)
        {
            victim.Alive = false;
            AwardKillXp(attackerIdx, content.Units[victim.ProtoIdx].Cost);
            RaiseDeath(victimIdx, depth: 0);
        }

        // Clearing a building: the same blow that hits the structure hits everyone inside.
        // This is the ONLY way damage reaches an occupant, which is what makes garrison a
        // position rather than a hiding place — and it is ZH's own model, where the weapon
        // engages the BUILDING and the occupants take it.
        //
        // Occupants are visited in ascending index and resolved against the host's state as
        // it was before this blow, so whether the host died to this same shot cannot change
        // who inside was hurt.
        if (weapon.ClearsGarrison && content.HasGarrison
            && content.Units[victim.ProtoIdx].GarrisonCapacity > 0)
        {
            for (int o = 0; o < World.UnitCount; o++)
            {
                if (World.Units[o].GarrisonHost != victimIdx || !World.Units[o].Alive) continue;
                ApplyDamage(attackerIdx, o, amount, weapon);
            }
        }
    }

    /// <summary>
    /// Record a death for rule processing. Position and flags are captured NOW because the
    /// slot is a tombstone by the time rules run, and a wreck must appear where the thing
    /// actually died rather than wherever the slot is later reused.
    /// </summary>
    private void RaiseDeath(int unitIdx, int depth)
    {
        if (!World.Content.HasRules) return;
        ref readonly var u = ref World.Units[unitIdx];
        if (World.Content.Units[u.ProtoIdx].Rules.Length == 0) return;
        _deaths.Add(new PendingDeath(u.ProtoIdx, u.Team, u.X, u.Y, u.Flags, depth));
    }

    /// <summary>
    /// Drain the death queue, firing each dead unit's matching rules.
    ///
    /// Runs after combat so that every death this tick is known before any rule fires —
    /// otherwise a blast that kills three units would process the first one's explosion
    /// while the other two were still notionally alive, and the outcome would depend on
    /// index order within the blast.
    ///
    /// Cascades are bounded by <see cref="MaxDeathCascadeDepth"/> generations. The loop
    /// consumes a growing list by index so deaths caused BY rules are processed in the same
    /// phase, in the order they were raised.
    /// </summary>
    private void DeathRuleSystem()
    {
        if (_deaths.Count == 0) return;

        var content = World.Content;
        for (int d = 0; d < _deaths.Count; d++)
        {
            var death = _deaths[d];
            if (death.Depth >= MaxDeathCascadeDepth) continue;   // clamp, do not recurse

            var proto = content.Units[death.ProtoIdx];
            ulong effective = death.Flags | World.Teams[death.Team].Flags;

            foreach (var rule in proto.Rules)
            {
                if (rule.On != RuleEvent.Death) continue;
                if ((rule.RequiredFlags & ~effective) != 0) continue;

                foreach (var e in rule.Effects)
                    ApplyEffect(e, in death);
            }
        }
        _deaths.Clear();
    }

    private void ApplyEffect(EffectDef e, in PendingDeath death)
    {
        switch (e.Kind)
        {
            case EffectKind.Spawn:
            {
                if (e.ProtoIdx < 0) return;
                for (int n = 0; n < e.Count; n++)
                {
                    Fix64 x = death.X, y = death.Y;
                    if (e.Spread > Fix64.Zero)
                    {
                        // Own RNG stream: adding a wreck must not shift combat's rolls.
                        Fix64 fx = _rngSpawn.NextFraction01() * Fix64.Two - Fix64.One;
                        Fix64 fy = _rngSpawn.NextFraction01() * Fix64.Two - Fix64.One;
                        x += fx * e.Spread;
                        y += fy * e.Spread;
                    }
                    if (World.UnitCount >= World.MaxUnits) return;   // clamp, never throw
                    World.Spawn(e.ProtoIdx, death.Team, x, y);
                }
                return;
            }
            case EffectKind.GrantMoney:
                World.Teams[death.Team].Money += Fix64.FromInt(e.Amount);
                return;

            case EffectKind.DamageInRadius:
            {
                if (e.WeaponIdx < 0) return;
                var weapon = World.Content.Weapons[e.WeaponIdx];
                // Hits everything in range including the owner's own units — a death
                // explosion that spared allies would be strictly free.
                for (int v = 0; v < World.UnitCount; v++)
                {
                    ref readonly var victim = ref World.Units[v];
                    if (!victim.Alive) continue;
                    Fix64 dx = victim.X - death.X, dy = victim.Y - death.Y;
                    if (dx * dx + dy * dy > e.RadiusSq) continue;
                    DamageFromRule(v, weapon, death.Depth);
                }
                return;
            }
            case EffectKind.GrantFlag:
                if (e.FlagBit >= 0) World.Teams[death.Team].Flags |= 1UL << e.FlagBit;
                return;
        }
    }

    /// <summary>
    /// Rule-sourced damage. Separate from <see cref="ApplyDamage"/> because there is no
    /// attacker to award XP to — a wreck's explosion credits nobody — and because the
    /// resulting death must carry the incremented cascade depth.
    /// </summary>
    private void DamageFromRule(int victimIdx, WeaponDef weapon, int depth)
    {
        ref var victim = ref World.Units[victimIdx];
        if (!victim.Alive) return;

        var content = World.Content;
        Fix64 typeMult = content.DamageVsArmor[weapon.DamageTypeIdx, World.EffectiveArmorIdx(victimIdx)];
        victim.Hp -= weapon.Damage * typeMult * World.Resolved(victimIdx, Stat.ArmorFactor);

        if (victim.Hp <= Fix64.Zero)
        {
            victim.Alive = false;
            RaiseDeath(victimIdx, depth + 1);
        }
    }

    /// <summary>
    /// Fire an activated power at a point. Gated on the team flag a science grants and on a
    /// recharge, then it runs the SAME closed effect vocabulary the death rules use — which
    /// is the payoff for keeping that vocabulary closed and its parameters open: a new
    /// trigger costs one method, not a new effect system.
    ///
    /// Effects originate from a synthetic PendingDeath at depth 0: it is really "an effect
    /// origin" (team, position, flags), and a power is one that no corpse produced. Depth 0
    /// means a power's spawns are bounded by exactly the same 4-generation cascade clamp.
    /// </summary>
    private void FirePower(int team, int powerIdx, Fix64 x, Fix64 y)
    {
        var content = World.Content;
        if ((uint)team >= World.TeamCount || (uint)powerIdx >= (uint)content.Powers.Length) return;
        var ts = World.Teams[team];
        if (ts.PowerCooldown[powerIdx] > 0) return;
        var power = content.Powers[powerIdx];
        if ((power.RequiresFlags & ~ts.Flags) != 0) return;   // science not owned

        ts.PowerCooldown[powerIdx] = power.RechargeTicks;
        var origin = new PendingDeath(-1, team, x, y, ts.Flags, depth: 0);
        foreach (var e in power.Effects) ApplyEffect(e, in origin);
    }

    /// <summary>
    /// Candidate unit indices within <paramref name="radius"/> of a point, ASCENDING.
    ///
    /// Falls back to the full unit table whenever the broad phase cannot serve the query —
    /// index disabled, or the candidate buffer too small. The fallback is the original scan,
    /// so a fallback is slow but never wrong; the buffer then grows so the next query of the
    /// same shape is served by the grid. Returning a truncated set instead would be a desync.
    /// </summary>
    private int Candidates(Fix64 x, Fix64 y, Fix64 radius)
    {
        if (UseSpatialIndex)
        {
            int n = _grid.Query(x, y, radius, _candidates);
            if (n >= 0) return n;
            _candidates = new int[Math.Max(_candidates.Length * 2, World.UnitCount)];
            n = _grid.Query(x, y, radius, _candidates);
            if (n >= 0) return n;
        }
        if (_candidates.Length < World.UnitCount) _candidates = new int[World.UnitCount];
        for (int i = 0; i < World.UnitCount; i++) _candidates[i] = i;
        return World.UnitCount;
    }

    private void AwardKillXp(int attackerIdx, int victimCost)
    {
        ref var a = ref World.Units[attackerIdx];
        // The commander is paid first and unconditionally. ZH's default is that a thing's
        // skill-point value IS its experience value — no retail object overrides it — so the
        // same number feeds both ladders, but the PLAYER's ladder does not depend on whether
        // the killer happens to carry a veterancy track.
        World.AwardSkillPoints(a.Team, victimCost / 10);

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
