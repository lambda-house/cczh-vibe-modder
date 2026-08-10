using System.Globalization;
using System.Text;
using RtsSkeleton.Core;
using RtsSkeleton.Runtime;

namespace RtsSkeleton.Content;

/// <summary>
/// Compile a resolved pack into Zero Hour <c>Data/INI</c> that the real engine will load.
///
/// This is the pivot from "a simulator shaped like ZH" to "an authoring engine for ZH":
/// content is designed and MEASURED here, then run there.
///
/// The output is ADDITIVE, which is the whole trick. For every content type the engine does
/// <c>loadFileDirectory(X)</c> — it loads <c>X.ini</c> and THEN scans the directory
/// <c>X/</c>. Writing <c>Data/INI/Weapon/&lt;pack&gt;.ini</c> therefore appends our weapons to
/// retail's 363 instead of replacing the file and deleting them all. Emitting
/// <c>Data/INI/Weapon.ini</c> would break the base game outright; emitting into the
/// subdirectory cannot.
///
/// Deliberately NOT emitted:
///   - <c>Data/Generals.str</c>. Release builds prefer the .str over the compiled .csf, so
///     writing one shadows all 6,422 retail strings and every label in the game becomes
///     MISSING. Opt in with --with-strings when the pack is a full conversion.
///   - art of any kind. Units reference models that already exist; see ZhTargetDto.Models.
/// </summary>
public static class ZhCompiler
{
    public sealed class Result
    {
        public List<string> Files = new();
        public List<string> Warnings = new();
        public List<string> Errors = new();
        public int Objects, Weapons, Armors, Locomotors, Buttons, Sets, Templates;
    }

    /// <summary>Footprint radius for a structure. Shared so the production exit's
    /// NaturalRallyPoint stays equal to GeometryMajorRadius — retail's own comment insists on
    /// it, and a smaller value spawns finished units inside the building.</summary>
    /// <summary>
    /// Footprint radius for a structure, and it must match the BORROWED MODEL's real size.
    /// 22 was invented and it hid every unit we produced: the ABWarFact mesh is built for a
    /// 53 radius, so a unit created at the exit point of a 22-radius box appeared INSIDE the
    /// visible building. Production completed, the object existed, nothing was on screen.
    /// 53/60 are AmericaWarFactory's own numbers — the object that uses this model.
    /// Adopting art means adopting its dimensions; geometry is not a free parameter.
    /// </summary>
    private const string StructureRadius = "53.0";
    private const string StructureMinorRadius = "60.0";

    private static string F(Fix64 v) => v.ToDoubleForDisplay().ToString("0.###", CultureInfo.InvariantCulture);
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static Result Compile(ContentDb db, ZhTargetDto zh, string outRoot, bool withStrings)
    {
        var r = new Result();
        string ini = Path.Combine(outRoot, "Data", "INI");
        string pack = Sanitize(db.PackName);

        // Emitted names are prefixed so a compiled pack can never collide with retail
        // content — the same reason ZH prefixes its own generals, except ours is one line
        // of code rather than 202,322 lines of duplication.
        string P(string id) => pack + "_" + Sanitize(id);

        var dmg = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dt in db.DamageTypes)
        {
            if (zh.DamageTypes.TryGetValue(dt, out var mapped)) dmg[dt] = mapped;
            else { r.Errors.Add($"zh.damageTypes has no mapping for '{dt}' (their DamageType is a closed 38-value enum)"); dmg[dt] = "EXPLOSION"; }
        }

        // ---- Armor: one block per armor class, a row per damage type -----------------
        var sb = new StringBuilder();
        Banner(sb, db, "Armor");
        for (int a = 0; a < db.ArmorClasses.Length; a++)
        {
            sb.AppendLine($"Armor {P(db.ArmorClasses[a])}Armor");
            sb.AppendLine("  Armor = DEFAULT 100%");
            for (int d = 0; d < db.DamageTypes.Length; d++)
            {
                double pct = db.DamageVsArmor[d, a].ToDoubleForDisplay() * 100.0;
                sb.AppendLine($"  Armor = {dmg[db.DamageTypes[d]]} {F(pct)}%");
            }
            sb.AppendLine("End").AppendLine();
            r.Armors++;
        }
        Write(r, Path.Combine(ini, "Armor", pack + ".ini"), sb);

        // ---- Weapons ------------------------------------------------------------------
        sb = new StringBuilder();
        Banner(sb, db, "Weapon");
        foreach (var w in db.Weapons)
        {
            // Their durations are MILLISECONDS; ours are 30 Hz ticks, and their loader
            // re-quantises with ceil(). FLOOR the conversion, never round: rounding 8 ticks
            // up to 267ms ceils back to 9 frames — a silent 12.5% loss of rate of fire, and
            // exactly the trap that makes their own DragonTank read 250 DPS and deliver 150.
            // Flooring round-trips every value exactly.
            int msPerShot = (int)Math.Floor(w.CooldownTicks * 1000.0 / ContentDb.TicksPerSecond);
            sb.AppendLine($"Weapon {P(w.Id)}");
            sb.AppendLine($"  PrimaryDamage = {F(w.Damage)}");
            sb.AppendLine($"  PrimaryDamageRadius = {F(Math.Sqrt(w.PrimaryRadiusSq.ToDoubleForDisplay()) * zh.WorldScale)}");
            if (w.SecondaryRadiusSq > Fix64.Zero)
            {
                sb.AppendLine($"  SecondaryDamage = {F(w.SecondaryDamage)}");
                sb.AppendLine($"  SecondaryDamageRadius = {F(Math.Sqrt(w.SecondaryRadiusSq.ToDoubleForDisplay()) * zh.WorldScale)}");
            }
            sb.AppendLine($"  AttackRange = {F(w.Range.ToDoubleForDisplay() * zh.WorldScale)}");
            if (w.MinRangeSq > Fix64.Zero)
                sb.AppendLine($"  MinimumAttackRange = {F(Math.Sqrt(w.MinRangeSq.ToDoubleForDisplay()) * zh.WorldScale)}");
            sb.AppendLine($"  DamageType = {dmg[db.DamageTypes[w.DamageTypeIdx]]}");
            sb.AppendLine("  DeathType = NORMAL");
            sb.AppendLine("  WeaponSpeed = 1000");
            sb.AppendLine($"  DelayBetweenShots = {msPerShot}");
            if (w.ClipSize > 0)
            {
                sb.AppendLine($"  ClipSize = {w.ClipSize}");
                sb.AppendLine($"  ClipReloadTime = {(int)Math.Floor(w.ClipReloadTicks * 1000.0 / ContentDb.TicksPerSecond)}");
            }
            sb.AppendLine("  ProjectileObject = None");
            if (w.ClearsGarrison) sb.AppendLine("  AllowAttackGarrisonedBldgs = Yes");
            sb.AppendLine("  RadiusDamageAffects = ALLIES ENEMIES NEUTRALS");
            sb.AppendLine("End").AppendLine();
            r.Weapons++;
        }
        Write(r, Path.Combine(ini, "Weapon", pack + ".ini"), sb);

        // ---- Locomotors: one per mobile prototype --------------------------------------
        sb = new StringBuilder();
        Banner(sb, db, "Locomotor");
        foreach (var u in db.Units)
        {
            if (u.IsStructure) continue;
            double spd = u.BaseStats[(int)Stat.Speed].ToDoubleForDisplay()
                         * ContentDb.TicksPerSecond * zh.WorldScale;
            sb.AppendLine($"Locomotor {P(u.Id)}Loco");
            sb.AppendLine("  Surfaces = GROUND");
            sb.AppendLine($"  Speed = {F(spd)}");
            sb.AppendLine("  TurnRate = 180");
            sb.AppendLine("  Acceleration = 1000");
            sb.AppendLine("  Braking = 1000");
            sb.AppendLine("  MinTurnSpeed = 0");
            // NO_Z_MOTIVE_FORCE, not the plausible-sounding NO_Z_MOTION: their enums are
            // closed and parsed with parseIndexList, so a wrong name is a hard load error.
            // Values here are ones retail actually uses, not ones that read correctly.
            sb.AppendLine("  ZAxisBehavior = NO_Z_MOTIVE_FORCE");
            sb.AppendLine("  Appearance = TREADS");
            sb.AppendLine("End").AppendLine();
            r.Locomotors++;
        }
        Write(r, Path.Combine(ini, "Locomotor", pack + ".ini"), sb);

        // ---- Upgrades: the carrier for conditional variants -----------------------------
        // An upgrade in ZH holds NO effect data — it is a named boolean with a cost. The
        // effect comes from a module on the object that watches for it (WeaponSetUpgrade,
        // ArmorUpgrade) and a condition-keyed set that switches when the bit flips. Our
        // flag-keyed variants are the same shape, which is why they map at all.
        //
        // The hard limit: an object has ONE PLAYER_UPGRADE condition bit, so only the FIRST
        // variant of a unit can be expressed. That single-bit restriction is what forced
        // retail into 268 ConflictsWith lines; we report the loss rather than inherit it.
        var upgradeFor = new Dictionary<ulong, string>();          // required-mask -> upgrade name
        var variantUnits = db.Units.Where(u => u.Variants.Length > 0).ToList();
        if (variantUnits.Count > 0)
        {
            sb = new StringBuilder();
            Banner(sb, db, "Upgrade");
            foreach (var u in variantUnits)
            {
                var v = u.Variants[0];
                if (v.Required == 0 || upgradeFor.ContainsKey(v.Required)) continue;

                // Name and price the upgrade after the flag that gates it. Where the flag
                // came from a tech node, inherit that node's real cost and time so the
                // in-game purchase matches what the harness charged.
                string flagName = db.Flags.Describe(v.Required).Replace('|', '_');
                string up = $"Upgrade_{P(flagName)}";
                upgradeFor[v.Required] = up;

                var tech = db.Tech.FirstOrDefault(t => t.Id == db.Flags.Describe(v.Required));
                sb.AppendLine($"Upgrade {up}");
                sb.AppendLine($"  DisplayName = UPGRADE:{P(flagName)}");
                sb.AppendLine($"  BuildTime = {F((tech?.ResearchTicks ?? 300) / (double)ContentDb.TicksPerSecond)}");
                sb.AppendLine($"  BuildCost = {tech?.Cost ?? 1000}");
                sb.AppendLine("  ButtonImage = SSAdvancedTraining");
                sb.AppendLine("End").AppendLine();
            }
            Write(r, Path.Combine(ini, "Upgrade", pack + ".ini"), sb);
        }

        // ---- Sciences: the second currency ---------------------------------------------
        // Additive like everything else, and confirmed against the RUNNING engine rather than
        // the source: EA's GameEngine.cpp passes no directory to TheScienceStore, but the
        // build we target logs loadDirectory('Data\\INI\\Science'). Schema authority is their
        // source; LOADING behaviour is whatever the runtime actually does, and the two differ.
        //
        // Rank prerequisites are deliberately omitted rather than guessed: their SCIENCE_RankN
        // are entries in a GLOBAL ladder we do not emit (see ZhLint), so naming one here would
        // bind our science to a rank ladder whose thresholds are retail's, not the pack's.
        if (db.Sciences.Length > 0)
        {
            sb = new StringBuilder();
            Banner(sb, db, "Science");
            foreach (var sc in db.Sciences)
            {
                sb.AppendLine($"Science {P(sc.Id)}");
                string prereq = sc.RequiresIdx.Length == 0
                    ? "None"
                    : string.Join(" ", sc.RequiresIdx.Select(i => P(db.Sciences[i].Id)));
                sb.AppendLine($"  PrerequisiteSciences = {prereq}");
                sb.AppendLine($"  SciencePurchasePointCost = {sc.Cost}");
                sb.AppendLine("  IsGrantable = Yes");
                sb.AppendLine("End").AppendLine();
            }
            Write(r, Path.Combine(ini, "Science", pack + ".ini"), sb);
        }

        // ---- ObjectCreationLists: the "make objects" indirection for spawn rules --------
        // ZH cannot spawn from a die module directly; the module names an OCL and the OCL
        // holds the nuggets. Only CreateObject matters to us — of their 7 nugget types just
        // CreateObject (237 uses) and DeliverPayload (58) are sim-relevant, the rest is debris.
        var oclFor = new Dictionary<string, string>(StringComparer.Ordinal);
        sb = new StringBuilder();
        Banner(sb, db, "ObjectCreationList");
        foreach (var u in db.Units)
        {
            var spawns = u.Rules.Where(r => r.On == RuleEvent.Death)
                                .SelectMany(r => r.Effects)
                                .Where(e => e.Kind == EffectKind.Spawn && e.ProtoIdx >= 0)
                                .ToList();
            if (spawns.Count == 0) continue;

            string ocl = $"OCL_{P(u.Id)}_Death";
            oclFor[u.Id] = ocl;
            sb.AppendLine($"ObjectCreationList {ocl}");
            foreach (var e in spawns)
            {
                sb.AppendLine(" CreateObject");
                sb.AppendLine($"   ObjectNames = {P(db.Units[e.ProtoIdx].Id)}");
                sb.AppendLine($"   Count = {e.Count}");
                // Their placement vocabulary is a closed Disposition enum, not a radius.
                // SPREAD_FORMATION is the nearest thing to our jitter; the exact scatter will
                // differ from the harness, which the divergence report says out loud.
                sb.AppendLine("   Disposition = ON_GROUND_ALIGNED");
                if (e.Spread > Fix64.Zero)
                {
                    // SpreadFormation is a BOOL; the distance lives in MinDistanceAFormation.
                    // Emitting the radius into the bool is a type error their parser rejects.
                    // Copied from SUPERWEAPON_RebelAmbush1, which loads.
                    sb.AppendLine("   SpreadFormation = Yes");
                    sb.AppendLine($"   MinDistanceAFormation = {F(e.Spread.ToDoubleForDisplay() * zh.WorldScale)}");
                }
                sb.AppendLine(" End");
            }
            sb.AppendLine("End").AppendLine();
        }
        if (oclFor.Count > 0) Write(r, Path.Combine(ini, "ObjectCreationList", pack + ".ini"), sb);

        // ---- Objects -------------------------------------------------------------------
        sb = new StringBuilder();
        Banner(sb, db, "Object");
        foreach (var u in db.Units)
        {
            // A faction variant is the same unit with tweaks, so it inherits the base unit's
            // model unless the pack overrides it — otherwise every general would have to
            // restate art it never changed, which is the duplication we exist to avoid.
            if (!zh.Models.TryGetValue(u.Id, out var model)
                && !(u.IsVariant && zh.Models.TryGetValue(u.RosterId, out model)))
            {
                r.Errors.Add($"zh.models has no model for unit '{u.Id}' — an Object with no Draw is invisible in game");
                model = "AVCrusader";
            }
            string draw = zh.DrawModules.TryGetValue(u.Id, out var dm) ? dm : "W3DModelDraw";
            var weapon = db.Weapons[u.WeaponIdx];

            sb.AppendLine($"Object {P(u.Id)}");
            sb.AppendLine($"  Side = {Sanitize(u.FactionId)}");
            // Portrait and button art. Without SelectPortrait the selected object shows an
            // empty tile in the control bar — cosmetic, but it reads as "broken", and both
            // are just names of images the player already has installed.
            sb.AppendLine($"  SelectPortrait = {(u.IsStructure ? "SACWeaponsfact_L" : "SACLeopard_L")}");
            sb.AppendLine($"  ButtonImage = {(u.IsStructure ? "SACWeaponsfact" : "SACLeopard")}");
            sb.AppendLine($"  EditorSorting = {(u.IsStructure ? "STRUCTURE" : "VEHICLE")}");
            sb.AppendLine($"  BuildCost = {u.Cost}");
            sb.AppendLine($"  BuildTime = {F(u.BuildTicks / (double)ContentDb.TicksPerSecond)}");
            sb.AppendLine($"  VisionRange = {F(weapon.Range.ToDoubleForDisplay() * zh.WorldScale * 1.2)}");
            sb.AppendLine($"  ShroudClearingRange = {F(weapon.Range.ToDoubleForDisplay() * zh.WorldScale * 1.5)}");

            var kinds = new List<string> { "SELECTABLE", "CAN_ATTACK", "SCORE" };
            kinds.AddRange(u.KindOf.Where(k => !k.StartsWith("IS_") && k != "MP_COUNT_FOR_VICTORY"));
            if (u.IsStructure) { kinds.Add("IMMOBILE"); kinds.Add("MP_COUNT_FOR_VICTORY"); }
            sb.AppendLine($"  KindOf = {string.Join(' ', kinds.Distinct(StringComparer.Ordinal))}");

            if (u.PrereqObjectIdx.Length > 0)
            {
                sb.AppendLine("  Prerequisites");
                foreach (int p in u.PrereqObjectIdx)
                    sb.AppendLine($"    Object = {P(db.Units[p].Id)}");
                sb.AppendLine("  End");
            }

            sb.AppendLine($"  Body = {(u.IsStructure ? "StructureBody" : "ActiveBody")} ModuleTag_Body");
            sb.AppendLine($"    MaxHealth = {F(u.BaseStats[(int)Stat.MaxHp])}");
            sb.AppendLine("  End");

            // Base loadout, then the upgraded one. Their selector picks the best-matching
            // condition set at runtime, so both sets are declared and the PLAYER_UPGRADE bit
            // decides which is live — exactly how retail swaps a Humvee's guns.
            var v0 = u.Variants.Length > 0 && u.Variants[0].Required != 0
                     && upgradeFor.TryGetValue(u.Variants[0].Required, out var upName)
                     ? u.Variants[0] : null;

            sb.AppendLine("  ArmorSet");
            sb.AppendLine("    Conditions = None");
            sb.AppendLine($"    Armor = {P(db.ArmorClasses[u.ArmorClassIdx])}Armor");
            sb.AppendLine("  End");
            if (v0 is not null && v0.ArmorClassIdx >= 0)
            {
                sb.AppendLine("  ArmorSet");
                sb.AppendLine("    Conditions = PLAYER_UPGRADE");
                sb.AppendLine($"    Armor = {P(db.ArmorClasses[v0.ArmorClassIdx])}Armor");
                sb.AppendLine("  End");
            }

            sb.AppendLine("  WeaponSet");
            sb.AppendLine("    Conditions = None");
            sb.AppendLine($"    Weapon = PRIMARY {P(weapon.Id)}");
            sb.AppendLine("  End");
            if (v0 is not null && v0.WeaponIdx >= 0)
            {
                sb.AppendLine("  WeaponSet");
                sb.AppendLine("    Conditions = PLAYER_UPGRADE");
                sb.AppendLine($"    Weapon = PRIMARY {P(db.Weapons[v0.WeaponIdx].Id)}");
                sb.AppendLine("  End");
            }

            if (!u.IsStructure)
            {
                // A Locomotor line writes into the object's AIUpdate module data, so the
                // module must exist or the loader throws.
                sb.AppendLine("  Behavior = AIUpdateInterface ModuleTag_AI");
                sb.AppendLine("  End");
                sb.AppendLine($"  Locomotor = SET_NORMAL {P(u.Id)}Loco");
            }

            // Death rules become their die modules. Both literals below are copied from
            // retail blocks that load: FireWeaponWhenDeadBehavior (203 uses) and
            // CreateObjectDie (566 uses) are the two most common death responses in the game
            // after the purely cosmetic FXListDie.
            // The modules that watch the upgrade bit. They take NO parameters — they flip a
            // condition and the sets above do the rest, which is why ZH's 173 ArmorUpgrade
            // and 117 WeaponSetUpgrade instances are pure data.
            if (v0 is not null && upgradeFor.TryGetValue(v0.Required, out var trig))
            {
                if (v0.WeaponIdx >= 0)
                {
                    sb.AppendLine("  Behavior = WeaponSetUpgrade ModuleTag_UpW");
                    sb.AppendLine($"    TriggeredBy = {trig}");
                    sb.AppendLine("  End");
                }
                if (v0.ArmorClassIdx >= 0)
                {
                    sb.AppendLine("  Behavior = ArmorUpgrade ModuleTag_UpA");
                    sb.AppendLine($"    TriggeredBy = {trig}");
                    sb.AppendLine("  End");
                }
                // A variant's stat modifiers have exactly one carrier in ZH: MaxHealthUpgrade.
                // It takes an ABSOLUTE amount, not a factor, so a ×1.10 becomes base×0.10 —
                // exact for this upgrade alone, and the source of the veterancy composition
                // difference ZhLint reports. Every other stat is reported as dropped.
                int hpTag = 0;
                foreach (var m in v0.Modifiers)
                {
                    if (m.Stat != Stat.MaxHp) continue;
                    Fix64 bas = u.BaseStats[(int)Stat.MaxHp];
                    double add = m.Op == ModOp.Add
                        ? m.Value.ToDoubleForDisplay()
                        : bas.ToDoubleForDisplay() * (m.Value.ToDoubleForDisplay() - 1.0);
                    sb.AppendLine($"  Behavior = MaxHealthUpgrade ModuleTag_UpHp{hpTag++}");
                    sb.AppendLine($"    TriggeredBy = {trig}");
                    sb.AppendLine($"    AddMaxHealth = {F(add)}");
                    sb.AppendLine("    ChangeType = ADD_CURRENT_HEALTH_TOO");
                    sb.AppendLine("  End");
                }
            }

            // Garrison. Copied field-for-field from a retail GarrisonContain that loads;
            // ImmuneToClearBuildingAttacks is spelled out as No rather than omitted because
            // it is the switch our whole counter mechanic turns on, and a reader of the
            // emitted file should see which way it points.
            if (u.GarrisonCapacity > 0)
            {
                sb.AppendLine("  Behavior = GarrisonContain ModuleTag_Garrison");
                sb.AppendLine($"    ContainMax = {u.GarrisonCapacity}");
                sb.AppendLine("    EnterSound = GarrisonEnter");
                sb.AppendLine("    ExitSound = GarrisonExit");
                sb.AppendLine("    ImmuneToClearBuildingAttacks = No");
                sb.AppendLine("  End");
            }

            // Their AutoDepositUpdate is milliseconds, and InitialCaptureBonus is present in
            // every retail instance — emitted explicitly as 0 rather than left to a default
            // we have not verified.
            if (u.DepositAmount > 0 && u.DepositTicks > 0)
            {
                sb.AppendLine("  Behavior = AutoDepositUpdate ModuleTag_Deposit");
                sb.AppendLine($"    DepositTiming = {(int)Math.Floor(u.DepositTicks * 1000.0 / ContentDb.TicksPerSecond)}");
                sb.AppendLine($"    DepositAmount = {u.DepositAmount}");
                sb.AppendLine("    InitialCaptureBonus = 0");
                sb.AppendLine("  End");
            }

            // Every mobile retail object carries PhysicsBehavior, and without it a ground
            // unit is never driven by its locomotor: it is created, it renders a shadow, and
            // it sits inside the building it came from, unselectable and unable to leave.
            // Mass is the only field retail sets — 50 for a tank, 5 for infantry.
            if (!u.IsStructure)
            {
                sb.AppendLine("  Behavior = PhysicsBehavior ModuleTag_Physics");
                sb.AppendLine($"    Mass = {(u.Is("INFANTRY") ? "5.0" : "50.0")}");
                sb.AppendLine("  End");
            }

            // A factory with a CommandSet renders BUTTONS but cannot BUILD. Production needs
            // two more modules, and their absence is silent: the menu appears, the buttons
            // draw, and clicking one does nothing. Both copied from AmericaWarFactory.
            //
            // DefaultProductionExitUpdate is not optional decoration either — it is the door
            // the finished unit walks out of, and its NaturalRallyPoint X must match
            // GeometryMajorRadius or units spawn inside the building's own footprint.
            if (u.Is(Runtime.KindOf.Factory))
            {
                sb.AppendLine("  Behavior = ProductionUpdate ModuleTag_Production");
                // ZERO doors, and this is load-bearing. ProductionUpdate emits the finished
                // unit when `m_numDoorAnimations == 0 || door->m_doorWaitOpenFrame != 0`
                // (ProductionUpdate.cpp:819) — with a door count above zero it waits for a
                // DOOR_1_OPENING model condition state to finish, and our Draw module has only
                // a DefaultConditionState. The queue then stalls forever with no error: the
                // menu works, the button clicks, and nothing is ever produced.
                // Retail's 26 factories all use 1 because their models HAVE door animations.
                sb.AppendLine("    NumDoorAnimations = 0");
                sb.AppendLine("    ConstructionCompleteDuration = 0");
                sb.AppendLine("  End");
                sb.AppendLine("  Behavior = DefaultProductionExitUpdate ModuleTag_ProductionExit");
                // Offset, not dead centre: retail's war factory uses X:-10 Y:-30 against a
                // 53 radius, i.e. inside the footprint but away from the origin.
                sb.AppendLine("    UnitCreatePoint = X:-10.0 Y:-30.0 Z:0.0");
                // Y matches the create point so the unit drives straight out of the same
                // side it was made on, exactly as AmericaWarFactory does.
                sb.AppendLine($"    NaturalRallyPoint = X:{StructureRadius} Y:-30.0 Z:0.0");
                sb.AppendLine("  End");
            }

            int dieTag = 0;
            foreach (var rule in u.Rules)
            {
                if (rule.On != RuleEvent.Death) continue;
                foreach (var e in rule.Effects)
                {
                    if (e.Kind == EffectKind.DamageInRadius && e.WeaponIdx >= 0)
                    {
                        sb.AppendLine($"  Behavior = FireWeaponWhenDeadBehavior ModuleTag_Die{dieTag++}");
                        sb.AppendLine($"    DeathWeapon = {P(db.Weapons[e.WeaponIdx].Id)}");
                        sb.AppendLine("    StartsActive = Yes");
                        sb.AppendLine("  End");
                    }
                }
            }
            if (oclFor.TryGetValue(u.Id, out var myOcl))
            {
                sb.AppendLine($"  Behavior = CreateObjectDie ModuleTag_Die{dieTag++}");
                sb.AppendLine($"    CreationList = {myOcl}");
                sb.AppendLine("  End");
            }
            // Something must actually remove the corpse; without a Die module the object
            // lingers. DestroyDie is retail's default (754 uses).
            sb.AppendLine($"  Behavior = DestroyDie ModuleTag_Die{dieTag}");
            sb.AppendLine("  End");

            sb.AppendLine($"  Draw = {draw} ModuleTag_Draw");
            sb.AppendLine("    DefaultConditionState");
            sb.AppendLine($"      Model = {model}");
            sb.AppendLine("    End");
            sb.AppendLine("  End");

            sb.AppendLine($"  Geometry = {(u.IsStructure ? "BOX" : "BOX")}");
            sb.AppendLine($"  GeometryMajorRadius = {(u.IsStructure ? StructureRadius : "13.0")}");
            if (u.IsStructure)
            {
                // Both were missing against retail's war factory. BuildCompletion is authored
                // in our content and was simply never emitted; FactoryExitWidth defaults to 0
                // and reserves the space a produced unit needs to get clear of the building.
                sb.AppendLine($"  BuildCompletion = {(u.BuildCompletion == BuildCompletion.PlacedByPlayer ? "PLACED_BY_PLAYER" : "APPEARS_AT_RALLY_POINT")}");
                if (u.Is(Runtime.KindOf.Factory)) sb.AppendLine("  FactoryExitWidth = 25");
            }
            sb.AppendLine($"  GeometryMinorRadius = {(u.IsStructure ? StructureMinorRadius : "10.0")}");
            sb.AppendLine($"  GeometryHeight = {(u.IsStructure ? "40.0" : "10.0")}");
            sb.AppendLine($"  CommandSet = {P(u.Id)}CommandSet");
            sb.AppendLine("End").AppendLine();
            r.Objects++;
        }
        Write(r, Path.Combine(ini, "Object", pack + ".ini"), sb);

        // ---- CommandButtons and CommandSets: what makes a unit REACHABLE ---------------
        // A unit with perfect stats and no button is invisible to the player. This is the
        // "emittable but not simulated" half of the model, and it is not optional.
        sb = new StringBuilder();
        Banner(sb, db, "CommandButton");
        foreach (var u in db.Units)
        {
            if (u.Cost <= 0) continue;
            sb.AppendLine($"CommandButton Command_Build{P(u.Id)}");
            // Every literal below is copied from a retail button that loads, not invented.
            // Their enums are closed and parsed with parseIndexList: a plausible-sounding
            // name like "CANCELABLE" or "NO_Z_MOTION" is a hard load error, and the round
            // trip is the only thing that catches it. Real UNIT_BUILD buttons set no Options.
            sb.AppendLine($"  Command = {(u.IsStructure ? "DOZER_CONSTRUCT" : "UNIT_BUILD")}");
            sb.AppendLine($"  Object = {P(u.Id)}");
            sb.AppendLine($"  TextLabel = CONTROLBAR:{P(u.Id)}");
            sb.AppendLine("  ButtonImage = SACLeopard");
            sb.AppendLine("  ButtonBorderType = BUILD");
            sb.AppendLine("End").AppendLine();
            r.Buttons++;
        }
        // An upgrade nobody can buy is an upgrade that never fires, so every emitted
        // Upgrade gets a purchase button. Copied from Command_UpgradeAmericaAdvancedTraining.
        foreach (var up in upgradeFor.Values.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            sb.AppendLine($"CommandButton Command_{up}");
            sb.AppendLine("  Command = PLAYER_UPGRADE");
            sb.AppendLine($"  Upgrade = {up}");
            sb.AppendLine($"  TextLabel = CONTROLBAR:{up}");
            sb.AppendLine("  ButtonImage = SSAdvancedTraining");
            sb.AppendLine("  ButtonBorderType = UPGRADE");
            sb.AppendLine("End").AppendLine();
            r.Buttons++;
        }
        Write(r, Path.Combine(ini, "CommandButton", pack + ".ini"), sb);

        sb = new StringBuilder();
        Banner(sb, db, "CommandSet");
        foreach (var u in db.Units)
        {
            // Only a factory shows a build menu; everything else gets an empty set so the
            // Object's CommandSet reference always resolves.
            sb.AppendLine($"CommandSet {P(u.Id)}CommandSet");
            if (u.Is(KindOf.Factory))
            {
                int slot = 1;
                foreach (var b in db.Units)
                {
                    if (b.Cost <= 0 || b.IsStructure) continue;
                    // Same faction only. A unit whose Side differs from the producing
                    // structure's is filtered out by the control bar anyway, so listing it
                    // burns one of the 14 slots on a button that can never do anything.
                    if (!string.Equals(b.FactionId, u.FactionId, StringComparison.Ordinal)) continue;
                    if (slot > MaxCommandSlots) { r.Warnings.Add($"'{u.Id}' build menu truncated at {MaxCommandSlots} slots (their cap)"); break; }
                    sb.AppendLine($"  {slot++} = Command_Build{P(b.Id)}");
                }
                // Every retail factory carries these two in slots 13/14. Without them the bar
                // shows only build tiles: no rally point, no sell. They are retail COMMANDS we
                // reference by name, not content we define.
                sb.AppendLine("  13 = Command_SetRallyPoint");
                sb.AppendLine("  14 = Command_Sell");
                // Upgrades share the factory's 14 slots with its units — their cap, not ours.
                foreach (var up in upgradeFor.Values.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (slot > MaxCommandSlots) { r.Warnings.Add($"'{u.Id}' menu full; upgrade '{up}' unreachable"); break; }
                    sb.AppendLine($"  {slot++} = Command_{up}");
                }
            }
            sb.AppendLine("End").AppendLine();
            r.Sets++;
        }
        Write(r, Path.Combine(ini, "CommandSet", pack + ".ini"), sb);

        // An upgrade nobody can reach is a variant that never fires — the unit ships with its
        // base loadout and the pack plays differently from what the harness measured. Our sim
        // researches from a team queue with no building; theirs needs a button on something.
        if (upgradeFor.Count > 0 && !db.Units.Any(u => u.Is(KindOf.Factory)))
            r.Warnings.Add($"{upgradeFor.Count} upgrade(s) emitted but the pack has no factory to buy them from; " +
                           $"compiled units keep their base loadout. Add a KindOf FS_FACTORY structure.");

        // ---- PlayerTemplates ------------------------------------------------------------
        sb = new StringBuilder();
        Banner(sb, db, "PlayerTemplate");
        foreach (var fid in db.FactionOrder)
        {
            var f = db.Factions[fid];
            if (!f.IsStartable) { r.Warnings.Add($"faction '{fid}' has no startingBuilding; emitted as unplayable"); }
            bool playable = f.IsStartable;

            // Which retail side's chrome this faction borrows. UI only — none of it is
            // simulated, and all of it is required for the faction to be SELECTABLE.
            string baseSide = zh.Sides.TryGetValue(fid, out var bs) ? bs : "USA";
            var ui = UiChrome(baseSide);

            sb.AppendLine($"PlayerTemplate Faction{Sanitize(fid)}");
            sb.AppendLine($"  Side = {Sanitize(fid)}");
            sb.AppendLine($"  BaseSide = {baseSide}");
            sb.AppendLine($"  PlayableSide = {(playable ? "Yes" : "No")}");
            sb.AppendLine($"  DisplayName = INI:Faction{Sanitize(fid)}");
            sb.AppendLine($"  StartMoney = {(f.StartMoney >= 0 ? f.StartMoney : 0)}");
            sb.AppendLine("  PreferredColor = R:0 G:0 B:255");
            if (f.StartingBuildingIdx >= 0)
                sb.AppendLine($"  StartingBuilding = {P(db.Units[f.StartingBuildingIdx].Id)}");
            for (int i = 0; i < f.StartingUnitIdx.Length && i < MaxStartingUnits; i++)
                sb.AppendLine($"  StartingUnit{i} = {P(db.Units[f.StartingUnitIdx[i]].Id)}");
            if (f.StartingUnitIdx.Length > MaxStartingUnits)
                r.Warnings.Add($"faction '{fid}' has {f.StartingUnitIdx.Length} starting units; their cap is {MaxStartingUnits}");
            sb.AppendLine($"  IntrinsicSciences = {ui.Science}");
            // Presentation, borrowed by NAME from assets the player already has installed.
            // A playable faction without these is not merely ugly — several are read
            // unconditionally by the skirmish setup screen.
            sb.AppendLine($"  ScoreScreenImage = {ui.ScoreScreen}");
            sb.AppendLine($"  LoadScreenImage = {ui.LoadScreen}");
            sb.AppendLine($"  LoadScreenMusic = {ui.LoadMusic}");
            sb.AppendLine($"  ScoreScreenMusic = {ui.ScoreMusic}");
            sb.AppendLine($"  FlagWaterMark = {ui.Watermark}");
            sb.AppendLine($"  EnabledImage = {ui.Enabled}");
            sb.AppendLine("  BeaconName = MultiplayerBeacon");
            sb.AppendLine($"  SideIconImage = {ui.SideIcon}");
            sb.AppendLine($"  GeneralImage = {ui.General}");
            sb.AppendLine($"  MedallionRegular = {ui.MedRegular}");
            sb.AppendLine($"  MedallionHilite = {ui.MedHilite}");
            sb.AppendLine($"  MedallionSelect = {ui.MedSelect}");
            sb.AppendLine("End").AppendLine();
            r.Templates++;
        }
        Write(r, Path.Combine(ini, "PlayerTemplate", pack + ".ini"), sb);

        // ---- ControlBarSchemes: without one, a new faction has NO command bar -------------
        // ControlBarSchemeManager::setControlBarSchemeByPlayer matches CBScheme->m_side
        // against the player's Side and leaves the bar unset when nothing matches. Retail
        // ships schemes for America, China, GLA and Observer only, so every faction we invent
        // needs its own or it is selectable and unplayable.
        //
        // DELIBERATELY MINIMAL. Retail's blocks are 87 lines of layout, and restating them
        // would copy EA's content into our output for no simulation benefit. We emit the few
        // fields that decide whether the bar FUNCTIONS and reference their image names; the
        // background art comes from ControlBar.wnd regardless. Expect a plain bar, not a
        // broken one.
        //
        // Note the syntax: these fields take NO '=' sign. "Side America", not "Side = America".
        if (db.FactionOrder.Any(fid => db.Factions[fid].IsStartable))
        {
            var cb = new StringBuilder();
            Banner(cb, db, "ControlBarScheme");
            foreach (var fid in db.FactionOrder)
            {
                if (!db.Factions[fid].IsStartable) continue;
                var ui = UiChrome(zh.Sides.TryGetValue(fid, out var bs2) ? bs2 : "USA");
                cb.AppendLine($"ControlBarScheme {Sanitize(fid)}8x6");
                cb.AppendLine("  ScreenCreationRes X:800 Y:600");
                cb.AppendLine($"  Side {Sanitize(fid)}");
                cb.AppendLine("  QueueButtonImage SCBigButton");
                cb.AppendLine($"  RightHUDImage {ui.HudLogo}");
                cb.AppendLine("  CommandBarBorderColor R:0 G:21 B:126 A:255");
                cb.AppendLine("  BuildUpClockColor R:0 G:0 B:0 A:160");
                cb.AppendLine("  ButtonBorderBuildColor R:67 G:108 B:190 A:255");
                cb.AppendLine("  ButtonBorderActionColor R:1 G:175 B:2 A:255");
                cb.AppendLine("  ButtonBorderUpgradeColor R:208 G:108 B:0 A:255");
                cb.AppendLine("  ButtonBorderSystemColor R:207 G:195 B:2 A:255");
                cb.AppendLine($"  GenBarButtonIn {ui.GenBarIn}");
                cb.AppendLine($"  GenBarButtonOn {ui.GenBarOn}");
                cb.AppendLine("End").AppendLine();
            }
            Write(r, Path.Combine(ini, "ControlBarScheme", pack + ".ini"), cb);
        }

        // ---- Overrides: modifications to units that ALREADY EXIST in the target game -----
        //
        // Emitted to <mapdir>/map.ini, never to Data/INI, and the split is not stylistic:
        //   - Redeclaring a block in Data/INI aborts the engine at 29 of 42 subsystems.
        //   - map.ini is the sole INI_LOAD_CREATE_OVERRIDES path, so it is the only place a
        //     redeclaration is legal. It reloads every match from startNewGame.
        //
        // Two rules are enforced BY CONSTRUCTION here rather than left to the author:
        //   - A new weapon/locomotor leaf goes to Data/INI (already emitted above) and the
        //     override only REPOINTS the object at it. Patching a shared leaf in place makes
        //     it unreachable and the weapon inert — a Ranger's rifle went 5 damage -> zero.
        //   - Nothing new is ever DECLARED in map.ini. A new name there is marked as an
        //     override, and LocomotorStore::reset() erases without reassigning its iterator,
        //     hanging the game on match teardown.
        if (db.Overrides.Length > 0)
        {
            var mi = new StringBuilder();
            Banner(mi, db, "map.ini overrides");
            mi.AppendLine("; Drop this beside a .map as <mapdir>/map.ini. It is re-read on every match start,");
            mi.AppendLine("; so iterating on these values costs a match restart, not a game restart.").AppendLine();

            // Locomotors for speed overrides are NEW NAMES, so they belong in Data/INI.
            var loco = new StringBuilder();
            Banner(loco, db, "Locomotor (for overrides)");
            bool anyLoco = false;

            foreach (var o in db.Overrides)
            {
                string locoName = $"{pack}_{Sanitize(o.Id)}Loco";
                if (o.Speed > Fix64.Zero)
                {
                    anyLoco = true;
                    loco.AppendLine($"Locomotor {locoName}");
                    loco.AppendLine("  Surfaces = GROUND RUBBLE");
                    loco.AppendLine($"  Speed = {F(o.Speed.ToDoubleForDisplay() * ContentDb.TicksPerSecond * zh.WorldScale)}");
                    loco.AppendLine("  TurnRate = 500");
                    loco.AppendLine("  Acceleration = 200");
                    loco.AppendLine("  Braking = 200");
                    loco.AppendLine("  MinTurnSpeed = 0");
                    loco.AppendLine("  ZAxisBehavior = NO_Z_MOTIVE_FORCE");
                    loco.AppendLine("  Appearance = TWO_LEGS");
                    loco.AppendLine("  StickToGround = Yes");
                    loco.AppendLine("End").AppendLine();
                }

                foreach (var target in o.Targets)
                {
                    mi.AppendLine($"Object {target}");
                    if (o.Scale > 0) mi.AppendLine($"  Scale = {F(o.Scale)}");
                    if (o.Model is not null)
                    {
                        // ReplaceModule, not AddModule: same module type, and a NEW unique tag.
                        mi.AppendLine($"  ReplaceModule {o.ModelReplacesTag}");
                        mi.AppendLine($"    Draw = W3DModelDraw {o.ModelReplacesTag}_Override");
                        mi.AppendLine("      OkToChangeModelColor = Yes");
                        mi.AppendLine("      DefaultConditionState");
                        mi.AppendLine($"        Model = {o.Model}");
                        if (o.IdleAnimation is not null)
                        {
                            mi.AppendLine($"        IdleAnimation = {o.IdleAnimation}");
                            mi.AppendLine("        AnimationMode = ONCE");
                        }
                        mi.AppendLine("      End");
                        mi.AppendLine("    End");
                        mi.AppendLine("  End");
                    }
                    if (o.WeaponIdx >= 0)
                    {
                        // Declaring one WeaponSet CLEARS every inherited one — setCopiedFromDefault
                        // makes the first parsed set wipe the list. Total, never additive.
                        mi.AppendLine("  WeaponSet");
                        mi.AppendLine("    Conditions = None");
                        mi.AppendLine($"    Weapon = PRIMARY {P(db.Weapons[o.WeaponIdx].Id)}");
                        mi.AppendLine("  End");
                    }
                    if (o.Speed > Fix64.Zero)
                        mi.AppendLine($"  Locomotor = SET_NORMAL {locoName}");
                    mi.AppendLine("End").AppendLine();
                }
            }

            Write(r, Path.Combine(outRoot, "map.ini"), mi);
            if (anyLoco) Write(r, Path.Combine(ini, "Locomotor", pack + "_overrides.ini"), loco);
            r.Warnings.Add($"{db.Overrides.Length} override(s) emitted to map.ini — copy it beside a .map " +
                           $"(e.g. Maps/<name>/map.ini). Data/INI files install as usual.");
        }

        if (withStrings)
        {
            var st = new StringBuilder();
            foreach (var u in db.Units)
                st.AppendLine($"CONTROLBAR:{P(u.Id)}").AppendLine($"\"{u.Id}\"").AppendLine("END").AppendLine();
            foreach (var fid in db.FactionOrder)
                st.AppendLine($"INI:Faction{Sanitize(fid)}").AppendLine($"\"{fid}\"").AppendLine("END").AppendLine();
            Write(r, Path.Combine(outRoot, "Data", "Generals.str"), st);
            r.Warnings.Add("Generals.str SHADOWS all 6,422 retail strings — every other label becomes MISSING. " +
                           "Only correct for a full conversion.");
        }
        else
        {
            r.Warnings.Add("no strings emitted: labels render as MISSING:'...' in game. " +
                           "Cosmetic only; --with-strings replaces the whole retail string table.");
        }

        return r;
    }

    /// <summary>Their caps, enforced here so a pack fails at compile rather than at load.</summary>
    public const int MaxCommandSlots = 14;      // ControlBar.wnd exposes 14 of MAX_COMMANDS_PER_SET 18
    public const int MaxStartingUnits = 10;     // MAX_MP_STARTING_UNITS
    public const int MaxUpgrades = 128;         // UpgradeMaskType is BitFlags<128>

    private static void Banner(StringBuilder sb, ContentDb db, string kind)
    {
        sb.AppendLine($"; {kind} — generated by rts compile --target zh");
        sb.AppendLine($"; pack '{db.PackName}'  contentHash={db.ContentHash:x16}");
        sb.AppendLine("; ADDITIVE: loaded from the Data/INI/<type>/ directory scan, so retail content is untouched.");
        sb.AppendLine("; Do not hand-edit — regenerate from the pack.");
        sb.AppendLine();
    }

    private static void Write(Result r, string path, StringBuilder sb)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // CRLF: their parser is a 2003 Windows line reader and retail INI is CRLF throughout.
        File.WriteAllText(path, sb.ToString().ReplaceLineEndings("\r\n"));
        r.Files.Add(path);
    }

    /// <summary>
    /// Presentation asset names for a retail base side. Every value is an identifier that
    /// already exists in the installed game — we point at their art, we do not ship it.
    /// Copied from the shipping PlayerTemplate blocks, so each one is known to resolve.
    /// </summary>
    private static (string Science, string ScoreScreen, string LoadScreen, string LoadMusic,
                    string ScoreMusic, string Watermark, string Enabled, string SideIcon,
                    string General, string MedRegular, string MedHilite, string MedSelect,
                    string HudLogo, string GenBarIn, string GenBarOn)
        UiChrome(string baseSide) => baseSide switch
    {
        "China" => ("SCIENCE_CHINA", "China_ScoreScreen", "SAFactionLogoPage_China", "Load_China",
                    "Score_China", "WatermarkChina", "SSObserverChina", "GameinfoChina",
                    "China_Logo", "ChinaGeneral_slvr", "ChinaGeneral_blue", "ChinaGeneral_orng",
                    "SNLogo", "SNBarButtonGen2IN", "SNBarButtonGen2ON"),
        "GLA" => ("SCIENCE_GLA", "GLA_ScoreScreen", "SAFactionLogoPage_GLA", "Load_GLA",
                  "Score_GLA", "WatermarkGLA", "SSObserverGLA", "GameinfoGLA",
                  "GLA_Logo", "GLAGeneral_slvr", "GLAGeneral_blue", "GLAGeneral_orng",
                  "SULogo", "SUBarButtonGen2IN", "SUBarButtonGen2ON"),
        _ => ("SCIENCE_AMERICA", "America_ScoreScreen", "SAFactionLogoPage_US", "Load_USA",
              "Score_USA", "WatermarkUSA", "SSObserverUSA", "GameinfoAMRCA",
              "USA_Logo", "USAGeneral_slvr", "USAGeneral_blue", "USAGeneral_orng",
              "SALogo", "SABarButtonGen2IN", "SABarButtonGen2ON"),
    };

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}
