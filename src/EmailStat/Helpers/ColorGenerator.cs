namespace EmailStat.Helpers;

using Windows.UI;

/// <summary>
/// Generates a WinDirStat-inspired colour palette for treemap nodes.
/// Colours are evenly distributed around the HSV colour wheel so that adjacent
/// groups are visually distinct.
/// </summary>
public static class ColorGenerator
{
    // Fixed golden-ratio increment gives perceptually distinct hues even when
    // the number of items is unknown ahead of time.
    private const double GoldenRatioConjugate = 0.618033988749895;

    // A small curated set of vivid base colours used as a quick lookup for the
    // first dozen groups (matches WinDirStat's style closely).
    private static readonly Color[] BasePalette =
    [
        Color.FromArgb(255, 204,  27,  27),  // red
        Color.FromArgb(255,  39, 127,  39),  // green
        Color.FromArgb(255,  27,  94, 190),  // blue
        Color.FromArgb(255, 196, 140,   0),  // gold
        Color.FromArgb(255, 130,  40, 160),  // purple
        Color.FromArgb(255,   0, 160, 160),  // teal
        Color.FromArgb(255, 210,  90,   0),  // orange
        Color.FromArgb(255, 140,  80,   0),  // brown
        Color.FromArgb(255,   0, 140, 200),  // sky blue
        Color.FromArgb(255, 180,  30, 100),  // rose
        Color.FromArgb(255,  80, 180,  60),  // lime
        Color.FromArgb(255,  60,  60, 160),  // indigo
    ];

    /// <summary>Returns the fill colour for a treemap node at position <paramref name="index"/>.</summary>
    public static Color GetColor(int index)
    {
        if (index < BasePalette.Length)
            return BasePalette[index];

        // Beyond the palette, generate by rotating through the hue wheel.
        double hue = (index * GoldenRatioConjugate * 360.0) % 360.0;
        return FromHsv(hue, saturation: 0.72, value: 0.78);
    }

    /// <summary>Returns a lightened version of the colour at <paramref name="index"/> for hover/selection.</summary>
    public static Color GetHighlightColor(int index)
    {
        Color c = GetColor(index);
        return Color.FromArgb(
            255,
            Lighten(c.R),
            Lighten(c.G),
            Lighten(c.B));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static byte Lighten(byte channel) =>
        (byte)Math.Min(255, channel + 70);

    /// <summary>Convert HSV (hue 0-360, saturation 0-1, value 0-1) to an sRGB colour.</summary>
    private static Color FromHsv(double hue, double saturation, double value)
    {
        int hi = (int)(hue / 60) % 6;
        double f  = hue / 60 - Math.Floor(hue / 60);
        double p  = value * (1 - saturation);
        double q  = value * (1 - f * saturation);
        double t  = value * (1 - (1 - f) * saturation);

        (double r, double g, double b) = hi switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };

        return Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
