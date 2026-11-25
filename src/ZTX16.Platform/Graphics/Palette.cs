namespace ZTX16.Graphics;

/// <summary>
/// The 32-entry runtime palette. ColorIndex 0 is transparent inside Graphics Layers;
/// indices 1..31 are visible colors.
/// </summary>
/// <remarks>
/// The color stored at index 0 is never drawn by a layer pixel. It is used as the
/// backdrop color shown where every enabled layer is transparent.
/// </remarks>
public sealed class Palette
{
    /// <summary>Number of palette entries.</summary>
    public const int Size = 32;

    /// <summary>The transparent ColorIndex.</summary>
    public const byte Transparent = 0;

    private readonly Color[] _colors = new Color[Size];

    /// <summary>Creates a palette with every entry set to black.</summary>
    public Palette()
    {
    }

    /// <summary>Creates a palette from exactly 32 colors.</summary>
    /// <exception cref="ArgumentException"><paramref name="colors"/> does not contain exactly 32 entries.</exception>
    public Palette(ReadOnlySpan<Color> colors)
    {
        if (colors.Length != Size)
        {
            throw new ArgumentException($"A palette requires exactly {Size} colors.", nameof(colors));
        }

        colors.CopyTo(_colors);
    }

    /// <summary>Returns the color at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0..31.</exception>
    public Color GetColor(int index)
    {
        ValidateIndex(index);
        return _colors[index];
    }

    /// <summary>Sets the color at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0..31.</exception>
    public void SetColor(int index, Color color)
    {
        ValidateIndex(index);
        _colors[index] = color;
    }

    /// <summary>Copies all entries from <paramref name="source"/>.</summary>
    public void CopyFrom(Palette source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source._colors.CopyTo(_colors, 0);
    }

    /// <summary>Validates that <paramref name="index"/> is a legal ColorIndex.</summary>
    internal static void ValidateIndex(int index, string paramName = "index")
    {
        if ((uint)index >= Size)
        {
            throw new ArgumentOutOfRangeException(paramName, index, "ColorIndex must be in 0..31.");
        }
    }

    /// <summary>
    /// Creates the default palette given to a new session: a dark backdrop at index 0,
    /// a grey ramp, and saturated hues.
    /// </summary>
    public static Palette CreateDefault()
    {
        Color[] colors =
        [
            new(0, 0, 1), // 0  backdrop
            new(0, 0, 0), // 1  black
            new(2, 2, 2), // 2  dark grey
            new(4, 4, 4), // 3  grey
            new(6, 6, 6), // 4  light grey
            new(7, 7, 7), // 5  white
            new(4, 0, 0), // 6  dark red
            new(7, 0, 0), // 7  red
            new(7, 4, 4), // 8  pink
            new(7, 3, 0), // 9  orange
            new(4, 2, 0), // 10 brown
            new(7, 7, 0), // 11 yellow
            new(7, 7, 4), // 12 pale yellow
            new(0, 3, 0), // 13 dark green
            new(0, 6, 0), // 14 green
            new(4, 7, 2), // 15 lime
            new(0, 3, 3), // 16 teal
            new(0, 7, 7), // 17 cyan
            new(0, 0, 4), // 18 navy
            new(0, 2, 7), // 19 blue
            new(3, 5, 7), // 20 sky
            new(3, 0, 4), // 21 purple
            new(6, 0, 6), // 22 magenta
            new(7, 4, 7), // 23 lavender
            new(5, 4, 3), // 24 tan
            new(3, 2, 1), // 25 dark brown
            new(1, 2, 3), // 26 slate
            new(2, 4, 5), // 27 steel
            new(5, 6, 7), // 28 ice
            new(6, 5, 2), // 29 sand
            new(2, 1, 0), // 30 umber
            new(1, 1, 1), // 31 near black
        ];
        return new Palette(colors);
    }
}
