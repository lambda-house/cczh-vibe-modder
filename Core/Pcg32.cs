namespace RtsSkeleton.Core;

/// <summary>
/// PCG-XSH-RR 32-bit generator (O'Neill). One instance per named purpose
/// ("combat spread", future: "crate drops", "ai jitter") so that adding a new
/// consumer of randomness never perturbs the draw sequence of existing systems —
/// a classic source of accidental desyncs when everything shares one RNG.
/// Generator state is part of the hashed sim state.
/// </summary>
public sealed class Pcg32
{
    private ulong _state;
    private readonly ulong _inc;

    public Pcg32(ulong seed, ulong streamId)
    {
        _inc = (streamId << 1) | 1UL;
        _state = 0;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    public uint NextUInt()
    {
        ulong old = _state;
        _state = unchecked(old * 6364136223846793005UL + _inc);
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }

    /// <summary>Uniform Q32.32 fraction in [0, 1). Deterministic; modulo-bias irrelevant here.</summary>
    public Fix64 NextFraction01() => new((long)NextUInt());

    public ulong StateForHash => _state;
}
