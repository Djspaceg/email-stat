namespace EmailStat.Helpers;

using EmailStat.Models;
using Windows.Foundation;

/// <summary>
/// Implements the Squarified Treemap algorithm (Bruls, Huizing &amp; van Wijk 2000).
/// Assigns a <see cref="Windows.Foundation.Rect"/> to every <see cref="TreemapNode"/> so that
/// areas are proportional to <see cref="TreemapNode.Value"/> and aspect ratios are minimised.
/// </summary>
public static class TreemapLayoutEngine
{
    /// <summary>
    /// Compute bounds for every node in <paramref name="nodes"/> inside <paramref name="bounds"/>.
    /// Nodes are reordered (largest first) before layout.
    /// </summary>
    public static void Compute(IList<TreemapNode> nodes, Rect bounds)
    {
        if (nodes.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        long totalValue = nodes.Sum(n => n.Value);
        if (totalValue <= 0) return;

        // Normalise: convert each node's value into an area proportional to bounds
        double totalArea = bounds.Width * bounds.Height;
        var sorted = nodes
            .Where(n => n.Value > 0)
            .OrderByDescending(n => n.Value)
            .ToList();

        var areas = sorted
            .Select(n => (double)n.Value / totalValue * totalArea)
            .ToList();

        Squarify(sorted, areas, 0, sorted.Count, bounds);
    }

    // -------------------------------------------------------------------------
    // Core recursive squarify
    // -------------------------------------------------------------------------

    private static void Squarify(
        List<TreemapNode> nodes,
        List<double> areas,
        int start,
        int end,
        Rect bounds)
    {
        if (start >= end || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        double shortSide = Math.Min(bounds.Width, bounds.Height);

        // Build the current row by greedily adding items until the worst aspect
        // ratio would get worse with the next item.
        int rowStart = start;
        double rowSum = 0;

        for (int i = start; i < end; i++)
        {
            double prevWorst = WorstRatio(areas, rowStart, i, rowSum, shortSide);
            rowSum += areas[i];
            double newWorst = WorstRatio(areas, rowStart, i + 1, rowSum, shortSide);

            if (i > rowStart && newWorst > prevWorst)
            {
                // Adding this item would worsen the layout – close the row now.
                rowSum -= areas[i];
                Rect remaining = LayoutRow(nodes, areas, rowStart, i, rowSum, bounds);
                Squarify(nodes, areas, i, end, remaining);
                return;
            }
        }

        // Last (or only) row: place everything that is left.
        LayoutRow(nodes, areas, rowStart, end, rowSum, bounds);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Worst aspect ratio for the items [<paramref name="from"/>, <paramref name="to"/>)
    /// given they share a strip of width <paramref name="w"/> and total area <paramref name="sum"/>.
    /// </summary>
    private static double WorstRatio(
        List<double> areas,
        int from,
        int to,
        double sum,
        double w)
    {
        if (to <= from || sum <= 0 || w <= 0) return double.MaxValue;

        double max = 0, min = double.MaxValue;
        for (int i = from; i < to; i++)
        {
            if (areas[i] > max) max = areas[i];
            if (areas[i] < min) min = areas[i];
        }

        double s2 = sum * sum;
        double w2 = w * w;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    /// <summary>
    /// Lay out the items [<paramref name="from"/>, <paramref name="to"/>)
    /// along the shorter side of <paramref name="bounds"/>,
    /// set their <see cref="TreemapNode.Bounds"/>, and return the remaining rectangle.
    /// </summary>
    private static Rect LayoutRow(
        List<TreemapNode> nodes,
        List<double> areas,
        int from,
        int to,
        double sum,
        Rect bounds)
    {
        if (sum <= 0) return bounds;

        // Strip thickness uses the shorter side to compute item dimensions.
        // When the available area is wider than tall (horizontal layout),
        // we place the strip along the left edge: items share the full height
        // and the strip "thickness" is measured along the width axis.
        // When taller than wide (vertical layout), items share the full width
        // and thickness is measured along the height axis.
        bool horizontal = bounds.Width >= bounds.Height;
        double thickness = horizontal
            ? sum / bounds.Height   // strip runs vertically on the left; thickness is in the X direction
            : sum / bounds.Width;   // strip runs horizontally on top; thickness is in the Y direction

        double offset = 0;
        for (int i = from; i < to; i++)
        {
            double dim = thickness > 0 ? areas[i] / thickness : 0;

            nodes[i].Bounds = horizontal
                ? new Rect(bounds.X + offset, bounds.Y, dim, thickness)
                : new Rect(bounds.X, bounds.Y + offset, thickness, dim);

            offset += dim;
        }

        // Return the remaining rectangle after the strip has been placed.
        return horizontal
            ? new Rect(
                bounds.X + thickness,
                bounds.Y,
                Math.Max(0, bounds.Width - thickness),
                bounds.Height)
            : new Rect(
                bounds.X,
                bounds.Y + thickness,
                bounds.Width,
                Math.Max(0, bounds.Height - thickness));
    }
}
