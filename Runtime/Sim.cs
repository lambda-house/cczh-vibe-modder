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
///   commands → production/economy → loadout/stat re-resolve → cooldowns →
///   targeting → movement → combat/deaths/veterancy → hash
/// Production sits right after commands so an enqueue issued this tick can start
/// building this tick, and a unit finished this tick acts (cooldown/targeting/
/// movement/combat) this tick — the same treatment command-spawned units get.
/// The loadout phase sits between production and cooldowns because a flag gained this
/// tick (research completing, an upgrade landing) re-selects the conditional loadout,
/// which changes weapon and armor class — i.e. STATS. Resolving it in its own pinned
/// phase is what stops damage depending on which system happened to run first.
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
        LoadoutSystem();
        TickCooldowns();
        TargetingSystem();
        MovementSystem();
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
            // Effective weapon: a variant that swaps the gun also changes acquire range,
            // so targeting must agree with combat about which weapon is in force.
            var weapon = World.Content.Weapons[World.EffectiveWeaponIdx(i)];

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
            Fix64 dsq = DistSq(in u, in t);
            if (dsq > range * range) continue;

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
                for (int v = 0; v < World.UnitCount; v++)
                {
                    ref readonly var victim = ref World.Units[v];
                    if (!victim.Alive) continue;
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
