using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ZTX16.Cartridge;

/// <summary>
/// Palette resources. Project side: a text file with exactly 32 lines of <c>R G B</c> components
/// (0..7 each, entry 0 is the backdrop); <c>#</c> starts a comment. Cartridge side: 32 little-endian
/// u16 values packed as RGB 3:3:3.
/// </summary>
public static class PaletteFormat
{
    /// <summary>Number of entries.</summary>
    public const int Entries = 32;

    /// <summary>Parses the project text format into packed 9-bit colors.</summary>
    /// <exception cref="ProjectException">The text is invalid.</exception>
    public static ushort[] ParseText(string text, string file)
    {
        var colors = new List<ushort>(Entries);
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int lineNumber = 1; lineNumber <= lines.Length; lineNumber++)
        {
            string line = lines[lineNumber - 1];
            int comment = line.IndexOf('#');
            string content = (comment >= 0 ? line[..comment] : line).Trim();
            if (content.Length == 0)
            {
                continue;
            }

            string[] parts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                throw new ProjectException($"{file}({lineNumber}): expected 'R G B', found '{content}'.");
            }

            int[] rgb = new int[3];
            for (int i = 0; i < 3; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out rgb[i]) || rgb[i] > 7)
                {
                    throw new ProjectException($"{file}({lineNumber}): color components must be 0..7, found '{parts[i]}'.");
                }
            }

            colors.Add((ushort)((rgb[0] << 6) | (rgb[1] << 3) | rgb[2]));
        }

        if (colors.Count != Entries)
        {
            throw new ProjectException($"{file}: a palette needs exactly {Entries} colors, found {colors.Count}.");
        }

        return colors.ToArray();
    }

    /// <summary>Formats packed colors as project text.</summary>
    public static string FormatText(ReadOnlySpan<ushort> colors)
    {
        var text = new StringBuilder("# ZTX16 palette: 32 entries of R G B (0..7). Entry 0 is the backdrop.\n");
        for (int i = 0; i < colors.Length; i++)
        {
            int c = colors[i];
            text.Append(CultureInfo.InvariantCulture, $"{(c >> 6) & 7} {(c >> 3) & 7} {c & 7}");
            text.Append(CultureInfo.InvariantCulture, $"    # {i}\n");
        }

        return text.ToString();
    }

    /// <summary>Encodes packed colors as cartridge data.</summary>
    public static byte[] Encode(ReadOnlySpan<ushort> colors)
    {
        byte[] data = new byte[colors.Length * 2];
        for (int i = 0; i < colors.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), colors[i]);
        }

        return data;
    }

    /// <summary>Decodes cartridge data into packed colors.</summary>
    /// <exception cref="ProjectException">The data has the wrong size or values out of range.</exception>
    public static ushort[] Decode(ReadOnlySpan<byte> data, string name)
    {
        if (data.Length != Entries * 2)
        {
            throw new ProjectException($"Palette '{name}' has {data.Length} bytes; expected {Entries * 2}.");
        }

        var colors = new ushort[Entries];
        for (int i = 0; i < Entries; i++)
        {
            colors[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
            if (colors[i] > 511)
            {
                throw new ProjectException($"Palette '{name}' entry {i} is out of range.");
            }
        }

        return colors;
    }
}
