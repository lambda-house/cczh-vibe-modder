using System.Text.Json;
using System.Text.Json.Nodes;
using RtsSkeleton.Content;
using RtsSkeleton.Runtime;

namespace RtsSkeleton.Harness;

/// <summary>
/// MCP server over stdio: the agent-facing seam.
///
/// This is what the whole project points at. Everything below it — the deterministic core, the
/// counter matrix, the ZH compiler — exists so that an agent can author a pack, be told
/// precisely what is wrong with it, measure it, and compile it, without a human in the loop.
/// The CLI verbs were always "the future MCP tool seam"; this is that future, and it reuses
/// them rather than reimplementing anything.
///
/// ZERO DEPENDENCIES, deliberately. MCP is JSON-RPC 2.0 over newline-delimited stdio, and
/// System.Text.Json is in the BCL, so the root NuGet.config's &lt;clear/&gt; stays untouched.
/// A protocol this small is not worth relaxing a project invariant for.
///
/// Two design decisions worth stating:
///
///   EVERY RESULT CARRIES contentHash. It is the provenance anchor the rest of the project
///   already uses to attribute a balance number to an exact pack. An agent that reports "this
///   matchup is 70/30" without it has said nothing reproducible.
///
///   put_pack RETURNS THE HASH AND STORES BY IT. That makes the loop stateless for the agent:
///   upload content once, then reference it by hash for every measurement. It also means two
///   agents that author byte-identical content collide on the same entry, which is correct.
/// </summary>
public static class Mcp
{
    private const string ProtocolVersion = "2024-11-05";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static int Serve(string basePack, string storeDir)
    {
        Directory.CreateDirectory(storeDir);
        // stderr, never stdout: stdout IS the protocol channel and one stray line desyncs it.
        Console.Error.WriteLine($"rts mcp: base='{basePack}' store='{storeDir}'");

        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            JsonNode? req;
            try { req = JsonNode.Parse(line); }
            catch (JsonException e) { Respond(null, error: (-32700, $"parse error: {e.Message}")); continue; }
            if (req is null) continue;

            string method = req["method"]?.GetValue<string>() ?? "";
            JsonNode? id = req["id"];

            // A notification has no id and must never be answered.
            if (id is null)
            {
                if (method == "notifications/initialized") continue;
                continue;
            }

            try
            {
                switch (method)
                {
                    case "initialize":
                        Respond(id, new JsonObject
                        {
                            ["protocolVersion"] = ProtocolVersion,
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject
                            {
                                ["name"] = "rts",
                                ["version"] = "0.1.0",
                            },
                        });
                        break;

                    case "tools/list":
                        Respond(id, new JsonObject { ["tools"] = ToolList() });
                        break;

                    case "tools/call":
                    {
                        string name = req["params"]?["name"]?.GetValue<string>() ?? "";
                        var argsNode = req["params"]?["arguments"] as JsonObject ?? new JsonObject();
                        var (text, isError) = Dispatch(name, argsNode, basePack, storeDir);
                        Respond(id, new JsonObject
                        {
                            ["content"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = text,
                            }),
                            ["isError"] = isError,
                        });
                        break;
                    }

                    default:
                        Respond(id, error: (-32601, $"unknown method '{method}'"));
                        break;
                }
            }
            catch (Exception ex)
            {
                // A content error is a RESULT, not a transport failure — the agent needs to
                // read it and fix the pack. Only genuinely unexpected faults land here.
                Respond(id, error: (-32603, ex.Message));
            }
        }
        return 0;
    }

    private static void Respond(JsonNode? id, JsonNode? result = null, (int Code, string Message)? error = null)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone() };
        if (error is { } e)
            msg["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
        else
            msg["result"] = result ?? new JsonObject();
        Console.Out.WriteLine(msg.ToJsonString(Json));
        Console.Out.Flush();
    }

    // ---- tool surface -------------------------------------------------------------------

    private static JsonObject Str(string desc) => new() { ["type"] = "string", ["description"] = desc };
    private static JsonObject Int(string desc) => new() { ["type"] = "integer", ["description"] = desc };

    private static JsonObject Schema(JsonObject props, params string[] required)
    {
        var o = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Length > 0) o["required"] = new JsonArray(required.Select(x => (JsonNode)x!).ToArray());
        return o;
    }

    private static JsonArray ToolList() => new(
        Tool("put_pack",
             "Store a pack (JSON text of a mod layered over the base) and return its contentHash "
             + "plus the full lint report. Every other tool takes that hash, so the agent never "
             + "has to keep files around.",
             Schema(new JsonObject { ["content"] = Str("the mod's JSON text") }, "content")),

        Tool("validate_mod",
             "Lint a stored pack. Reports content errors and warnings, and with target='zh' also "
             + "their hard caps, round-trip loss (a duration that changes meaning when compiled) "
             + "and DIVERGENCE — mechanics we simulate that the real engine will play differently.",
             Schema(new JsonObject
             {
                 ["pack"] = Str("contentHash from put_pack, or a file path"),
                 ["target"] = Str("omit for sim-only; 'zh' to also check the Zero Hour target"),
             }, "pack")),

        Tool("run_matchup",
             "Cost-normalised duel between two prototypes over n seeded runs. Returns win counts "
             + "and the final state hash, so a result is reproducible and attributable.",
             Schema(new JsonObject
             {
                 ["pack"] = Str("contentHash or path"),
                 ["a"] = Str("prototype id"),
                 ["b"] = Str("prototype id"),
                 ["n"] = Int("runs (default 40)"),
                 ["seed"] = Int("base seed (default 42)"),
             }, "pack", "a", "b")),

        Tool("query_counter_matrix",
             "Pairwise cost-normalised win rates over every prototype: the counter table. This is "
             + "the measurement that costs hours of playtesting and seconds here.",
             Schema(new JsonObject
             {
                 ["pack"] = Str("contentHash or path"),
                 ["n"] = Int("runs per pair (default 20)"),
                 ["seed"] = Int("base seed (default 1)"),
             }, "pack")),

        Tool("compare_packs",
             "Structural diff between two stored packs: what changed, by category, plus the "
             + "duplication ratio. Answers 'what did my edit actually do' without reading files.",
             Schema(new JsonObject
             {
                 ["base"] = Str("contentHash or path"),
                 ["head"] = Str("contentHash or path"),
             }, "base", "head")),

        Tool("list_units",
             "Prototype ids in a pack with cost, health and speed. An agent needs this before it "
             + "can name anything in the other tools.",
             Schema(new JsonObject { ["pack"] = Str("contentHash or path") }, "pack")),

        Tool("compile_pack",
             "Compile a pack to Zero Hour Data/INI (and map.ini for overrides). Returns the files "
             + "written plus every warning, including which adopted meshes had no measured profile.",
             Schema(new JsonObject
             {
                 ["pack"] = Str("contentHash or path"),
                 ["out"] = Str("output directory"),
             }, "pack", "out")));

    private static JsonObject Tool(string name, string desc, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = desc,
        ["inputSchema"] = schema,
    };

    // ---- dispatch -----------------------------------------------------------------------

    private static string? S(JsonObject a, string k) => a[k]?.GetValue<string>();
    private static int I(JsonObject a, string k, int fallback)
        => a[k] is JsonNode n && n.GetValueKind() == JsonValueKind.Number ? n.GetValue<int>() : fallback;

    /// <summary>
    /// Resolve a pack reference: a contentHash stored by put_pack, or a plain file path. Both
    /// are accepted because an agent iterating on a checked-out repo should not have to upload
    /// files it already has on disk.
    /// </summary>
    private static List<string> Resolve(string basePack, string storeDir, string pack)
    {
        var paths = new List<string> { basePack };
        string stored = Path.Combine(storeDir, pack + ".json");
        if (File.Exists(stored)) paths.Add(stored);
        else if (File.Exists(pack)) paths.Add(pack);
        else if (!string.Equals(pack, "base", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException($"no pack '{pack}' in the store and no such file");
        return paths;
    }

    private static (string Text, bool IsError) Dispatch(string name, JsonObject a, string basePack, string storeDir)
    {
        switch (name)
        {
            case "put_pack":
            {
                string content = S(a, "content") ?? throw new ArgumentException("put_pack needs 'content'");
                // Write to a temp layer first: the contentHash is a property of the RESOLVED
                // stack, so it cannot be known until the pack has been loaded alongside base.
                string tmp = Path.Combine(Path.GetTempPath(), $"rts-put-{Guid.NewGuid():N}.json");
                File.WriteAllText(tmp, content);
                try
                {
                    var db = ContentDb.Load(new[] { basePack, tmp }, out var errors, out var warnings);
                    string hash = $"{db.ContentHash:x16}";
                    // Store even when it does not lint: the agent's next move is usually to fix
                    // it and re-measure, and throwing the content away makes that harder.
                    File.Copy(tmp, Path.Combine(storeDir, hash + ".json"), overwrite: true);
                    return (Report(new JsonObject
                    {
                        ["contentHash"] = hash,
                        ["packName"] = db.PackName,
                        ["ok"] = errors.Count == 0,
                        ["units"] = db.Units.Length,
                        ["errors"] = Arr(errors),
                        ["warnings"] = Arr(warnings),
                    }), errors.Count > 0);
                }
                finally { File.Delete(tmp); }
            }

            case "validate_mod":
            {
                var paths = Resolve(basePack, storeDir, S(a, "pack")!);
                var db = ContentDb.Load(paths, out var errors, out var warnings);
                var o = new JsonObject
                {
                    ["contentHash"] = $"{db.ContentHash:x16}",
                    ["ok"] = errors.Count == 0,
                    ["errors"] = Arr(errors),
                    ["warnings"] = Arr(warnings),
                };
                if (string.Equals(S(a, "target"), "zh", StringComparison.OrdinalIgnoreCase))
                {
                    var zr = ZhLint.Check(db, ContentDb.LoadZhTarget(paths));
                    o["zhCapErrors"] = Arr(zr.CapErrors);
                    o["zhRoundTrip"] = Arr(zr.RoundTrip);
                    // The dangerous list: these all compile, load and play, and behave
                    // differently from the numbers the agent just tuned against.
                    o["zhDivergence"] = Arr(zr.Divergence);
                }
                return (Report(o), errors.Count == 0 ? false : true);
            }

            case "run_matchup":
            {
                var paths = Resolve(basePack, storeDir, S(a, "pack")!);
                var db = LoadOrThrow(paths);
                // Name the mistake and list the alternatives. A raw KeyNotFoundException is
                // useless to an agent; "no prototype 'x', did you mean one of these" is a
                // repair instruction. Same principle as the harness reporting WHY a build
                // queue stalled instead of quietly producing a flat draw.
                string ida = Proto(db, S(a, "a")!), idb = Proto(db, S(a, "b")!);
                var s = Scenarios.RunDuelSeries(db, ida, idb, 3600,
                                                I(a, "n", 40), (ulong)I(a, "seed", 42));
                return (Report(new JsonObject
                {
                    ["contentHash"] = $"{db.ContentHash:x16}",
                    ["a"] = s.A, ["b"] = s.B, ["runs"] = s.Runs,
                    ["winsA"] = s.WinsA, ["winsB"] = s.WinsB, ["draws"] = s.Draws,
                    ["winRateA"] = Math.Round((double)s.WinsA / s.Runs, 4),
                    ["avgSeconds"] = Math.Round(s.AvgTicks / ContentDb.TicksPerSecond, 2),
                    ["lastFinalHash"] = $"{s.LastFinalHash:x16}",
                }), false);
            }

            case "query_counter_matrix":
            {
                var paths = Resolve(basePack, storeDir, S(a, "pack")!);
                var db = LoadOrThrow(paths);
                var rows = Scenarios.RunMatrix(db, 3600, I(a, "n", 20), (ulong)I(a, "seed", 1));
                var arr = new JsonArray();
                foreach (var s in rows)
                    arr.Add(new JsonObject
                    {
                        ["a"] = s.A, ["b"] = s.B,
                        ["winRateA"] = Math.Round((double)s.WinsA / s.Runs, 4),
                        ["draws"] = s.Draws,
                    });
                return (Report(new JsonObject
                {
                    ["contentHash"] = $"{db.ContentHash:x16}",
                    ["pairs"] = rows.Count,
                    ["rows"] = arr,
                }), false);
            }

            case "compare_packs":
            {
                var basePaths = Resolve(basePack, storeDir, S(a, "base")!);
                var headPaths = Resolve(basePack, storeDir, S(a, "head")!);
                var d = PackDiff.Compare(LoadOrThrow(basePaths), LoadOrThrow(headPaths));
                return (Report(new JsonObject
                {
                    ["baseHash"] = $"{d.BaseHash:x16}",
                    ["headHash"] = $"{d.HeadHash:x16}",
                    ["changes"] = d.Entries.Count,
                    ["duplicationRatio"] = Math.Round(d.DuplicationRatio, 4),
                    ["entries"] = Arr(d.Entries.Select(e => $"{e.Kind}: {e.Subject} — {e.Detail}")),
                }), false);
            }

            case "list_units":
            {
                var paths = Resolve(basePack, storeDir, S(a, "pack")!);
                var db = LoadOrThrow(paths);
                var arr = new JsonArray();
                foreach (var u in db.Units)
                    arr.Add(new JsonObject
                    {
                        ["id"] = u.Id,
                        ["faction"] = u.FactionId,
                        ["cost"] = u.Cost,
                        ["maxHp"] = u.BaseStats[(int)Stat.MaxHp].ToDoubleForDisplay(),
                        ["speed"] = u.BaseStats[(int)Stat.Speed].ToDoubleForDisplay() * ContentDb.TicksPerSecond,
                        ["isStructure"] = u.IsStructure,
                        ["isVariant"] = u.IsVariant,
                    });
                return (Report(new JsonObject
                {
                    ["contentHash"] = $"{db.ContentHash:x16}",
                    ["units"] = arr,
                }), false);
            }

            case "compile_pack":
            {
                var paths = Resolve(basePack, storeDir, S(a, "pack")!);
                var db = LoadOrThrow(paths);
                var art = ArtProfiles.Load("reference/art-profiles.json");
                var r = ZhCompiler.Compile(db, ContentDb.LoadZhTarget(paths), S(a, "out")!, false, art);
                return (Report(new JsonObject
                {
                    ["contentHash"] = $"{db.ContentHash:x16}",
                    ["ok"] = r.Errors.Count == 0,
                    ["artProfilesLoaded"] = art.Count,
                    ["files"] = Arr(r.Files),
                    ["errors"] = Arr(r.Errors),
                    ["warnings"] = Arr(r.Warnings),
                }), r.Errors.Count > 0);
            }

            default:
                return ($"unknown tool '{name}'", true);
        }
    }

    /// <summary>Resolve a prototype id, or fail with the list of ids that would have worked.</summary>
    private static string Proto(ContentDb db, string id)
    {
        if (db.UnitIndexById.ContainsKey(id)) return id;
        var known = db.Units.Where(u => !u.IsVariant).Select(u => u.Id)
                            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        throw new ArgumentException(
            $"no prototype '{id}' in this pack. Available: {string.Join(", ", known)}" +
            (db.Units.Any(u => u.IsVariant) ? " (plus faction variants named 'faction/unit')" : ""));
    }

    private static ContentDb LoadOrThrow(IReadOnlyList<string> paths)
    {
        var db = ContentDb.Load(paths, out var errors, out _);
        if (errors.Count > 0)
            throw new InvalidDataException($"pack does not lint ({errors.Count} error(s)): {string.Join("; ", errors.Take(5))}");
        return db;
    }

    private static JsonArray Arr(IEnumerable<string> items)
        => new(items.Select(s => (JsonNode)s!).ToArray());

    private static string Report(JsonObject o) => o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
