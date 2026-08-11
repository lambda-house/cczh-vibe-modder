using RtsSkeleton.Core;
using RtsSkeleton.Runtime;

namespace RtsSkeleton.Content;

/// <summary>
/// Structural diff between two resolved content stacks, reported as a delta taxonomy.
///
/// This is the capability Zero Hour lacks entirely. Retail's only integrity mechanism is
/// a whole-corpus iniCRC exchanged in the lobby: it can tell you two clients differ, never
/// what differs. Meanwhile 97% of its own general content is duplication that no tool ever
/// measured — the 23.9x tax was invisible to the people paying it.
///
/// The categories are chosen to answer "what kind of change is this?" without reading the
/// diff: a scalar retune reads very differently from a roster change, and duplication
/// ratio is the number that would have told EA what their content model was costing them.
/// </summary>
public static class PackDiff
{
    public enum Kind { UnitAdded, UnitRemoved, UnitScalar, UnitWeapon, UnitArmor, UnitOther,
                       WeaponAdded, WeaponRemoved, WeaponChanged,
                       FactionAdded, FactionRemoved, FactionChanged,
                       TechChanged, MatrixCell, VocabularyAdded }

    public sealed record Entry(Kind Kind, string Subject, string Detail);

    public sealed class Report
    {
        public required string BaseName;
        public required string HeadName;
        public required ulong BaseHash;
        public required ulong HeadHash;
        public List<Entry> Entries = new();
        /// <summary>Prototypes present in both stacks whose resolved stats are identical.</summary>
        public int IdenticalUnits;
        public int TotalUnits;
        /// <summary>Share of the head roster that is byte-identical to base. ZH's number was 96.4%.</summary>
        public double DuplicationRatio => TotalUnits == 0 ? 0 : (double)IdenticalUnits / TotalUnits;
    }

    public static Report Compare(ContentDb b, ContentDb h)
    {
        var r = new Report
        {
            BaseName = b.PackName, HeadName = h.PackName,
            BaseHash = b.ContentHash, HeadHash = h.ContentHash,
        };

        // --- vocabularies: additions only; removal/reorder is caught by the identity rule
        foreach (var dt in h.DamageTypes.Except(b.DamageTypes, StringComparer.Ordinal))
            r.Entries.Add(new Entry(Kind.VocabularyAdded, dt, "damage type"));
        foreach (var ac in h.ArmorClasses.Except(b.ArmorClasses, StringComparer.Ordinal))
            r.Entries.Add(new Entry(Kind.VocabularyAdded, ac, "armor class"));

        // --- units, compared on RESOLVED stats so a faction variant is diffed as it plays
        var bu = b.Units.ToDictionary(u => u.Id, StringComparer.Ordinal);
        var hu = h.Units.ToDictionary(u => u.Id, StringComparer.Ordinal);

        foreach (var id in hu.Keys.Except(bu.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            r.Entries.Add(new Entry(Kind.UnitAdded, id, $"cost {hu[id].Cost}"));
        foreach (var id in bu.Keys.Except(hu.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            r.Entries.Add(new Entry(Kind.UnitRemoved, id, ""));

        foreach (var id in bu.Keys.Intersect(hu.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            var (x, y) = (bu[id], hu[id]);
            r.TotalUnits++;
            int before = r.Entries.Count;

            if (x.Cost != y.Cost)
                r.Entries.Add(new Entry(Kind.UnitScalar, id, $"cost {x.Cost} -> {y.Cost}"));
            if (x.BuildTicks != y.BuildTicks)
                r.Entries.Add(new Entry(Kind.UnitScalar, id, $"buildTicks {x.BuildTicks} -> {y.BuildTicks}"));
            // Compare by NAME, never by index. A weapon index is a position in a table built
            // by sorting a dictionary, so removing ANY weapon renumbers everything after it
            // and an index comparison reports every shifted unit as changed — with identical
            // names on both sides of the arrow. Same rule as prototype identity: an ordinal
            // is a runtime array position and must never stand in for identity.
            var (xw, yw) = (b.Weapons[x.WeaponIdx].Id, h.Weapons[y.WeaponIdx].Id);
            if (!string.Equals(xw, yw, StringComparison.Ordinal))
                r.Entries.Add(new Entry(Kind.UnitWeapon, id, $"{xw} -> {yw}"));
            var (xa, ya) = (b.ArmorClasses[x.ArmorClassIdx], h.ArmorClasses[y.ArmorClassIdx]);
            if (!string.Equals(xa, ya, StringComparison.Ordinal))
                r.Entries.Add(new Entry(Kind.UnitArmor, id, $"{xa} -> {ya}"));

            for (int s = 0; s < StatResolver.StatCount; s++)
                if (x.BaseStats[s].Raw != y.BaseStats[s].Raw)
                    r.Entries.Add(new Entry(Kind.UnitOther, id,
                        $"{(Stat)s} {x.BaseStats[s].ToDoubleForDisplay():0.##} -> {y.BaseStats[s].ToDoubleForDisplay():0.##}"));

            if (r.Entries.Count == before) r.IdenticalUnits++;
        }

        // --- weapons
        var bw = b.Weapons.ToDictionary(w => w.Id, StringComparer.Ordinal);
        var hw = h.Weapons.ToDictionary(w => w.Id, StringComparer.Ordinal);
        foreach (var id in hw.Keys.Except(bw.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            r.Entries.Add(new Entry(Kind.WeaponAdded, id, ""));
        foreach (var id in bw.Keys.Except(hw.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            r.Entries.Add(new Entry(Kind.WeaponRemoved, id, ""));
        foreach (var id in bw.Keys.Intersect(hw.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            var (x, y) = (bw[id], hw[id]);
            if (x.Damage.Raw != y.Damage.Raw)
                r.Entries.Add(new Entry(Kind.WeaponChanged, id,
                    $"damage {x.Damage.ToDoubleForDisplay():0.##} -> {y.Damage.ToDoubleForDisplay():0.##}"));
            if (x.Range.Raw != y.Range.Raw)
                r.Entries.Add(new Entry(Kind.WeaponChanged, id,
                    $"range {x.Range.ToDoubleForDisplay():0.##} -> {y.Range.ToDoubleForDisplay():0.##}"));
            if (x.CooldownTicks != y.CooldownTicks)
                r.Entries.Add(new Entry(Kind.WeaponChanged, id, $"cooldown {x.CooldownTicks} -> {y.CooldownTicks}"));
        }

        // --- the counter matrix, cell by cell: this is the primary balance surface
        foreach (var dt in b.DamageTypes.Intersect(h.DamageTypes, StringComparer.Ordinal))
        foreach (var ac in b.ArmorClasses.Intersect(h.ArmorClasses, StringComparer.Ordinal))
        {
            int bd = Array.IndexOf(b.DamageTypes, dt), ba = Array.IndexOf(b.ArmorClasses, ac);
            int hd = Array.IndexOf(h.DamageTypes, dt), ha = Array.IndexOf(h.ArmorClasses, ac);
            var (x, y) = (b.DamageVsArmor[bd, ba], h.DamageVsArmor[hd, ha]);
            if (x.Raw != y.Raw)
                r.Entries.Add(new Entry(Kind.MatrixCell, $"{dt} vs {ac}",
                    $"{x.ToDoubleForDisplay():0.##} -> {y.ToDoubleForDisplay():0.##}"));
        }

        // --- factions
        var bf = b.Factions.Keys.ToHashSet(StringComparer.Ordinal);
        var hf = h.Factions.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var id in hf.Except(bf).OrderBy(x => x, StringComparer.Ordinal))
            r.Entries.Add(new Entry(Kind.FactionAdded, id, $"{h.Factions[id].RosterIds.Length} units"));
        foreach (var id in bf.Except(hf).OrderBy(x => x, StringComparer.Ordinal))
            r.Entries.Add(new Entry(Kind.FactionRemoved, id, ""));
        foreach (var id in bf.Intersect(hf).OrderBy(x => x, StringComparer.Ordinal))
        {
            var (x, y) = (b.Factions[id], h.Factions[id]);
            if (!x.RosterIds.SequenceEqual(y.RosterIds, StringComparer.Ordinal))
                r.Entries.Add(new Entry(Kind.FactionChanged, id,
                    $"roster [{string.Join(' ', x.RosterIds)}] -> [{string.Join(' ', y.RosterIds)}]"));
        }

        return r;
    }
}
