using System.Text.Json;
using RtsSkeleton.Content;
using RtsSkeleton.Harness;

namespace RtsSkeleton;

/// <summary>
/// CLI façade over the headless sim. Each verb is a pure (content, params) → report
/// function; --json emits machine-readable output so wrapping these as MCP tools
/// (validate_mod, run_matchup, query_counter_matrix) is transport plumbing only.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();
        string verb = args[0];
        string content = Opt(args, "--content", "content/game.json");

        try
        {
            return verb switch
            {
                "lint" => Lint(content),
                "duel" => Duel(args, content),
                "matrix" => Matrix(args, content),
                "replay" => Replay(args, content),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            rts-skeleton — deterministic RTS sim core walking skeleton

            usage:
              rts lint   [--content content/game.json]
              rts duel   --a <unit> --b <unit> [--budget 3600] [--n 200] [--seed 42] [--json]
              rts matrix [--budget 3600] [--n 100] [--seed 1] [--json]
              rts replay --a <unit> --b <unit> [--budget 3600] [--seed 7]
            """);
        return 1;
    }

    private static string Opt(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return fallback;
    }

    private static bool Flag(string[] args, string name) => args.Contains(name);

    private static ContentDb LoadOrDie(string path, bool printReport)
    {
        var db = ContentDb.Load(path, out var errors, out var warnings);
        if (printReport)
        {
            foreach (var w in warnings) Console.WriteLine($"warn:  {w}");
            foreach (var e in errors) Console.WriteLine($"ERROR: {e}");
        }
        if (errors.Count > 0)
            throw new InvalidDataException($"{errors.Count} content error(s); aborting");
        return db;
    }

    private static int Lint(string contentPath)
    {
        var db = ContentDb.Load(contentPath, out var errors, out var warnings);
        Console.WriteLine($"pack '{db.PackName}'  contentHash={db.ContentHash:x16}");
        Console.WriteLine($"units={db.Units.Length} weapons={db.Weapons.Length} damageTypes={db.DamageTypes.Length} armorClasses={db.ArmorClasses.Length} techNodes={db.TechNodes.Count}");
        foreach (var w in warnings) Console.WriteLine($"warn:  {w}");
        foreach (var e in errors) Console.WriteLine($"ERROR: {e}");
        Console.WriteLine(errors.Count == 0 ? "lint: OK" : $"lint: {errors.Count} error(s)");
        return errors.Count == 0 ? 0 : 1;
    }

    private static int Duel(string[] args, string contentPath)
    {
        var db = LoadOrDie(contentPath, printReport: false);
        string a = Opt(args, "--a", "");
        string b = Opt(args, "--b", "");
        if (a.Length == 0 || b.Length == 0) return Usage();
        int budget = int.Parse(Opt(args, "--budget", "3600"));
        int n = int.Parse(Opt(args, "--n", "200"));
        ulong seed = ulong.Parse(Opt(args, "--seed", "42"));

        var s = Scenarios.RunDuelSeries(db, a, b, budget, n, seed);

        if (Flag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                contentHash = $"{db.ContentHash:x16}",
                s.A, s.B, budget, s.Runs, s.WinsA, s.WinsB, s.Draws,
                winRateA = (double)s.WinsA / s.Runs,
                avgSeconds = s.AvgTicks / ContentDb.TicksPerSecond,
                lastFinalHash = $"{s.LastFinalHash:x16}",
            }, JsonOpts));
            return 0;
        }

        Console.WriteLine($"duel {a} vs {b}  budget={budget}  n={n}  seed={seed}  contentHash={db.ContentHash:x16}");
        Console.WriteLine($"  {a}: {s.WinsA} wins ({100.0 * s.WinsA / s.Runs:0.#}%)   {b}: {s.WinsB} wins ({100.0 * s.WinsB / s.Runs:0.#}%)   draws: {s.Draws}");
        Console.WriteLine($"  avg battle length: {s.AvgTicks / ContentDb.TicksPerSecond:0.#}s   last final hash: {s.LastFinalHash:x16}");
        return 0;
    }

    private static int Matrix(string[] args, string contentPath)
    {
        var db = LoadOrDie(contentPath, printReport: false);
        int budget = int.Parse(Opt(args, "--budget", "3600"));
        int n = int.Parse(Opt(args, "--n", "100"));
        ulong seed = ulong.Parse(Opt(args, "--seed", "1"));

        var results = Scenarios.RunMatrix(db, budget, n, seed);
        var ids = db.Units.Select(u => u.Id).ToArray();

        if (Flag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                contentHash = $"{db.ContentHash:x16}",
                budget, runsPerPair = n,
                cells = results.Select(r => new { r.A, r.B, winRateA = (double)r.WinsA / r.Runs, r.Draws }),
            }, JsonOpts));
            return 0;
        }

        Console.WriteLine($"counter matrix (row win% vs column)  budget={budget}  n={n}/pair  contentHash={db.ContentHash:x16}");
        int w = Math.Max(12, ids.Max(s => s.Length) + 2);
        Console.Write("".PadRight(w));
        foreach (var id in ids) Console.Write(id.PadRight(w));
        Console.WriteLine();
        foreach (var row in ids)
        {
            Console.Write(row.PadRight(w));
            foreach (var col in ids)
            {
                if (row == col) { Console.Write("-".PadRight(w)); continue; }
                var cell = results.First(r => r.A == row && r.B == col);
                Console.Write($"{100.0 * cell.WinsA / cell.Runs:0.#}%".PadRight(w));
            }
            Console.WriteLine();
        }

        // Degenerate-balance signal: a row that never loses is a dominant strategy.
        foreach (var row in ids)
        {
            var cells = results.Where(r => r.A == row).ToList();
            if (cells.All(c => c.WinsA == c.Runs))
                Console.WriteLine($"note: '{row}' wins every matchup — dominant, needs a counter");
            if (cells.All(c => c.WinsA == 0))
                Console.WriteLine($"note: '{row}' loses every matchup — strictly dominated");
        }
        return 0;
    }

    private static int Replay(string[] args, string contentPath)
    {
        var db = LoadOrDie(contentPath, printReport: false);
        string a = Opt(args, "--a", "");
        string b = Opt(args, "--b", "");
        if (a.Length == 0 || b.Length == 0) return Usage();
        int budget = int.Parse(Opt(args, "--budget", "3600"));
        ulong seed = ulong.Parse(Opt(args, "--seed", "7"));

        var (ok, r1, r2) = Scenarios.VerifyDeterminism(db, a, b, budget, seed);

        Console.WriteLine($"replay check {a} vs {b}  seed={seed}  contentHash={db.ContentHash:x16}  commandLogHash={r1.CommandLogHash:x16}");
        Console.WriteLine("  tick        run1              run2");
        foreach (var (p1, p2) in r1.HashTrace.Zip(r2.HashTrace))
            Console.WriteLine($"  {p1.Tick,5}  {p1.Hash:x16}  {p2.Hash:x16}  {(p1.Hash == p2.Hash ? "" : "MISMATCH")}");
        Console.WriteLine($"  final  {r1.FinalHash:x16}  {r2.FinalHash:x16}");
        Console.WriteLine(ok ? "DETERMINISM OK — bit-identical replay from (contentHash, seed, command log)" : "DETERMINISM FAILED");
        return ok ? 0 : 1;
    }
}
