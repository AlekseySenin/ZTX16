namespace ZTX16.Cartridge;

/// <summary>
/// Tilemap resources: 32×32 tiles of 8×8 pixels (256×256 ColorIndex values).
/// </summary>
/// <remarks>
/// Project side: an indexed-color PNG up to 256×256 (both sides multiples of 8). Each pixel's palette
/// index is its ColorIndex (0 = transparent, at most 31). The image is placed at the lower-left of the
/// tilemap, so tile 0 is the image's lower-left tile; tile IDs grow to the right and then upward
/// (<c>id = row * 32 + column</c>, row 0 at the bottom).
/// Cartridge side: 65 536 ColorIndex bytes, row 0 (the bottom row) first, followed by the PNG's first
/// 32 palette colors as RGB triples (96 bytes) so extraction can restore the artist's colors.
/// </remarks>
public static class TilemapFormat
{
    /// <summary>Tilemap size in pixels.</summary>
    public const int Size = 256;

    /// <summary>Tilemap size in tiles.</summary>
    public const int Tiles = 32;

    /// <summary>Tile size in pixels.</summary>
    public const int TileSize = 8;

    private const int PixelBytes = Size * Size;
    private const int PaletteBytes = 32 * 3;

    /// <summary>Converts a project PNG into cartridge data.</summary>
    /// <exception cref="ProjectException">The image is not a valid tilemap.</exception>
    public static byte[] FromPng(ReadOnlySpan<byte> png, string file)
    {
        IndexedImage image = PngCodec.Decode(png, file);
        if (image.Width > Size || image.Height > Size || image.Width % TileSize != 0 || image.Height % TileSize != 0)
        {
            throw new ProjectException($"{file}: a tilemap image must be at most 256×256 pixels with sides that are multiples of 8 (found {image.Width}×{image.Height}).");
        }

        byte[] data = new byte[PixelBytes + PaletteBytes];
        for (int py = 0; py < image.Height; py++)
        {
            int y = image.Height - 1 - py; // PNG rows run top-down; ZTX16 rows run bottom-up.
            for (int x = 0; x < image.Width; x++)
            {
                byte index = image.Indices[(py * image.Width) + x];
                if (index > 31)
                {
                    throw new ProjectException($"{file}: pixel ({x}, {py}) uses palette index {index}; ZTX16 has 32 colors (indices 0..31).");
                }

                data[(y * Size) + x] = index;
            }
        }

        image.Palette.AsSpan(0, Math.Min(image.Palette.Length, PaletteBytes)).CopyTo(data.AsSpan(PixelBytes));
        return data;
    }

    /// <summary>Converts cartridge data back into a 256×256 indexed PNG.</summary>
    public static byte[] ToPng(ReadOnlySpan<byte> data, string name)
    {
        Validate(data, name);
        byte[] indices = new byte[PixelBytes];
        for (int y = 0; y < Size; y++)
        {
            data.Slice(y * Size, Size).CopyTo(indices.AsSpan((Size - 1 - y) * Size));
        }

        return PngCodec.Encode(new IndexedImage(Size, Size, indices, data.Slice(PixelBytes, PaletteBytes).ToArray()));
    }

    /// <summary>The ColorIndex pixels (row 0 at the bottom).</summary>
    public static byte[] Pixels(ReadOnlySpan<byte> data, string name)
    {
        Validate(data, name);
        return data[..PixelBytes].ToArray();
    }

    private static void Validate(ReadOnlySpan<byte> data, string name)
    {
        if (data.Length != PixelBytes + PaletteBytes)
        {
            throw new ProjectException($"Tilemap '{name}' has {data.Length} bytes; expected {PixelBytes + PaletteBytes}.");
        }
    }
}
