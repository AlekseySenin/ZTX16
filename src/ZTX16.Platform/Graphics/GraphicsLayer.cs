namespace ZTX16.Platform.Graphics;

/// <summary>
/// A physical Graphics Layer: a persistent raster buffer of ColorIndex values.
/// Its dimensions are whole multiples of the 256×256 screen.
/// </summary>
/// <remarks>
/// A layer stores pixels only. It has no knowledge of tilemaps, sprites, levels or
/// actors; everything drawn into it is rasterized immediately.
/// Coordinates use the ZTX16 convention: (0,0) is the lower-left pixel, +Y is up.
/// Storage is row-major starting at row y = 0 (the bottom row).
/// </remarks>
public sealed class GraphicsLayer
{
    /// <summary>Width and height of one screen unit in pixels.</summary>
    public const int ScreenSize = 256;

    /// <summary>
    /// Provisional implementation limit on layer size per axis, in screens.
    /// Not a public machine-profile guarantee yet.
    /// </summary>
    internal const int MaxScreensPerAxis = 8;

    private byte[] _pixels;

    internal GraphicsLayer(int index)
    {
        Index = index;
        WidthInScreens = 1;
        HeightInScreens = 1;
        _pixels = new byte[ScreenSize * ScreenSize];
    }

    /// <summary>Physical layer index, 0 (lowest) through 7 (highest).</summary>
    public int Index { get; }

    /// <summary>Layer width in screen units.</summary>
    public int WidthInScreens { get; private set; }

    /// <summary>Layer height in screen units.</summary>
    public int HeightInScreens { get; private set; }

    /// <summary>Layer width in pixels.</summary>
    public int Width => WidthInScreens * ScreenSize;

    /// <summary>Layer height in pixels.</summary>
    public int Height => HeightInScreens * ScreenSize;

    /// <summary>
    /// Recreates the layer with a new size. The resulting buffer is always cleared to
    /// ColorIndex 0, even if the size is unchanged.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is outside the supported range.</exception>
    public void Resize(int widthInScreens, int heightInScreens)
    {
        ValidateScreens(widthInScreens, nameof(widthInScreens));
        ValidateScreens(heightInScreens, nameof(heightInScreens));

        WidthInScreens = widthInScreens;
        HeightInScreens = heightInScreens;
        _pixels = new byte[Width * Height];
    }

    /// <summary>Clears every pixel to ColorIndex 0 (transparent).</summary>
    public void Clear() => Array.Clear(_pixels);

    /// <summary>Fills every pixel with <paramref name="colorIndex"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="colorIndex"/> is outside 0..31.</exception>
    public void Fill(int colorIndex)
    {
        Palette.ValidateIndex(colorIndex, nameof(colorIndex));
        Array.Fill(_pixels, (byte)colorIndex);
    }

    /// <summary>Writes one pixel. Writing ColorIndex 0 erases the pixel.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The position or color index is invalid.</exception>
    public void SetPixel(int x, int y, int colorIndex)
    {
        ValidatePosition(x, y);
        Palette.ValidateIndex(colorIndex, nameof(colorIndex));
        _pixels[(y * Width) + x] = (byte)colorIndex;
    }

    /// <summary>Reads one pixel.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the layer.</exception>
    public int GetPixel(int x, int y)
    {
        ValidatePosition(x, y);
        return _pixels[(y * Width) + x];
    }

    /// <summary>
    /// Fills an axis-aligned rectangle whose lower-left corner is (<paramref name="x"/>, <paramref name="y"/>).
    /// The rectangle is clipped to the layer bounds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is negative or the color index is invalid.</exception>
    public void FillRectangle(int x, int y, int width, int height, int colorIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Palette.ValidateIndex(colorIndex, nameof(colorIndex));

        int left = Math.Max(x, 0);
        int bottom = Math.Max(y, 0);
        int right = Math.Min(x + width, Width);
        int top = Math.Min(y + height, Height);
        if (left >= right || bottom >= top)
        {
            return;
        }

        int rowWidth = Width;
        for (int row = bottom; row < top; row++)
        {
            _pixels.AsSpan((row * rowWidth) + left, right - left).Fill((byte)colorIndex);
        }
    }

    /// <summary>Read-only view of the raw pixel rows for the compositor.</summary>
    internal ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>Writable view of the raw pixel rows for rasterizers (callers clip).</summary>
    internal Span<byte> WritablePixels => _pixels;

    private void ValidatePosition(int x, int y)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, $"X must be in 0..{Width - 1}.");
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, $"Y must be in 0..{Height - 1}.");
        }
    }

    private static void ValidateScreens(int screens, string paramName)
    {
        if (screens < 1 || screens > MaxScreensPerAxis)
        {
            throw new ArgumentOutOfRangeException(paramName, screens, $"Layer size must be 1..{MaxScreensPerAxis} screens per axis.");
        }
    }
}
