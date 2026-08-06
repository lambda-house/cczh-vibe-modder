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
