using ZTX16.Cartridge;

namespace ZTX16.Platform.Graphics;

/// <summary>A font ready for rendering: its definition plus the pixels of its tilemap.</summary>
public sealed class RuntimeFont
{
    internal RuntimeFont(FontDefinition definition, byte[] tilemapPixels)
    {
        Definition = definition;
        TilemapPixels = tilemapPixels;
    }

    /// <summary>Glyph mapping, advances and metrics.</summary>
    public FontDefinition Definition { get; }

    /// <summary>The tilemap holding the glyph bitmaps (256×256, row 0 at the bottom).</summary>
    internal byte[] TilemapPixels { get; }

    /// <summary>Line height in pixels.</summary>
    public int LineHeight => Definition.LineHeight;

    /// <summary>The built-in system font: 8×8 monospaced glyphs for ASCII 32..126, line height 9.</summary>
    public static RuntimeFont System { get; } = SystemFont.Create();
}

/// <summary>
/// Lays out and renders text with bitmap fonts. The text stream carries two commands:
/// New Line (<c>'\n'</c>) and Set Color (<c>'\u0001'</c> followed by one character whose code is the
/// ColorIndex). Characters map to the 256-code ZTX16 character set; codes above 255 render as '?'.
/// </summary>
/// <remarks>
/// Positions follow the ZTX16 convention: the given point is the lower-left corner of the first line,
/// and following lines go down. In a box, the first line sits at the top of the box and rendering stops at
/// the first line that would cross the box's bottom edge.
/// </remarks>
internal static class TextRenderer
{
    public const char SetColor = '\u0001';
    private const int Unbounded = 0;

    /// <summary>Draws text. A <paramref name="maxWidth"/> or <paramref name="maxHeight"/> of 0 means unbounded.</summary>
    public static void Draw(GraphicsLayer layer, RuntimeFont font, string text, int x, int y, int color, int maxWidth, int maxHeight, bool wordWrap)
    {
        Palette.ValidateIndex(color, nameof(color));
        FontDefinition def = font.Definition;
        int glyphWidth = def.GlyphWidth * 8, glyphHeight = def.GlyphHeight * 8;
        int top = maxHeight > 0 ? y + maxHeight - def.LineHeight : y;

        Layout(font, text, maxWidth, wordWrap, (code, penX, line, currentColor) =>
        {
            int lineY = top - (line * def.LineHeight);
            if (maxHeight > 0 && lineY < y)
            {
                return false; // reached the bottom of the box
            }

            int tile = def.GlyphTiles[code];
            if (tile >= 0 && currentColor != Palette.Transparent)
            {
                DrawGlyph(layer, font.TilemapPixels, tile, glyphWidth, glyphHeight, x + penX, lineY, currentColor);
            }

            return true;
        }, color);
    }

    /// <summary>Width in pixels of the widest line of unwrapped text.</summary>
    public static int Measure(RuntimeFont font, string text)
    {
        int widest = 0;
        Layout(font, text, Unbounded, wordWrap: false, (code, penX, _, _) =>
        {
            widest = Math.Max(widest, penX + font.Definition.Advances[code]);
            return true;
        }, color: 1);
        return widest;
    }

    /// <summary>Number of lines the text occupies with the given wrapping.</summary>
    public static int CountLines(RuntimeFont font, string text, int maxWidth, bool wordWrap)
    {
        int lines = 0;
        Layout(font, text, maxWidth, wordWrap, (_, _, line, _) =>
        {
            lines = Math.Max(lines, line + 1);
            return true;
        }, color: 1);
        return Math.Max(lines, text.Length == 0 ? 0 : 1);
    }

    /// <summary>
    /// Places every character; <paramref name="place"/> receives (code, pen x, line index, color) and returns
    /// false to stop.
    /// </summary>
    private static void Layout(RuntimeFont font, string text, int maxWidth, bool wordWrap, Func<int, int, int, int, bool> place, int color)
    {
        int[] advances = font.Definition.Advances;
        int penX = 0, line = 0;
        bool wrapped = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                line++;
                penX = 0;
                wrapped = false;
                continue;
            }

            if (c == SetColor)
            {
                if (i + 1 < text.Length)
                {
                    color = Math.Min((int)text[++i], 31);
                }

                continue;
            }

            int code = Code(c);
            bool isSpace = c is ' ' or '\t';

            if (maxWidth > 0)
            {
                if (wordWrap && !isSpace && (i == 0 || text[i - 1] is ' ' or '\t' or '\n'))
                {
                    int wordWidth = WordWidth(text, i, advances);
                    if (penX > 0 && penX + wordWidth > maxWidth)
                    {
                        line++;
                        penX = 0;
                        wrapped = true;
                    }
                }

                if (wordWrap && isSpace && penX == 0 && wrapped)
                {
                    continue; // spaces at the start of a wrapped line are dropped
                }

                if (penX > 0 && penX + advances[code] > maxWidth)
                {
                    line++;
                    penX = 0;
                    wrapped = true;
                    if (wordWrap && isSpace)
                    {
                        continue;
                    }
                }
            }

            if (!place(code, penX, line, color))
            {
                return;
            }

            penX += advances[code];
            if (penX > 0)
            {
                wrapped = false;
            }
        }
    }

    private static int WordWidth(string text, int start, int[] advances)
    {
        int width = 0;
        for (int i = start; i < text.Length && text[i] is not (' ' or '\t' or '\n'); i++)
        {
            if (text[i] == SetColor)
            {
                i++;
                continue;
            }

            width += advances[Code(text[i])];
        }

        return width;
    }

    private static int Code(char c) => c <= 255 ? c : '?';

    private static void DrawGlyph(GraphicsLayer layer, byte[] tilemap, int tile, int width, int height, int x, int y, int color)
    {
        Span<byte> pixels = layer.WritablePixels;
        int baseX = (tile % 32) * 8, baseY = (tile / 32) * 8;
        for (int v = 0; v < height; v++)
        {
            int dy = y + v;
            if ((uint)dy >= (uint)layer.Height)
            {
                continue;
            }

            for (int u = 0; u < width; u++)
            {
                int dx = x + u;
                if ((uint)dx < (uint)layer.Width && tilemap[((baseY + v) * 256) + baseX + u] != 0)
                {
                    pixels[(dy * layer.Width) + dx] = (byte)color;
                }
            }
        }
    }
}

/// <summary>The built-in 8×8 font (glyph data from the public-domain font8x8_basic).</summary>
internal static class SystemFont
{
    // ASCII 32..126; one byte per row, top row first; bit 0 is the leftmost pixel.
    private static readonly ulong[] Glyphs =
    [
        0x0000000000000000, 0x00180018183C3C18, 0x0000000000003636, 0x0036367F367F3636, 0x000C1F301E033E0C, 0x0063660C18336300, 0x006E333B6E1C361C, 0x0000000000030606,
        0x00180C0606060C18, 0x00060C1818180C06, 0x0000663CFF3C6600, 0x00000C0C3F0C0C00, 0x060C0C0000000000, 0x000000003F000000, 0x000C0C0000000000, 0x000103060C183060,
        0x003E676F7B73633E, 0x003F0C0C0C0C0E0C, 0x003F33061C30331E, 0x001E33301C30331E, 0x0078307F33363C38, 0x001E3330301F033F, 0x001E33331F03061C, 0x000C0C0C1830333F,
        0x001E33331E33331E, 0x000E18303E33331E, 0x000C0C00000C0C00, 0x060C0C00000C0C00, 0x00180C0603060C18, 0x00003F00003F0000, 0x00060C1830180C06, 0x000C000C1830331E,
        0x001E037B7B7B633E, 0x0033333F33331E0C, 0x003F66663E66663F, 0x003C66030303663C, 0x001F36666666361F, 0x007F46161E16467F, 0x000F06161E16467F, 0x007C66730303663C,
        0x003333333F333333, 0x001E0C0C0C0C0C1E, 0x001E333330303078, 0x006766361E366667, 0x007F66460606060F, 0x0063636B7F7F7763, 0x006363737B6F6763, 0x001C36636363361C,
        0x000F06063E66663F, 0x00381E3B3333331E, 0x006766363E66663F, 0x001E33380E07331E, 0x001E0C0C0C0C2D3F, 0x003F333333333333, 0x000C1E3333333333, 0x0063777F6B636363,
        0x0063361C1C366363, 0x001E0C0C1E333333, 0x007F664C1831637F, 0x001E06060606061E, 0x00406030180C0603, 0x001E18181818181E, 0x0000000063361C08, 0xFF00000000000000,
        0x0000000000180C0C, 0x006E333E301E0000, 0x003B66663E060607, 0x001E3303331E0000, 0x006E33333E303038, 0x001E033F331E0000, 0x000F06060F06361C, 0x1F303E33336E0000,
        0x006766666E360607, 0x001E0C0C0C0E000C, 0x1E33333030300030, 0x0067361E36660607, 0x001E0C0C0C0C0C0E, 0x00636B7F7F330000, 0x00333333331F0000, 0x001E3333331E0000,
        0x0F063E66663B0000, 0x78303E33336E0000, 0x000F06666E3B0000, 0x001F301E033E0000, 0x00182C0C0C3E0C08, 0x006E333333330000, 0x000C1E3333330000, 0x00367F7F6B630000,
        0x0063361C36630000, 0x1F303E3333330000, 0x003F260C193F0000, 0x00380C0C070C0C38, 0x0018181800181818, 0x00070C0C380C0C07, 0x0000000000003B6E,
    ];

    public static RuntimeFont Create()
    {
        byte[] tilemap = new byte[256 * 256];
        int[] tiles = Enumerable.Repeat(-1, FontFormat.Codes).ToArray();
        int[] advances = Enumerable.Repeat(8, FontFormat.Codes).ToArray();

        for (int i = 0; i < Glyphs.Length; i++)
        {
            int code = 32 + i;
            tiles[code] = code; // glyph tile = character code
            int baseX = (code % 32) * 8, baseY = (code / 32) * 8;
            for (int row = 0; row < 8; row++)
            {
                int bits = (int)((Glyphs[i] >> (row * 8)) & 0xFF);
                int y = baseY + 7 - row; // data rows run top-down; tilemap rows bottom-up
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((bits & (1 << bit)) != 0)
                    {
                        tilemap[(y * 256) + baseX + bit] = 1;
                    }
                }
            }
        }

        return new RuntimeFont(new FontDefinition("<system>", 1, 1, 9, tiles, advances), tilemap);
    }
}
