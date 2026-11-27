using ZTX16;
using ZTX16.Collections;

namespace ZTX16Graphics;

/// <summary>
/// The tilemap being edited: 256×256 ColorIndex pixels (row 0 at the bottom, like ZTX16 coordinates), with
/// undo/redo snapshots and the pixel operations of the tools.
/// </summary>
public sealed class Image
{
    public const int Size = 256;
    private const int MaxUndo = 40;

    private readonly List<byte[]> _undo = new();
    private readonly List<byte[]> _redo = new();

    public Image()
    {
        Pixels = new byte[Size * Size];
    }

    /// <summary>The pixels: index (y * 256) + x, y = 0 at the bottom.</summary>
    public byte[] Pixels { get; private set; }

    /// <summary>True if changed since loading or saving.</summary>
    public bool Modified { get; set; }

    /// <summary>Replaces all pixels (history cleared).</summary>
    public void Load(byte[] pixels)
    {
        Pixels = pixels;
        Modified = false;
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Remembers the current pixels before a change (one undo step).</summary>
    public void Snapshot()
    {
        _undo.Add(Copy(Pixels));
        if (_undo.Count > MaxUndo)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        Modified = true;
    }

    public bool Undo() => Swap(_undo, _redo);

    public bool Redo() => Swap(_redo, _undo);

    private bool Swap(List<byte[]> from, List<byte[]> to)
    {
        if (from.Count == 0)
        {
            return false;
        }

        to.Add(Pixels);
        Pixels = from[from.Count - 1];
        from.RemoveAt(from.Count - 1);
        Modified = true;
        return true;
    }

    public int Get(int x, int y) => x < 0 || y < 0 || x >= Size || y >= Size ? 0 : Pixels[(y * Size) + x];

    public void Set(int x, int y, int color)
    {
        if (x >= 0 && y >= 0 && x < Size && y < Size)
        {
            Pixels[(y * Size) + x] = (byte)color;
        }
    }

    /// <summary>A square or round brush of <paramref name="size"/> pixels at (x, y), clipped to a rectangle.</summary>
    public void Brush(int x, int y, int size, bool round, int color, int clipX, int clipY, int clipWidth, int clipHeight)
    {
        int half = (size - 1) / 2;
        for (int dy = 0; dy < size; dy++)
        {
            for (int dx = 0; dx < size; dx++)
            {
                int px = x - half + dx, py = y - half + dy;
                if ((!round || InCircle(dx, dy, size)) && px >= clipX && py >= clipY && px < clipX + clipWidth && py < clipY + clipHeight)
                {
                    Set(px, py, color);
                }
            }
        }
    }

    /// <summary>Brushes along the line from (x0, y0) to (x1, y1) (Bresenham), so fast strokes leave no gaps.</summary>
    public void Line(int x0, int y0, int x1, int y1, int size, bool round, int color, int clipX, int clipY, int clipWidth, int clipHeight)
    {
        int dx = Abs(x1 - x0), dy = -Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        while (true)
        {
            Brush(x0, y0, size, round, color, clipX, clipY, clipWidth, clipHeight);
            if (x0 == x1 && y0 == y1)
            {
                return;
            }

            int e2 = 2 * error;
            if (e2 >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>
    /// Flood fill from (x, y) inside the rectangle: 4-connected pixels of the same colour (or, with
    /// <paramref name="global"/>, every pixel of that colour in the rectangle).
    /// </summary>
    public void Fill(int x, int y, int color, bool global, int rx, int ry, int rw, int rh)
    {
        if (x < rx || y < ry || x >= rx + rw || y >= ry + rh)
        {
            return;
        }

        int target = Get(x, y);
        if (target == color)
        {
            return;
        }

        if (global)
        {
            for (int py = ry; py < ry + rh; py++)
            {
                for (int px = rx; px < rx + rw; px++)
                {
                    if (Get(px, py) == target)
                    {
                        Set(px, py, color);
                    }
                }
            }

            return;
        }

        int[] stack = new int[(rw * rh) + 4];
        int top = 0;
        stack[top++] = (y * Size) + x;
        Set(x, y, color);
        while (top > 0)
        {
            int p = stack[--top];
            int px = p % Size, py = p / Size;
            if (px > rx && Pixels[p - 1] == target)
            {
                Pixels[p - 1] = (byte)color;
                stack[top++] = p - 1;
            }

            if (px < rx + rw - 1 && Pixels[p + 1] == target)
            {
                Pixels[p + 1] = (byte)color;
                stack[top++] = p + 1;
            }

            if (py > ry && Pixels[p - Size] == target)
            {
                Pixels[p - Size] = (byte)color;
                stack[top++] = p - Size;
            }

            if (py < ry + rh - 1 && Pixels[p + Size] == target)
            {
                Pixels[p + Size] = (byte)color;
                stack[top++] = p + Size;
            }
        }
    }

    /// <summary>The pixels of a rectangle (row 0 at the bottom).</summary>
    public byte[] Read(int x, int y, int width, int height)
    {
        byte[] result = new byte[width * height];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                result[(row * width) + col] = (byte)Get(x + col, y + row);
            }
        }

        return result;
    }

    /// <summary>Writes pixels at (x, y), clipped to the image (and, if transparent, skipping index 0).</summary>
    public void Write(byte[] data, int width, int height, int x, int y, bool transparent)
    {
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int value = data[(row * width) + col];
                if (!(transparent && value == 0))
                {
                    Set(x + col, y + row, value);
                }
            }
        }
    }

    /// <summary>Sets a rectangle to one colour.</summary>
    public void Clear(int x, int y, int width, int height, int color)
    {
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                Set(x + col, y + row, color);
            }
        }
    }

    /// <summary>Mirrors a pixel block left–right.</summary>
    public static byte[] FlipX(byte[] data, int width, int height)
    {
        byte[] result = new byte[data.Length];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                result[(row * width) + col] = data[(row * width) + (width - 1 - col)];
            }
        }

        return result;
    }

    /// <summary>Mirrors a pixel block top–bottom.</summary>
    public static byte[] FlipY(byte[] data, int width, int height)
    {
        byte[] result = new byte[data.Length];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                result[(row * width) + col] = data[((height - 1 - row) * width) + col];
            }
        }

        return result;
    }

    /// <summary>Rotates a pixel block 90° counter-clockwise; the result is height × width.</summary>
    public static byte[] Rotate(byte[] data, int width, int height)
    {
        byte[] result = new byte[data.Length];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                // (col, row) → (height - 1 - row, col) in a block 'height' wide.
                result[(col * height) + (height - 1 - row)] = data[(row * width) + col];
            }
        }

        return result;
    }

    public static byte[] Copy(byte[] data)
    {
        byte[] result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = data[i];
        }

        return result;
    }

    /// <summary>True if cell (dx, dy) of a size×size brush lies inside its circle (sizes 1–2 are full squares).</summary>
    public static bool InCircle(int dx, int dy, int size)
    {
        if (size <= 2)
        {
            return true;
        }

        // Doubled coordinates around the centre, compared with the doubled radius.
        int cx = (2 * dx) - (size - 1), cy = (2 * dy) - (size - 1);
        return (cx * cx) + (cy * cy) <= (size * size) + 1;
    }

    private static int Abs(int value) => value < 0 ? -value : value;
}
