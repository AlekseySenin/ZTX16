namespace ZTX16.Graphics;

/// <summary>
/// A color from the ZTX16 global color space: 512 colors encoded as RGB 3:3:3.
/// Each component is in the range 0..7.
/// </summary>
public readonly record struct Color
{
    /// <summary>Maximum value of a single color component.</summary>
    public const int MaxComponent = 7;

    /// <summary>Number of colors in the global color space.</summary>
    public const int ColorSpaceSize = 512;

    private readonly ushort _value;

    /// <summary>Creates a color from three 3-bit components.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A component is outside 0..7.</exception>
    public Color(int r, int g, int b)
    {
        ValidateComponent(r, nameof(r));
        ValidateComponent(g, nameof(g));
        ValidateComponent(b, nameof(b));
        _value = (ushort)((r << 6) | (g << 3) | b);
    }

    /// <summary>Red component, 0..7.</summary>
    public int R => (_value >> 6) & MaxComponent;

    /// <summary>Green component, 0..7.</summary>
    public int G => (_value >> 3) & MaxComponent;

    /// <summary>Blue component, 0..7.</summary>
    public int B => _value & MaxComponent;

    /// <summary>The packed 9-bit value (RRRGGGBBB), 0..511.</summary>
    public int Value => _value;

    /// <summary>Creates a color from its packed 9-bit value (RRRGGGBBB).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is outside 0..511.</exception>
    public static Color FromValue(int value)
    {
        if ((uint)value >= ColorSpaceSize)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Packed color value must be in 0..511.");
        }

        return new Color((value >> 6) & MaxComponent, (value >> 3) & MaxComponent, value & MaxComponent);
    }

    /// <summary>Host presentation helper: converts to 32-bit ARGB with full alpha.</summary>
    internal uint ToArgb32()
    {
        return 0xFF000000u | ((uint)Expand(R) << 16) | ((uint)Expand(G) << 8) | Expand(B);
    }

    /// <inheritdoc />
    public override string ToString() => $"Color({R}, {G}, {B})";

    // Maps 0..7 linearly onto 0..255 so that 7 is exactly full intensity.
    private static byte Expand(int component) => (byte)((component * 255 + 3) / MaxComponent);

    private static void ValidateComponent(int component, string paramName)
    {
        if ((uint)component > MaxComponent)
        {
            throw new ArgumentOutOfRangeException(paramName, component, "Color component must be in 0..7.");
        }
    }
}
