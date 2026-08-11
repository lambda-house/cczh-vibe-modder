using System;
using System.Threading.Tasks;
using Godot;
using RtsSkeleton.Content;
using RtsSkeleton.Runtime;

namespace RtsShell;

/// <summary>
/// The presentation shell: reads snapshots, interpolates, draws. It knows nothing
/// about how the sim reaches a state — only what the state looks like — which is
/// why the same sim binary serves this window, the batch balancing harness, and
/// (later) a lockstep session without changing a line of sim code.
/// </summary>
public partial class Main : Node2D
{
    private const float MinScale = 3.5f;      // pixels per world unit
    private const float MaxScale = 16.0f;
    private const float WorldMargin = 7.0f;   // world units of padding around the action

    private static readonly Color[] TeamFill =
    {
        new(0.36f, 0.62f, 0.94f),   // team 0 — blue
        new(0.93f, 0.44f, 0.36f),   // team 1 — red
    };

    private ContentDb _content = null!;
    private SimHost _host = null!;
    private RichTextLabel _hud = null!;
    private ulong _seed = 42;
    private Vector2 _origin;
    private float _scale = 13f;
    private Vector2 _worldCenter = Vector2.Zero;

    public override void _Ready()
    {
        string root = ProjectSettings.GlobalizePath("res://");
        string contentPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "..", "content", "game.json"));

        // The shell loads a pack STACK, exactly as the CLI does: --mod may repeat and
        // layers apply in order. Presentation must never see a different content model
        // from the harness, or the shell/harness hash equality e2e asserts is meaningless.
        var stack = new System.Collections.Generic.List<string> { contentPath };
        var argv = OS.GetCmdlineUserArgs();
        for (int i = 0; i < argv.Length - 1; i++)
            if (argv[i] == "--mod")
                stack.Add(System.IO.Path.GetFullPath(
                    System.IO.Path.IsPathRooted(argv[i + 1])
                        ? argv[i + 1]
                        : System.IO.Path.Combine(root, argv[i + 1])));

        _content = ContentDb.Load(stack, out var errors, out _);
        if (errors.Count > 0)
        {
            GD.PrintErr($"content errors in {string.Join(" + ", stack)}:");
            foreach (var e in errors) GD.PrintErr("  " + e);
            GetTree().Quit(1);
            return;
        }
        GD.Print($"loaded '{_content.PackName}' contentHash={_content.ContentHash:x16}");

        _hud = GetNode<RichTextLabel>("Hud/Label");
        _host = new SimHost(_content, ScenarioKind.LargeBattle, _seed);

        var userArgs = OS.GetCmdlineUserArgs();
        if (Array.IndexOf(userArgs, "--verify") >= 0)
        {
            RunSelfCheck();
            return;
        }

        UpdateCamera(0, snap: true);

        int shotArg = Array.IndexOf(userArgs, "--shots");
        if (shotArg >= 0 && shotArg + 1 < userArgs.Length)
        {
            _shotMode = true;
            _ = CaptureShots(userArgs[shotArg + 1]);
        }
    }

    private bool _shotMode;

    /// <summary>
    /// Renders the shell at chosen ticks and writes PNGs of its own viewport —
    /// a screenshot path that needs no OS capture permission and lands on the
    /// exact tick every time, so the images are as reproducible as the sim.
    /// </summary>
    private async Task CaptureShots(string dir)
    {
        (ScenarioKind Scenario, int Tick, string Name)[] shots =
        {
            (ScenarioKind.LargeBattle, 60, "01-large-advance.png"),
            (ScenarioKind.LargeBattle, 150, "02-large-contact.png"),
            (ScenarioKind.LargeBattle, 400, "03-large-resolved.png"),
            (ScenarioKind.MacroBattle, 2050, "04-macro-trickle.png"),
        };

        foreach (var (scenario, tick, name) in shots)
        {
            _host.Restart(scenario, _seed);
            _host.StepTo(tick);
            UpdateCamera(0, snap: true);
            UpdateHud();
            QueueRedraw();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var image = GetViewport().GetTexture().GetImage();
            string path = System.IO.Path.Combine(dir, name);
            image.SavePng(path);
            GD.Print($"shot {name}  tick={_host.Tick}  units={_host.Current.UnitCount}");
        }
        GetTree().Quit();
    }

    /// <summary>
    /// Headless equivalence check against the batch harness. Prints the same
    /// fields `rts econ --json` reports so the two can be diffed directly.
    /// </summary>
    private void RunSelfCheck()
    {
        foreach (var scenario in new[] { ScenarioKind.SetPieceDuel, ScenarioKind.MacroBattle })
        {
            _host.Restart(scenario, _seed);
            _host.RunToCompletion();
            GD.Print($"scenario={scenario} seed={_seed} ticks={_host.Tick} " +
                     $"winnerTeam={_host.Outcome} finalHash={_host.StateHash:x16}");
        }
        GetTree().Quit();
    }

    /// <summary>
    /// Frames whatever is alive. Scenarios range from 10 units in a tight column to
    /// 40 across a wide front, so a fixed zoom either crops the big ones or leaves
    /// the small ones as specks. Purely a presentation concern — the sim has no
    /// notion of a camera, and this reads only from the snapshot.
    /// </summary>
    private void UpdateCamera(double delta, bool snap)
    {
        var size = GetViewport().GetVisibleRect().Size;
        // Leave room for the HUD block at the top.
        var view = new Rect2(40f, 190f, Mathf.Max(size.X - 80f, 1f), Mathf.Max(size.Y - 230f, 1f));

        var cur = _host.Current;
        if (cur.UnitCount > 0)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < cur.UnitCount; i++)
            {
                var u = cur.Units[i];
                minX = Math.Min(minX, u.X); maxX = Math.Max(maxX, u.X);
                minY = Math.Min(minY, u.Y); maxY = Math.Max(maxY, u.Y);
            }
            float w = (float)(maxX - minX) + WorldMargin * 2f;
            float h = (float)(maxY - minY) + WorldMargin * 2f;
            float target = Mathf.Clamp(Mathf.Min(view.Size.X / w, view.Size.Y / h), MinScale, MaxScale);
            var center = new Vector2((float)(minX + maxX) * 0.5f, (float)(minY + maxY) * 0.5f);

            // Exponential smoothing keeps the view from snapping as units die.
            float k = snap ? 1f : 1f - Mathf.Exp((float)-delta * 3f);
            _scale = Mathf.Lerp(_scale, target, k);
            _worldCenter = _worldCenter.Lerp(center, k);
        }

        _origin = view.Position + view.Size * 0.5f - _worldCenter * _scale;
    }

    public override void _Process(double delta)
    {
        if (_shotMode) return;   // CaptureShots drives ticking and redraws itself
        _host.Advance(delta);
        UpdateCamera(delta, snap: false);
        UpdateHud();
        QueueRedraw();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

        switch (key.Keycode)
        {
            case Key.Space: _host.Paused = !_host.Paused; break;
            case Key.Key1: Load(ScenarioKind.SetPieceDuel); break;
            case Key.Key2: Load(ScenarioKind.LargeBattle); break;
            case Key.Key3: Load(ScenarioKind.MacroBattle); break;
            // Only meaningful with a structures pack layered on; without one the pack
            // declares no factory and the scenario says so rather than pretending.
            case Key.Key4:
                if (_content.HasFactories) Load(ScenarioKind.BaseAssault);
                else GD.Print("scenario 4 needs: --mod ../content/mods/structures.json");
                break;
            case Key.R: Load(_host.Scenario); break;
            case Key.S: _seed++; Load(_host.Scenario); break;
            case Key.Equal or Key.Plus: _host.Speed = Mathf.Min(_host.Speed * 2, 32); break;
            case Key.Minus: _host.Speed = Mathf.Max(_host.Speed / 2, 1); break;
            case Key.Escape: GetTree().Quit(); break;
        }
    }

    private void Load(ScenarioKind scenario)
    {
        _host.Restart(scenario, _seed);
        UpdateCamera(0, snap: true);
    }

    private Vector2 ToScreen(double wx, double wy)
        => _origin + new Vector2((float)wx, (float)wy) * _scale;

    private Vector2[] _screenPos = new Vector2[64];
    private readonly System.Collections.Generic.Dictionary<int, int> _slotById = new();

    public override void _Draw()
    {
        DrawTerrain();
        DrawSpawnLines();

        var prev = _host.Previous;
        var cur = _host.Current;
        float alpha = (float)_host.Alpha;

        if (_screenPos.Length < cur.UnitCount) _screenPos = new Vector2[cur.UnitCount * 2];
        _slotById.Clear();

        // Both snapshots list units by ascending index, so a merge-walk finds each
        // unit's previous position without allocating a lookup table per frame.
        int p = 0;
        for (int i = 0; i < cur.UnitCount; i++)
        {
            var u = cur.Units[i];
            while (p < prev.UnitCount && prev.Units[p].Id < u.Id) p++;

            if (p < prev.UnitCount && prev.Units[p].Id == u.Id)
            {
                var q = prev.Units[p];
                _screenPos[i] = ToScreen(q.X + (u.X - q.X) * alpha, q.Y + (u.Y - q.Y) * alpha);
            }
            else _screenPos[i] = ToScreen(u.X, u.Y);   // spawned this tick: nothing to interpolate from

            _slotById[u.Id] = i;
        }

        // Tracers first so they sit under the units. Suppressed once the battle is
        // decided: the sim stops stepping, cooldowns freeze, and a still-true Firing
        // flag would otherwise leave every last shot painted on screen forever.
        if (_host.Outcome == Sim.Running)
        {
            for (int i = 0; i < cur.UnitCount; i++)
            {
                var u = cur.Units[i];
                if (!u.Firing || u.TargetId < 0) continue;
                if (!_slotById.TryGetValue(u.TargetId, out int slot)) continue;
                DrawLine(_screenPos[i], _screenPos[slot], new Color(1f, 0.88f, 0.45f, 0.35f), 1.5f);
            }
        }

        for (int i = 0; i < cur.UnitCount; i++)
            DrawUnit(cur.Units[i], _screenPos[i]);
    }

    /// <summary>
    /// The passability map, under everything else. Content, not snapshot state — it cannot
    /// change during a match, so reading it from the ContentDb the shell already holds is not
    /// a hole in the "presentation reads Snapshot and nothing else" rule; there is no mutable
    /// sim state here to leak. Nothing is drawn at all when the pack declares no map.
    ///
    /// Runs of identical cells are merged along each row into one rectangle. A 48x48 map is
    /// 2,304 draw calls a frame otherwise, for a picture that is mostly two colours.
    /// </summary>
    private void DrawTerrain()
    {
        var map = _content.Map;
        if (map is null) return;

        float cell = (float)map.CellSize.ToDoubleForDisplay();
        for (int cy = 0; cy < map.Height; cy++)
        {
            int cx = 0;
            while (cx < map.Width)
            {
                var surf = map.At(cy * map.Width + cx);
                int run = 1;
                while (cx + run < map.Width && map.At(cy * map.Width + cx + run) == surf) run++;
                if (surf != Surface.Clear)
                {
                    map.CellCentre(cy * map.Width + cx, out var wx, out var wy);
                    var tl = ToScreen(wx.ToDoubleForDisplay() - cell / 2,
                                      wy.ToDoubleForDisplay() - cell / 2);
                    var br = ToScreen(wx.ToDoubleForDisplay() - cell / 2 + cell * run,
                                      wy.ToDoubleForDisplay() + cell / 2);
                    DrawRect(new Rect2(tl, br - tl), TerrainFill(surf));
                }
                cx += run;
            }
        }
    }

    private static Color TerrainFill(Surface s) => s switch
    {
        Surface.Water => new Color(0.20f, 0.38f, 0.62f, 0.55f),
        Surface.Cliff => new Color(0.42f, 0.36f, 0.30f, 0.60f),
        Surface.Rubble => new Color(0.45f, 0.42f, 0.38f, 0.45f),
        _ => new Color(0.30f, 0.30f, 0.34f, 0.85f),      // impassable
    };

    private void DrawSpawnLines()
    {
        var size = GetViewport().GetVisibleRect().Size;
        foreach (int x in new[] { -40, 40 })
        {
            var top = ToScreen(x, 0);
            DrawLine(new Vector2(top.X, 0), new Vector2(top.X, size.Y),
                new Color(1, 1, 1, 0.06f), 1.0f);
        }
    }

    private void DrawUnit(in UnitSnapshot u, Vector2 pos)
    {
        var proto = _content.Units[u.ProtoIdx];
        var armor = _content.ArmorClasses[proto.ArmorClassIdx];
        Color fill = TeamFill[u.Team];

        // Shape carries the armor class — the thing the counter matrix keys on —
        // so the rock-paper-scissors structure is legible at a glance.
        // Sized in world units so zooming keeps formations readable at any scale.
        float r = _scale * armor switch
        {
            "heavy_vehicle" => 1.00f,
            "light_vehicle" => 0.78f,
            _ => 0.50f,
        };

        // Dark backing shape drawn slightly larger acts as an outline, so units
        // stacked on the same point stay countable. They stack because the sim has
        // no collision yet — the renderer must not hide that, only make it legible.
        var outline = new Color(0.05f, 0.06f, 0.07f, 0.95f);
        if (armor == "infantry")
        {
            DrawCircle(pos, r + 1.5f, outline);
            DrawCircle(pos, r, fill);
        }
        else
        {
            var half = new Vector2(r, r * 0.75f);
            DrawRect(new Rect2(pos - half - new Vector2(1.5f, 1.5f), half * 2 + new Vector2(3f, 3f)), outline);
            DrawRect(new Rect2(pos - half, half * 2), fill);
        }

        // Muzzle flash: a bright core at the unit, not a halo around it.
        if (u.Firing && _host.Outcome == Sim.Running)
            DrawCircle(pos, r * 0.4f, new Color(1f, 0.95f, 0.7f, 0.9f));

        // Veterancy chevrons: rank is a sim fact, so it belongs on screen.
        for (int c = 0; c < u.Rank; c++)
            DrawCircle(pos + new Vector2((c - (u.Rank - 1) * 0.5f) * 5f, -r - 6f), 2f,
                new Color(1f, 0.85f, 0.3f));

        // Health bar; the fraction comes straight from the snapshot, never recomputed.
        float w = r * 2f;
        var barPos = pos + new Vector2(-r, r + 4f);
        DrawRect(new Rect2(barPos, new Vector2(w, 3f)), new Color(0, 0, 0, 0.55f));
        DrawRect(new Rect2(barPos, new Vector2(w * (float)u.HpFraction, 3f)),
            new Color(0.45f, 0.85f, 0.45f));
    }

    private void UpdateHud()
    {
        var cur = _host.Current;
        double seconds = (double)_host.Tick / ContentDb.TicksPerSecond;

        string status = _host.Outcome switch
        {
            Sim.Running when _host.Tick >= _host.MaxTicks => "[color=#d9c34a]TICK CAP — draw[/color]",
            Sim.Running => _host.Paused ? "[color=#d9c34a]PAUSED[/color]" : "running",
            -1 => "[color=#d9c34a]DRAW[/color]",
            0 => "[color=#5c9ef0]TEAM 0 WINS[/color]",
            _ => "[color=#ed7059]TEAM 1 WINS[/color]",
        };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[b]{_host.Title}[/b]");
        sb.AppendLine($"contentHash={_content.ContentHash:x16}  seed={_seed}  tick={_host.Tick}  t={seconds:0.0}s  speed={_host.Speed}x  {status}");
        sb.AppendLine();
        for (int t = 0; t < cur.Teams.Length; t++)
        {
            var ts = cur.Teams[t];
            string color = t == 0 ? "#5c9ef0" : "#ed7059";
            sb.AppendLine($"[color={color}]team {t}[/color]  alive={ts.AliveUnits,3}  money={ts.Money,7:0}  pool={ts.PoolRemaining,7:0}  " +
                          $"queue={ts.QueuedUnits,3} units / {ts.QueuedResearch} research  building={ts.BuildProgress * 100,3:0}%");
        }
        sb.AppendLine();
        sb.Append("[color=#8a8f99]space pause · +/- speed · 1 duel · 2 large battle · 3 macro battle · R restart · S next seed · Esc quit[/color]");
        _hud.Text = sb.ToString();
    }
}
