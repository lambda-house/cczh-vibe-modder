using RtsSkeleton.Core;

namespace RtsSkeleton.Runtime;

/// <summary>
/// Uniform-grid broad phase over live unit positions.
///
/// Why before splash and auras rather than after: every radius query in this sim was a full
/// scan of the unit table, so the tick cost was O(n²) and the product's whole value is BATCH
/// measurement — a counter matrix is pairs x runs x ticks. Measured before this landed, a
/// single duel cost 0.25s at ~200 units, 0.65s at ~800 and 6.9s at ~2000. That curve is what
/// decides whether a balance corpus is cheap or unaffordable, and it gets worse, not better,
/// as structures and maps push counts up.
///
/// Three properties make it safe to put in front of a deterministic sim:
///
///   NO FLOATS. Cell coordinates come from an arithmetic shift of the Fix64 raw value, which
///   floors toward negative infinity for negative coordinates as well — the one place a
///   naive `(int)(x / cell)` would round toward zero and split the origin cell in four.
///
///   NO HASH CONTAINERS. Buckets are a counting sort: count per cell, prefix-sum, scatter.
///   Two flat int arrays, rebuilt in O(n), iterated in a fixed order. A Dictionary here would
///   be exactly the hash-order nondeterminism the project's invariants forbid.
///
///   ASCENDING RESULTS. <see cref="Query"/> returns candidates sorted by unit index, so a
///   caller sees precisely the order a full ascending scan would have produced. That is not
///   a nicety: splash applies damage in visit order, and death order feeds the rule cascade,
///   which draws from a Pcg32 stream. An unsorted broad phase would silently reorder RNG
///   draws and change results that are supposed to be reproducible.
///
/// The grid is a pure accelerator: it must never change an answer, only the cost of getting
/// it. e2e asserts that by comparing against the brute-force scan on every gate scenario.
/// </summary>
public sealed class SpatialGrid
{
    /// <summary>
    /// Cell edge = 2^CellShift world units. Eight is chosen against the content, not guessed:
    /// weapon acquire ranges in the packs here run 4-12 world units, so a typical query
    /// touches a 3x3 or 5x5 neighbourhood — few enough cells that the per-query overhead
    /// stays small, large enough that a cell holds a handful of units rather than one.
    /// </summary>
    public const int CellShift = 3;

    /// <summary>
    /// Cap on grid dimensions, so a distant outlier cannot make this allocate an enormous
    /// table. When the extent will not fit, the CELL GROWS rather than the coordinates being
    /// clamped into it — see <see cref="_shift"/>.
    /// </summary>
    private const int MaxDim = 512;

    /// <summary>
    /// Live cell shift, >= <see cref="CellShift"/>, chosen per rebuild so the occupied extent
    /// fits <see cref="MaxDim"/>.
    ///
    /// The first version clamped out-of-range coordinates into the edge cells instead. That is
    /// still CORRECT — a clamped candidate set only grows — but it degenerates: at 4,000 units
    /// spread over 12,000 world units, every unit past the cap piled into two edge rows and the
    /// broad phase collapsed back to a full scan with overhead on top. Measured 28.8s against
    /// 2.0s for 2,000 units, i.e. worse than quadratic. Growing the cell keeps the grid useful
    /// at any extent, and the arithmetic stays integer so determinism is unaffected.
    /// </summary>
    private int _shift = CellShift;

    private int _minCellX, _minCellY, _dimX, _dimY;
    private int[] _cellStart = Array.Empty<int>();   // length dimX*dimY + 1, prefix sums
    private int[] _items = Array.Empty<int>();       // unit indices, ascending within a cell
    /// <summary>Scatter cursors, kept as a field. Allocating this per rebuild cost more than
    /// the whole broad phase saved — the grid runs twice a tick and the cell count is in the
    /// thousands, so a fresh array each time was the dominant term in the first measurement.</summary>
    private int[] _cursor = Array.Empty<int>();
    private int _count;

    /// <summary>Units the last rebuild placed. Zero means every query returns nothing.</summary>
    public int Count => _count;

    private int CellOf(Fix64 v) => (int)(v.Raw >> (32 + _shift));

    /// <summary>
    /// Rebuild from the world's live units. Called once per tick before the systems that
    /// query it; positions change every tick, so incremental maintenance would cost more
    /// than the O(n) rebuild it replaced.
    /// </summary>
    public void Rebuild(World world)
    {
        _count = 0;
        int n = world.UnitCount;
        if (n == 0) { _dimX = _dimY = 0; return; }

        long loX = long.MaxValue, loY = long.MaxValue, hiX = long.MinValue, hiY = long.MinValue;
        for (int i = 0; i < n; i++)
        {
            ref readonly var u = ref world.Units[i];
            if (!u.Alive) continue;
            if (u.X.Raw < loX) loX = u.X.Raw;
            if (u.X.Raw > hiX) hiX = u.X.Raw;
            if (u.Y.Raw < loY) loY = u.Y.Raw;
            if (u.Y.Raw > hiY) hiY = u.Y.Raw;
            _count++;
        }
        if (_count == 0) { _dimX = _dimY = 0; return; }

        // Grow the cell until the occupied extent fits. Pure integer arithmetic on the Fix64
        // raw values, so every machine picks the same shift for the same positions.
        _shift = CellShift;
        while (_shift < 40
               && (((hiX >> (32 + _shift)) - (loX >> (32 + _shift)) + 1) > MaxDim
                   || ((hiY >> (32 + _shift)) - (loY >> (32 + _shift)) + 1) > MaxDim))
            _shift++;

        _minCellX = (int)(loX >> (32 + _shift));
        _minCellY = (int)(loY >> (32 + _shift));
        _dimX = (int)((hiX >> (32 + _shift)) - _minCellX + 1);
        _dimY = (int)((hiY >> (32 + _shift)) - _minCellY + 1);

        int cells = _dimX * _dimY;
        if (_cellStart.Length < cells + 1) _cellStart = new int[cells + 1];
        if (_items.Length < n) _items = new int[n];
        Array.Clear(_cellStart, 0, cells + 1);

        // Counting sort, pass 1: how many per cell.
        for (int i = 0; i < n; i++)
        {
            ref readonly var u = ref world.Units[i];
            if (!u.Alive) continue;
            _cellStart[CellIndex(u.X, u.Y) + 1]++;
        }
        for (int c = 0; c < cells; c++) _cellStart[c + 1] += _cellStart[c];

        // Pass 2: scatter in ASCENDING unit index, which is what leaves each cell's slice
        // ascending and makes the merge in Query a cheap insertion sort rather than a real one.
        if (_cursor.Length < cells) _cursor = new int[cells];
        Array.Copy(_cellStart, _cursor, cells);
        for (int i = 0; i < n; i++)
        {
            ref readonly var u = ref world.Units[i];
            if (!u.Alive) continue;
            _items[_cursor[CellIndex(u.X, u.Y)]++] = i;
        }
    }

    private int CellIndex(Fix64 x, Fix64 y)
    {
        int cx = Math.Clamp(CellOf(x) - _minCellX, 0, _dimX - 1);
        int cy = Math.Clamp(CellOf(y) - _minCellY, 0, _dimY - 1);
        return cy * _dimX + cx;
    }

    /// <summary>
    /// Candidate unit indices whose CELL overlaps the query box, written ascending into
    /// <paramref name="buffer"/>; returns how many. Candidates are a superset of the true
    /// radius hit set — the caller still does the exact distance test it always did, so the
    /// grid cannot admit a unit that a full scan would have rejected.
    ///
    /// Returns -1 if the buffer is too small, which the caller must treat as "fall back to
    /// the full scan" rather than as an empty result. Silently truncating would turn a
    /// capacity problem into a wrong answer, and a wrong answer here is a desync.
    /// </summary>
    public int Query(Fix64 x, Fix64 y, Fix64 radius, int[] buffer)
    {
        if (_count == 0 || _dimX == 0) return 0;

        // Cell span of the query box. The radius is a Fix64; take its ceiling in cells so a
        // partial cell at the edge is still visited.
        int r = (int)((radius.Raw + (1L << (32 + _shift)) - 1) >> (32 + _shift));
        if (r < 0) r = 0;
        int cx = CellOf(x) - _minCellX, cy = CellOf(y) - _minCellY;
        int x0 = Math.Clamp(cx - r, 0, _dimX - 1), x1 = Math.Clamp(cx + r, 0, _dimX - 1);
        int y0 = Math.Clamp(cy - r, 0, _dimY - 1), y1 = Math.Clamp(cy + r, 0, _dimY - 1);

        int written = 0;
        for (int gy = y0; gy <= y1; gy++)
        for (int gx = x0; gx <= x1; gx++)
        {
            int cell = gy * _dimX + gx;
            for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
            {
                if (written >= buffer.Length) return -1;
                buffer[written++] = _items[k];
            }
        }

        // Merge the per-cell ascending runs. Insertion sort because the input is a handful of
        // already-sorted runs, which is its best case — and because the alternative, leaving
        // it unsorted, would reorder splash damage and therefore the death-rule RNG draws.
        for (int i = 1; i < written; i++)
        {
            int v = buffer[i], j = i - 1;
            while (j >= 0 && buffer[j] > v) { buffer[j + 1] = buffer[j]; j--; }
            buffer[j + 1] = v;
        }
        return written;
    }
}
