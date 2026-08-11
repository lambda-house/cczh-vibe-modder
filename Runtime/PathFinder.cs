using RtsSkeleton.Core;

namespace RtsSkeleton.Runtime;

/// <summary>
/// Deterministic 8-way A* over a <see cref="PassabilityGrid"/>.
///
/// <para><b>Integers only, and a TOTAL order on the open set.</b> A heap ordered on cost
/// alone has ties, and which of two equal-cost cells pops first decides the whole shape of
/// the route. That is not a cosmetic difference: the route decides where units meet, and
/// where they meet decides who dies. The key packs <c>(f, cellIndex)</c> into one long, so
/// the comparison is a single integer compare and the tie-break is structural rather than
/// remembered — there is no way to add a node that forgets it.</para>
///
/// <para>Costs are ZH's grain: 10 orthogonal, 14 diagonal (their <c>PATHFIND_CELL_SIZE</c>
/// is 10 and 14/10 is 1.4, the integer sqrt(2) everyone uses). The heuristic is octile
/// distance with the same constants, which is admissible, so the first path found is a
/// shortest one.</para>
///
/// <para><b>Expansion is CAPPED and failure is a fallback, never an exception.</b> Same
/// rule as the death-rule cascade: a content bug — a walled-in spawn, an island with no
/// bridge — degrades one unit's movement to a straight line. It does not crash a sweep of
/// ten thousand matchups. A sweep that dies on the eight hundredth pack teaches nothing.</para>
///
/// <para>The scratch arrays are reused across calls and are NOT sim state: every cell they
/// describe is rewritten before it is read, guarded by a visit stamp. They are owned by the
/// <see cref="Sim"/> so nothing is shared between two sims in one process.</para>
/// </summary>
public sealed class PathFinder
{
    public const int Orthogonal = 10;
    public const int Diagonal = 14;

    /// <summary>Cells expanded before giving up. 4,096 is a 64x64 map searched exhaustively;
    /// beyond that the answer is not worth the tick it costs.</summary>
    public const int MaxExpansions = 4096;

    private readonly PassabilityGrid _grid;
    private readonly int[] _g;          // best known cost to a cell
    private readonly int[] _from;       // predecessor cell
    private readonly int[] _stamp;      // which call last wrote _g/_from
    private readonly long[] _heap;      // (f << 32) | cell
    private int _heapCount;
    private int _visit;

    /// <summary>Cells expanded by the most recent <see cref="FindPath"/>. Diagnostics only —
    /// never read by the sim, so it cannot influence a result.</summary>
    public int LastExpanded { get; private set; }

    public PathFinder(PassabilityGrid grid)
    {
        _grid = grid;
        int n = grid.CellCount;
        _g = new int[n];
        _from = new int[n];
        _stamp = new int[n];
        _heap = new long[n + 1];
    }

    // Neighbour order is fixed and iterated ascending. It never decides a tie on its own —
    // the heap key does that — but a stable order keeps the search itself reproducible.
    private static readonly int[] Dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
    private static readonly int[] Dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

    /// <summary>
    /// Fill <paramref name="into"/> with the first cells of a shortest route from
    /// (<paramref name="sx"/>, <paramref name="sy"/>) to (<paramref name="gx"/>,
    /// <paramref name="gy"/>), nearest first, excluding the start cell. Returns how many
    /// were written, or 0 when there is no route within the cap.
    ///
    /// <para>Truncation at <paramref name="into"/>.Length is deliberate and is not an
    /// approximation: the unit walks what it was given and asks again, which is also how it
    /// notices that the world moved while it was walking.</para>
    /// </summary>
    public int FindPath(int sx, int sy, int gx, int gy, SurfaceMask mask, int[] into, int offset, int capacity)
    {
        LastExpanded = 0;
        if (!_grid.InBounds(sx, sy) || !_grid.InBounds(gx, gy)) return 0;

        int start = sy * _grid.Width + sx;
        int goal = gy * _grid.Width + gx;
        if (start == goal) return 0;
        // The GOAL must be standable; the START need not be. A unit that spawned on a cliff
        // has to be able to walk off it, and refusing to path from a bad cell would strand it
        // silently — the failure mode that is hardest to see from a state hash.
        if (!_grid.Passable(gx, gy, mask)) return 0;

        if (++_visit == int.MaxValue) { Array.Clear(_stamp); _visit = 1; }

        _heapCount = 0;
        _g[start] = 0;
        _from[start] = -1;
        _stamp[start] = _visit;
        Push(Heuristic(sx, sy, gx, gy), start);

        bool found = false;
        while (_heapCount > 0)
        {
            long top = Pop();
            int cur = (int)(top & 0xFFFFFFFFL);
            if (cur == goal) { found = true; break; }
            if (++LastExpanded > MaxExpansions) break;

            int cx = cur % _grid.Width, cy = cur / _grid.Width;
            int gCur = _g[cur];

            for (int d = 0; d < 8; d++)
            {
                int nx = cx + Dx[d], ny = cy + Dy[d];
                if (!_grid.InBounds(nx, ny) || !_grid.Passable(nx, ny, mask)) continue;

                bool diag = Dx[d] != 0 && Dy[d] != 0;
                // No cutting a corner between two blocked cells: a diagonal step needs both
                // of the orthogonal cells it passes between. Without this a unit slips
                // through a wall that has no gap in it, which reads as a pathing bug but is
                // really a missing adjacency rule.
                if (diag && (!_grid.Passable(cx + Dx[d], cy, mask) || !_grid.Passable(cx, cy + Dy[d], mask)))
                    continue;

                int nCell = ny * _grid.Width + nx;
                int tentative = gCur + (diag ? Diagonal : Orthogonal);
                if (_stamp[nCell] == _visit && tentative >= _g[nCell]) continue;

                _stamp[nCell] = _visit;
                _g[nCell] = tentative;
                _from[nCell] = cur;
                Push(tentative + Heuristic(nx, ny, gx, gy), nCell);
            }
        }

        if (!found) return 0;

        // Walk back to the start, then reverse in place. The route is emitted nearest-first
        // because that is the order the unit consumes it, and truncation must drop the FAR
        // end — dropping the near end would teleport the intent.
        int len = 0;
        for (int c = goal; c != start && c >= 0; c = _from[c]) len++;
        int keep = Math.Min(len, capacity);
        int skip = len - keep;
        int w = offset + keep;
        int cell = goal;
        for (int i = 0; i < len; i++)
        {
            if (i >= skip) into[--w] = cell;
            cell = _from[cell];
        }
        return keep;
    }

    private static int Heuristic(int ax, int ay, int bx, int by)
    {
        int dx = Math.Abs(ax - bx), dy = Math.Abs(ay - by);
        int lo = Math.Min(dx, dy);
        return Diagonal * lo + Orthogonal * (dx + dy - 2 * lo);
    }

    // --- Binary heap over packed (f, cell) keys ---------------------------------------
    // Packing is what makes the order total. f is bounded by MaxExpansions * Diagonal plus
    // the heuristic, far inside 31 bits, so the shift cannot collide with the cell index.

    private void Push(int f, int cell)
    {
        long key = ((long)f << 32) | (uint)cell;
        int i = ++_heapCount;
        _heap[i] = key;
        while (i > 1)
        {
            int p = i >> 1;
            if (_heap[p] <= _heap[i]) break;
            (_heap[p], _heap[i]) = (_heap[i], _heap[p]);
            i = p;
        }
    }

    private long Pop()
    {
        long top = _heap[1];
        _heap[1] = _heap[_heapCount--];
        int i = 1;
        while (true)
        {
            int l = i << 1, r = l + 1, best = i;
            if (l <= _heapCount && _heap[l] < _heap[best]) best = l;
            if (r <= _heapCount && _heap[r] < _heap[best]) best = r;
            if (best == i) break;
            (_heap[best], _heap[i]) = (_heap[i], _heap[best]);
            i = best;
        }
        return top;
    }
}
