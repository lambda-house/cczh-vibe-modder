using RtsSkeleton.Core;

namespace RtsSkeleton.Runtime;

/// <summary>
/// A named flag set, compiled to a 64-bit mask.
///
/// Zero Hour's fatal simplification here was making the upgrade condition ONE BIT: an object
/// can hold a single "upgraded" armor set and a single "upgraded" weapon set. Content works
/// around that with 268 `ConflictsWith` lines, 55 `RequiresAllTriggers`, hand-written 3x3
/// exclusion matrices, and an entire extra module type (`CommandSetUpgrade`, 114 uses) that
/// exists only because a CommandSet cannot carry a per-slot condition.
///
/// A set instead of a bit makes all of that disappear, and costs one integer.
///
/// Flags live in two places and are unioned when tested:
///   - per UNIT   (born with it, or gained in play)
///   - per TEAM   (researched tech, and later player-scope upgrades)
/// which is exactly ZH's OBJECT vs PLAYER upgrade split, the only structural distinction in
/// its 82 upgrades.
/// </summary>
public sealed class FlagTable
{
    public const int MaxFlags = 64;

    /// <summary>Ordinal-sorted flag names; index is the bit position.</summary>
    public string[] Names = Array.Empty<string>();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public int Count => Names.Length;

    /// <summary>
    /// Build from every flag name any content mentions. Ordinal-sorted so bit positions are
    /// a pure function of the name set — the same discipline as prototype identity. Note
    /// that unlike prototypes these bits never reach the wire or a replay on their own; the
    /// unit's mask does, so the sort keeps that mask stable for a fixed pack.
    /// </summary>
    public FlagTable(IEnumerable<string> allNames)
    {
        Names = allNames.Distinct(StringComparer.Ordinal)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToArray();
        for (int i = 0; i < Names.Length; i++) _index[Names[i]] = i;
    }

    public bool TryBit(string name, out int bit) => _index.TryGetValue(name, out bit);

    public ulong MaskOf(IEnumerable<string> names)
    {
        ulong m = 0;
        foreach (var n in names)
            if (_index.TryGetValue(n, out int b)) m |= 1UL << b;
        return m;
    }

    public string Describe(ulong mask)
    {
        if (mask == 0) return "-";
        var parts = new List<string>();
        for (int i = 0; i < Names.Length; i++)
            if ((mask & (1UL << i)) != 0) parts.Add(Names[i]);
        return string.Join('|', parts);
    }
}

/// <summary>
/// One compiled conditional loadout. <see cref="Required"/> must be a subset of the unit's
/// effective flags for this variant to apply; variants are tested in authored order and the
/// first match wins.
///
/// First-match rather than ZH's best-match scorer, deliberately: a scorer makes the selected
/// loadout depend on a similarity metric nobody can see in the content, and two variants can
/// tie. Authored order is visible, total, and trivially explainable in a diff.
/// </summary>
public sealed class LoadoutVariant
{
    public ulong Required;
    public int WeaponIdx = -1;        // -1 = inherit
    public int ArmorClassIdx = -1;    // -1 = inherit
    public Modifier[] Modifiers = Array.Empty<Modifier>();
}
