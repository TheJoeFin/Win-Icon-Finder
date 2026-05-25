using WinIconFinder.Models;

namespace WinIconFinder.Services;

/// <summary>
/// Arranges icons in a 2-D spiral grid sorted by cosine similarity to a chosen
/// pivot icon. The pivot always occupies cell (0, 0) — the canvas centre — and
/// each ring outward contains progressively less-similar icons.
/// No external computation required: sorting is O(n log n) and runs in &lt;5 ms.
/// </summary>
public sealed class SimilarityLayoutService
{
    private FluentIcon[] _icons = [];
    private (int gx, int gy)[] _spiralOrder = [];
    private LayoutPosition[] _positions = [];
    private Dictionary<(int gx, int gy), int> _cellToPositionIndex = [];

    /// <summary>True after <see cref="Initialize"/> has been called.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Current icon positions in spiral order (index 0 = pivot / grid centre).</summary>
    public IReadOnlyList<LayoutPosition> Positions => _positions;

    /// <summary>O(1) grid-cell → Positions-list-index lookup, rebuilt on every sort.</summary>
    public IReadOnlyDictionary<(int gx, int gy), int> CellIndex => _cellToPositionIndex;

    // -------------------------------------------------------------------------
    // Initialisation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pre-computes the spiral order and sets the default alphabetical layout.
    /// Synchronous and fast — call once after glyph vectors are ready.
    /// </summary>
    public void Initialize(IReadOnlyList<FluentIcon> icons)
    {
        _icons = [.. icons];
        _spiralOrder = GenerateSpiralOrder(icons.Count);
        SetDefaultLayout();
        IsReady = true;
    }

    /// <summary>Alphabetical layout used before any pivot is selected.</summary>
    public void SetDefaultLayout()
    {
        RebuildPositions([.. Enumerable.Range(0, _icons.Length)]);
    }

    // -------------------------------------------------------------------------
    // Grid sort
    // -------------------------------------------------------------------------

    /// <summary>
    /// Re-sorts the grid so the pivot icon lands at (0, 0) and every other icon
    /// is placed at the spiral cell corresponding to its rank by similarity.
    /// </summary>
    public void SetPivotLayout(int pivotIconIndex, float[] similarities)
    {
        var sorted = Enumerable.Range(0, _icons.Length)
            .OrderByDescending(i => i == pivotIconIndex ? float.MaxValue : similarities[i])
            .ToArray();
        RebuildPositions(sorted);
    }

    private void RebuildPositions(int[] sortedIconIndices)
    {
        var positions = new LayoutPosition[sortedIconIndices.Length];
        var lookup = new Dictionary<(int, int), int>(sortedIconIndices.Length);

        for (int spiralIdx = 0; spiralIdx < sortedIconIndices.Length; spiralIdx++)
        {
            int iconIdx = sortedIconIndices[spiralIdx];
            var (gx, gy) = _spiralOrder[spiralIdx];
            positions[spiralIdx] = new LayoutPosition(_icons[iconIdx], iconIdx, gx, gy);
            lookup[(gx, gy)] = spiralIdx;
        }

        _positions = positions;
        _cellToPositionIndex = lookup;
    }

    // -------------------------------------------------------------------------
    // Similarity scoring
    // -------------------------------------------------------------------------

    /// <summary>
    /// Dot product of each icon vector with the pivot (cosine similarity, since
    /// all vectors are L2-normalised), clamped to [0, 1].
    /// </summary>
    public float[] ComputeSimilarities(float[][] vectors, int pivotIndex)
    {
        var pivot = vectors[pivotIndex];
        int n = vectors.Length;
        int d = pivot.Length;
        var scores = new float[n];

        for (int i = 0; i < n; i++)
        {
            float dot = 0f;
            var v = vectors[i];
            for (int j = 0; j < d; j++)
                dot += pivot[j] * v[j];
            scores[i] = MathF.Max(0f, dot);
        }

        return scores;
    }

    // -------------------------------------------------------------------------
    // Spiral order
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates <paramref name="count"/> grid coordinates in a square outward
    /// spiral: (0,0), right, up, left×2, down×2, right×3, …
    /// Y increases downward (screen convention).
    /// </summary>
    private static (int gx, int gy)[] GenerateSpiralOrder(int count)
    {
        var result = new (int gx, int gy)[count];
        if (count == 0) return result;

        result[0] = (0, 0);
        int gx = 0, gy = 0, n = 1, step = 1;

        while (n < count)
        {
            for (int i = 0; i < step && n < count; i++) { gx++; result[n++] = (gx, gy); }   // →
            for (int i = 0; i < step && n < count; i++) { gy--; result[n++] = (gx, gy); }   // ↑
            step++;
            for (int i = 0; i < step && n < count; i++) { gx--; result[n++] = (gx, gy); }   // ←
            for (int i = 0; i < step && n < count; i++) { gy++; result[n++] = (gx, gy); }   // ↓
            step++;
        }

        return result;
    }
}

/// <summary>An icon placed at integer grid cell (GX, GY) in the similarity spiral.</summary>
public readonly record struct LayoutPosition(FluentIcon Icon, int Index, int GX, int GY);
