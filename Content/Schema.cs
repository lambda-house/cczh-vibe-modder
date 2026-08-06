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
    public List<string> DamageTypes { get; set; } = new();
    public List<string> ArmorClasses { get; set; } = new();
    public Dictionary<string, Dictionary<string, double>> DamageVsArmor { get; set; } = new();
    public Dictionary<string, WeaponDto> Weapons { get; set; } = new();
    public Dictionary<string, VeterancyTrackDto> VeterancyTracks { get; set; } = new();
    public TechDto Tech { get; set; } = new();
    public Dictionary<string, UnitDto> Units { get; set; } = new();
    public Dictionary<string, List<string>> Factions { get; set; } = new();
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
}

public sealed class UnitDto
{
    public string Faction { get; set; } = "";
    public int Cost { get; set; }
    public List<string> Prerequisites { get; set; } = new();
    public ComponentsDto Components { get; set; } = new();
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

public sealed class LintConfigDto
{
    /// <summary>[min, max] allowed raw DPS per 1000 cost. Coarse first-pass balance gate.</summary>
    public List<double> DpsPer1000CostBand { get; set; } = new() { 20, 200 };
}
