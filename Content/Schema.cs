namespace RtsSkeleton.Content;

/// <summary>
/// Raw deserialization targets for the content pack. This is the "AI-open" surface:
/// a closed vocabulary of component/modifier types with open parameters. Generators
/// (human or model) emit documents in this shape; ContentDb validates and compiles
/// them into dense runtime tables. No behavior lives here — only data.
/// </summary>
public sealed class ContentPackDto
{
    public MetaDto Meta { get; set; } = new();
    public DefaultsDto Defaults { get; set; } = new();
    public List<string> DamageTypes { get; set; } = new();
    public List<string> ArmorClasses { get; set; } = new();
    /// <summary>
    /// damageType -> armorClass -> multiplier. The reserved key "default" sets the value
    /// for every armor class not named in that row. ZH does this positionally
    /// (`Armor = DEFAULT n%` blast-fills all 38 slots and its POSITION in the block changes
    /// the result — correct in all 38 shipping armor sets only because every author wrote
    /// it first). A declared field cannot be order-sensitive.
    /// </summary>
    public Dictionary<string, Dictionary<string, double>> DamageVsArmor { get; set; } = new();
    public Dictionary<string, WeaponDto> Weapons { get; set; } = new();
    public Dictionary<string, VeterancyTrackDto> VeterancyTracks { get; set; } = new();
    public TechDto Tech { get; set; } = new();
    /// <summary>Commander ranks in ascending order. Empty = no second currency in this pack.</summary>
    public List<RankDto> Ranks { get; set; } = new();
    public Dictionary<string, ScienceDto> Sciences { get; set; } = new();
    public Dictionary<string, PowerDto> Powers { get; set; } = new();
    /// <summary>Modifications to units that already exist in the target game. Keyed by a
    /// name of OUR choosing, so a later pack can amend or remove one by name.</summary>
    public Dictionary<string, OverrideDto> Overrides { get; set; } = new();
    public Dictionary<string, UnitDto> Units { get; set; } = new();
    public Dictionary<string, FactionDto> Factions { get; set; } = new();
    public ZhTargetDto Zh { get; set; } = new();
    public LintConfigDto Lint { get; set; } = new();
}

public sealed class MetaDto
{
    public string Name { get; set; } = "unnamed";
    public int Version { get; set; } = 1;
}

public sealed class WeaponDto
{
    public string DamageType { get; set; } = "";
    public double Damage { get; set; }
    public double Range { get; set; }
    public int CooldownTicks { get; set; }
    /// <summary>Symmetric damage spread, e.g. 0.1 => multiplier drawn from [0.9, 1.1).</summary>
    public double Spread { get; set; }

    // --- ZH's Weapon vocabulary. All default to 0/absent, so a pack that ignores them
    // --- behaves exactly as before and its hashes do not move.

    /// <summary>
    /// Splash. ZH's model is a TWO-BAND STEP FUNCTION with no falloff — inside
    /// primaryDamageRadius you take primaryDamage, inside secondaryDamageRadius you take
    /// secondaryDamage, beyond that nothing (Weapon.cpp:1462 is the whole thing). Simpler
    /// and cheaper than a curve, and every balance number in the corpus assumes it, so
    /// adding smooth falloff would silently invalidate all of them.
    /// 0 means single-target.
    /// </summary>
    public double PrimaryDamageRadius { get; set; }
    public double SecondaryDamage { get; set; }
    public double SecondaryDamageRadius { get; set; }

    /// <summary>
    /// Burst fire. Shots available before a long reload; 0 means unlimited (fire every
    /// cooldown forever). ZH pairs ClipSize with ClipReloadTime and it is what separates a
    /// machine gun from an artillery piece at equal DPS-on-paper.
    /// </summary>
    public int ClipSize { get; set; }
    public int ClipReloadTicks { get; set; }
    /// <summary>Seconds; quantised with ceil() at load. Wins over ClipReloadTicks.</summary>
    public double? ClipReloadSeconds { get; set; }

    /// <summary>Cannot fire closer than this. Artillery's defining weakness. 0 = none.</summary>
    public double MinimumAttackRange { get; set; }

    /// <summary>
    /// Can hit infantry garrisoned inside a building. ZH: <c>AllowAttackGarrisonedBldgs</c>,
    /// set by 17 of retail's 363 weapons — flame, toxin, flashbang, sniper, C4. That 4.7%
    /// is the entire counter to a held position, and it is the reason garrison is a
    /// tactical mechanic rather than a free damage sponge.
    /// </summary>
    public bool ClearsGarrison { get; set; }
}

/// <summary>
/// One commander rank. ZH's Rank.ini is five of these and that is the entire second
/// currency: skill points earned by KILLING cross a threshold, the rank grants purchase
/// points, and purchase points buy sciences. Their numbers are 0/800/1500/2500/5000 needed
/// and 1/1/1/1/3 granted — seven points, total, for a whole game.
/// </summary>
public sealed class RankDto
{
    public int SkillPointsNeeded { get; set; }
    public int PurchasePointsGranted { get; set; } = 1;
}

/// <summary>
/// A purchasable science. In ZH a science carries no effect data of its own — exactly like
/// an upgrade, it is a name you own, and content keys off it. All 78 purchasable retail
/// sciences cost exactly 1 point; the tree's shape comes entirely from prerequisites.
/// </summary>
public sealed class ScienceDto
{
    public int Cost { get; set; } = 1;
    /// <summary>Other sciences that must already be owned. ZH: PrerequisiteSciences.</summary>
    public List<string>? Requires { get; set; }
    /// <summary>Minimum commander rank. ZH spells this as a SCIENCE_RankN prerequisite.</summary>
    public int RequiresRank { get; set; }
    /// <summary>Team flags granted on purchase — the seam to conditional variants.</summary>
    public List<string>? GrantsFlags { get; set; }
}

/// <summary>
/// An activated commander power: gated by a flag (so a science unlocks it), on a recharge,
/// firing the same closed effect vocabulary the death rules use. ZH's SpecialPowerTemplate
/// plus an OCLSpecialPower module, minus the closed SpecialPowerType enum we cannot extend.
/// </summary>
public sealed class PowerDto
{
    /// <summary>Flag that must be present on the team. Usually granted by a science.</summary>
    public string RequiresFlag { get; set; } = "";
    public int RechargeTicks { get; set; }
    /// <summary>Seconds; quantised with ceil() at load. Wins over RechargeTicks.</summary>
    public double? RechargeSeconds { get; set; }
    public List<EffectDto> Do { get; set; } = new();
}

/// <summary>
/// A modification to a unit that ALREADY EXISTS in the target game.
///
/// Distinct from authoring a unit, and it compiles somewhere else entirely: new content goes
/// to Data/INI, overrides go to a map.ini, and mixing them up is fatal in both directions
/// (see "Modding the retail game" in CLAUDE.md). The author declares intent; the compiler
/// picks the file and the mechanism.
/// </summary>
public sealed class OverrideDto
{
    /// <summary>Target object name IN THE TARGET GAME, e.g. "AmericaInfantryRanger".</summary>
    public string Object { get; set; } = "";

    /// <summary>
    /// Which forks of the target to touch. "base" is the named object alone; "all" also
    /// applies to every general's fork of it.
    ///
    /// Not a convenience. Zero Hour's generals are FORKS, so there are four Ranger objects,
    /// and whether an edit propagates depends on whether the thing you happened to touch is
    /// shared. Guessing wrong is exactly what put 88 bad references into EA's shipping
    /// content; making the author state it is the fix.
    /// </summary>
    public string Scope { get; set; } = "base";

    /// <summary>Prefixes of the target's forks, e.g. ["SupW_","Lazr_","AirF_"]. Required by scope=all.</summary>
    public List<string>? Forks { get; set; }

    /// <summary>Replacement weapon: OUR weapon id. Compiled as a new leaf plus a repoint.</summary>
    public string? Weapon { get; set; }
    /// <summary>Movement speed in OUR units/sec. Compiled as a new Locomotor plus a repoint.</summary>
    public double? Speed { get; set; }
    /// <summary>Visual scale multiplier. A plain ThingTemplate field, applied directly.</summary>
    public double? Scale { get; set; }
    /// <summary>Target-game model name to swap in, e.g. "AIHERO_SKN".</summary>
    public string? Model { get; set; }
    /// <summary>Draw module tag being replaced. Required with <see cref="Model"/>.</summary>
    public string? ModelReplacesTag { get; set; }
    /// <summary>Skeleton.animation for the idle pose, e.g. "AIHERO_SKL.AIHERO_STA 0 25".</summary>
    public string? IdleAnimation { get; set; }
}

public sealed class VeterancyTrackDto
{
    public List<int> Thresholds { get; set; } = new();
    /// <summary>Bundles[i] applies at rank i+1. Bundles are cumulative across ranks.</summary>
    public List<List<ModifierDto>> Ranks { get; set; } = new();
}

/// <summary>
/// One entry in the modifier algebra: (stat, op, value). Upgrades, veterancy,
/// general's powers, auras and achievement perks all reduce to lists of these.
/// The skeleton implements ops Add and Mul with resolve order (base + Σadd) * Πmul.
/// </summary>
public sealed class ModifierDto
{
    public string Stat { get; set; } = "";
    public string Op { get; set; } = "";
    public double Value { get; set; }
}

public sealed class TechDto
{
    public Dictionary<string, TechNodeDto> Nodes { get; set; } = new();
}

public sealed class TechNodeDto
{
    public List<string> Requires { get; set; } = new();
    /// <summary>Money deducted when research starts (queue head, funds available).</summary>
    public int Cost { get; set; }
    public int ResearchTicks { get; set; }
}

public sealed class UnitDto
{
    public string Faction { get; set; } = "";
    public int? Cost { get; set; }
    public int? BuildTicks { get; set; }
    /// <summary>Seconds; quantised to ticks with ceil() at load. Wins over BuildTicks.</summary>
    public double? BuildSeconds { get; set; }

    // --- Zero Hour's Object vocabulary. Their schema is the target model, so these keep
    // --- their names: content and knowledge then transfer in both directions.

    /// <summary>
    /// ZH's taxonomy flag set. In ZH this is a 120-bit mask and the single most load-bearing
    /// field on an Object — it is what makes a thing a structure, a factory, a victory
    /// condition or a valid target. We take the same idea with an open string set, closed by
    /// lint rather than by a compiled enum, because a fixed enum is exactly the wall that
    /// stops ZH modders adding a genuinely new category.
    /// </summary>
    public List<string>? KindOf { get; set; }

    /// <summary>
    /// PLACED_BY_PLAYER (a structure: built at a site) or APPEARS_AT_RALLY_POINT (a unit:
    /// leaves a factory). This one enum is what distinguishes a building from a unit in ZH —
    /// not a separate content type. We copy that: a structure IS a unit with a different
    /// completion mode, speed 0 and role flags.
    /// </summary>
    public string? BuildCompletion { get; set; }

    /// <summary>Net power. Negative consumes. ZH's whole power economy is this one integer.</summary>
    public int? EnergyProduction { get; set; }

    // RefundValue and VisionRange belong here and are deliberately ABSENT until a system
    // consumes them. ZH is full of declared-but-dead schema — ProductionCostChange,
    // ProductionVeterancyLevel and IntrinsicSciencePurchasePoints are parseable and used
    // zero times in retail AND in the community patch — and nothing tells an author which
    // fields are real. A field that does nothing is worse than a missing one: it invites
    // content that silently has no effect.

    /// <summary>Hard cap on live instances per team. ZH: MaxSimultaneousOfType.</summary>
    public int? MaxSimultaneousOfType { get; set; }

    /// <summary>
    /// Occupants this structure holds. ZH: <c>GarrisonContain.ContainMax</c>, whose retail
    /// distribution is startlingly flat — 204 of 236 civilian buildings hold exactly 10, the
    /// rest 1, 4, 5 or 8. Requires KindOf GARRISONABLE; 0 means not garrisonable.
    /// </summary>
    public int? GarrisonCapacity { get; set; }

    /// <summary>
    /// Ticks of uncontested adjacency an enemy needs to flip ownership. Requires KindOf
    /// CAPTURABLE. ZH gates capture on an infantry unit reaching the building; the duration
    /// is ours, because theirs is animation-driven and we have no animations.
    /// </summary>
    public int? CaptureTicks { get; set; }
    /// <summary>Seconds; quantised with ceil() at load. Wins over CaptureTicks.</summary>
    public double? CaptureSeconds { get; set; }

    /// <summary>
    /// Cash paid to the owner every <see cref="DepositTicks"/>. ZH: <c>AutoDepositUpdate</c>
    /// (the oil derrick pays 200 every 12s). This is what makes a neutral building an
    /// economic target rather than scenery.
    /// </summary>
    public int? DepositAmount { get; set; }
    public int? DepositTicks { get; set; }
    /// <summary>Seconds; quantised with ceil() at load. Wins over DepositTicks.</summary>
    public double? DepositSeconds { get; set; }

    /// <summary>Flags this unit carries from birth (e.g. "STEALTHED", "ELITE_TRAINING").</summary>
    public List<string>? Flags { get; set; }

    /// <summary>
    /// Flag-keyed alternate loadouts, tested in order — FIRST MATCH WINS.
    ///
    /// This is ZH's real upgrade mechanism and it is not a modifier stack. `ArmorUpgrade`
    /// (173 uses) and `WeaponSetUpgrade` (117) take ZERO parameters: they set one condition
    /// bit, and a separate condition-keyed ArmorSet/WeaponSet is selected. Weighted by real
    /// usage our (base+Σadd)×Πmul algebra covers 61 of 1,348 upgrade references — 4.5% —
    /// while selectors cover armor 220 + weapon-swap 123 + menu-swap 122.
    ///
    /// The grammar stays deliberately tiny because the corpus is: 1,716 of ~2,300 real
    /// condition clauses are literally `None` and 237 more are a single `PLAYER_UPGRADE`,
    /// so a required-flag list covers over 85% of shipping usage. No expression language.
    /// </summary>
    public List<VariantDto>? Variants { get; set; }

    /// <summary>
    /// How many bodies one purchase yields. ZH ships this (QuantityModifier = Redguard 2,
    /// SpawnNumber = 3 on the Stinger Site) and any DPS-per-cost figure is 2-3x wrong
    /// without it. Defaults to 1.
    /// </summary>
    public int? UnitsPerPurchase { get; set; }
    public List<string>? Prerequisites { get; set; }

    /// <summary>
    /// Event rules — our replacement for ZH's C++ behavior modules.
    ///
    /// ZH attaches ~217 compiled module classes to objects by name. That is its whole
    /// extensibility story and its whole extensibility wall: every new behaviour is a
    /// recompile, so every mod that needs one is dead on arrival. The canonical case is
    /// eight separate classes (SabotageSupplyDropzone/SupplyCenter/Superweapon/PowerPlant/
    /// MilitaryFactory/InternetCenter/FakeBuilding/CommandCenter) at three uses each,
    /// differing only in which building type they hit.
    ///
    /// The corpus says that wall buys almost nothing: 7 modules cover 50% of all usage and
    /// 27 cover 80%; 86.7% of non-draw instances are PURE DATA averaging 3.9 parameter
    /// lines; and the eleven Die-family classes share one filter schema, differing only by
    /// a single typed effect. So the vocabulary collapses to {on, when, do}.
    ///
    /// This is NOT a scripting language, deliberately: closed event enum, closed filter
    /// grammar, closed effect enum with open parameters. No control flow, no variables, no
    /// loops, fixed arity — statically lintable and bounded per tick.
    /// </summary>
    public List<RuleDto>? Rules { get; set; }

    public ComponentsDto Components { get; set; } = new();
}

/// <summary>One {on, when, do} rule.</summary>
public sealed class RuleDto
{
    /// <summary>Closed event enum. Currently: "death".</summary>
    public string On { get; set; } = "";
    /// <summary>All must be set on the dying unit (or its team) for the rule to fire.</summary>
    public List<string> WhenFlags { get; set; } = new();
    public List<EffectDto> Do { get; set; } = new();
}

/// <summary>
/// One effect. <see cref="Kind"/> is a closed enum; the other fields are its open
/// parameters and which ones apply depends on the kind — the same shape as ZH's module
/// parameter blocks, which is why 930 of its instances need no parameters at all.
/// </summary>
public sealed class EffectDto
{
    /// <summary>"spawn" | "grantMoney" | "damageInRadius" | "grantFlag".</summary>
    public string Kind { get; set; } = "";

    // spawn
    public string? Proto { get; set; }
    public int Count { get; set; } = 1;
    /// <summary>Placement jitter radius, world units. Uses its own RNG stream.</summary>
    public double Spread { get; set; }

    // grantMoney (salvage, bounties)
    public int Amount { get; set; }

    // damageInRadius (death explosions); reuses a named weapon's damage + type
    public string? Weapon { get; set; }
    public double Radius { get; set; }

    // grantFlag
    public string? Flag { get; set; }
    /// <summary>Grant to the whole team rather than to the spawned/affected unit.</summary>
    public bool TeamScope { get; set; }
}

/// <summary>One conditional loadout. Every field is optional; absent means "inherit".</summary>
public sealed class VariantDto
{
    /// <summary>All of these must be set (on the unit or its team) for this to apply.</summary>
    public List<string> WhenFlags { get; set; } = new();
    public string? Weapon { get; set; }
    public string? ArmorClass { get; set; }
    /// <summary>Applied on top of the base stats while this variant is active.</summary>
    public List<ModifierDto> Modifiers { get; set; } = new();
}

/// <summary>
/// Field defaults applied to every unit that does not state them.
///
/// ZH's Default/Object.ini DefaultThingTemplate is 130 lines serving all 1,949 objects —
/// it is why 60 objects need no Draw block and 349 need no Body. Without an equivalent,
/// every new schema field is a breaking change to every existing unit in every pack.
/// Cheap to add now, impossible to retrofit once mods exist in the wild.
/// </summary>
public sealed class DefaultsDto
{
    public UnitDefaultsDto Unit { get; set; } = new();
}

public sealed class UnitDefaultsDto
{
    public int Cost { get; set; } = 0;
    public int BuildTicks { get; set; } = 1;
    public int UnitsPerPurchase { get; set; } = 1;
    public List<string> Prerequisites { get; set; } = new();
}

/// <summary>
/// Component blocks. The runtime flattens these into a dense archetype for the
/// skeleton (one unit shape); a real build swaps this for proper ECS composition
/// so prototypes can omit/add components freely. The content format is already
/// component-shaped so that migration is a loader change, not a data change.
/// </summary>
public sealed class ComponentsDto
{
    public HealthDto? Health { get; set; }
    public MobileDto? Mobile { get; set; }
    public WeaponBearerDto? WeaponBearer { get; set; }
    public VeterancyCarrierDto? VeterancyCarrier { get; set; }
}

public sealed class HealthDto
{
    public double Max { get; set; }
    public string ArmorClass { get; set; } = "";
}

public sealed class MobileDto
{
    /// <summary>World units per second; the loader converts to per-tick fixed-point.</summary>
    public double Speed { get; set; }
}

public sealed class WeaponBearerDto
{
    public string Weapon { get; set; } = "";
}

public sealed class VeterancyCarrierDto
{
    public string Track { get; set; } = "";
}

/// <summary>
/// A faction is either a base roster or a delta on another faction — the Zero Hour
/// generals model, where each general is his parent faction plus a small patch.
/// Layering is the customization surface: a generated faction is a diff a human can
/// read, not a wholesale copy of the parent that silently drifts from it.
/// </summary>
public sealed class FactionDto
{
    /// <summary>Parent faction id. Null/empty means this is a base faction.</summary>
    public string? Extends { get; set; }
    /// <summary>Roster for a base faction. Ignored when <see cref="Extends"/> is set.</summary>
    public List<string> Units { get; set; } = new();
    /// <summary>Units added on top of the parent roster.</summary>
    public List<string> Add { get; set; } = new();
    /// <summary>Unit ids dropped from the parent roster.</summary>
    public List<string> Remove { get; set; } = new();
    /// <summary>Per-unit tweaks. Each produces a faction-local variant of the unit.</summary>
    public Dictionary<string, UnitPatchDto> Modify { get; set; } = new();

    // --- What makes a faction PLAYABLE rather than just a roster. ZH's PlayerTemplate
    // --- carries exactly these, and without them a faction is a list nobody can start.

    /// <summary>
    /// The structure a player begins with. In retail this is uniformly a command center,
    /// and the engine treats a template with no StartingBuilding as unplayable — it is the
    /// lobby's "is this faction real" test. Inherited from the parent unless restated.
    /// </summary>
    public string? StartingBuilding { get; set; }

    /// <summary>
    /// Units in play at t=0. ZH declares StartingUnit0..9 as ten separate fields (a
    /// #define'd cap of 10) and then never uses ANY of them in retail — every template
    /// ships a dozer as the lone starting unit via StartingBuilding's own build queue.
    /// A list has no cap and no dead fields.
    /// </summary>
    public List<string>? StartingUnits { get; set; }

    /// <summary>
    /// Starting cash. Worth knowing: every one of ZH's 15 PlayerTemplates sets this to 0
    /// and the real value comes from a global plus a lobby dropdown, so faction-specific
    /// starting money is NOT a retail concept — it is one we are adding deliberately,
    /// because asymmetric economies are a design space their format could not express.
    /// Null inherits the scenario default.
    /// </summary>
    public int? StartMoney { get; set; }
}

/// <summary>
/// A patch against an inherited unit. Stat changes go through the same
/// (base + Σadd) × Πmul algebra as veterancy and upgrades; cost and build time are
/// plain overrides because they are not stats the sim resolves per-unit.
/// </summary>
public sealed class UnitPatchDto
{
    public int? Cost { get; set; }
    public int? BuildTicks { get; set; }
    public List<ModifierDto> Modifiers { get; set; } = new();
}

/// <summary>
/// Everything the ZH compiler needs that our own model has no reason to carry.
///
/// Our vocabulary is open where theirs is closed, so the bridge has to be declared rather
/// than guessed: our damage-type names must land on their 38-value enum, and every unit
/// needs a model, because an Object with no Draw module is invisible even when its stats
/// are perfect. Making this explicit means lint can check it before the engine does.
/// </summary>
public sealed class ZhTargetDto
{
    /// <summary>
    /// Our damage type -> theirs. Their DamageType is a closed C++ enum of 38 values; an
    /// unknown name is a hard parse error at load, so this mapping is mandatory.
    /// </summary>
    public Dictionary<string, string> DamageTypes { get; set; } = new();

    /// <summary>
    /// Our unit id -> a `.w3d` model name that already exists in the archives. During
    /// development this is how content is playable with zero new art: 2,928 shipped models
    /// (32.9%) are referenced by nothing at all and are free to adopt.
    /// </summary>
    public Dictionary<string, string> Models { get; set; } = new();

    /// <summary>
    /// World-unit scale. Our ranges and speeds are in our own units; ZH's are roughly 16x
    /// larger (a Crusader's 150 range against our 9). Applied to range, radius and speed.
    /// </summary>
    public double WorldScale { get; set; } = 16.0;

    /// <summary>Draw module class per unit id; defaults to W3DModelDraw, which suits anything.</summary>
    public Dictionary<string, string> DrawModules { get; set; } = new();

    /// <summary>
    /// Our unit id -> the adopted model's RIG: "&lt;launchBone&gt;:&lt;muzzleFlashSubObject&gt;".
    ///
    /// A borrowed mesh brings bones and sub-objects that have to be DECLARED or the object
    /// misbehaves in ways that look like content bugs. Adopting AVCrusader without naming its
    /// muzzle flash sub-object left the flash mesh permanently visible — a tank with a static
    /// flame welded to its gun — because nothing ever told the engine to hide it. Its firing
    /// bone is TurretMS and its flash sub-object is TurretFX; other models differ.
    ///
    /// Same principle as <see cref="Sides"/> and geometry: adopted art is not free, it comes
    /// with a contract. Empty means an unturreted, boneless model.
    /// </summary>
    public Dictionary<string, string> ArtRig { get; set; } = new();

    /// <summary>
    /// Unit ids whose adopted model has a TURRET. A turreted mesh needs a Turret block with
    /// ControlledWeaponSlots or the weapon can never be brought to bear — the unit simply
    /// never fires, with no error anywhere.
    /// </summary>
    public List<string> Turreted { get; set; } = new();

    /// <summary>
    /// Our faction id -> the retail BASE side whose UI chrome it borrows ("USA", "China",
    /// "GLA"). Our faction keeps its OWN Side; only the presentation is inherited.
    ///
    /// A playable PlayerTemplate is mostly UI: score screen, load screen, watermark, side
    /// icon, medallions, tooltip. None of it is simulated and all of it is mandatory for the
    /// faction to be selectable — the "emittable is not simulated" axis in its purest form.
    /// We REFERENCE retail's asset names, which is what every mod does; we copy nothing.
    /// </summary>
    public Dictionary<string, string> Sides { get; set; } = new();

    // There is deliberately NO "attach to an existing menu" field, and the reason is
    // measured rather than assumed.
    //
    // EA's source reads as though it would work: with INI_LOAD_OVERWRITE, both
    // ThingFactory::parseObjectDefinition and ControlBar::parseCommandSetDefinition find the
    // existing block and call initFromINI, which writes only the fields present — a partial
    // patch. Adding one free slot to AmericaWarFactoryCommandSet should have added one button
    // and left retail's twelve alone.
    //
    // On the runtime we actually target it is a FATAL load error. Both functions guard the
    // duplicate with DEBUG_CRASH, and this build has it compiled in: the load throws, the
    // engine aborts at 29 of 42 subsystems, and the game never reaches its main loop. Proven
    // with a minimal probe that redeclared the set using nothing but a RETAIL button, so no
    // name of ours was involved.
    //
    // The consequence is architectural, not cosmetic: emission is strictly ADD-NEW-NAMES.
    // A pack cannot extend a retail faction's menu, so a pack is a NEW FACTION rather than an
    // addition to an existing one. The usual mod workaround — ship a merged CommandSet.ini
    // that shadows retail's — is closed to us on legal grounds, since it means redistributing
    // their content.
}

public sealed class LintConfigDto
{
    /// <summary>[min, max] allowed raw DPS per 1000 cost. Coarse first-pass balance gate.</summary>
    public List<double> DpsPer1000CostBand { get; set; } = new() { 20, 200 };
}
