using RtsSkeleton.Core;

namespace RtsSkeleton.Runtime;

/// <summary>
/// Closed event vocabulary. Adding one is a deliberate act with a documented tick-order
/// position — never an open string, or content could name an event nothing raises and the
/// failure would be silent.
/// </summary>
public enum RuleEvent
{
    /// <summary>
    /// The unit died this tick. First because it is the largest authoring category in the
    /// reference corpus by a wide margin: 5,083 of 16,685 module instances (30.5%).
    /// </summary>
    Death = 0,
}

/// <summary>Closed effect vocabulary with open parameters.</summary>
public enum EffectKind
{
    /// <summary>Create N prototypes near the event. Wrecks, crates, ambush squads, debris.</summary>
    Spawn = 0,
    /// <summary>Credit the owning team. Salvage economies, kill bounties.</summary>
    GrantMoney = 1,
    /// <summary>Apply a named weapon's damage to everything in a radius. Death explosions.</summary>
    DamageInRadius = 2,
    /// <summary>Set a flag on the owning team. Unlocks keyed off losses or milestones.</summary>
    GrantFlag = 3,
}

public sealed class EffectDef
{
    public EffectKind Kind;
    public int ProtoIdx = -1;
    public int Count = 1;
    public Fix64 Spread;
    public int Amount;
    public int WeaponIdx = -1;
    public Fix64 RadiusSq;
    public int FlagBit = -1;
    public bool TeamScope;
}

public sealed class RuleDef
{
    public RuleEvent On;
    /// <summary>Required flags, as a mask over the pack's flag table. 0 = unconditional.</summary>
    public ulong RequiredFlags;
    public EffectDef[] Effects = Array.Empty<EffectDef>();
}

/// <summary>
/// A death awaiting rule processing. Position is captured at the moment of death because
/// the corpse is gone by the time rules run, and a spawn must appear where the thing died.
/// </summary>
public readonly struct PendingDeath
{
    public readonly int ProtoIdx;
    public readonly int Team;
    public readonly Fix64 X;
    public readonly Fix64 Y;
    public readonly ulong Flags;
    /// <summary>How many rule generations deep this death is. Bounds cascades.</summary>
    public readonly int Depth;

    public PendingDeath(int protoIdx, int team, Fix64 x, Fix64 y, ulong flags, int depth)
    {
        ProtoIdx = protoIdx; Team = team; X = x; Y = y; Flags = flags; Depth = depth;
    }
}
