using RtsSkeleton.Core;

namespace RtsSkeleton.Runtime;

public enum CommandKind
{
    Spawn = 1,        // A = proto index, B = team, X/Y = position
    RallyTeam = 2,    // A = team, X/Y = rally point (attack-move destination; also the
                      //   default rally applied to units the team produces afterwards)
    SetupEconomy = 3, // A = team, B = supply pool, X = starting money, Y = income per tick
    SetSpawn = 4,     // A = team, X/Y = where produced units enter the world
    ProduceUnit = 5,  // A = team, B = proto index → tail of the team's unit queue
    ResearchTech = 6, // A = team, B = tech index → tail of the team's research queue
    Garrison = 7,     // A = unit index, B = host structure index. No-op if not admissible.
}

/// <summary>
/// The only way anything enters the simulation from outside. Tick-stamped and
/// ordered by (tick, sequence). A replay is exactly (contentHash, seed, this list);
/// in lockstep multiplayer the same list is what peers exchange. Fixed binary-ish
/// layout so the log itself is hashable and trivially serializable later.
/// </summary>
public readonly struct Command
{
    public readonly int Tick;
    public readonly int Seq;
    public readonly CommandKind Kind;
    public readonly int A;
    public readonly int B;
    public readonly Fix64 X;
    public readonly Fix64 Y;

    public Command(int tick, int seq, CommandKind kind, int a, int b, Fix64 x, Fix64 y)
    {
        Tick = tick;
        Seq = seq;
        Kind = kind;
        A = a;
        B = b;
        X = x;
        Y = y;
    }

    public static ulong HashLog(IReadOnlyList<Command> log)
    {
        var h = Fnv1a64.Create();
        foreach (var c in log)
        {
            h.AddInt32(c.Tick);
            h.AddInt32(c.Seq);
            h.AddInt32((int)c.Kind);
            h.AddInt32(c.A);
            h.AddInt32(c.B);
            h.AddInt64(c.X.Raw);
            h.AddInt64(c.Y.Raw);
        }
        return h.Value;
    }
}
