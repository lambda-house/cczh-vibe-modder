using RtsSkeleton.Core;
using RtsSkeleton.Runtime;

namespace RtsSkeleton.Content;

/// <summary>
/// Check a pack against the Zero Hour target: their hard caps, what survives compilation
/// unchanged, and what silently does not.
///
/// Three separate questions, deliberately kept apart because they fail differently:
///
///   CAPS        — their limits are compile-time C++ constants. Over one and the mod does
///                 not load, or loads with content silently truncated. Hard errors.
///   ROUND-TRIP  — we emit seconds and milliseconds; their loader re-quantises to 30 Hz
///                 frames with ceil(). A value that does not survive that trip means the
///                 unit we MEASURED is not the unit that ships.
///   DIVERGENCE  — features we simulate that have no ZH equivalent. These are the dangerous
///                 ones: the pack lints, compiles, loads and plays, and behaves differently
///                 from the numbers you tuned against. Silence here would be a lie.
/// </summary>
public static class ZhLint
{
    public sealed class Report
    {
        public List<string> CapErrors = new();
        public List<string> RoundTrip = new();
        public List<string> Divergence = new();
        public int Checked;
        public bool Ok => CapErrors.Count == 0;
    }

    /// <summary>KindOf names verified to exist in retail content. Ours may be a subset.</summary>
    private static readonly HashSet<string> RealKindOf = new(StringComparer.Ordinal)
    {
        "STRUCTURE", "FS_FACTORY", "CASH_GENERATOR", "MP_COUNT_FOR_VICTORY",
        "VEHICLE", "INFANTRY", "AIRCRAFT", "SELECTABLE", "CAN_ATTACK", "IMMOBILE", "SCORE",
    };

    public static Report Check(ContentDb db, ZhTargetDto zh)
    {
        var r = new Report();

        // ---- CAPS -------------------------------------------------------------------
        // Measured from their source, not guessed: ControlBar.wnd exposes 14 of
        // MAX_COMMANDS_PER_SET 18; MAX_MP_STARTING_UNITS is 10; LEVEL_COUNT is 4;
        // DAMAGE_NUM_TYPES is 38; UpgradeMaskType is BitFlags<128> with 82 already used.
        int buildables = db.Units.Count(u => u.Cost > 0 && !u.IsStructure);
        if (buildables > ZhCompiler.MaxCommandSlots)
            r.CapErrors.Add($"{buildables} buildable units but a command set holds {ZhCompiler.MaxCommandSlots}; " +
                            $"the menu would truncate. Split production across more factories.");

        foreach (var fid in db.FactionOrder)
        {
            var f = db.Factions[fid];
            if (f.StartingUnitIdx.Length > ZhCompiler.MaxStartingUnits)
                r.CapErrors.Add($"faction '{fid}' has {f.StartingUnitIdx.Length} starting units; their cap is {ZhCompiler.MaxStartingUnits}");
        }

        foreach (var t in db.VetTracks)
            if (t.RankBundles.Length > 3)
                r.CapErrors.Add($"veterancy '{t.Id}' has {t.RankBundles.Length} ranks above regular; " +
                                $"their LEVEL_COUNT is 4 (regular + 3)");

        if (db.DamageTypes.Length > 38)
            r.CapErrors.Add($"{db.DamageTypes.Length} damage types; their DamageType enum holds 38");
        foreach (var dt in db.DamageTypes)
            if (!zh.DamageTypes.ContainsKey(dt))
                r.CapErrors.Add($"damage type '{dt}' has no zh.damageTypes mapping — their enum is closed");

        foreach (var u in db.Units)
            if (!zh.Models.ContainsKey(u.Id) && !(u.IsVariant && zh.Models.ContainsKey(u.RosterId)))
                r.CapErrors.Add($"unit '{u.Id}' has no zh.models entry — it would be invisible in game");

        // ---- ROUND-TRIP --------------------------------------------------------------
        // We author ticks; we emit seconds/milliseconds; their loader ceils back to frames.
        // Anything that does not land on the same tick means the shipped unit differs from
        // the measured one — which is exactly the trap that makes ZH's own DragonTank read
        // as 250 DPS in the file and deliver 150.
        foreach (var u in db.Units)
        {
            r.Checked++;
            double secs = u.BuildTicks / (double)ContentDb.TicksPerSecond;
            int back = (int)Math.Ceiling(secs * ContentDb.TicksPerSecond - 1e-9);
            if (back != u.BuildTicks)
                r.RoundTrip.Add($"unit '{u.Id}': buildTicks {u.BuildTicks} -> {secs:0.###}s -> reads back {back}");
        }

        foreach (var w in db.Weapons)
        {
            r.Checked++;
            int ms = (int)Math.Floor(w.CooldownTicks * 1000.0 / ContentDb.TicksPerSecond);
            int back = (int)Math.Ceiling(ms / 1000.0 * ContentDb.TicksPerSecond - 1e-9);
            if (back != w.CooldownTicks)
                r.RoundTrip.Add($"weapon '{w.Id}': cooldown {w.CooldownTicks} ticks -> {ms}ms -> reads back {back} " +
                                $"({100.0 * (back - w.CooldownTicks) / w.CooldownTicks:+0.#;-0.#}% rate of fire)");

            if (w.ClipSize > 0 && w.ClipReloadTicks > 0)
            {
                r.Checked++;
                int rms = (int)Math.Floor(w.ClipReloadTicks * 1000.0 / ContentDb.TicksPerSecond);
                int rback = (int)Math.Ceiling(rms / 1000.0 * ContentDb.TicksPerSecond - 1e-9);
                if (rback != w.ClipReloadTicks)
                    r.RoundTrip.Add($"weapon '{w.Id}': clip reload {w.ClipReloadTicks} ticks -> {rms}ms -> reads back {rback}");
            }
        }

        // ---- DIVERGENCE ---------------------------------------------------------------
        // Everything here compiles, loads and plays — and behaves differently from what the
        // harness measured. Listed loudest-first by how much it moves a result.

        var spread = db.Weapons.Where(w => w.Spread > Fix64.Zero).Select(w => w.Id).ToList();
        if (spread.Count > 0)
            r.Divergence.Add($"spread is DROPPED on {spread.Count} weapon(s) ({string.Join(", ", spread.Take(4))}" +
                             $"{(spread.Count > 4 ? ", …" : "")}). We model inaccuracy as a damage multiplier drawn " +
                             $"per shot; ZH models it geometrically (ScatterRadius makes the projectile miss). " +
                             $"They are not interchangeable, so compiled damage is the un-spread mean.");

        // Death rules mostly compile now: damageInRadius -> FireWeaponWhenDeadBehavior,
        // spawn -> ObjectCreationList + CreateObjectDie. Report only what still does not,
        // per effect kind, so the list shrinks as the compiler grows rather than staying a
        // blanket warning nobody reads.
        var effects = db.Units.SelectMany(u => u.Rules).SelectMany(rule => rule.Effects).ToList();

        int money = effects.Count(e => e.Kind == EffectKind.GrantMoney);
        if (money > 0)
            r.Divergence.Add($"{money} grantMoney effect(s) are NOT emitted. Their nearest mechanic is " +
                             $"CreateCrateDie, which drops a crate the KILLER must collect — not money to " +
                             $"the owner. Compiling it as a crate would change who benefits, so we don't.");

        // A flag granted by a death rule has no ZH carrier. Their upgrades are PURCHASED —
        // GrantUpgradeCreate exists but grants to the dying object, which is about to stop
        // existing, not to its player. Emitting it would look right and do nothing.
        int flags = effects.Count(e => e.Kind == EffectKind.GrantFlag);
        if (flags > 0)
            r.Divergence.Add($"{flags} grantFlag effect(s) are NOT emitted. We emit Upgrade blocks for flags " +
                             $"that gate a variant, but those are bought, not awarded: ZH has no module that " +
                             $"grants a PLAYER upgrade on death, only GrantUpgradeCreate scoped to the object.");

        int spawns = effects.Count(e => e.Kind == EffectKind.Spawn && e.Spread > Fix64.Zero);
        if (spawns > 0)
            r.Divergence.Add($"{spawns} spawn effect(s) use a jitter radius; ZH places from a closed " +
                             $"Disposition enum (emitted as SpreadFormation), so scatter geometry will " +
                             $"differ from the harness. Counts and identities are exact.");

        // Variants now compile to Upgrade + condition-keyed WeaponSet/ArmorSet + the matching
        // *Upgrade behavior. What does NOT survive is a unit with more than one: an object
        // carries exactly ONE PLAYER_UPGRADE condition bit, so variants 2..n have nowhere to
        // live. This is their limitation, not ours — it is why retail needed 268 ConflictsWith
        // lines to keep upgrades from overlapping on the same object.
        var multi = db.Units.Where(u => u.Variants.Length > 1).ToList();
        foreach (var u in multi)
            r.Divergence.Add($"unit '{u.Id}' has {u.Variants.Length} variants; only the first compiles. " +
                             $"An object has ONE PLAYER_UPGRADE condition bit, so " +
                             $"[{string.Join(", ", u.Variants.Skip(1).Select(v => db.Flags.Describe(v.Required)))}] " +
                             $"never take effect in game. Split the unit, or fold the loadouts into one flag.");

        // MaxHp is the only variant stat with a ZH carrier (MaxHealthUpgrade). Speed has
        // LocomotorSetUpgrade, but it swaps a whole named Locomotor rather than scaling one,
        // so a factor cannot be expressed; the rest have no module at all.
        var droppedStats = db.Units.Where(u => u.Variants.Length > 0)
            .SelectMany(u => u.Variants[0].Modifiers)
            .Where(m => m.Stat != Stat.MaxHp)
            .Select(m => m.Stat.ToString()).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (droppedStats.Count > 0)
            r.Divergence.Add($"variant modifiers on {string.Join(", ", droppedStats)} are DROPPED — ZH has an " +
                             $"upgrade module only for MaxHp. Express those as a swapped weapon or armor class " +
                             $"in the variant instead, which does compile.");

        bool hpVariant = db.Units.Any(u => u.Variants.Length > 0 && u.Variants[0].Modifiers.Any(m => m.Stat == Stat.MaxHp));
        if (hpVariant)
            r.Divergence.Add("variant MaxHp compiles as MaxHealthUpgrade, whose AddMaxHealth is ABSOLUTE. " +
                             "We convert a factor against the base, so it is exact alone but composes " +
                             "additively with veterancy rather than by our op.");

        // Their upgrade namespace is a BitFlags<128> shared with retail's 82. A pack that
        // overruns it does not fail loudly — it fails at the 47th upgrade, in someone's game.
        int upgrades = db.Units.Where(u => u.Variants.Length > 0)
                               .Select(u => u.Variants[0].Required).Where(m => m != 0)
                               .Distinct().Count();
        if (upgrades > ZhCompiler.MaxUpgrades - 82)
            r.CapErrors.Add($"{upgrades} upgrades needed but their UpgradeMaskType is BitFlags<{ZhCompiler.MaxUpgrades}> " +
                            $"with 82 already spent by retail — {ZhCompiler.MaxUpgrades - 82} bits are free.");

        foreach (var t in db.VetTracks)
            foreach (var bundle in t.RankBundles)
                foreach (var m in bundle)
                    if (m.Op == ModOp.Mul)
                    {
                        r.Divergence.Add($"veterancy '{t.Id}' uses Mul; ZH composes situational bonuses " +
                                         $"additively-in-excess, so stacked bonuses resolve lower there than here. " +
                                         $"Use the Excess op to match.");
                        goto doneVet;
                    }
        doneVet:

        foreach (var name in db.Units.SelectMany(u => u.KindOf).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            if (!RealKindOf.Contains(name))
                r.Divergence.Add($"kindOf '{name}' is ours, not theirs — it drives our sim but is stripped on " +
                                 $"compile, so it has no in-game effect.");

        if (db.HasPower)
            r.Divergence.Add("power: we halt ALL production on a negative grid; ZH disables individual " +
                             "KINDOF_POWERED structures instead. Brownout behaviour will differ.");

        return r;
    }
}
