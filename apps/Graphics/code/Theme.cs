using ZTX16;
using ZTX16.Collections;

namespace ZTX16Graphics;

/// <summary>
/// The palette being edited (32 colours, 3 bits per channel) and the editor's own interface colours, which are
/// chosen from it — the interface shares the one ZTX16 palette, so it adapts to whatever palette is loaded.
/// </summary>
public sealed class Theme
{
    public const int Colors = 32;

    public readonly int[] R = new int[Colors];
    public readonly int[] G = new int[Colors];
    public readonly int[] B = new int[Colors];

    /// <summary>Interface colours (palette indices).</summary>
    public int Dark, Panel, Mid, Light, Accent, Check;

    /// <summary>True if the palette changed since it was loaded or saved.</summary>
    public bool Modified { get; set; }

    /// <summary>Path of the palette file ("" if none).</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Reads ".pal" text: lines of three numbers 0..7 (R G B), '#' starts a comment. Returns false if it does
    /// not hold 32 entries.
    /// </summary>
    public bool Parse(string text)
    {
        int[] r = new int[Colors], g = new int[Colors], b = new int[Colors];
        int count = 0;
        List<string> lines = Strings.Split(text, '\n');
        for (int i = 0; i < lines.Count && count < Colors; i++)
        {
            string line = lines[i];
            int hash = Strings.IndexOf(line, '#');
            if (hash >= 0)
            {
                line = Strings.Sub(line, 0, hash);
            }

            List<string> words = Strings.Words(Strings.Trim(Strings.Replace(line, "\r", "")));
            int rv, gv, bv;
            if (words.Count >= 3 && Strings.TryParseInt(words[0], out rv) && Strings.TryParseInt(words[1], out gv) && Strings.TryParseInt(words[2], out bv))
            {
                r[count] = Clamp7(rv);
                g[count] = Clamp7(gv);
                b[count] = Clamp7(bv);
                count++;
            }
        }

        if (count < Colors)
        {
            return false;
        }

        for (int i = 0; i < Colors; i++)
        {
            R[i] = r[i];
            G[i] = g[i];
            B[i] = b[i];
        }

        Modified = false;
        Apply();
        return true;
    }

    /// <summary>The palette as ".pal" text.</summary>
    public string Format()
    {
        var lines = new List<string>();
        lines.Add("# ZTX16 palette: 32 entries of R G B (0..7). Entry 0 is the backdrop.");
        for (int i = 0; i < Colors; i++)
        {
            int r = R[i], g = G[i], b = B[i];
            lines.Add(r.ToString() + " " + g.ToString() + " " + b.ToString() + "    # " + i.ToString());
        }

        return Strings.Join(lines, "\n") + "\n";
    }

    /// <summary>The palette as 32 × RGB bytes (0..255), for PNG files.</summary>
    public byte[] Rgb8()
    {
        byte[] result = new byte[Colors * 3];
        for (int i = 0; i < Colors; i++)
        {
            result[i * 3] = (byte)(R[i] * 255 / 7);
            result[(i * 3) + 1] = (byte)(G[i] * 255 / 7);
            result[(i * 3) + 2] = (byte)(B[i] * 255 / 7);
        }

        return result;
    }

    /// <summary>Changes one colour (live).</summary>
    public void SetColor(int index, int r, int g, int b)
    {
        R[index] = Clamp7(r);
        G[index] = Clamp7(g);
        B[index] = Clamp7(b);
        Modified = true;
        Apply();
    }

    /// <summary>Puts the palette on the screen and picks the interface colours from it.</summary>
    public void Apply()
    {
        for (int i = 0; i < Colors; i++)
        {
            Graphics.SetPaletteColor(i, R[i], G[i], B[i]);
        }

        // Neutral (grey) colours are preferred for the interface; any colour serves if a palette has no greys.
        int min = 1000, max = -1;
        for (int i = 1; i < Colors; i++)
        {
            int l = Luma(i) + (Saturation(i) * 4);
            if (l < min)
            {
                min = l;
                Dark = i;
            }

            int bright = Luma(i) - (Saturation(i) * 4);
            if (bright > max)
            {
                max = bright;
                Light = i;
            }
        }

        min = Luma(Dark);
        max = Luma(Light);

        Panel = Closest(min + ((max - min) / 6), Dark);
        Check = Closest(min + ((max - min) / 4), Panel);
        Mid = Closest(min + ((max - min) / 2), Panel);

        int bestSaturation = -1;
        Accent = Light;
        for (int i = 1; i < Colors; i++)
        {
            int saturation = Max3(R[i], G[i], B[i]) - Min3(R[i], G[i], B[i]);
            if (Luma(i) > (min + max) / 2 && saturation > bestSaturation)
            {
                bestSaturation = saturation;
                Accent = i;
            }
        }
    }

    // Perceived brightness 0..70.
    public int Luma(int i) => (3 * R[i]) + (6 * G[i]) + B[i];

    // The colour whose brightness is nearest to 'target', other than 'avoid' when possible.
    private int Closest(int target, int avoid)
    {
        int best = avoid, distance = 1000;
        for (int i = 1; i < Colors; i++)
        {
            int d = Luma(i) - target;
            if (d < 0)
            {
                d = -d;
            }

            d += Saturation(i) * 6;        // prefer greys
            if (i != avoid && d < distance)
            {
                distance = d;
                best = i;
            }
        }

        return best;
    }

    private int Saturation(int i) => Max3(R[i], G[i], B[i]) - Min3(R[i], G[i], B[i]);

    private static int Clamp7(int v) => v < 0 ? 0 : v > 7 ? 7 : v;

    private static int Max3(int a, int b, int c) => a > b ? (a > c ? a : c) : (b > c ? b : c);

    private static int Min3(int a, int b, int c) => a < b ? (a < c ? a : c) : (b < c ? b : c);
}
