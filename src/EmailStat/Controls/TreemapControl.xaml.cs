namespace EmailStat.Controls;

using EmailStat.Helpers;
using EmailStat.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.Specialized;
using Windows.Foundation;
using Windows.UI;

/// <summary>
/// A WinUI 3 UserControl that renders a WinDirStat-style squarified treemap
/// using Win2D (hardware-accelerated Direct2D).
/// </summary>
public sealed partial class TreemapControl : UserControl
{
    // -------------------------------------------------------------------------
    // Dependency Properties
    // -------------------------------------------------------------------------

    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(
            nameof(Items),
            typeof(IList<EmailGroup>),
            typeof(TreemapControl),
            new PropertyMetadata(null, static (d, e) => ((TreemapControl)d).OnItemsChanged(e)));

    /// <summary>The collection of email groups to visualise.</summary>
    public IList<EmailGroup>? Items
    {
        get => (IList<EmailGroup>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>Raised when the user clicks a rectangle.  Argument is <c>null</c> when clicking empty space.</summary>
    public event EventHandler<EmailGroup?>? SelectedGroupChanged;

    // -------------------------------------------------------------------------
    // Private state
    // -------------------------------------------------------------------------

    private List<TreemapNode> _nodes = [];
    private int _hoveredIndex = -1;
    private int _selectedIndex = -1;
    private INotifyCollectionChanged? _subscribedCollection;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public TreemapControl()
    {
        InitializeComponent();
    }

    // -------------------------------------------------------------------------
    // Property-change plumbing
    // -------------------------------------------------------------------------

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => RebuildNodes();

    private void OnItemsChanged(DependencyPropertyChangedEventArgs e)
    {
        // Unsubscribe from the previous collection's change events.
        if (_subscribedCollection is not null)
        {
            _subscribedCollection.CollectionChanged -= OnCollectionChanged;
            _subscribedCollection = null;
        }

        if (e.NewValue is INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged += OnCollectionChanged;
            _subscribedCollection = ncc;
        }

        RebuildNodes();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildNodes();

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    private void RebuildNodes()
    {
        _nodes = [];
        _hoveredIndex = -1;
        _selectedIndex = -1;

        double w = ActualWidth;
        double h = ActualHeight;

        if (Items is null || Items.Count == 0 || w <= 0 || h <= 0)
        {
            canvas.Invalidate();
            return;
        }

        int idx = 0;
        foreach (EmailGroup group in Items.Where(g => g.EmailCount > 0))
        {
            _nodes.Add(new TreemapNode
            {
                Label = group.DisplayName,
                Value = group.EmailCount,
                Color = ColorGenerator.GetColor(idx++),
                Group = group,
            });
        }

        TreemapLayoutEngine.Compute(_nodes, new Rect(0, 0, w, h));
        canvas.Invalidate();
    }

    // -------------------------------------------------------------------------
    // Win2D drawing
    // -------------------------------------------------------------------------

    private void Canvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // No persistent resources needed; everything is created per-frame.
    }

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession ds = args.DrawingSession;
        ds.Clear(Color.FromArgb(255, 18, 18, 18));

        for (int i = 0; i < _nodes.Count; i++)
        {
            DrawNode(ds, _nodes[i], i);
        }
    }

    private void DrawNode(CanvasDrawingSession ds, TreemapNode node, int index)
    {
        Rect r = node.Bounds;
        const float gap = 1f;

        float x = (float)(r.X + gap);
        float y = (float)(r.Y + gap);
        float w = (float)(r.Width - gap * 2);
        float h = (float)(r.Height - gap * 2);

        if (w < 1 || h < 1) return;

        bool hovered   = index == _hoveredIndex;
        bool selected  = index == _selectedIndex;

        // Fill
        Color fill = (hovered || selected) ? ColorGenerator.GetHighlightColor(index) : node.Color;
        ds.FillRectangle(x, y, w, h, fill);

        // Border: thicker and white for the selected item
        float borderW  = selected ? 2.5f : 0.8f;
        Color borderC  = selected
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(130, 0, 0, 0);
        ds.DrawRectangle(x, y, w, h, borderC, borderW);

        // Label text (only when the rectangle is large enough to be readable)
        if (w >= 28 && h >= 16)
        {
            float fontSize = Math.Clamp(Math.Min(h / 4f, w / 7f), 8f, 13f);

            using var fmt = new CanvasTextFormat
            {
                FontFamily           = "Segoe UI",
                FontSize             = fontSize,
                WordWrapping         = CanvasWordWrapping.NoWrap,
                HorizontalAlignment  = CanvasHorizontalAlignment.Left,
                VerticalAlignment    = CanvasVerticalAlignment.Top,
            };

            string name  = node.Label;
            string count = $"{node.Value:N0}";
            Color  white = Color.FromArgb(255, 255, 255, 255);
            Color  dim   = Color.FromArgb(190, 255, 255, 255);

            if (h >= 34)
            {
                // Two lines: name + count
                ds.DrawText(TruncateLabel(name, (int)(w / (fontSize * 0.55f))),
                    x + 4, y + 4, white, fmt);

                using var smallFmt = new CanvasTextFormat
                {
                    FontFamily          = "Segoe UI",
                    FontSize            = Math.Max(7f, fontSize - 2f),
                    WordWrapping        = CanvasWordWrapping.NoWrap,
                    HorizontalAlignment = CanvasHorizontalAlignment.Left,
                    VerticalAlignment   = CanvasVerticalAlignment.Top,
                };
                ds.DrawText(count, x + 4, y + 5 + fontSize, dim, smallFmt);
            }
            else
            {
                // Single line: "name: count"
                int maxChars = (int)(w / (fontSize * 0.55f));
                string line  = $"{TruncateLabel(name, Math.Max(4, maxChars - count.Length - 2))}: {count}";
                ds.DrawText(line, x + 4, y + (h - fontSize) / 2, white, fmt);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Pointer interaction
    // -------------------------------------------------------------------------

    private void Canvas_PointerEntered(object sender, PointerRoutedEventArgs e) { }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Point pos = e.GetCurrentPoint(canvas).Position;
        int hit = HitTest(pos);

        if (hit != _hoveredIndex)
        {
            _hoveredIndex = hit;
            canvas.Invalidate();
        }

        if (hit >= 0)
        {
            TreemapNode n = _nodes[hit];
            tooltipTitle.Text = n.Label;
            tooltipCount.Text = $"{n.Value:N0} email{(n.Value == 1 ? "" : "s")}";

            // Position the tooltip just below-right of the cursor, keeping it inside bounds.
            double tx = Math.Min(pos.X + 14, ActualWidth - 170);
            double ty = Math.Min(pos.Y + 14, ActualHeight - 58);
            tooltipTransform.X = tx;
            tooltipTransform.Y = ty;
            tooltipBorder.Visibility = Visibility.Visible;
        }
        else
        {
            tooltipBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Point pos = e.GetCurrentPoint(canvas).Position;
        int hit = HitTest(pos);

        if (hit != _selectedIndex)
        {
            _selectedIndex = hit;
            canvas.Invalidate();
            SelectedGroupChanged?.Invoke(this, hit >= 0 ? _nodes[hit].Group : null);
        }
    }

    private void Canvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoveredIndex = -1;
        tooltipBorder.Visibility = Visibility.Collapsed;
        canvas.Invalidate();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private int HitTest(Point pos)
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            Rect r = _nodes[i].Bounds;
            if (pos.X >= r.X && pos.X < r.X + r.Width &&
                pos.Y >= r.Y && pos.Y < r.Y + r.Height)
                return i;
        }
        return -1;
    }

    private static string TruncateLabel(string label, int maxChars)
    {
        if (maxChars <= 1) return "…";
        if (label.Length <= maxChars) return label;
        return string.Concat(label.AsSpan(0, maxChars - 1), "…");
    }
}
