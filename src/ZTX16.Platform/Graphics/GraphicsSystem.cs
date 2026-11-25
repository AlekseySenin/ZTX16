namespace ZTX16.Platform.Graphics;

/// <summary>
/// The ZTX16 graphics hardware: a 256×256 logical display, a global 32-entry palette,
/// eight Graphics Layers and eight Display Layers mapped 1:1 onto them.
/// </summary>
public sealed class GraphicsSystem
{
    /// <summary>Logical display width in pixels.</summary>
    public const int DisplayWidth = 256;

    /// <summary>Logical display height in pixels.</summary>
    public const int DisplayHeight = 256;

    /// <summary>Number of physical layers.</summary>
    public const int LayerCount = 8;

    private readonly GraphicsLayer[] _layers = new GraphicsLayer[LayerCount];
    private readonly DisplayLayer[] _displayLayers = new DisplayLayer[LayerCount];

    internal GraphicsSystem()
    {
        for (int i = 0; i < LayerCount; i++)
        {
            _layers[i] = new GraphicsLayer(i);
            _displayLayers[i] = new DisplayLayer(_layers[i]);
        }

        Layers = Array.AsReadOnly(_layers);
        DisplayLayers = Array.AsReadOnly(_displayLayers);
    }

    /// <summary>The global runtime palette.</summary>
    public Palette Palette { get; } = Palette.CreateDefault();

    /// <summary>Graphics Layers 0 (lowest) through 7 (highest).</summary>
    public IReadOnlyList<GraphicsLayer> Layers { get; }

    /// <summary>Display Layers 0 through 7; Display Layer N exposes Graphics Layer N.</summary>
    public IReadOnlyList<DisplayLayer> DisplayLayers { get; }

    /// <summary>Replaces every palette entry with the entries of <paramref name="palette"/>.</summary>
    public void SetPalette(Palette palette) => Palette.CopyFrom(palette);

    /// <summary>Clears every Graphics Layer to ColorIndex 0.</summary>
    public void ClearLayers()
    {
        foreach (GraphicsLayer layer in _layers)
        {
            layer.Clear();
        }
    }

    /// <summary>
    /// Composes the final image as ColorIndex values into <paramref name="frame"/>,
    /// which must hold 256×256 entries in ZTX16 row order (row 0 = bottom).
    /// For each pixel the highest enabled layer with a non-zero value wins; if none,
    /// the result is 0 (backdrop).
    /// </summary>
    internal void Compose(Span<byte> frame)
    {
        if (frame.Length != DisplayWidth * DisplayHeight)
        {
            throw new ArgumentException("Frame must hold exactly 256×256 entries.", nameof(frame));
        }

        frame.Clear();
        Span<bool> rowSettled = stackalloc bool[DisplayWidth];

        for (int y = 0; y < DisplayHeight; y++)
        {
            Span<byte> outRow = frame.Slice(y * DisplayWidth, DisplayWidth);
            rowSettled.Clear();
            int remaining = DisplayWidth;

            for (int layerIndex = LayerCount - 1; layerIndex >= 0 && remaining > 0; layerIndex--)
            {
                DisplayLayer display = _displayLayers[layerIndex];
                if (!display.Enabled)
                {
                    continue;
                }

                GraphicsLayer layer = display.Source;
                int width = layer.Width;
                int sourceY = Wrap(display.OriginY + y, layer.Height);
                ReadOnlySpan<byte> sourceRow = layer.Pixels.Slice(sourceY * width, width);
                int sourceX = Wrap(display.OriginX, width);

                for (int x = 0; x < DisplayWidth; x++)
                {
                    if (!rowSettled[x])
                    {
                        byte value = sourceRow[sourceX];
                        if (value != Palette.Transparent)
                        {
                            outRow[x] = value;
                            rowSettled[x] = true;
                            remaining--;
                        }
                    }

                    if (++sourceX == width)
                    {
                        sourceX = 0;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Converts a composed ColorIndex frame into 32-bit ARGB pixels in host row order
    /// (top row first). Index 0 is drawn with the backdrop color at palette entry 0.
    /// </summary>
    internal void ConvertToArgb(ReadOnlySpan<byte> frame, Span<uint> argb)
    {
        int count = DisplayWidth * DisplayHeight;
        if (frame.Length != count || argb.Length != count)
        {
            throw new ArgumentException("Frame and output must hold exactly 256×256 entries.");
        }

        Span<uint> lookup = stackalloc uint[Palette.Size];
        for (int i = 0; i < Palette.Size; i++)
        {
            lookup[i] = Palette.GetColor(i).ToArgb32();
        }

        for (int y = 0; y < DisplayHeight; y++)
        {
            ReadOnlySpan<byte> sourceRow = frame.Slice(y * DisplayWidth, DisplayWidth);
            Span<uint> targetRow = argb.Slice((DisplayHeight - 1 - y) * DisplayWidth, DisplayWidth);
            for (int x = 0; x < DisplayWidth; x++)
            {
                targetRow[x] = lookup[sourceRow[x]];
            }
        }
    }

    private static int Wrap(int value, int size)
    {
        int result = value % size;
        return result < 0 ? result + size : result;
    }
}
