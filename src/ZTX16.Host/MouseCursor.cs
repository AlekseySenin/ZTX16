namespace ZTX16.Host;

/// <summary>
/// The system mouse arrow: pixel art in true black and white, drawn by the host over the composed ARGB frame
/// so it is the same in every app and scales with the display. Cartridges can hide it
/// (<c>Input.SetCursorVisible(false)</c>) to draw their own.
/// </summary>
internal static class MouseCursor
{
    private const uint Black = 0xFF000000, White = 0xFFFFFFFF;

    // Hot spot at the top-left pixel. '#' outline, '.' fill, ' ' transparent.
    private static readonly string[] Art =
    {
        "#",
        "##",
        "#.#",
        "#..#",
        "#...#",
        "#....#",
        "#.....#",
        "#......#",
        "#.......#",
        "#....####",
        "#..#..#",
        "#.# #..#",
        "##   #..#",
        "      ##",
    };

    /// <summary>Draws the arrow with its hot spot at (x, y) (top-left origin), clipped to the frame.</summary>
    public static void Draw(Span<uint> argb, int width, int height, int x, int y)
    {
        for (int row = 0; row < Art.Length; row++)
        {
            int py = y + row;
            if (py < 0 || py >= height)
            {
                continue;
            }

            string line = Art[row];
            for (int col = 0; col < line.Length; col++)
            {
                int px = x + col;
                if (px < 0 || px >= width || line[col] == ' ')
                {
                    continue;
                }

                argb[(py * width) + px] = line[col] == '#' ? Black : White;
            }
        }
    }
}
