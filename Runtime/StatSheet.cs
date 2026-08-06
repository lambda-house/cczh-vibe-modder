using RtsSkeleton.Core;

namespace RtsSkeleton.Runtime;

/// <summary>
/// The closed stat vocabulary. Every number a modifier can touch is listed here.
/// Keeping this enum small and explicit is what makes AI-generated content
/// statically checkable: an unknown stat name is a lint error, not a runtime surprise.
/// </summary>
public enum Stat
{
    MaxHp = 0,
    Speed = 1,          // world units per tick (converted at load)
    Damage = 2,
    CooldownScale = 3,  // multiplier on weapon cooldown ticks; <1 fires faster
    Range = 4,
    ArmorFactor = 5,    // multiplier on damage taken; <1 is tougher
}

public enum ModOp { Add, Mul }

public readonly struct Modifier
{
    public readonly Stat Stat;
    public readonly ModOp Op;
    public readonly Fix64 Value;

    public Modifier(Stat stat, ModOp op, Fix64 value)
    {
        Stat = stat;
        Op = op;
        Value = value;
    }
}

/// <summary>
/// Resolves base stats + modifier bundles into effective values.
/// Resolve order: effective = (base + Σ adds) * Π muls, per stat.
/// Skeleton scope: additive stacking of all bundles (veterancy ranks are cumulative).
/// A full build adds stacking rules (unique-by-source, max-one, diminishing) and
/// durations; both are fields on the modifier, not new systems.
/// </summary>
public static class StatResolver
{
    public const int StatCount = 6;

    public static void Resolve(ReadOnlySpan<Fix64> baseStats, IEnumerable<Modifier> mods, Span<Fix64> outStats)
    {
        Span<Fix64> add = stackalloc Fix64[StatCount];
        Span<Fix64> mul = stackalloc Fix64[StatCount];
        for (int i = 0; i < StatCount; i++)
        {
            add[i] = Fix64.Zero;
            mul[i] = Fix64.One;
        }

        foreach (var m in mods)
        {
            int s = (int)m.Stat;
            if (m.Op == ModOp.Add) add[s] += m.Value;
            else mul[s] *= m.Value;
        }

        for (int i = 0; i < StatCount; i++)
            outStats[i] = (baseStats[i] + add[i]) * mul[i];
    }
}
