using System.Numerics;

namespace RtsSkeleton.Core;

/// <summary>
/// Q32.32 signed fixed-point number. All simulation math goes through this type.
/// No IEEE floats may ever touch sim state: fixed-point arithmetic is bit-identical
/// across CPUs, compilers and optimization levels, which is what makes lockstep
/// multiplayer, replays and batch AI balancing trustworthy.
/// Doubles appear only at the content-loading boundary and in display formatting.
/// </summary>
public readonly struct Fix64 : IEquatable<Fix64>, IComparable<Fix64>
{
    public readonly long Raw;

    public const int FractionalBits = 32;
    public static readonly Fix64 Zero = new(0L);
    public static readonly Fix64 One = new(1L << FractionalBits);
    public static readonly Fix64 Two = new(2L << FractionalBits);
    public static readonly Fix64 Half = new(1L << (FractionalBits - 1));
    public static readonly Fix64 MaxValue = new(long.MaxValue);

    public Fix64(long raw) => Raw = raw;

    public static Fix64 FromInt(int v) => new((long)v << FractionalBits);

    /// <summary>Exact rational constructor; preferred for content-derived constants.</summary>
    public static Fix64 FromRatio(long num, long den)
    {
        if (den == 0) throw new DivideByZeroException("Fix64.FromRatio");
        return new Fix64((long)(((Int128)num << FractionalBits) / den));
    }

    /// <summary>
    /// Content-boundary conversion only. JSON numbers arrive as doubles; converting
    /// once at load time is deterministic (same file bytes -> same doubles -> same raw).
    /// Never call this on values produced inside the simulation.
    /// </summary>
    public static Fix64 FromDoubleAtLoadBoundary(double v)
        => new(checked((long)Math.Round(v * 4294967296.0)));

    public static Fix64 operator +(Fix64 a, Fix64 b) => new(checked(a.Raw + b.Raw));
    public static Fix64 operator -(Fix64 a, Fix64 b) => new(checked(a.Raw - b.Raw));
    public static Fix64 operator -(Fix64 a) => new(checked(-a.Raw));

    public static Fix64 operator *(Fix64 a, Fix64 b)
        => new((long)(((Int128)a.Raw * b.Raw) >> FractionalBits));

    public static Fix64 operator /(Fix64 a, Fix64 b)
    {
        if (b.Raw == 0) throw new DivideByZeroException("Fix64 division");
        return new Fix64((long)(((Int128)a.Raw << FractionalBits) / b.Raw));
    }

    public static Fix64 operator *(Fix64 a, int b) => new(checked(a.Raw * b));

    public static bool operator <(Fix64 a, Fix64 b) => a.Raw < b.Raw;
    public static bool operator >(Fix64 a, Fix64 b) => a.Raw > b.Raw;
    public static bool operator <=(Fix64 a, Fix64 b) => a.Raw <= b.Raw;
    public static bool operator >=(Fix64 a, Fix64 b) => a.Raw >= b.Raw;
    public static bool operator ==(Fix64 a, Fix64 b) => a.Raw == b.Raw;
    public static bool operator !=(Fix64 a, Fix64 b) => a.Raw != b.Raw;

    public static Fix64 Min(Fix64 a, Fix64 b) => a.Raw <= b.Raw ? a : b;
    public static Fix64 Max(Fix64 a, Fix64 b) => a.Raw >= b.Raw ? a : b;
    public static Fix64 Abs(Fix64 a) => a.Raw < 0 ? new Fix64(-a.Raw) : a;

    /// <summary>Integer Newton's method on the widened raw value. Exact floor sqrt.</summary>
    public static Fix64 Sqrt(Fix64 x)
    {
        if (x.Raw < 0) throw new ArgumentOutOfRangeException(nameof(x), "Fix64.Sqrt of negative");
        if (x.Raw == 0) return Zero;
        // sqrt(raw / 2^32) in Q32.32 == isqrt(raw * 2^32)
        UInt128 n = (UInt128)(ulong)x.Raw << FractionalBits;
        UInt128 g = (UInt128)1 << ((BitLength(n) + 1) / 2);
        while (true)
        {
            UInt128 next = (g + n / g) >> 1;
            if (next >= g) break;
            g = next;
        }
        return new Fix64((long)(ulong)g);
    }

    private static int BitLength(UInt128 v)
    {
        ulong hi = (ulong)(v >> 64);
        if (hi != 0) return 64 + BitOperations.Log2(hi) + 1;
        ulong lo = (ulong)v;
        return lo == 0 ? 0 : BitOperations.Log2(lo) + 1;
    }

    /// <summary>Ceiling of the value as an int. Used for cooldown-tick derivation.</summary>
    public int CeilToInt()
    {
        long intPart = Raw >> FractionalBits;
        return (Raw & 0xFFFF_FFFFL) != 0 && Raw > 0 ? checked((int)(intPart + 1)) : checked((int)intPart);
    }

    public bool Equals(Fix64 other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is Fix64 f && Equals(f);
    public override int GetHashCode() => Raw.GetHashCode();
    public int CompareTo(Fix64 other) => Raw.CompareTo(other.Raw);

    /// <summary>Display only. Never feed the result back into sim logic.</summary>
    public double ToDoubleForDisplay() => Raw / 4294967296.0;
    public override string ToString() => ToDoubleForDisplay().ToString("0.####");
}
