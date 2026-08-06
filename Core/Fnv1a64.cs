namespace RtsSkeleton.Core;

/// <summary>
/// FNV-1a 64-bit. Not cryptographic — it fingerprints sim state for replay
/// verification and content bundles for provenance. Swap for xxHash3 when
/// state size grows; the contract (order-sensitive fold over canonical
/// little-endian words) stays the same.
/// </summary>
public struct Fnv1a64
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private ulong _hash;

    public static Fnv1a64 Create() => new() { _hash = OffsetBasis };

    public void AddByte(byte b)
    {
        _hash ^= b;
        _hash = unchecked(_hash * Prime);
    }

    public void AddUInt64(ulong v)
    {
        for (int i = 0; i < 8; i++) AddByte((byte)(v >> (8 * i)));
    }

    public void AddInt64(long v) => AddUInt64(unchecked((ulong)v));
    public void AddInt32(int v) => AddUInt64(unchecked((uint)v));
    public void AddBool(bool v) => AddByte(v ? (byte)1 : (byte)0);

    public void AddBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes) AddByte(b);
    }

    public readonly ulong Value => _hash;

    public static ulong HashBytes(ReadOnlySpan<byte> bytes)
    {
        var h = Create();
        h.AddBytes(bytes);
        return h.Value;
    }
}
