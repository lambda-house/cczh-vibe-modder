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
                "lint" => Lint(args, Stack(args, content)),
                "duel" => Duel(args, Stack(args, content)),
                "hold" => Hold(args, Stack(args, content)),
                "science-matrix" => ScienceMatrix(args, Stack(args, content)),
                "matrix" => Matrix(args, Stack(args, content)),
                "econ" => Econ(args, Stack(args, content)),
                "faction" => Faction(args, Stack(args, content)),
                "replay" => Replay(args, Stack(args, content)),
                "skirmish" => Skirmish(args, Stack(args, content)),
                "compile" => Compile(args, Stack(args, content)),
                "diff" => Diff(args),
                // The agent-facing seam. Speaks JSON-RPC on stdout, so nothing else may print
                // there — diagnostics go to stderr inside Mcp.Serve.
                "mcp" => Harness.Mcp.Serve(content, Opt(args, "--store", ".rts-packs")),
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
              rts lint   [--content content/game.json] [--target zh]
              rts hold   --host <structure> --holder <infantry> --attacker <unit> [--no-garrison]
              rts science-matrix [--order <build>] [--points N]   (needs a pack with sciences)
              rts mcp    [--store .rts-packs]      MCP server on stdio for an agent
                         --target zh also checks their caps, round-trip fidelity,
                         and what silently diverges once compiled
              rts duel   --a <unit> --b <unit> [--budget 3600] [--n 200] [--seed 42] [--json]
              rts matrix [--budget 3600] [--n 100] [--seed 1] [--json]
              rts econ   --a <order> --b <order> [--money 3000] [--income 50] [--pool 15000]
                         [--n 50] [--seed 42] [--maxsec 600] [--hold-a] [--hold-b] [--json]
              rts faction [--id <faction>] [--json]
              rts skirmish --a <faction> --b <faction> [--money 3000] [--income 50]
                         [--pool 15000] [--n 20] [--seed 42] [--maxsec 600] [--json]
              rts replay --a <unit> --b <unit> [--budget 3600] [--seed 7]
              rts compile --target zh --out <dir> [--with-strings]
                         emit additive Data/INI the Zero Hour engine will load

            econ build orders are comma-separated unit/tech ids issued at tick 0;
            'id*N' repeats N times, a trailing unit 'id*' repeats to resource cap.
            example: --a "war_factory,crusader*" --b "technical*"
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

    /// <summary>All values of a repeatable option, in command-line order.</summary>
    private static List<string> OptAll(string[] args, string name)
    {
        var vals = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) vals.Add(args[i + 1]);
        return vals;
    }

    /// <summary>
    /// The pack stack: one --content base, then zero or more --mod layers in order.
    /// A mod is a patch over the base, never a fork of it.
    /// </summary>
    private static List<string> Stack(string[] args, string content)
    {
        var paths = new List<string> { content };
        paths.AddRange(OptAll(args, "--mod"));
        return paths;
    }

    private static ContentDb LoadOrDie(IReadOnlyList<string> paths, bool printReport)
    {
        var db = ContentDb.Load(paths, out var errors, out var warnings);
        if (printReport)
        {
            foreach (var w in warnings) Console.WriteLine($"warn:  {w}");
            foreach (var e in errors) Console.WriteLine($"ERROR: {e}");
        }
        if (errors.Count > 0)
            throw new InvalidDataException($"{errors.Count} content error(s); aborting");
        return db;
    }

    /// <summary>
    /// rts diff --base &lt;pack&gt; [--base-mod ...] --head &lt;pack&gt; [--head-mod ...]
    /// Structural delta between two resolved stacks. The thing ZH's iniCRC cannot do.
    /// </summary>
    /// <summary>
    /// Faction vs faction, started from the factions' own definitions rather than from a
    /// build order someone typed. This is the verb the product goal actually needs: author
    /// a faction, measure it, without also having to author an opening for it.
    /// </summary>
    private static int Skirmish(string[] args, IReadOnlyList<string> contentPath)
    {
        var db = LoadOrDie(contentPath, printReport: false);
        string a = Opt(args, "--a", ""), b = Opt(args, "--b", "");
        if (a.Length == 0 || b.Length == 0) return Usage();
        int money = int.Parse(Opt(args, "--money", "3000"));
        int income = int.Parse(Opt(args, "--income", "50"));
        int pool = int.Parse(Opt(args, "--pool", "15000"));
        int n = int.Parse(Opt(args, "--n", "20"));
        ulong seed = ulong.Parse(Opt(args, "--seed", "42"));
        int maxTicks = int.Parse(Opt(args, "--maxsec", "600")) * ContentDb.TicksPerSecond;

        var s = Scenarios.RunSkirmishSeries(db, a, b, money, income, pool, n, seed, maxTicks);

        if (Flag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                contentHash = $"{db.ContentHash:x16}",
                factionA = a, factionB = b,
                s.Runs, s.WinsA, s.WinsB, s.Draws,
                winRateA = (double)s.WinsA / s.Runs,
                avgSeconds = s.AvgTicks / ContentDb.TicksPerSecond,
                s.AvgNetWorthA, s.AvgNetWorthB, s.AvgAliveA, s.AvgAliveB,
                stallA = s.StallA, stallB = s.StallB,
                determinismOk = s.DeterminismOk,
                lastFinalHash = $"{s.LastFinalHash:x16}",
            }, JsonOpts));
            return 0;
        }

        Console.WriteLine($"skirmish {a} vs {b}  n={n}  seed={seed}  contentHash={db.ContentHash:x16}");
        Console.WriteLine($"  {a}: {s.WinsA} wins ({100.0 * s.WinsA / s.Runs:0.#}%)   " +
                          $"{b}: {s.WinsB} wins ({100.0 * s.WinsB / s.Runs:0.#}%)   draws: {s.Draws}");
        Console.WriteLine($"  avg length: {s.AvgTicks / ContentDb.TicksPerSecond:0.#}s   " +
                          $"alive: A={s.AvgAliveA:0.#} B={s.AvgAliveB:0.#}");
        if (s.StallA is not null) Console.WriteLine($"  STALLED A: {s.StallA}");
        if (s.StallB is not null) Console.WriteLine($"  STALLED B: {s.StallB}");
        Console.WriteLine($"  determinism: {(s.DeterminismOk ? "OK" : "FAILED")}");
        return 0;
    }

    /// <summary>
    /// rts compile --target zh --out &lt;dir&gt;
    ///
    /// Turn a measured pack into a Zero Hour mod. Output is additive Data/INI that layers
    /// over the player's own install — the distribution model every ZH conversion uses, and
    /// the one that ships no EA content at all.
    /// </summary>
    private static int Compile(string[] args, IReadOnlyList<string> contentPath)
    {
        string target = Opt(args, "--target", "zh");
        if (target != "zh") { Console.Error.WriteLine($"unknown target '{target}' (expected: zh)"); return 2; }

        var db = LoadOrDie(contentPath, printReport: false);
        var zh = ContentDb.LoadZhTarget(contentPath);
        string outRoot = Opt(args, "--out", "build/zh-mod");

        // Measured contracts for adopted meshes. Generated locally by `zhasset artprofile`
        // and gitignored, so absence is normal — the compiler falls back to defaults and
        // reports which meshes it had to guess for.
        var art = ArtProfiles.Load(Opt(args, "--art-profiles", "reference/art-profiles.json"));
        if (art.Count > 0) Console.WriteLine($"art profiles: {art.Count} adoptable meshes measured");
        else Console.WriteLine("art profiles: none loaded — run `tools/zhasset artprofile` " +
                               "or geometry, bones and turrets fall back to guesses");
        var r = ZhCompiler.Compile(db, zh, outRoot, Flag(args, "--with-strings"), art);

        foreach (var w in r.Warnings) Console.WriteLine($"warn:  {w}");
        foreach (var e in r.Errors) Console.WriteLine($"ERROR: {e}");
        if (r.Errors.Count > 0)
        {
            Console.WriteLine($"compile: {r.Errors.Count} error(s); the mod would not load");
            return 1;
        }

        Console.WriteLine($"compiled '{db.PackName}' contentHash={db.ContentHash:x16} -> {outRoot}");
        Console.WriteLine($"  {r.Objects} objects · {r.Weapons} weapons · {r.Armors} armors · " +
                          $"{r.Locomotors} locomotors · {r.Buttons} buttons · {r.Sets} command sets · " +
                          $"{r.Templates} factions");
        foreach (var f in r.Files) Console.WriteLine($"  {Path.GetRelativePath(outRoot, f)}");
        Console.WriteLine();
        Console.WriteLine("install:  rsync -a " + outRoot + "/ ~/GeneralsX/GeneralsZH/");
        Console.WriteLine("play:     cd ~/GeneralsX/GeneralsZH && ./run.sh -win");
        return 0;
    }

    private static int Diff(string[] args)
    {
        var basePaths = new List<string> { Opt(args, "--base", "content/game.json") };
        basePaths.AddRange(OptAll(args, "--base-mod"));
        var headPaths = new List<string> { Opt(args, "--head", "content/game.json") };
        headPaths.AddRange(OptAll(args, "--head-mod"));

        var b = LoadOrDie(basePaths, printReport: false);
        var h = LoadOrDie(headPaths, printReport: false);
        var r = PackDiff.Compare(b, h);

        var byKind = r.Entries.GroupBy(e => e.Kind)
                              .OrderByDescending(g => g.Count())
                              .ToList();

        if (Flag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                baseHash = $"{r.BaseHash:x16}",
                headHash = $"{r.HeadHash:x16}",
                changes = r.Entries.Count,
                identicalUnits = r.IdenticalUnits,
                comparedUnits = r.TotalUnits,
                duplicationRatio = r.DuplicationRatio,
                taxonomy = byKind.ToDictionary(g => g.Key.ToString(), g => g.Count()),
                entries = r.Entries.Select(e => new { kind = e.Kind.ToString(), e.Subject, e.Detail }),
            }, JsonOpts));
            return 0;
        }

        Console.WriteLine($"diff  base '{r.BaseName}' {r.BaseHash:x16}  ->  head '{r.HeadName}' {r.HeadHash:x16}");
        if (r.Entries.Count == 0)
        {
            Console.WriteLine("  no differences — the stacks resolve identically");
            return 0;
        }
        Console.WriteLine($"  {r.Entries.Count} change(s) across {byKind.Count} categor(y/ies)");
        foreach (var g in byKind)
        {
            Console.WriteLine($"  {g.Key} ({g.Count()})");
            foreach (var e in g.Take(12))
                Console.WriteLine($"     {e.Subject}{(e.Detail.Length > 0 ? ": " + e.Detail : "")}");
            if (g.Count() > 12) Console.WriteLine($"     … {g.Count() - 12} more");
        }
        Console.WriteLine($"  duplication: {r.IdenticalUnits}/{r.TotalUnits} shared prototypes unchanged " +
                          $"({100.0 * r.DuplicationRatio:0.#}%)");
        return 0;
    }

    private static int Lint(string[] args, IReadOnlyList<string> contentPath)
    {
        var db = ContentDb.Load(contentPath, out var errors, out var warnings);
        Console.WriteLine($"pack '{db.PackName}'  contentHash={db.ContentHash:x16}");
        Console.WriteLine($"units={db.Units.Length} weapons={db.Weapons.Length} damageTypes={db.DamageTypes.Length} armorClasses={db.ArmorClasses.Length} techNodes={db.TechNodes.Count}");
        Console.WriteLine($"features: structures={db.HasFactories} power={db.HasPower} flags={db.HasFlags}({db.Flags.Count}) rules={db.HasRules}({db.Units.Sum(u => u.Rules.Length)})");
        if (db.Map is { } m)
        {
            // Report the BLOCKED share, not just the size. A map every unit can cross is a
            // no-op that moves no hash, and saying so here is the difference between "terrain
            // loaded" and "terrain does anything" — the distinction a screenshot would
            // otherwise have to make.
            int blocked = 0;
            for (int c = 0; c < m.CellCount; c++)
                if (m.At(c) != Runtime.Surface.Clear) blocked++;
            Console.WriteLine($"map: {m.Width}x{m.Height} cells of {m.CellSize.ToDoubleForDisplay():0.###} " +
                              $"({m.Width * m.CellSize.ToDoubleForDisplay():0.#} world units square), " +
                              $"{blocked} non-clear ({100.0 * blocked / m.CellCount:0.#}%), " +
                              $"pathing={db.HasPassability} sight={db.HasLineOfSight}");
        }
        foreach (var w in warnings) Console.WriteLine($"warn:  {w}");
        foreach (var e in errors) Console.WriteLine($"ERROR: {e}");
        // --target zh asks a different question: not "is this pack coherent" but "will it
        // survive compilation to their engine, and will it still mean what we measured".
        if (Opt(args, "--target", "") == "zh")
        {
            var zr = ZhLint.Check(db, ContentDb.LoadZhTarget(contentPath));
            Console.WriteLine();
            Console.WriteLine($"target zh — {zr.Checked} value(s) round-trip checked");

            foreach (var e in zr.CapErrors) Console.WriteLine($"CAP:   {e}");
            foreach (var t in zr.RoundTrip) Console.WriteLine($"TRIP:  {t}");
            foreach (var d in zr.Divergence) Console.WriteLine($"DIVERGE: {d}");

            if (zr.CapErrors.Count == 0 && zr.RoundTrip.Count == 0 && zr.Divergence.Count == 0)
                Console.WriteLine("  nothing lost in translation");
            else
                Console.WriteLine($"  {zr.CapErrors.Count} cap · {zr.RoundTrip.Count} round-trip · " +
                                  $"{zr.Divergence.Count} divergence");
            if (!zr.Ok) return 1;
        }

        Console.WriteLine(errors.Count == 0 ? "lint: OK" : $"lint: {errors.Count} error(s)");
        return errors.Count == 0 ? 0 : 1;
    }

    private static int Duel(string[] args, IReadOnlyList<string> contentPath)
    {
        var db = LoadOrDie(contentPath, printReport: false);
        string a = Opt(args, "--a", "");
        string b = Opt(args, "--b", "");
        if (a.Length == 0 || b.Length == 0) return Usage();
        int budget = int.Parse(Opt(args, "--budget", "3600"));
        int n = int.Parse(Opt(args, "--n", "200"));
        ulong seed = ulong.Parse(Opt(args, "--seed", "42"));

        // --brute disables the spatial broad phase. It exists so equivalence is TESTABLE:
        // the grid is a pure accelerator and both paths must produce identical state hashes.
        var s = Scenarios.RunDuelSeries(db, a, b, budget, n, seed,
                                        bruteForce: Flag(args, "--brute"));

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

    private static int Hold(string[] args, IReadOnlyList<string> contentPath)
    {
        var db = LoadOrDie(contentPath, printReport: false);
        string host = Opt(args, "--host", "");
        string holder = Opt(args, "--holder", "");
        string attacker = Opt(args, "--attacker", "");
        if (host.Length == 0 || holder.Length == 0 || attacker.Length == 0) return Usage();
        int budget = int.Parse(Opt(args, "--budget", "3600"));
        int n = int.Parse(Opt(args, "--n", "40"));
        ulong seed = ulong.Parse(Opt(args, "--seed", "42"));
        // Default ON; --no-garrison is the ablation, so the comparison is one flag apart.
        bool garrison = !Flag(args, "--no-garrison");
        string prizeOpt = Opt(args, "--prize", "");
        string? prize = prizeOpt.Length == 0 ? null : prizeOpt;

        var h = Scenarios.RunHoldSeries(db, host, holder, attacker, budget, n, seed, garrison, prize);
        var s = h.Duel;

        if (Flag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                contentHash = $"{db.ContentHash:x16}",
                host, holder, attacker, garrison, prize, budget, s.Runs, s.WinsA, s.WinsB, s.Draws,
                winRateA = (double)s.WinsA / s.Runs,
                avgSeconds = s.AvgTicks / ContentDb.TicksPerSecond,
                h.PrizeOwner, h.MoneyA, h.MoneyB,
                lastFinalHash = $"{s.LastFinalHash:x16}",
            }, JsonOpts));
            return 0;
        }

        Console.WriteLine($"hold {holder} in {host} vs {attacker}  garrison={(garrison ? "on" : "OFF")}  " +
                          $"budget={budget}  n={n}  seed={seed}  contentHash={db.ContentHash:x16}");
        Console.WriteLine($"  defenders: {s.WinsA} wins ({100.0 * s.WinsA / s.Runs:0.#}%)   " +
                          $"{attacker}: {s.WinsB} wins ({100.0 * s.WinsB / s.Runs:0.#}%)   draws: {s.Draws}");
        if (prize is not null)
            Console.WriteLine($"  prize '{prize}' ends owned by team {h.PrizeOwner}   " +
                              $"money: A={h.MoneyA:0} B={h.MoneyB:0}");
        Console.WriteLine($"  avg battle length: {s.AvgTicks / ContentDb.TicksPerSecond:0.#}s   last final hash: {s.LastFinalHash:x16}");
        return 0;
    }

    private static int ScienceMatrix(string[] args, IReadOnlyList<string> contentPath)
    {
        var db = LoadOrDie(contentPath, printReport: false);
        if (db.Sciences.Length == 0) { Console.Error.WriteLine("error: this pack declares no sciences"); return 2; }

        string order = Opt(args, "--order", "war_factory,crusader*");
        int points = int.Parse(Opt(args, "--points", "0"));
        if (points <= 0) points = db.Ranks.Sum(rk => rk.PurchasePointsGranted);
        int n = int.Parse(Opt(args, "--n", "12"));
        ulong seed = ulong.Parse(Opt(args, "--seed", "42"));
        int money = int.Parse(Opt(args, "--money", "5000"));
        int income = int.Parse(Opt(args, "--income", "50"));
        int pool = int.Parse(Opt(args, "--pool", "20000"));

        var rows = Scenarios.RunScienceMatrix(db, order, points, money, income, pool, n, seed);

        if (Flag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                contentHash = $"{db.ContentHash:x16}",
                order, points, runs = n,
                rows = rows.Select(r => new
                {
                    r.Sciences, r.Points, r.Wins, r.Runs,
                    winRate = (double)r.Wins / r.Runs,
                    lastFinalHash = $"{r.LastFinalHash:x16}",
                }),
            }, JsonOpts));
            return 0;
        }

        Console.WriteLine($"science-matrix  order='{order}'  points={points}  n={n}  seed={seed}  " +
                          $"contentHash={db.ContentHash:x16}");
        Console.WriteLine($"  mirror match — both sides run the same build order, so the only variable");
        Console.WriteLine($"  is which sciences team A owns. {rows.Count} legal loadout(s) of " +
                          $"{db.Sciences.Length} science(s).");
        Console.WriteLine();
        foreach (var r in rows.OrderByDescending(x => x.Wins).ThenBy(x => x.Points).ThenBy(x => x.Sciences, StringComparer.Ordinal))
            Console.WriteLine($"  {r.Wins,3}/{r.Runs,-3} ({100.0 * r.Wins / r.Runs,5:0.#}%)  {r.Points}pt  {r.Sciences}");
        return 0;
    }

    private static int Matrix(string[] args, IReadOnlyList<string> contentPath)
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

    private static int Econ(string[] args, IReadOnlyList<string> contentPath)
    {
        var db = LoadOrDie(contentPath, printReport: false);
        string a = Opt(args, "--a", "");
        string b = Opt(args, "--b", "");
        if (a.Length == 0 || b.Length == 0) return Usage();
        int money = int.Parse(Opt(args, "--money", "3000"));
        int income = int.Parse(Opt(args, "--income", "50"));
        int pool = int.Parse(Opt(args, "--pool", "15000"));
        int n = int.Parse(Opt(args, "--n", "50"));
        ulong seed = ulong.Parse(Opt(args, "--seed", "42"));
        int maxsec = int.Parse(Opt(args, "--maxsec", "600"));
        bool holdA = Flag(args, "--hold-a");
        bool holdB = Flag(args, "--hold-b");

        var s = Scenarios.RunEconSeries(db, a, b, money, income, pool, holdA, holdB, n, seed,
            maxsec * ContentDb.TicksPerSecond);

        if (Flag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                contentHash = $"{db.ContentHash:x16}",
                orderA = s.OrderA, orderB = s.OrderB,
                startingMoney = money, incomePerSecond = income, supplyPool = pool,
                holdA, holdB, s.Runs, s.WinsA, s.WinsB, s.Draws,
                winRateA = (double)s.WinsA / s.Runs,
                avgSeconds = s.AvgTicks / ContentDb.TicksPerSecond,
                avgNetWorthA = s.AvgNetWorthA, avgNetWorthB = s.AvgNetWorthB,
                determinismOk = s.DeterminismOk,
                lastFinalHash = $"{s.LastFinalHash:x16}",
            }, JsonOpts));
            return s.DeterminismOk ? 0 : 1;
        }

        Console.WriteLine($"econ  money={money} income={income}/s pool={pool}  n={n}  seed={seed}  maxsec={maxsec}  contentHash={db.ContentHash:x16}");
        Console.WriteLine($"  A: {s.OrderA}{(holdA ? "  (hold)" : "")}");
        Console.WriteLine($"  B: {s.OrderB}{(holdB ? "  (hold)" : "")}");
        Console.WriteLine($"  A: {s.WinsA} wins ({100.0 * s.WinsA / s.Runs:0.#}%)   B: {s.WinsB} wins ({100.0 * s.WinsB / s.Runs:0.#}%)   draws: {s.Draws}");
        Console.WriteLine($"  avg battle length: {s.AvgTicks / ContentDb.TicksPerSecond:0.#}s   avg end net worth: A={s.AvgNetWorthA:0} B={s.AvgNetWorthB:0}   alive: A={s.AvgAliveA:0.#} B={s.AvgAliveB:0.#}");
        if (s.StallA is not null) Console.WriteLine($"  STALLED A: {s.StallA}");
        if (s.StallB is not null) Console.WriteLine($"  STALLED B: {s.StallB}");
        Console.WriteLine(s.DeterminismOk
            ? "  determinism: OK (base seed double-run bit-identical)"
            : "  DETERMINISM FAILED");
        return s.DeterminismOk ? 0 : 1;
    }

    /// <summary>
    /// Faction rosters and, for a general, the diff against its parent. This is the
    /// review surface for generated content: what did this faction actually change?
    /// </summary>
    private static int Faction(string[] args, IReadOnlyList<string> contentPath)
    {
        var db = LoadOrDie(contentPath, printReport: false);
        string id = Opt(args, "--id", "");

        if (Flag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                contentHash = $"{db.ContentHash:x16}",
                factions = db.FactionOrder
                    .Where(f => id.Length == 0 || f == id)
                    .Select(f => db.Factions[f])
                    .Select(f => new
                    {
                        f.Id, f.Parent,
                        roster = f.RosterIds.Zip(f.OwnUnitIdx)
                            .Select(p => new { rosterId = p.First, proto = db.Units[p.Second].Id, cost = db.Units[p.Second].Cost }),
                        added = f.AddedIds, removed = f.RemovedIds, patched = f.PatchedIds,
                    }),
            }, JsonOpts));
            return 0;
        }

        Console.WriteLine($"factions  contentHash={db.ContentHash:x16}");
        foreach (var fid in db.FactionOrder)
        {
            if (id.Length > 0 && fid != id) continue;
            var f = db.Factions[fid];
            string lineage = f.Parent is null ? "(base)" : $"extends {f.Parent}";
            Console.WriteLine($"\n  {f.Id}  {lineage}   roster={f.RosterIds.Length}");
            foreach (var (rosterId, idx) in f.RosterIds.Zip(f.OwnUnitIdx))
            {
                var u = db.Units[idx];
                string mark = u.IsVariant ? " *patched" : (f.AddedIds.Contains(rosterId) ? " +added" : "");
                Console.WriteLine($"      {rosterId,-16} -> {u.Id,-28} cost={u.Cost,5} build={u.BuildTicks,4}{mark}");
            }
            foreach (var r in f.RemovedIds) Console.WriteLine($"      {r,-16} -removed");
        }
        return 0;
    }

    private static int Replay(string[] args, IReadOnlyList<string> contentPath)
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
