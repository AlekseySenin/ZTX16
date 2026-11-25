namespace ZTX16.Platform.Graphics;

/// <summary>
/// Per-draw transform: Flip → Scale → Rotation → pixel-perfect rasterization, around a normalized pivot
/// (0,0 = lower-left, 1,1 = upper-right). The draw position is where the pivot lands.
/// </summary>
/// <param name="FlipX">Mirror horizontally.</param>
/// <param name="FlipY">Mirror vertically.</param>
/// <param name="ScaleX">Horizontal scale (fractional allowed; negative also flips).</param>
/// <param name="ScaleY">Vertical scale (fractional allowed; negative also flips).</param>
/// <param name="Rotation">Degrees, counter-clockwise (ZTX16 +Y is up).</param>
/// <param name="PivotX">Normalized pivot X (0 = left, 1 = right).</param>
/// <param name="PivotY">Normalized pivot Y (0 = bottom, 1 = top).</param>
public readonly record struct DrawTransform(bool FlipX, bool FlipY, float ScaleX, float ScaleY, float Rotation, float PivotX, float PivotY)
{
    /// <summary>No flip, scale 1, no rotation, lower-left pivot.</summary>
    public static DrawTransform Identity { get; } = new(false, false, 1f, 1f, 0f, 0f, 0f);

    internal bool IsSimple => ScaleX == 1f && ScaleY == 1f && Rotation % 360f == 0f;
}

/// <summary>Reads the ColorIndex of source pixel (u, v), with (0,0) the source's lower-left pixel.</summary>
internal delegate int PixelSource(int u, int v);

/// <summary>Rasterizes a rectangular pixel source into a Graphics Layer, with transforms and clipping.</summary>
internal static class Rasterizer
{
    /// <summary>
    /// Draws a <paramref name="width"/>×<paramref name="height"/> source so that its pivot lands on
    /// (<paramref name="x"/>, <paramref name="y"/>). ColorIndex 0 is transparent; the result is clipped to
    /// the layer.
    /// </summary>
    public static void Draw(GraphicsLayer layer, int width, int height, PixelSource source, int x, int y, in DrawTransform t)
    {
        if (width <= 0 || height <= 0 || t.ScaleX == 0f || t.ScaleY == 0f)
        {
            return;
        }

        if (float.IsNaN(t.ScaleX) || float.IsNaN(t.ScaleY) || float.IsNaN(t.Rotation) || float.IsNaN(t.PivotX) || float.IsNaN(t.PivotY)
            || float.IsInfinity(t.ScaleX) || float.IsInfinity(t.ScaleY) || float.IsInfinity(t.Rotation))
        {
            throw new ArgumentException("Transform values must be finite numbers.");
        }

        Span<byte> pixels = layer.WritablePixels;
        int layerWidth = layer.Width, layerHeight = layer.Height;
        bool flipX = t.FlipX ^ (t.ScaleX < 0f), flipY = t.FlipY ^ (t.ScaleY < 0f);
        float scaleX = MathF.Abs(t.ScaleX), scaleY = MathF.Abs(t.ScaleY);

        if (scaleX == 1f && scaleY == 1f && t.Rotation % 360f == 0f)
        {
            // Pixel-exact fast path: a translated copy.
            int left = x - (int)MathF.Floor((t.PivotX * width) + 0.5f);
            int bottom = y - (int)MathF.Floor((t.PivotY * height) + 0.5f);
            for (int v = 0; v < height; v++)
            {
                int dy = bottom + v;
                if ((uint)dy >= (uint)layerHeight)
                {
                    continue;
                }

                int sv = flipY ? height - 1 - v : v;
                for (int u = 0; u < width; u++)
                {
                    int dx = left + u;
                    if ((uint)dx >= (uint)layerWidth)
                    {
                        continue;
                    }

                    int color = source(flipX ? width - 1 - u : u, sv);
                    if (color != 0)
                    {
                        pixels[(dy * layerWidth) + dx] = (byte)color;
                    }
                }
            }

            return;
        }

        (float cos, float sin) = CosSin(t.Rotation);
        float pivotU = t.PivotX * width, pivotV = t.PivotY * height;

        // Bounding box of the transformed source rectangle.
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach ((float cu, float cv) in new[] { (0f, 0f), (width, 0f), (0f, height), ((float)width, (float)height) })
        {
            float px = (cu - pivotU) * scaleX, py = (cv - pivotV) * scaleY;
            float rx = (px * cos) - (py * sin) + x, ry = (px * sin) + (py * cos) + y;
            minX = MathF.Min(minX, rx);
            maxX = MathF.Max(maxX, rx);
            minY = MathF.Min(minY, ry);
            maxY = MathF.Max(maxY, ry);
        }

        int x0 = Math.Max(0, (int)MathF.Floor(minX)), x1 = Math.Min(layerWidth - 1, (int)MathF.Ceiling(maxX));
        int y0 = Math.Max(0, (int)MathF.Floor(minY)), y1 = Math.Min(layerHeight - 1, (int)MathF.Ceiling(maxY));

        // Inverse mapping of each destination pixel center (nearest neighbour).
        for (int dy = y0; dy <= y1; dy++)
        {
            for (int dx = x0; dx <= x1; dx++)
            {
                float qx = dx + 0.5f - x, qy = dy + 0.5f - y;
                float px = (qx * cos) + (qy * sin), py = (-qx * sin) + (qy * cos);
                float su = (px / scaleX) + pivotU, sv = (py / scaleY) + pivotV;
                if (su < 0f || sv < 0f || su >= width || sv >= height)
                {
                    continue;
                }

                int u = (int)su, v = (int)sv;
                int color = source(flipX ? width - 1 - u : u, flipY ? height - 1 - v : v);
                if (color != 0)
                {
                    pixels[(dy * layerWidth) + dx] = (byte)color;
                }
            }
        }
    }

    // Exact values for right angles keep the common rotations pixel-perfect.
    private static (float Cos, float Sin) CosSin(float degrees)
    {
        float normalized = ((degrees % 360f) + 360f) % 360f;
        return normalized switch
        {
            0f => (1f, 0f),
            90f => (0f, 1f),
            180f => (-1f, 0f),
            270f => (0f, -1f),
            _ => (MathF.Cos(normalized * MathF.PI / 180f), MathF.Sin(normalized * MathF.PI / 180f)),
        };
    }
}

/// <summary>Pixel sources over tilemap pixels (256×256 ColorIndex values, row 0 at the bottom).</summary>
internal static class TileSources
{
    private const int TileSize = 8;
    private const int TilemapSize = 256;

    /// <summary>A rectangular region of tiles, with a per-tile cyclic pixel offset.</summary>
    public static PixelSource Region(byte[] tilemap, int tileX, int tileY, int offsetX, int offsetY) =>
        (u, v) =>
        {
            int lx = Mod((u % TileSize) - offsetX), ly = Mod((v % TileSize) - offsetY);
            int px = ((tileX + (u / TileSize)) * TileSize) + lx;
            int py = ((tileY + (v / TileSize)) * TileSize) + ly;
            return tilemap[(py * TilemapSize) + px];
        };

    /// <summary>A grid of arbitrary tile IDs (row-major, bottom row first), with a per-tile cyclic pixel offset.</summary>
    public static PixelSource Grid(byte[] tilemap, int[] tiles, int gridWidth, int offsetX, int offsetY) =>
        (u, v) =>
        {
            int id = tiles[((v / TileSize) * gridWidth) + (u / TileSize)];
            int lx = Mod((u % TileSize) - offsetX), ly = Mod((v % TileSize) - offsetY);
            int px = ((id % 32) * TileSize) + lx;
            int py = ((id / 32) * TileSize) + ly;
            return tilemap[(py * TilemapSize) + px];
        };

    private static int Mod(int value) => ((value % TileSize) + TileSize) % TileSize;
}
