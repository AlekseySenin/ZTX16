using System.Buffers.Binary;
using System.IO.Compression;

namespace ZTX16.Cartridge;

/// <summary>A decoded indexed-color image. Rows are stored top row first, as in the PNG.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Indices">One palette index per pixel, row-major, top row first.</param>
/// <param name="Palette">RGB triples from the PLTE chunk (3 bytes per entry).</param>
public sealed record IndexedImage(int Width, int Height, byte[] Indices, byte[] Palette);

/// <summary>
/// Minimal PNG support for indexed-color images (color type 3, bit depths 1/2/4/8, no interlacing):
/// what pixel art for a 32-color machine needs, with no external dependency.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decodes an indexed PNG.</summary>
    /// <exception cref="ProjectException">The file is not a supported PNG.</exception>
    public static IndexedImage Decode(ReadOnlySpan<byte> png, string file)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new ProjectException($"{file}: not a PNG file.");
        }

        int width = 0, height = 0, bitDepth = 0;
        byte[] palette = [];
        using var compressed = new MemoryStream();
        int pos = Signature.Length;
        bool sawHeader = false;

        while (pos + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[pos..]);
            if (length < 0 || pos + 12 + length > png.Length)
            {
                throw new ProjectException($"{file}: truncated PNG chunk.");
            }

            string type = System.Text.Encoding.ASCII.GetString(png.Slice(pos + 4, 4));
            ReadOnlySpan<byte> data = png.Slice(pos + 8, length);
            pos += 12 + length;

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    bitDepth = data[8];
                    byte colorType = data[9];
                    if (colorType != 3)
                    {
                        throw new ProjectException($"{file}: only indexed-color PNGs are supported (save the image with an indexed palette of at most 32 colors).");
                    }

                    if (data[12] != 0)
                    {
                        throw new ProjectException($"{file}: interlaced PNGs are not supported.");
                    }

                    if (bitDepth is not (1 or 2 or 4 or 8))
                    {
                        throw new ProjectException($"{file}: unsupported bit depth {bitDepth}.");
                    }

                    sawHeader = true;
                    break;
                case "PLTE":
                    palette = data.ToArray();
                    break;
                case "IDAT":
                    compressed.Write(data);
                    break;
                case "IEND":
                    pos = png.Length;
                    break;
            }
        }

        if (!sawHeader || width <= 0 || height <= 0 || width > 4096 || height > 4096)
        {
            throw new ProjectException($"{file}: missing or invalid PNG header.");
        }

        int stride = ((width * bitDepth) + 7) / 8;
        byte[] raw;
        try
        {
            compressed.Position = 0;
            using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            raw = output.ToArray();
        }
        catch (InvalidDataException e)
        {
            throw new ProjectException($"{file}: corrupted PNG image data.", e);
        }

        if (raw.Length < (stride + 1) * height)
        {
            throw new ProjectException($"{file}: PNG image data is too short.");
        }

        byte[] indices = new byte[width * height];
        byte[] previous = new byte[stride];
        byte[] current = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            raw.AsSpan((y * (stride + 1)) + 1, stride).CopyTo(current);
            Unfilter(filter, current, previous, bytesPerPixel: Math.Max(1, bitDepth / 8), file);

            for (int x = 0; x < width; x++)
            {
                int bit = x * bitDepth;
                int value = (current[bit / 8] >> (8 - bitDepth - (bit % 8))) & ((1 << bitDepth) - 1);
                indices[(y * width) + x] = (byte)value;
            }

            (previous, current) = (current, previous);
        }

        return new IndexedImage(width, height, indices, palette);
    }

    /// <summary>Encodes an 8-bit indexed PNG.</summary>
    public static byte[] Encode(IndexedImage image) => Encode(image, filter: 0);

    /// <summary>Encodes using one PNG filter type for every row (exposed for tests of the decoder).</summary>
    internal static byte[] Encode(IndexedImage image, int filter)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8;  // bit depth
        header[9] = 3;  // indexed color
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "PLTE", image.Palette);

        using (var raw = new MemoryStream())
        {
            byte[] previous = new byte[image.Width];
            for (int y = 0; y < image.Height; y++)
            {
                byte[] row = image.Indices.AsSpan(y * image.Width, image.Width).ToArray();
                raw.WriteByte((byte)filter);
                raw.Write(Filter(filter, row, previous));
                previous = row;
            }

            using var compressed = new MemoryStream();
            using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                raw.Position = 0;
                raw.CopyTo(deflate);
            }

            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Unfilter(int filter, byte[] row, byte[] previous, int bytesPerPixel, string file)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
            int up = previous[i];
            int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
            row[i] = filter switch
            {
                0 => row[i],
                1 => (byte)(row[i] + left),
                2 => (byte)(row[i] + up),
                3 => (byte)(row[i] + ((left + up) / 2)),
                4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                _ => throw new ProjectException($"{file}: invalid PNG filter type {filter}."),
            };
        }
    }

    private static byte[] Filter(int filter, byte[] row, byte[] previous)
    {
        byte[] result = new byte[row.Length];
        for (int i = 0; i < row.Length; i++)
        {
            int left = i > 0 ? row[i - 1] : 0;
            int up = previous[i];
            int upLeft = i > 0 ? previous[i - 1] : 0;
            result[i] = (byte)(row[i] - filter switch
            {
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => 0,
            });
        }

        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        uint crc = Crc32(Crc32(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
