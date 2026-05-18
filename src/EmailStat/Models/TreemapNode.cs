namespace EmailStat.Models;

using Windows.Foundation;
using Windows.UI;

/// <summary>
/// An intermediate node produced by <c>TreemapLayoutEngine</c>.
/// Holds the computed screen rectangle and colour for one <see cref="EmailGroup"/>.
/// </summary>
public sealed class TreemapNode
{
    /// <summary>Short label shown inside the rectangle.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Numeric weight (email count) used to compute the rectangle area.</summary>
    public long Value { get; set; }

    /// <summary>Screen-space rectangle assigned by the layout engine.</summary>
    public Rect Bounds { get; set; }

    /// <summary>Fill colour for this rectangle.</summary>
    public Color Color { get; set; }

    /// <summary>The <see cref="EmailGroup"/> this node was created from.</summary>
    public EmailGroup? Group { get; set; }
}
