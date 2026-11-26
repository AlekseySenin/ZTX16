using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ZTX16.Cartridge;

/// <summary>
/// Imports any common PNG (greyscale, RGB, indexed, with or without alpha; 8-bit channels, or 1/2/4/8-bit
/// palettes) into ZTX16 ColorIndex pixels by choosing the nearest colour of a 32-colour palette. Transparent
/// pixels (alpha below 128) become index 0.
/// </summary>
public static class PngImport
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Largest image side accepted.</summary>
    public const int MaxSide = 4096;

    /// <summary>
    /// Decodes <paramref name="png"/> and maps it onto <paramref name="palette"/> (32 × RGB, 8 bits per channel).
    /// Returns indices with row 0 at the <b>bottom</b> (ZTX16 order).
    /// </summary>
    /// <exception cref="ProjectException">The file is not a PNG this importer supports.</exception>
    public static byte[] ToIndices(ReadOnlySpan<byte> png, ReadOnlySpan<byte> palette, out int width, out int height)
    {
        byte[] rgba = DecodeRgba(png, out width, out height);
        var indices = new byte[width * height];
        var cache = new Dictionary<int, byte>();
        for (int py = 0; py < height; py++)
        {
            int y = height - 1 - py;
            for (int x = 0; x < width; x++)
            {
                int i = ((py * width) + x) * 4;
                byte index = 0;
                if (rgba[i + 3] >= 128)
                {
                    int key = (rgba[i] << 16) | (rgba[i + 1] << 8) | rgba[i + 2];
                    if (!cache.TryGetValue(key, out index))
                    {
                        index = Nearest(palette, rgba[i], rgba[i + 1], rgba[i + 2]);
                        cache[key] = index;
                    }
                }

                indices[(y * width) + x] = index;
            }
        }

        return indices;
    }

    // The palette entry (1..31; 0 is transparent) closest in RGB, weighted for perceived brightness.
    private static byte Nearest(ReadOnlySpan<byte> palette, int r, int g, int b)
    {
        int best = 1;
        long bestDistance = long.MaxValue;
        for (int c = 1; c < 32 && (c * 3) + 2 < palette.Length; c++)
        {
            int dr = r - palette[c * 3], dg = g - palette[(c * 3) + 1], db = b - palette[(c * 3) + 2];
            long distance = (3L * dr * dr) + (4L * dg * dg) + (2L * db * db);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = c;
            }
        }

        return (byte)best;
    }

    /// <summary>Decodes a PNG into RGBA bytes (row 0 at the top).</summary>
    public static byte[] DecodeRgba(ReadOnlySpan<byte> png, out int width, out int height)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new ProjectException("Not a PNG file.");
        }

        width = height = 0;
        int bitDepth = 0, colorType = 0;
        byte[] plte = [];
        byte[] trns = [];
        using var compressed = new MemoryStream();
        int pos = Signature.Length;
        while (pos + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[pos..]);
            if (length < 0 || pos + 12 + length > png.Length)
            {
                throw new ProjectException("Truncated PNG chunk.");
            }

            string type = Encoding.ASCII.GetString(png.Slice(pos + 4, 4));
            ReadOnlySpan<byte> data = png.Slice(pos + 8, length);
            pos += 12 + length;
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bitDepth = data[8];
                colorType = data[9];
                if (data[12] != 0)
                {
                    throw new ProjectException("Interlaced PNGs are not supported; save it without interlacing.");
                }
            }
            else if (type == "PLTE")
            {
                plte = data.ToArray();
            }
            else if (type == "tRNS")
            {
                trns = data.ToArray();
            }
            else if (type == "IDAT")
            {
                compressed.Write(data);
            }
            else if (type == "IEND")
            {
                break;
            }
        }

        if (width < 1 || height < 1 || width > MaxSide || height > MaxSide)
        {
            throw new ProjectException($"Unsupported PNG size {width}×{height} (at most {MaxSide}×{MaxSide}).");
        }

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        bool supported = channels > 0 && (bitDepth == 8 || (colorType is 0 or 3 && bitDepth is 1 or 2 or 4));
        if (!supported)
        {
            throw new ProjectException($"Unsupported PNG format (colour type {colorType}, {bitDepth}-bit); use 8-bit RGB, RGBA, grey or indexed.");
        }

        int bitsPerPixel = channels * bitDepth;
        int stride = ((width * bitsPerPixel) + 7) / 8;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        byte[] raw;
        compressed.Position = 0;
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            inflater.CopyTo(output);
            raw = output.ToArray();
        }

        if (raw.Length < (stride + 1) * height)
        {
            throw new ProjectException("PNG image data is truncated.");
        }

        var rgba = new byte[width * height * 4];
        var previous = new byte[stride];
        var line = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            Array.Copy(raw, (y * (stride + 1)) + 1, line, 0, stride);
            Unfilter(filter, line, previous, bpp);
            for (int x = 0; x < width; x++)
            {
                int o = ((y * width) + x) * 4;
                switch (colorType)
                {
                    case 0:
                    {
                        int v = Sample(line, x, bitDepth);
                        byte g = (byte)(v * 255 / ((1 << bitDepth) - 1));
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = g;
                        rgba[o + 3] = 255;
                        break;
                    }

                    case 3:
                    {
                        int i = Sample(line, x, bitDepth);
                        rgba[o] = (i * 3) + 2 < plte.Length ? plte[i * 3] : (byte)0;
                        rgba[o + 1] = (i * 3) + 2 < plte.Length ? plte[(i * 3) + 1] : (byte)0;
                        rgba[o + 2] = (i * 3) + 2 < plte.Length ? plte[(i * 3) + 2] : (byte)0;
                        rgba[o + 3] = i < trns.Length ? trns[i] : (byte)255;
                        break;
                    }

                    case 2:
                        rgba[o] = line[x * 3];
                        rgba[o + 1] = line[(x * 3) + 1];
                        rgba[o + 2] = line[(x * 3) + 2];
                        rgba[o + 3] = 255;
                        break;
                    case 4:
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = line[x * 2];
                        rgba[o + 3] = line[(x * 2) + 1];
                        break;
                    default:
                        rgba[o] = line[x * 4];
                        rgba[o + 1] = line[(x * 4) + 1];
                        rgba[o + 2] = line[(x * 4) + 2];
                        rgba[o + 3] = line[(x * 4) + 3];
                        break;
                }
            }

            (previous, line) = (line, previous);
        }

        return rgba;
    }

    private static int Sample(byte[] line, int x, int bitDepth)
    {
        if (bitDepth == 8)
        {
            return line[x];
        }

        int perByte = 8 / bitDepth;
        int shift = 8 - (bitDepth * ((x % perByte) + 1));
        return (line[x / perByte] >> shift) & ((1 << bitDepth) - 1);
    }

    private static void Unfilter(int filter, byte[] line, byte[] previous, int bpp)
    {
        for (int i = 0; i < line.Length; i++)
        {
            int a = i >= bpp ? line[i - bpp] : 0;
            int b = previous[i];
            int c = i >= bpp ? previous[i - bpp] : 0;
            line[i] = filter switch
            {
                1 => (byte)(line[i] + a),
                2 => (byte)(line[i] + b),
                3 => (byte)(line[i] + ((a + b) / 2)),
                4 => (byte)(line[i] + Paeth(a, b, c)),
                0 => line[i],
                _ => throw new ProjectException($"Unknown PNG filter {filter}."),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
