namespace RtsSkeleton.Runtime;

/// <summary>
/// The taxonomy flag set, modelled on Zero Hour's KindOf.
///
/// In ZH this is the most load-bearing field on an Object: a 120-bit mask (116 live) that
/// decides what a thing IS — structure, factory, victory condition, crushable, garrisonable.
/// It appears on 98.8% of their 1,949 objects. Almost every rule in the engine is really a
/// KindOf test.
///
/// We take the mechanism and drop the wall. ZH's set is a compiled C++ enum with a
/// hand-maintained parallel name table, so a modder cannot add a category without a rebuild
/// — the same closed-enum trap that blocks new damage types and special powers. Ours is an
/// open string set: content declares names, lint closes the set against the known roles, and
/// unknown flags are a lint error rather than a parse crash. Adding a role is a data change.
///
/// Only roles the sim actually consults get a constant here. The rest live as strings and
/// cost nothing until something needs them.
/// </summary>
public static class KindOf
{
    /// <summary>A building: placed at a site, does not move, is an economic target.</summary>
    public const string Structure = "STRUCTURE";

    /// <summary>Produces units. A team with no live factory cannot start a unit.</summary>
    public const string Factory = "FS_FACTORY";

    /// <summary>Generates income. Destroying it is how you starve an opponent.</summary>
    public const string CashGenerator = "CASH_GENERATOR";

    /// <summary>Losing every one of these loses the game (ZH: MP_COUNT_FOR_VICTORY).</summary>
    public const string CountForVictory = "MP_COUNT_FOR_VICTORY";

    /// <summary>Satisfies an object prerequisite for other buildables.</summary>
    public const string Prerequisite = "IS_PREREQUISITE";

    /// <summary>The roles the simulation consults. Lint warns on anything outside this set.</summary>
    public static readonly string[] Known =
    {
        Structure, Factory, CashGenerator, CountForVictory, Prerequisite,
        // Declared, not yet consulted — present so content can be authored ahead of the
        // systems that will read them, exactly as ZH does.
        "VEHICLE", "INFANTRY", "AIRCRAFT", "SELECTABLE", "CAN_ATTACK", "IMMOBILE",
    };

    /// <summary>Dense bit index for the roles above, so a proto carries a mask not a list.</summary>
    public static int BitOf(string flag) => Array.IndexOf(Known, flag);
}

/// <summary>How a purchased thing enters the world. ZH's BuildCompletion, verbatim.</summary>
public enum BuildCompletion
{
    /// <summary>A unit: leaves its factory and walks to the rally point.</summary>
    AppearsAtRallyPoint = 0,
    /// <summary>A structure: appears at a build site and stays there.</summary>
    PlacedByPlayer = 1,
}
