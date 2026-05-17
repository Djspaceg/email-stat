namespace EmailStat.Controls;

using EmailStat.Helpers;
using EmailStat.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.Specialized;
using System.Numerics;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

/// <summary>
/// A WinUI 3 UserControl that renders a WinDirStat-style squarified treemap
/// using Win2D (hardware-accelerated Direct2D).
/// Each rectangle is filled with a diagonal cushion gradient that simulates
/// WinDirStat's 3-D surface shading with a top-left light source.
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
    // Drawing constants
    // -------------------------------------------------------------------------

    /// <summary>Pixel offset applied to the shadow pass of each label to create a drop-shadow effect.</summary>
    private const float ShadowOffsetX = 1f;
    private const float ShadowOffsetY = 1f;

    /// <summary>Pixel offset of the tooltip from the cursor position.</summary>
    private const double TooltipCursorOffset = 14;
    /// <summary>Approximate rendered width of the tooltip, used to keep it within bounds.</summary>
    private const double TooltipWidth = 170;
    /// <summary>Approximate rendered height of the tooltip, used to keep it within bounds.</summary>
    private const double TooltipHeight = 58;

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

        // Redraw when the user switches between light and dark mode so the canvas
        // background colour stays in sync with the rest of the UI.
        ActualThemeChanged += (_, _) => canvas.Invalidate();
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
        ds.Clear(GetCanvasBackground());

        for (int i = 0; i < _nodes.Count; i++)
        {
            DrawNode(ds, _nodes[i], i);
        }
    }

    /// <summary>
    /// Returns the current theme's page-background colour so the Win2D canvas
    /// background matches the rest of the UI in both light and dark mode.
    /// These are the exact values of <c>ApplicationPageBackgroundThemeBrush</c>
    /// in the WinUI 3 default resource dictionary.
    /// </summary>
    private Color GetCanvasBackground() => ActualTheme switch
    {
        ElementTheme.Light => Color.FromArgb(255, 243, 243, 243), // #F3F3F3 – WinUI 3 Light
        ElementTheme.Dark  => Color.FromArgb(255, 28, 28, 28),    // #1C1C1C – WinUI 3 Dark
        _                  => Color.FromArgb(255, 28, 28, 28),    // Default → follow system (assume Dark)
    };

    private void DrawNode(CanvasDrawingSession ds, TreemapNode node, int index)
    {
        Rect r = node.Bounds;
        const float gap = 1f;

        float x = (float)(r.X + gap);
        float y = (float)(r.Y + gap);
        float w = (float)(r.Width - gap * 2);
        float h = (float)(r.Height - gap * 2);

        if (w < 1 || h < 1) return;

        bool hovered  = index == _hoveredIndex;
        bool selected = index == _selectedIndex;

        Color baseColor = (hovered || selected) ? ColorGenerator.GetHighlightColor(index) : node.Color;

        // ── Cushion gradient ──────────────────────────────────────────────────
        // Simulate WinDirStat's 3-D surface shading: light comes from the
        // top-left, so the upper-left corner is lighter and the lower-right
        // corner uses the saturated base color.  A mid-stop at 45% gives a
        // smooth roll-off that approximates a parabolic cushion surface.
        Color gradStart = LightenColor(baseColor, selected ? 85 : 60);
        Color gradMid   = LightenColor(baseColor, selected ? 30 : 15);
        Color gradEnd   = DarkenColor(baseColor, 18);

        using var fillBrush = new CanvasLinearGradientBrush(
            ds,
            [
                new CanvasGradientStop { Color = gradStart, Position = 0.00f },
                new CanvasGradientStop { Color = gradMid,   Position = 0.45f },
                new CanvasGradientStop { Color = gradEnd,   Position = 1.00f },
            ])
        {
            // Diagonal: top-left corner → bottom-right corner
            StartPoint = new Vector2(x, y),
            EndPoint   = new Vector2(x + w, y + h),
        };

        ds.FillRectangle(x, y, w, h, fillBrush);

        // ── Inner bevel highlight on top and left edges ───────────────────────
        // Gives each rectangle a subtle raised look, matching WinDirStat.
        if (w >= 4 && h >= 4)
        {
            Color bevel = Color.FromArgb(70, 255, 255, 255);
            ds.DrawLine(x, y, x + w - 1, y, bevel, 1.2f);      // top edge
            ds.DrawLine(x, y, x, y + h - 1, bevel, 1.2f);      // left edge
        }

        // ── Border ────────────────────────────────────────────────────────────
        // White + thicker for the selected rectangle, dark-translucent otherwise.
        float borderW = selected ? 2.5f : 0.8f;
        Color borderC = selected
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(100, 0, 0, 0);
        ds.DrawRectangle(x, y, w, h, borderC, borderW);

        // ── Label text ────────────────────────────────────────────────────────
        // Only when the rectangle is large enough to be readable.
        if (w >= 28 && h >= 16)
        {
            float fontSize = Math.Clamp(Math.Min(h / 4f, w / 7f), 8f, 13f);

            using var fmt = new CanvasTextFormat
            {
                FontFamily          = "Segoe UI",
                FontSize            = fontSize,
                WordWrapping        = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
                VerticalAlignment   = CanvasVerticalAlignment.Top,
            };

            string name  = node.Label;
            string count = $"{node.Value:N0}";
            Color  white  = Color.FromArgb(255, 255, 255, 255);
            Color  dim    = Color.FromArgb(190, 255, 255, 255);
            Color  shadow = Color.FromArgb(130, 0, 0, 0);

            if (h >= 34)
            {
                // Two lines: name + count
                string nameStr = TruncateLabel(name, (int)(w / (fontSize * 0.55f)));
                ds.DrawText(nameStr, x + 4 + ShadowOffsetX, y + 4 + ShadowOffsetY, shadow, fmt);  // shadow
                ds.DrawText(nameStr, x + 4, y + 4, white, fmt);                                    // foreground

                using var smallFmt = new CanvasTextFormat
                {
                    FontFamily          = "Segoe UI",
                    FontSize            = Math.Max(7f, fontSize - 2f),
                    WordWrapping        = CanvasWordWrapping.NoWrap,
                    HorizontalAlignment = CanvasHorizontalAlignment.Left,
                    VerticalAlignment   = CanvasVerticalAlignment.Top,
                };
                float countY = y + 5 + fontSize;
                ds.DrawText(count, x + 4 + ShadowOffsetX, countY + ShadowOffsetY, shadow, smallFmt);  // shadow
                ds.DrawText(count, x + 4, countY, dim, smallFmt);                                     // foreground
            }
            else
            {
                // Single line: "name: count"
                int maxChars = (int)(w / (fontSize * 0.55f));
                string line  = $"{TruncateLabel(name, Math.Max(4, maxChars - count.Length - 2))}: {count}";
                float lineY  = y + (h - fontSize) / 2;
                ds.DrawText(line, x + 4 + ShadowOffsetX, lineY + ShadowOffsetY, shadow, fmt);  // shadow
                ds.DrawText(line, x + 4, lineY, white, fmt);                                    // foreground
            }
        }
    }

    // -------------------------------------------------------------------------
    // Pointer interaction
    // -------------------------------------------------------------------------

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

            // Position the tooltip just below-right of the cursor, clamped within visible bounds.
            double tx = Math.Max(0, Math.Min(pos.X + TooltipCursorOffset, ActualWidth - TooltipWidth));
            double ty = Math.Max(0, Math.Min(pos.Y + TooltipCursorOffset, ActualHeight - TooltipHeight));
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

    /// <summary>
    /// Keyboard navigation: arrow keys move the selection through rectangles,
    /// Space/Enter activate the selection, Escape clears it.
    /// This provides an accessible path for users who cannot use a pointer.
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_nodes.Count == 0) return;

        int next = _selectedIndex;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Right:
            case Windows.System.VirtualKey.Down:
                next = (_selectedIndex < 0) ? 0 : Math.Min(_selectedIndex + 1, _nodes.Count - 1);
                break;

            case Windows.System.VirtualKey.Left:
            case Windows.System.VirtualKey.Up:
                next = (_selectedIndex < 0) ? 0 : Math.Max(_selectedIndex - 1, 0);
                break;

            case Windows.System.VirtualKey.Space:
            case Windows.System.VirtualKey.Enter:
                if (_selectedIndex >= 0)
                    SelectedGroupChanged?.Invoke(this, _nodes[_selectedIndex].Group);
                e.Handled = true;
                return;

            case Windows.System.VirtualKey.Escape:
                next = -1;
                break;

            default:
                return;
        }

        if (next != _selectedIndex)
        {
            _selectedIndex = next;
            canvas.Invalidate();
            SelectedGroupChanged?.Invoke(this, _selectedIndex >= 0 ? _nodes[_selectedIndex].Group : null);
        }

        e.Handled = true;
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

    /// <summary>Returns <paramref name="c"/> lightened by <paramref name="amount"/> per channel.</summary>
    private static Color LightenColor(Color c, int amount) =>
        Color.FromArgb(255,
            (byte)Math.Min(255, c.R + amount),
            (byte)Math.Min(255, c.G + amount),
            (byte)Math.Min(255, c.B + amount));

    /// <summary>Returns <paramref name="c"/> darkened by <paramref name="amount"/> per channel.</summary>
    private static Color DarkenColor(Color c, int amount) =>
        Color.FromArgb(255,
            (byte)Math.Max(0, c.R - amount),
            (byte)Math.Max(0, c.G - amount),
            (byte)Math.Max(0, c.B - amount));
}
