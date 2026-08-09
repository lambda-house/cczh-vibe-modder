using System;
using System.Collections.Generic;
using RtsSkeleton.Content;
using RtsSkeleton.Harness;
using RtsSkeleton.Runtime;

namespace RtsShell;

public enum ScenarioKind
{
    SetPieceDuel,
    LargeBattle,
    MacroBattle,
    /// <summary>
    /// Structures. Requires a pack that declares an FS_FACTORY — run the shell with
    /// --mod ../content/mods/structures.json. Watch what the harness can only tabulate:
    /// both sides build a plant and a factory, then tanks; when a factory dies its owner's
    /// production stops dead, because the crusader's prerequisite is the BUILDING and not
    /// a research flag. That revocability is the whole point of the slice.
    /// </summary>
    BaseAssault,
}

/// <summary>
/// Owns the headless sim and turns wall-clock frame time into fixed 30 Hz ticks.
/// Deliberately contains no Godot types: the boundary this project exists to prove
/// is that presentation pulls from the sim and never pushes into it, so the piece
/// that touches the sim is plain C# and the piece that touches the engine is not.
///
/// Two snapshots are kept — the tick just completed and the one before it — so the
/// renderer can interpolate between them at whatever frame rate the display runs.
/// The sim itself never sees a frame, a delta, or a clock.
/// </summary>
public sealed class SimHost
{
    public const double TickSeconds = 1.0 / ContentDb.TicksPerSecond;
    /// <summary>Ceiling on catch-up steps per frame; without it a stalled frame
    /// compounds into an unrecoverable backlog (the classic spiral of death).</summary>
    private const int MaxStepsPerFrame = 240;

    private readonly ContentDb _content;
    private Sim _sim;
    private Snapshot _prev = new();
    private Snapshot _cur = new();
    private double _accumulator;

    public ScenarioKind Scenario { get; private set; }
    public bool Paused { get; set; }
    public int Speed { get; set; } = 1;
    public int MaxTicks { get; private set; }

    public Snapshot Previous => _prev;
    public Snapshot Current => _cur;
    public int Tick => _sim.Tick;
    public int Outcome => _sim.Outcome;
    public bool Finished => _sim.Outcome != Sim.Running || _sim.Tick >= MaxTicks;
    public ulong StateHash => _sim.HashState();

    /// <summary>
    /// Steps to completion ignoring wall time. Used by the headless self-check:
    /// the shell must reach the same final state hash as the batch harness from
    /// the same (contentHash, seed, command log), or presentation has somehow
    /// leaked into the sim.
    /// </summary>
    public void RunToCompletion()
    {
        while (!Finished) _sim.Step();
        _sim.CaptureSnapshot(_cur);
    }

    /// <summary>Fast-forwards to an exact tick, keeping both snapshots valid.</summary>
    public void StepTo(int tick)
    {
        while (_sim.Tick < tick && !Finished)
        {
            (_prev, _cur) = (_cur, _prev);
            _sim.Step();
            _sim.CaptureSnapshot(_cur);
        }
        _accumulator = 0;
    }

    /// <summary>Fraction of the way from <see cref="Previous"/> to <see cref="Current"/>.</summary>
    public double Alpha => Finished ? 1.0 : Math.Clamp(_accumulator / TickSeconds, 0.0, 1.0);

    public string Title { get; private set; } = "";

    public SimHost(ContentDb content, ScenarioKind scenario, ulong seed)
    {
        _content = content;
        _sim = null!;
        Restart(scenario, seed);
    }

    public void Restart(ScenarioKind scenario, ulong seed)
    {
        Scenario = scenario;
        _accumulator = 0;

        List<Command> log;
        if (scenario == ScenarioKind.SetPieceDuel)
        {
            const int budget = 3600;
            log = Scenarios.BuildDuelCommands(_content,
                _content.UnitIndexById["crusader"], _content.UnitIndexById["technical"], budget);
            MaxTicks = Scenarios.DefaultMaxTicks;
            Title = "set-piece duel — crusader vs technical (cost-normalized, budget 3600)";
        }
        else if (scenario == ScenarioKind.LargeBattle)
        {
            // Same cost-normalized rules, four times the budget: 16 crusaders against
            // 24 technicals. Massed lines make the concentration effects the duel
            // verb measures actually visible.
            const int budget = 14400;
            log = Scenarios.BuildDuelCommands(_content,
                _content.UnitIndexById["crusader"], _content.UnitIndexById["technical"], budget);
            MaxTicks = Scenarios.DefaultMaxTicks;
            Title = "large battle — 16 crusaders vs 24 technicals (cost-normalized, budget 14400)";
        }
        else if (scenario == ScenarioKind.BaseAssault)
        {
            if (!_content.HasFactories)
                throw new InvalidOperationException(
                    "BaseAssault needs a pack declaring an FS_FACTORY; " +
                    "run with --mod ../content/mods/structures.json");

            // Asymmetric on purpose: A opens with the power plant (so its grid is positive
            // and production never browns out), B skips it and pays for that later.
            string[] orderA = Scenarios.ExpandOrder(_content,
                "usa_power_plant,usa_factory,crusader*", 18000);
            string[] orderB = Scenarios.ExpandOrder(_content,
                "usa_power_plant,usa_factory,crusader*", 18000);
            log = Scenarios.BuildEconCommands(_content, orderA, orderB,
                startingMoney: 3000, incomePerSecond: 50, supplyPool: 15000);
            MaxTicks = Scenarios.EconDefaultMaxTicks;
            Title = "base assault — kill the factory and production stops";
        }
        else
        {
            string[] orderA = Scenarios.ExpandOrder(_content, "war_factory,battlemaster*", 18000);
            string[] orderB = Scenarios.ExpandOrder(_content, "war_factory,crusader*", 18000);
            log = Scenarios.BuildEconCommands(_content, orderA, orderB,
                startingMoney: 3000, incomePerSecond: 50, supplyPool: 15000);
            MaxTicks = Scenarios.EconDefaultMaxTicks;
            Title = "macro battle — battlemaster build order vs crusader build order";
        }

        _sim = new Sim(_content, seed, log);
        _prev = new Snapshot();
        _cur = new Snapshot();
        _sim.CaptureSnapshot(_prev);
        _sim.CaptureSnapshot(_cur);
    }

    /// <summary>
    /// Consumes elapsed wall time and advances whole ticks. Fractional remainder
    /// stays in the accumulator and becomes the interpolation alpha, so rendering
    /// is smooth at any frame rate while the sim only ever sees exact 30 Hz steps.
    /// </summary>
    public void Advance(double deltaSeconds)
    {
        if (Paused || Finished) return;

        _accumulator += deltaSeconds * Speed;
        int steps = 0;
        while (_accumulator >= TickSeconds && steps < MaxStepsPerFrame)
        {
            _accumulator -= TickSeconds;
            steps++;

            (_prev, _cur) = (_cur, _prev);
            _sim.Step();
            _sim.CaptureSnapshot(_cur);

            if (Finished) { _accumulator = 0; break; }
        }
        if (steps == MaxStepsPerFrame) _accumulator = 0;
    }
}
