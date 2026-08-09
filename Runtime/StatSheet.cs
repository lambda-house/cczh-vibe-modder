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

/// <summary>
/// How a modifier composes with others touching the same stat.
///
/// <c>Excess</c> is Zero Hour's rule for situational bonuses (Weapon.cpp:3487 —
/// <c>bonus += this - 1.0</c>): the amounts by which each multiplier exceeds 1 are summed,
/// so Veteran(1.10) + upgrade(1.25) gives 1.35, not the 1.375 that <c>Mul</c> produces.
/// It is deliberately anti-combinatorial — three +25% sources give x1.75, not x1.95 — and
/// that is what keeps a balance surface tractable as sources multiply. Reproducing any real
/// ZH number requires it.
///
/// <c>Mul</c> is kept because true compounding is sometimes what you mean. Mixing the two
/// on one stat is legal but almost never intended, so <c>rts lint</c> warns.
/// </summary>
public enum ModOp { Add, Mul, Excess }

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
        Span<Fix64> excess = stackalloc Fix64[StatCount];   // Σ(value - 1), ZH's rule
        for (int i = 0; i < StatCount; i++)
        {
            add[i] = Fix64.Zero;
            mul[i] = Fix64.One;
            excess[i] = Fix64.Zero;
        }

        foreach (var m in mods)
        {
            int s = (int)m.Stat;
            switch (m.Op)
            {
                case ModOp.Add: add[s] += m.Value; break;
                case ModOp.Mul: mul[s] *= m.Value; break;
                default: excess[s] += m.Value - Fix64.One; break;
            }
        }

        // (base + Σadd) × Πmul × (1 + Σ(excess - 1)).
        // The excess factor is 1 when nothing used the op, so this is exactly the old
        // formula for every existing pack — the third op is additive to the algebra, not
        // a replacement for it.
        for (int i = 0; i < StatCount; i++)
            outStats[i] = (baseStats[i] + add[i]) * mul[i] * (Fix64.One + excess[i]);
    }
}
