using RtsSkeleton.Core;

namespace RtsSkeleton.Runtime;

/// <summary>
/// What a cell IS. The numbering is EA's <c>PathfindCell::CellType</c> verbatim
/// (AIPathfind.h) rather than a fresh enum, because these values cross the boundary into
/// their engine and an invented number is the same class of mistake as an invented enum
/// NAME — the one that has already cost us five hard load errors.
///
/// <para>4 (<c>CELL_OBSTACLE</c>) and 5 (<c>CELL_BRIDGE_IMPASSABLE</c>) are deliberately
/// absent, and the gap is the honest record of a difference: in ZH those are DERIVED at
/// runtime — a structure registers itself into the cells it covers, so razing it reopens
/// the path. Ours is authored terrain only. Structures do not stamp the grid yet, which is
/// why "raze the wall to open the choke" is not a mechanic here.</para>
/// </summary>
public enum Surface : byte
{
    Clear = 0,
    Water = 1,
    Cliff = 2,
    Rubble = 3,
    Impassable = 6,
}

/// <summary>
/// What a unit can TRAVERSE — a mask, not a value, exactly like their
/// <c>LocomotorSurfaceType</c> (LocomotorSet.h), and with their bit positions. A locomotor
/// declaring GROUND|WATER is a hovercraft; declaring AIR ignores the grid entirely.
/// </summary>
[Flags]
public enum SurfaceMask : byte
{
    None = 0,
    Ground = 1 << 0,
    Water = 1 << 1,
    Cliff = 1 << 2,
    Air = 1 << 3,
    Rubble = 1 << 4,
}

/// <summary>
/// Static terrain passability: one byte per cell, of which the low three bits are the
/// surface. Content, not sim state — it never changes during a match, so it is hashed into
/// <c>contentHash</c> and never into the state hash.
///
/// <para><b>The cell size must be a power of two in world units</b>, so that world -> cell
/// is an arithmetic shift of the Fix64 raw and never a division. The same trick
/// <c>SpatialGrid</c> uses, and for the same reason: a shift is exact, total, and cannot
/// disagree between machines. An arithmetic shift right also floors toward negative
/// infinity, which is the rounding a grid index wants on the negative side of the origin —
/// truncation would fold cells -0 and +0 together and put a seam down the middle of
/// every map.</para>
///
/// <para>The grid is CENTRED on the world origin, because every scenario in the harness
/// spawns symmetrically about it. Outside the grid is a declared surface (clear by
/// default), so a map is a set of obstacles in an open plane rather than a box units can
/// be trapped outside of.</para>
///
/// <para>Scale: ZH's <c>PATHFIND_CELL_SIZE</c> is 10 of their world units, and our
/// <c>worldScale</c> to theirs is 16, so their cell is 0.625 of ours. Powers of two either
/// side are 0.5 and 1.0 — an authored map picks one, and lint rejects anything else.</para>
/// </summary>
public sealed class PassabilityGrid
{
    public readonly int Width;
    public readonly int Height;
    /// <summary>Right-shift taking a Fix64 raw to a cell index. 32 + log2(cellSize).</summary>
    public readonly int Shift;
    public readonly Fix64 CellSize;
    public readonly Surface Outside;

    private readonly byte[] _cells;
    private readonly long _leftRaw;   // world x of cell column 0
    private readonly long _originYRaw; // world y of cell row 0

    public PassabilityGrid(int width, int height, int shift, Surface outside, byte[] cells)
    {
        Width = width;
        Height = height;
        Shift = shift;
        Outside = outside;
        _cells = cells;
        CellSize = new Fix64(1L << shift);
        _leftRaw = -((long)width << shift) / 2;
        _originYRaw = -((long)height << shift) / 2;
    }

    public int CellCount => Width * Height;

    /// <summary>The surface at a cell index, without bounds checking.</summary>
    public Surface At(int cell) => (Surface)(_cells[cell] & 0x07);

    public int CellX(Fix64 x) => (int)((x.Raw - _leftRaw) >> Shift);
    /// <summary>
    /// Row 0 is the LOWEST world y, which is the TOP of the screen — the shell draws +y
    /// downward, as every 2D canvas does. Orienting the rows to the renderer rather than to a
    /// compass is the only choice under which the drawn map and the rendered map agree; the
    /// alternative reads as north-up in the JSON and appears vertically mirrored in play,
    /// which is a bug nobody finds by reading either one alone.
    /// </summary>
    public int CellY(Fix64 y) => (int)((y.Raw - _originYRaw) >> Shift);

    public bool InBounds(int cx, int cy) => (uint)cx < (uint)Width && (uint)cy < (uint)Height;

    public Surface SurfaceAt(int cx, int cy)
        => InBounds(cx, cy) ? (Surface)(_cells[cy * Width + cx] & 0x07) : Outside;

    /// <summary>Centre of a cell, which is where a waypoint sits.</summary>
    public void CellCentre(int cell, out Fix64 x, out Fix64 y)
    {
        int cx = cell % Width, cy = cell / Width;
        long half = 1L << (Shift - 1);
        x = new Fix64(_leftRaw + ((long)cx << Shift) + half);
        y = new Fix64(_originYRaw + ((long)cy << Shift) + half);
    }

    /// <summary>Which locomotor bit a surface demands. Impassable demands a bit that no
    /// locomotor has, so it is unreachable by anything on the ground; air never asks.</summary>
    public static SurfaceMask Required(Surface s) => s switch
    {
        Surface.Clear => SurfaceMask.Ground,
        Surface.Water => SurfaceMask.Water,
        Surface.Cliff => SurfaceMask.Cliff,
        Surface.Rubble => SurfaceMask.Rubble,
        _ => SurfaceMask.None,
    };

    public bool Passable(int cx, int cy, SurfaceMask mask)
        => (Required(SurfaceAt(cx, cy)) & mask) != 0;

    /// <summary>
    /// Can a unit walk the straight segment from a to b without leaving its surfaces?
    ///
    /// <para>This is not an optimisation bolted on afterwards — it is what makes the whole
    /// feature opt-in by content. On a map with no obstacles every segment is walkable, so
    /// movement takes the same straight step it took before this slice existed and every
    /// pinned hash stays put. It is also what keeps motion from looking blocky: the moment a
    /// unit clears a corner the direct line reopens and it stops following cell centres.</para>
    ///
    /// <para>A supercover walk, not Bresenham: it visits EVERY cell the segment touches,
    /// including both cells at a diagonal crossing. Bresenham would slip between two
    /// diagonally-adjacent walls through a corner that has no gap.</para>
    /// </summary>
    public bool LineWalkable(Fix64 ax, Fix64 ay, Fix64 bx, Fix64 by, SurfaceMask mask)
    {
        int x0 = CellX(ax), y0 = CellY(ay);
        int x1 = CellX(bx), y1 = CellY(by);

        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;

        // The start cell is never tested. A unit already standing on impassable ground —
        // spawned there, or pushed there by a scenario — must be able to walk out of it,
        // and a mover that refuses to leave a bad cell is stuck forever with no diagnosis.
        int x = x0, y = y0, n = dx + dy;
        int err = dx - dy;
        for (; n > 0; n--)
        {
            if (err > 0) { x += sx; err -= 2 * dy; }
            else if (err < 0) { y += sy; err += 2 * dx; }
            else
            {
                // Exactly diagonal: step both, and require BOTH orthogonal neighbours. This
                // is the corner case the supercover exists for.
                if (!Passable(x + sx, y, mask) || !Passable(x, y + sy, mask)) return false;
                x += sx; y += sy; err += 2 * dx - 2 * dy; n--;
            }
            if (!Passable(x, y, mask)) return false;
        }
        return true;
    }
}
