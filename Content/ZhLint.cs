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
        /// <summary>Retail art a faction depends on. Only ever populated when --no-borrow
        /// asked; an empty list means "not checked" as often as it means "clean", which is why
        /// the caller prints the check's own verdict rather than inferring one from here.</summary>
        public List<string> Borrowed = new();
        public int Checked;
        /// <summary>A borrow is a failure only because it was explicitly asked about. The
        /// default posture of this project is that adopting shipped art is FINE — 2,928 models
        /// are referenced by nothing and free to use — so this must never fail a pack that did
        /// not opt in.</summary>
        public bool Ok => CapErrors.Count == 0 && Borrowed.Count == 0;
    }

    /// <summary>KindOf names verified to exist in retail content. Ours may be a subset.</summary>
    private static readonly HashSet<string> RealKindOf = new(StringComparer.Ordinal)
    {
        "STRUCTURE", "FS_FACTORY", "CASH_GENERATOR", "MP_COUNT_FOR_VICTORY",
        "VEHICLE", "INFANTRY", "AIRCRAFT", "SELECTABLE", "CAN_ATTACK", "IMMOBILE", "SCORE",
        // CAPTURABLE is real (KindOf.cpp bit table, 177 retail uses). "GARRISONABLE" is NOT
        // and must never be added back — see the note in Runtime/KindOf.cs.
        "CAPTURABLE",
    };

    public static Report Check(ContentDb db, ZhTargetDto zh, AssetIndex? assets = null,
                               string? noBorrowFaction = null, ArtProfiles? art = null)
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

        // A MAPPING is not a MODEL. Having a zh.models entry was the only thing checked here,
        // and whether the file existed was nobody's job — so a typo, or an authored mesh not
        // yet built, produced a unit that moves, shoots, dies and cannot be seen or clicked,
        // with no error anywhere. Exactly the shape of `desertA`, the TerrainType whose
        // texture ships in no archive, which reached a real match before anyone noticed.
        if (assets is { IsUsable: true })
        {
            var shipped = zh.Art.Select(Path.GetFileName)
                                .Where(f => f is not null).Select(f => f!)
                                .ToList();
            var packIdx = AssetIndex.Load(null, shipped);
            foreach (var name in zh.Models.Values.Distinct(StringComparer.OrdinalIgnoreCase)
                                                 .OrderBy(v => v, StringComparer.Ordinal))
                if (!assets.Has(name) && !packIdx.Has(name))
                    r.CapErrors.Add($"model '{name}' resolves to no file — not in the {assets.Count} " +
                                    $"assets installed, and not shipped by zh.art. The unit would be " +
                                    $"INVISIBLE in game, with no error from the engine.");
        }

        // ---- ZERO BORROW ---------------------------------------------------------------
        // "Does this faction depend on any of EA's art?" Opt-in, and scoped to ONE FACTION on
        // purpose, because that is what the property turns out to be.
        //
        // *This was measured, not assumed:* `rts compile` emits the WHOLE content db, so the
        // base sim's demo units are in every compiled directory and they DO adopt retail
        // meshes. A check written against the output directory would therefore fail forever,
        // for a reason that has nothing to do with the faction being checked. Zero-borrow is a
        // property of a FACTION, not of a directory.
        //
        // What it does NOT cover, deliberately: `zh.sides` maps a faction onto a retail
        // ControlBarScheme, so the score screen, watermark and medallions are inherited BY
        // NAME from assets already installed. That borrow is the design — content art is ours,
        // HUD furniture is theirs — and folding it in here would turn a working decision into
        // a permanent failure.
        //
        // And what it does not MEAN: a pack that passes is clean of EA's assets. It is not
        // thereby redistributable. The pack is a mod — it loads into their engine, needs their
        // game installed, and their GPL release grants no trademark or publicity rights.
        if (!string.IsNullOrWhiteSpace(noBorrowFaction))
        {
            if (!db.Factions.TryGetValue(noBorrowFaction!, out var fac))
            {
                r.Borrowed.Add($"--no-borrow names faction '{noBorrowFaction}', which this pack " +
                               $"does not define. Known: " +
                               string.Join(", ", db.Factions.Keys.OrderBy(k => k, StringComparer.Ordinal)));
            }
            else
            {
                // Index by leaf name, so a zh.art path and a TEXTURE_NAME can be compared.
                var carried = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in zh.Art)
                {
                    var leaf = Path.GetFileName(p);
                    if (!string.IsNullOrEmpty(leaf)) carried[leaf] = p;
                }
                var carriedStems = new HashSet<string>(
                    carried.Keys.Select(Path.GetFileNameWithoutExtension)
                                .Where(s => !string.IsNullOrEmpty(s))!,
                    StringComparer.OrdinalIgnoreCase);

                foreach (var uid in fac.RosterIds.OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (!zh.Models.TryGetValue(uid, out var model) || string.IsNullOrWhiteSpace(model))
                        continue;                       // already reported by the mapping check
                    if (!carriedStems.Contains(model))
                    {
                        r.Borrowed.Add($"unit '{uid}' uses model '{model}', which this pack does " +
                                       $"not carry — it resolves into the installed game.");
                        continue;
                    }
                    // THE DEEP ONE. A carried mesh can still name a retail texture, because
                    // that reference lives inside the .w3d and no INI-level check can see it.
                    // In game it renders perfectly, right up until EA's file is not there.
                    var path = carried.First(kv =>
                        string.Equals(Path.GetFileNameWithoutExtension(kv.Key), model,
                                      StringComparison.OrdinalIgnoreCase)).Value;
                    foreach (var tex in W3dRefs.Textures(path))
                        if (!carried.ContainsKey(tex))
                            r.Borrowed.Add($"unit '{uid}': its mesh '{model}' names texture " +
                                           $"'{tex}', which this pack does not carry.");

                    // THE THIRD CHANNEL, and until now it was unchecked: DEATH PRESENTATION.
                    //
                    // The compiler does not author a unit's death from nothing — it looks the
                    // MODEL up in the art profiles and adopts whatever FXList and
                    // ObjectCreationList retail attaches to that mesh, because a FINAL OCL is
                    // mesh-specific (OCL_CrusaderTurret throws THAT tank's turret) and guessing
                    // one bolts the wrong debris to the wrong hull. Correct for adopted art,
                    // and a silent borrow for authored art the moment a profile has anything to
                    // say about it.
                    //
                    // Profiles are keyed by MODEL NAME with the reference catalogue merged
                    // under the pack's own, so this fires on the hazard that matters: an
                    // authored model NAMED LIKE A RETAIL ONE inherits its death FX, its debris,
                    // its turret bone and its geometry, all without a single reference in the
                    // pack for anything else to see. A mesh you built and a name you did not
                    // check is enough.
                    //
                    // The rule is the pack's own prefix. Everything ZhFx emits is
                    // "<pack>_...", so a name without it resolves into the installed game.
                    if (art is not null && art.TryGet(model, out var prof))
                    {
                        foreach (var (what, name) in new[]
                                 {
                                     ("FX = INITIAL", prof.DeathFX),
                                     ("OCL = MIDPOINT", prof.DeathOCL),
                                     ("FX = FINAL", prof.DeathFXFinal),
                                     ("OCL = FINAL", prof.DeathOCLFinal),
                                 })
                        {
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (name!.StartsWith(db.PackName + "_", StringComparison.OrdinalIgnoreCase))
                                continue;
                            r.Borrowed.Add($"unit '{uid}': its death emits '{what} {name}', which " +
                                           $"this pack does not author — the art profile for " +
                                           $"model '{model}' supplied it.");
                        }
                    }
                }
            }
        }

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

        // Capture DURATION has no ZH equivalent: theirs is driven by the capture animation's
        // length, which is art we do not have and cannot emit. The building still compiles as
        // CAPTURABLE and is still takeable in game — it just changes hands on their clock,
        // not the one the harness measured against.
        int capturable = db.Units.Count(u => u.CaptureTicks > 0);
        if (capturable > 0)
            r.Divergence.Add($"{capturable} capturable structure(s): captureTicks is NOT emitted. ZH times " +
                             $"capture by the capture animation, so in-game capture speed is theirs, not ours. " +
                             $"KindOf CAPTURABLE and any AutoDepositUpdate do compile.");

        if (db.HasGarrison)
            r.Divergence.Add("garrison: we make occupants strictly untargetable and let a ClearsGarrison " +
                             "weapon spill its damage through the host. ZH resolves occupancy geometrically " +
                             "— occupants fire from window positions and a big footprint extends their reach — " +
                             "so engagement ranges around a held building will differ from the harness.");

        // The rank ladder is a GLOBAL SINGLETON in ZH: Rank.ini blocks are numbered 1..5, not
        // named, so emitting ours would REPLACE retail's rather than add to it — the one place
        // in this compiler where additive emission is impossible by construction. A pack that
        // retunes the ladder therefore plays on retail's thresholds in game.
        // The map DOES cross over now, and this category used to say the opposite. What is
        // left is not "terrain cannot transfer" but a much narrower and more useful claim:
        // which SURFACES transfer, and at what resolution.
        //
        // Their engine has no authored passability layer at all — a cell is a cliff when the
        // corners of one 10-unit cell differ by more than 9.8 units. So a blocked cell is
        // emitted as a plateau and their pathfinder derives the block. Only the two surfaces
        // that mean "ground cannot pass" survive that translation: water needs a water
        // PolygonTrigger and rubble needs its own pass, and neither is written yet, so they
        // become ordinary open ground rather than something silently wrong.
        if (db.Map is { } pm)
        {
            int blocked = 0, soft = 0;
            for (int c = 0; c < pm.CellCount; c++)
            {
                var s = pm.At(c);
                if (s is Runtime.Surface.Cliff or Runtime.Surface.Impassable) blocked++;
                else if (s != Runtime.Surface.Clear) soft++;
            }

            // Their height grid is 10 of their units; ours is a power of two of ours. The two
            // have no common divisor, so the map is RESAMPLED and a feature thinner than one
            // of their cells can vanish in the process. Report the ratio rather than the
            // reassurance: a one-cell gate at a coarse ratio is the case that silently closes.
            double zhPerOurCell = pm.CellSize.ToDoubleForDisplay() * zh.Scale / 10.0;
            if (blocked > 0 && zhPerOurCell < 1.0)
                r.Divergence.Add($"map resolution: one authored cell is {zhPerOurCell:0.00} of a ZH " +
                                 $"pathfind cell, so features narrower than {Math.Ceiling(1 / zhPerOurCell)} " +
                                 $"cells may not survive the resample. Raise zh.worldScale or cellSize.");
            // LINE OF SIGHT DOES NOT TRANSFER, and this is the reverse of what the terrain
            // work assumed. Their engine HAS the check — Weapon::isClearFiringLineOfSightTerrain
            // — and its one call site in TurretAI.cpp is COMMENTED OUT, with a note that some
            // weapons (Tomahawk) must fire beyond LOS. The only live consumer is
            // Pathfinder::isAttackViewBlockedByObstacle, which uses it to pick where to STAND,
            // and which skips it entirely for KINDOF_IMMOBILE. Verified identical in
            // GeneralsX's own tree, i.e. the source of the running build.
            // Consequence: a wall that gives cover HERE gives only a detour THERE.
            int sight = 0;
            for (int c = 0; c < pm.CellCount; c++)
                if (Runtime.PassabilityGrid.BlocksSight(pm.At(c))) sight++;
            if (sight > 0)
                r.Divergence.Add($"{sight} cell(s) block LINE OF SIGHT here and will not there. " +
                                 $"ZH checks firing LOS nowhere: Weapon::isClearFiringLineOfSightTerrain " +
                                 $"is commented out at its only call site (TurretAI.cpp), and terrain LOS " +
                                 $"survives only in the pathfinder's choice of firing position. Cover is " +
                                 $"OURS alone — a measurement that depends on it does not transfer.");

            if (soft > 0)
                r.Divergence.Add($"{soft} water/rubble cell(s) are emitted as OPEN GROUND. Water needs a " +
                                 $"PolygonTrigger with isWater and rubble has no height analogue; only " +
                                 $"cliff and impassable become terrain their pathfinder blocks on.");
        }

        if (db.Ranks.Length > 0)
            r.Divergence.Add($"the {db.Ranks.Length}-rank ladder is NOT emitted. ZH's Rank blocks are numbered, " +
                             $"not named, so writing ours would overwrite retail's 0/800/1500/2500/5000 instead " +
                             $"of adding to it. Sciences compile; the thresholds that pay for them do not.");

        // A science compiles as a Science block, but what OUR sciences DO has no carrier:
        // theirs gate CommandButtons and OCLSpecialPowers, they do not set an upgrade bit.
        int granting = db.Sciences.Count(sc => sc.GrantsFlags != 0);
        if (granting > 0)
            r.Divergence.Add($"{granting} science(s) grant team flags, which is NOT emitted. A ZH science gates " +
                             $"buttons and special powers; it does not set the PLAYER_UPGRADE bit a conditional " +
                             $"variant keys off. The Science block compiles, the loadout switch it drives does not.");

        if (db.Powers.Length > 0)
            r.Divergence.Add($"{db.Powers.Length} activated power(s) are NOT emitted. Their SpecialPowerType is a " +
                             $"CLOSED C++ enum — an invented name is a hard load error — so a genuinely new power " +
                             $"cannot be added by data at all. This is their wall, not ours.");

        if (db.HasPower)
            r.Divergence.Add("power: we halt ALL production on a negative grid; ZH disables individual " +
                             "KINDOF_POWERED structures instead. Brownout behaviour will differ.");

        return r;
    }
}
