using RtsSkeleton.Content;
using RtsSkeleton.Core;
using RtsSkeleton.Runtime;

namespace RtsSkeleton.Harness;

public sealed class DuelStats
{
    public required string A;
    public required string B;
    public int Runs;
    public int WinsA;
    public int WinsB;
    public int Draws;
    public double AvgTicks;
    public ulong LastFinalHash;
}

/// <summary>A hold run, plus what happened to the contested prize. Separate from DuelStats
/// because ownership and payout are only meaningful in this scenario.</summary>
public sealed class HoldStats
{
    public required DuelStats Duel;
    /// <summary>Team owning the capturable prize at the end of the last run, or -1 if none.</summary>
    public int PrizeOwner = -1;
    /// <summary>Money each team finished the last run with — the deposit shows up here.</summary>
    public double MoneyA, MoneyB;
}

public sealed class EconStats
{
    public required string OrderA;
    public required string OrderB;
    public int Runs;
    public int WinsA;
    public int WinsB;
    public int Draws;
    public double AvgTicks;
    /// <summary>Money + undrawn pool + fielded army value at battle end; the
    /// economic-damage metric that makes tick-cap draws readable.</summary>
    public double AvgNetWorthA;
    public double AvgNetWorthB;
    public ulong LastFinalHash;
    public bool DeterminismOk;
    /// <summary>Units alive at the end, averaged. Distinguishes "never built" from
    /// "built and died" — net worth alone cannot, because it counts money and army
    /// together and so stays flat whether you spend or not.</summary>
    public double AvgAliveA;
    public double AvgAliveB;
    /// <summary>Why each side's queue stalled, if it did. Silence here is the dangerous case.</summary>
    public string? StallA;
    public string? StallB;
}

/// <summary>
/// The batch-evaluation side of the AI loop. Every scenario is a pure function of
/// (content, seed, parameters) producing metrics — which is exactly the shape an
/// MCP tool wants: run_matchup(content_hash, seed, armies) → JSON. The CLI verbs
/// in Program.cs are those tools minus the transport.
/// </summary>
public static class Scenarios
{
    public const int DefaultMaxTicks = ContentDb.TicksPerSecond * 180; // 3 min cap

    /// <summary>
    /// Builds the command log for a symmetric line battle: each side fields
    /// floor(budget / cost) units on opposing lines, then attack-moves through the
    /// enemy spawn. Cost normalization is what makes pairwise win rates comparable.
    /// </summary>
    public static List<Command> BuildDuelCommands(ContentDb content, int protoA, int protoB, int budget)
    {
        var log = new List<Command>();
        int seq = 0;
        SpawnLine(content, log, ref seq, protoA, team: 0, xLine: -40, budget);
        SpawnLine(content, log, ref seq, protoB, team: 1, xLine: +40, budget);
        log.Add(new Command(0, seq++, CommandKind.RallyTeam, 0, 0, Fix64.FromInt(40), Fix64.Zero));
        log.Add(new Command(0, seq++, CommandKind.RallyTeam, 1, 0, Fix64.FromInt(-40), Fix64.Zero));
        return log;
    }

    private static void SpawnLine(ContentDb content, List<Command> log, ref int seq, int proto, int team, int xLine, int budget)
    {
        int count = Math.Max(1, budget / content.Units[proto].Cost);
        for (int i = 0; i < count; i++)
        {
            // Deterministic formation: centered column, 3 units spacing.
            Fix64 y = Fix64.FromRatio(2 * i - (count - 1), 2) * 3;
            log.Add(new Command(0, seq++, CommandKind.Spawn, proto, team, Fix64.FromInt(xLine), y));
        }
    }

    public static DuelStats RunDuelSeries(ContentDb content, string a, string b, int budget, int runs, ulong baseSeed, int maxTicks = DefaultMaxTicks)
    {
        int pa = content.UnitIndexById[a];
        int pb = content.UnitIndexById[b];
        var log = BuildDuelCommands(content, pa, pb, budget);

        var stats = new DuelStats { A = a, B = b, Runs = runs };
        long tickSum = 0;
        for (int r = 0; r < runs; r++)
        {
            var sim = new Sim(content, baseSeed + (ulong)r, log);
            var result = sim.Run(maxTicks);
            switch (result.WinnerTeam)
            {
                case 0: stats.WinsA++; break;
                case 1: stats.WinsB++; break;
                default: stats.Draws++; break;
            }
            tickSum += result.Ticks;
            stats.LastFinalHash = result.FinalHash;
        }
        stats.AvgTicks = (double)tickSum / runs;
        return stats;
    }

    /// <summary>
    /// A held position: team 0 puts infantry inside a garrisonable structure, team 1 attacks.
    ///
    /// <paramref name="garrison"/> is an ABLATION SWITCH rather than a second content file.
    /// Same pack, same seeds, same spawn positions, same budget — the only difference is
    /// whether the Garrison commands are issued. A win-rate on its own cannot distinguish
    /// "the building helped" from "rifles beat technicals anyway"; this can.
    /// </summary>
    public static HoldStats RunHoldSeries(ContentDb content, string host, string holder, string attacker,
                                          int budget, int runs, ulong baseSeed, bool garrison,
                                          string? prize = null, int maxTicks = DefaultMaxTicks)
    {
        int ph = content.UnitIndexById[host];
        int pi = content.UnitIndexById[holder];
        int pa = content.UnitIndexById[attacker];

        var log = new List<Command>();
        int seq = 0;
        // The host takes unit index 0 because it spawns first, and every later index follows
        // from the spawn order. That is only sound because ApplyCommands consumes the log in
        // (tick, seq) order — the same property replays depend on.
        log.Add(new Command(0, seq++, CommandKind.Spawn, ph, 0, Fix64.FromInt(-40), Fix64.Zero));

        // Cost-normalised like every other scenario here: the defence spends the same
        // budget as the attack, so a win is about the POSITION and not about who was
        // handed more army. Capacity is the ceiling, the budget is usually the binding one.
        var proto = content.Units[ph];
        int affordable = (budget - proto.Cost) / Math.Max(1, content.Units[pi].Cost);
        int occupants = Math.Clamp(affordable, 1, Math.Max(1, proto.GarrisonCapacity));
        for (int i = 0; i < occupants; i++)
            log.Add(new Command(0, seq++, CommandKind.Spawn, pi, 0, Fix64.FromInt(-40), Fix64.Zero));
        if (garrison)
            for (int i = 0; i < occupants; i++)
                log.Add(new Command(0, seq++, CommandKind.Garrison, 1 + i, 0, Fix64.Zero, Fix64.Zero));

        // The prize is team 0's but stands in team 1's staging area, so team 1's units are
        // literally on top of it. Nothing scripts the capture — it happens because a unit is
        // standing there, and it beats the demolition it is racing: 5s to take versus the
        // ~15s six raiders need to chew through 2000 hitpoints.
        //
        // Placement matters and is the honest limit of this model. Capture is proximity-based
        // and attackers stop at WEAPON range, so a prize anywhere on the approach gets shelled
        // from 6 units away and never changes hands. A genuinely NEUTRAL owner would fix that,
        // and World.TeamCount is 2 — there is no third side to own it. Stated, not papered over.
        if (prize is not null)
            log.Add(new Command(0, seq++, CommandKind.Spawn, content.UnitIndexById[prize], 0,
                                Fix64.FromInt(40), Fix64.Zero));

        SpawnLine(content, log, ref seq, pa, team: 1, xLine: +40, budget);
        log.Add(new Command(0, seq++, CommandKind.RallyTeam, 1, 0, Fix64.FromInt(-40), Fix64.Zero));

        // Unit indices follow spawn order exactly: host, occupants, then the prize if any.
        int prizeUnit = prize is null ? -1 : 1 + occupants;

        var stats = new DuelStats { A = $"{holder}@{host}", B = attacker, Runs = runs };
        var hold = new HoldStats { Duel = stats };
        long tickSum = 0;
        for (int r = 0; r < runs; r++)
        {
            var sim = new Sim(content, baseSeed + (ulong)r, log);
            var result = sim.Run(maxTicks);
            if (prizeUnit >= 0 && prizeUnit < sim.World.UnitCount)
                hold.PrizeOwner = sim.World.Units[prizeUnit].Team;
            hold.MoneyA = sim.World.Teams[0].Money.ToDoubleForDisplay();
            hold.MoneyB = sim.World.Teams[1].Money.ToDoubleForDisplay();
            switch (result.WinnerTeam)
            {
                case 0: stats.WinsA++; break;
                case 1: stats.WinsB++; break;
                default: stats.Draws++; break;
            }
            tickSum += result.Ticks;
            stats.LastFinalHash = result.FinalHash;
        }
        stats.AvgTicks = (double)tickSum / runs;
        return hold;
    }

    /// <summary>Pairwise cost-normalized win-rate matrix over all prototypes: the counter table.</summary>
    public static List<DuelStats> RunMatrix(ContentDb content, int budget, int runsPerPair, ulong baseSeed)
    {
        var results = new List<DuelStats>();
        var ids = content.Units.Select(u => u.Id).ToArray(); // already ordinal-sorted at load
        for (int i = 0; i < ids.Length; i++)
        for (int j = 0; j < ids.Length; j++)
        {
            if (i == j) continue;
            // Distinct seed block per pair so pairs are independent experiments.
            ulong seed = baseSeed + (ulong)(i * ids.Length + j) * 1_000_003UL;
            results.Add(RunDuelSeries(content, ids[i], ids[j], budget, runsPerPair, seed));
        }
        return results;
    }

    public const int EconDefaultMaxTicks = ContentDb.TicksPerSecond * 600; // 10 min cap

    /// <summary>
    /// Expands a build-order spec into a flat id list. Spec is comma-separated
    /// unit/tech ids; `id*N` repeats N times; a trailing `id*` (units only)
    /// auto-fills to what the team's total resources can ever pay for. Unknown
    /// ids throw — build-order validation is a harness concern, the sim just
    /// stalls on orders it can't execute.
    /// </summary>
    public static string[] ExpandOrder(ContentDb content, string spec, int totalResources)
    {
        var result = new List<string>();
        var tokens = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string tok = tokens[i];
            int star = tok.IndexOf('*');
            string id = star >= 0 ? tok[..star] : tok;
            bool isUnit = content.UnitIndexById.ContainsKey(id);
            if (!isUnit && !content.TechIndexById.ContainsKey(id))
                throw new ArgumentException($"build order: unknown unit/tech id '{id}'");

            int repeat = 1;
            if (star >= 0)
            {
                string count = tok[(star + 1)..];
                if (count.Length == 0)
                {
                    if (i != tokens.Length - 1)
                        throw new ArgumentException($"build order: open repeat '{tok}' must be the last item");
                    if (!isUnit)
                        throw new ArgumentException($"build order: open repeat '{tok}' must name a unit");
                    repeat = Math.Max(1, totalResources / content.Units[content.UnitIndexById[id]].Cost);
                }
                else repeat = int.Parse(count);
            }
            for (int r = 0; r < repeat; r++) result.Add(id);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Command log for a macro battle: each side gets a finite economy, a spawn
    /// point on its own line, an attack-move rally through the enemy spawn
    /// (unless holding), and its entire build order enqueued at tick 0 — the
    /// stall-until-affordable queue rule turns that into a paced build order.
    /// </summary>
    /// <summary>
    /// Start two FACTIONS against each other, from their own definitions.
    ///
    /// Everything else in this harness starts from a build ORDER — a list of ids someone
    /// typed. This starts from the faction: its starting building is placed, its starting
    /// units are fielded, its starting money is applied, and its own roster supplies the
    /// production. That is the difference between "a roster" and "a playable side", and it
    /// is what lets an agent author a faction and immediately measure it rather than also
    /// having to author an opening for it.
    ///
    /// The build order is then generated FROM the roster: cheapest-first, repeated to the
    /// resource cap. Crude on purpose — a real opening is a later slice, and a fixed rule
    /// keeps faction-vs-faction results attributable to the factions.
    /// </summary>
    public static List<Command> BuildSkirmishCommands(ContentDb content, FactionDef a, FactionDef b,
        int startingMoney, int incomePerSecond, int supplyPool)
    {
        var log = new List<Command>();
        int seq = 0;
        AddSkirmishTeam(content, log, ref seq, 0, a, -40, startingMoney, incomePerSecond, supplyPool);
        AddSkirmishTeam(content, log, ref seq, 1, b, +40, startingMoney, incomePerSecond, supplyPool);
        return log;
    }

    private static void AddSkirmishTeam(ContentDb content, List<Command> log, ref int seq, int team,
        FactionDef f, int xSpawn, int money, int incomePerSecond, int pool)
    {
        int cash = f.StartMoney >= 0 ? f.StartMoney : money;
        log.Add(new Command(0, seq++, CommandKind.SetupEconomy, team, pool,
            Fix64.FromInt(cash), Fix64.FromRatio(incomePerSecond, ContentDb.TicksPerSecond)));
        log.Add(new Command(0, seq++, CommandKind.SetSpawn, team, 0, Fix64.FromInt(xSpawn), Fix64.Zero));
        log.Add(new Command(0, seq++, CommandKind.RallyTeam, team, 0, Fix64.FromInt(-xSpawn), Fix64.Zero));

        // The base goes down first and does not move, so it sits at the spawn point.
        if (f.StartingBuildingIdx >= 0)
            log.Add(new Command(0, seq++, CommandKind.Spawn, f.StartingBuildingIdx, team,
                                Fix64.FromInt(xSpawn), Fix64.Zero));

        // Starting units fan out deterministically around it.
        for (int i = 0; i < f.StartingUnitIdx.Length; i++)
            log.Add(new Command(0, seq++, CommandKind.Spawn, f.StartingUnitIdx[i], team,
                                Fix64.FromInt(xSpawn), Fix64.FromInt((i % 5 - 2) * 3)));

        // Production: cheapest buildable non-structure in the roster, repeated. Ordinal tie
        // break on the prototype id so two equally cheap units never race.
        int best = -1;
        foreach (int idx in f.OwnUnitIdx)
        {
            var p = content.Units[idx];
            if (p.IsStructure || p.Cost <= 0) continue;
            if (best < 0 || p.Cost < content.Units[best].Cost
                || (p.Cost == content.Units[best].Cost
                    && string.CompareOrdinal(p.Id, content.Units[best].Id) < 0))
                best = idx;
        }
        if (best < 0) return;

        // Research whatever that unit needs, first, in dependency order. Without this the
        // queue stalls forever on an unmet tech and the run reads as a dull draw — which is
        // exactly what QueueStallReason caught the first time this ran. A skirmish must be
        // self-sufficient from the faction definition alone, or "author a faction and
        // measure it" is not actually true.
        var need = new List<int>();
        void AddTech(int t)
        {
            if (need.Contains(t)) return;
            foreach (int dep in content.Tech[t].RequiresIdx) AddTech(dep);   // parents first
            need.Add(t);
        }
        foreach (int t in content.Units[best].PrereqTechIdx) AddTech(t);
        foreach (int t in need)
            log.Add(new Command(0, seq++, CommandKind.ResearchTech, team, t, Fix64.Zero, Fix64.Zero));

        int spend = cash + pool - need.Sum(t => content.Tech[t].Cost);
        int repeat = Math.Max(1, spend / content.Units[best].Cost);
        for (int r = 0; r < repeat; r++)
            log.Add(new Command(0, seq++, CommandKind.ProduceUnit, team, best, Fix64.Zero, Fix64.Zero));
    }

    public static List<Command> BuildEconCommands(ContentDb content, string[] orderA, string[] orderB,
        int startingMoney, int incomePerSecond, int supplyPool, bool holdA = false, bool holdB = false)
    {
        var log = new List<Command>();
        int seq = 0;
        AddEconTeam(content, log, ref seq, team: 0, orderA, xSpawn: -40, holdA, startingMoney, incomePerSecond, supplyPool);
        AddEconTeam(content, log, ref seq, team: 1, orderB, xSpawn: +40, holdB, startingMoney, incomePerSecond, supplyPool);
        return log;
    }

    private static void AddEconTeam(ContentDb content, List<Command> log, ref int seq, int team,
        string[] order, int xSpawn, bool hold, int money, int incomePerSecond, int pool)
    {
        log.Add(new Command(0, seq++, CommandKind.SetupEconomy, team, pool,
            Fix64.FromInt(money), Fix64.FromRatio(incomePerSecond, ContentDb.TicksPerSecond)));
        log.Add(new Command(0, seq++, CommandKind.SetSpawn, team, 0, Fix64.FromInt(xSpawn), Fix64.Zero));
        if (!hold)
            log.Add(new Command(0, seq++, CommandKind.RallyTeam, team, 0, Fix64.FromInt(-xSpawn), Fix64.Zero));
        foreach (var id in order)
            log.Add(content.UnitIndexById.TryGetValue(id, out int proto)
                ? new Command(0, seq++, CommandKind.ProduceUnit, team, proto, Fix64.Zero, Fix64.Zero)
                : new Command(0, seq++, CommandKind.ResearchTech, team, content.TechIndexById[id], Fix64.Zero, Fix64.Zero));
    }

    /// <summary>
    /// N-run build-order series. The first seed is run twice and compared — the
    /// determinism proof rides along with every econ experiment instead of being
    /// a separate verb, because production is new replay-contract surface.
    /// </summary>
    /// <summary>
    /// Faction vs faction, N runs, with the same determinism proof riding along. Shares
    /// <see cref="EconStats"/> so every reader of econ results reads these unchanged.
    /// </summary>
    public static EconStats RunSkirmishSeries(ContentDb content, string idA, string idB,
        int startingMoney, int incomePerSecond, int supplyPool,
        int runs, ulong baseSeed, int maxTicks = EconDefaultMaxTicks)
    {
        if (!content.Factions.TryGetValue(idA, out var fa))
            throw new ArgumentException($"unknown faction '{idA}'");
        if (!content.Factions.TryGetValue(idB, out var fb))
            throw new ArgumentException($"unknown faction '{idB}'");
        if (!fa.IsStartable) throw new ArgumentException($"faction '{idA}' has no startingBuilding or startingUnits");
        if (!fb.IsStartable) throw new ArgumentException($"faction '{idB}' has no startingBuilding or startingUnits");

        var log = BuildSkirmishCommands(content, fa, fb, startingMoney, incomePerSecond, supplyPool);

        var v1 = new Sim(content, baseSeed, log).Run(maxTicks);
        var v2 = new Sim(content, baseSeed, log).Run(maxTicks);
        bool deterministic = v1.FinalHash == v2.FinalHash && v1.Ticks == v2.Ticks;

        var stats = new EconStats { OrderA = idA, OrderB = idB, Runs = runs, DeterminismOk = deterministic };
        long tickSum = 0;
        for (int r = 0; r < runs; r++)
        {
            var sim = new Sim(content, baseSeed + (ulong)r, log);
            var result = sim.Run(maxTicks);
            switch (result.WinnerTeam)
            {
                case 0: stats.WinsA++; break;
                case 1: stats.WinsB++; break;
                default: stats.Draws++; break;
            }
            tickSum += result.Ticks;
            stats.AvgNetWorthA += NetWorth(sim.World, 0);
            stats.AvgNetWorthB += NetWorth(sim.World, 1);
            stats.AvgAliveA += sim.World.AliveCount(0);
            stats.AvgAliveB += sim.World.AliveCount(1);
            stats.StallA ??= sim.World.QueueStallReason(0);
            stats.StallB ??= sim.World.QueueStallReason(1);
            stats.LastFinalHash = result.FinalHash;
        }
        stats.AvgTicks = (double)tickSum / runs;
        stats.AvgNetWorthA /= runs; stats.AvgNetWorthB /= runs;
        stats.AvgAliveA /= runs; stats.AvgAliveB /= runs;
        return stats;
    }

    public static EconStats RunEconSeries(ContentDb content, string specA, string specB,
        int startingMoney, int incomePerSecond, int supplyPool, bool holdA, bool holdB,
        int runs, ulong baseSeed, int maxTicks = EconDefaultMaxTicks)
    {
        int totalResources = startingMoney + supplyPool;
        var orderA = ExpandOrder(content, specA, totalResources);
        var orderB = ExpandOrder(content, specB, totalResources);
        var log = BuildEconCommands(content, orderA, orderB, startingMoney, incomePerSecond, supplyPool, holdA, holdB);

        var v1 = new Sim(content, baseSeed, log).Run(maxTicks);
        var v2 = new Sim(content, baseSeed, log).Run(maxTicks);
        bool deterministic = v1.FinalHash == v2.FinalHash
                             && v1.Ticks == v2.Ticks
                             && v1.HashTrace.Count == v2.HashTrace.Count
                             && v1.HashTrace.Zip(v2.HashTrace).All(p => p.First == p.Second);

        var stats = new EconStats { OrderA = specA, OrderB = specB, Runs = runs, DeterminismOk = deterministic };
        long tickSum = 0;
        for (int r = 0; r < runs; r++)
        {
            var sim = new Sim(content, baseSeed + (ulong)r, log);
            var result = sim.Run(maxTicks);
            switch (result.WinnerTeam)
            {
                case 0: stats.WinsA++; break;
                case 1: stats.WinsB++; break;
                default: stats.Draws++; break;
            }
            tickSum += result.Ticks;
            stats.AvgNetWorthA += NetWorth(sim.World, 0);
            stats.AvgNetWorthB += NetWorth(sim.World, 1);
            stats.AvgAliveA += sim.World.AliveCount(0);
            stats.AvgAliveB += sim.World.AliveCount(1);
            stats.StallA ??= sim.World.QueueStallReason(0);
            stats.StallB ??= sim.World.QueueStallReason(1);
            stats.LastFinalHash = result.FinalHash;
        }
        stats.AvgTicks = (double)tickSum / runs;
        stats.AvgNetWorthA /= runs;
        stats.AvgNetWorthB /= runs;
        stats.AvgAliveA /= runs;
        stats.AvgAliveB /= runs;
        return stats;
    }

    private static double NetWorth(World w, int team)
    {
        Fix64 v = w.Teams[team].Money + w.Teams[team].PoolRemaining;
        for (int i = 0; i < w.UnitCount; i++)
            if (w.Units[i].Alive && w.Units[i].Team == team)
                v += Fix64.FromInt(w.Content.Units[w.Units[i].ProtoIdx].Cost);
        return v.ToDoubleForDisplay();
    }

    /// <summary>
    /// The determinism proof: two fresh sims, same (content, seed, command log),
    /// must produce identical hash traces and final hashes. This doubles as the
    /// replay contract check — a replay file is nothing more than these inputs.
    /// </summary>
    public static (bool Ok, SimResult First, SimResult Second) VerifyDeterminism(ContentDb content, string a, string b, int budget, ulong seed, int maxTicks = DefaultMaxTicks)
    {
        int pa = content.UnitIndexById[a];
        int pb = content.UnitIndexById[b];
        var log = BuildDuelCommands(content, pa, pb, budget);

        var r1 = new Sim(content, seed, log).Run(maxTicks);
        var r2 = new Sim(content, seed, log).Run(maxTicks);

        bool ok = r1.FinalHash == r2.FinalHash
                  && r1.Ticks == r2.Ticks
                  && r1.HashTrace.Count == r2.HashTrace.Count
                  && r1.HashTrace.Zip(r2.HashTrace).All(p => p.First == p.Second);
        return (ok, r1, r2);
    }
}
