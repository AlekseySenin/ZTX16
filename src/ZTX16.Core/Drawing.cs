using ZTX16.Core;

namespace ZTX16;

/// <summary>
/// A per-draw transform: Flip → Scale → Rotation, around a normalized pivot (0,0 = lower-left,
/// 1,1 = upper-right). The draw position is where the pivot lands.
/// </summary>
public struct Transform
{
    /// <summary>Mirror horizontally.</summary>
    public bool FlipX;

    /// <summary>Mirror vertically.</summary>
    public bool FlipY;

    /// <summary>Horizontal scale (fractional allowed).</summary>
    public float ScaleX;

    /// <summary>Vertical scale (fractional allowed).</summary>
    public float ScaleY;

    /// <summary>Rotation in degrees, counter-clockwise.</summary>
    public float Rotation;

    /// <summary>Normalized pivot X (0 = left, 1 = right).</summary>
    public float PivotX;

    /// <summary>Normalized pivot Y (0 = bottom, 1 = top).</summary>
    public float PivotY;

    /// <summary>No flip, scale 1, no rotation, lower-left pivot.</summary>
    public static Transform Identity
    {
        get
        {
            Transform t = default;
            t.ScaleX = 1f;
            t.ScaleY = 1f;
            return t;
        }
    }

    /// <summary>Flag bits passed to the graphics syscalls (1 = FlipX, 2 = FlipY).</summary>
    public int Flags => (FlipX ? 1 : 0) | (FlipY ? 2 : 0);

    /// <summary>A copy with the given flips.</summary>
    public Transform Flipped(bool x, bool y)
    {
        Transform t = this;
        t.FlipX = x;
        t.FlipY = y;
        return t;
    }

    /// <summary>A copy with the given scale.</summary>
    public Transform Scaled(float x, float y)
    {
        Transform t = this;
        t.ScaleX = x;
        t.ScaleY = y;
        return t;
    }

    /// <summary>A copy with the given rotation in degrees.</summary>
    public Transform Rotated(float degrees)
    {
        Transform t = this;
        t.Rotation = degrees;
        return t;
    }

    /// <summary>A copy with the given normalized pivot.</summary>
    public Transform WithPivot(float x, float y)
    {
        Transform t = this;
        t.PivotX = x;
        t.PivotY = y;
        return t;
    }

    /// <summary>A copy pivoting around the center.</summary>
    public Transform Centered() => WithPivot(0.5f, 0.5f);
}

/// <summary>Tile drawing with transforms.</summary>
public static class Tiles
{
    /// <summary>Draws a rectangle of tiles from a tilemap with a transform and a per-tile pixel offset.</summary>
    public static void Draw(int layer, string tilemap, int tileX, int tileY, int widthInTiles, int heightInTiles, int x, int y,
        Transform transform, int offsetX = 0, int offsetY = 0)
    {
        Graphics.DrawTilesTransformed(layer, tilemap, tileX, tileY, widthInTiles, heightInTiles, x, y, transform.Flags,
            transform.ScaleX, transform.ScaleY, transform.Rotation, transform.PivotX, transform.PivotY, offsetX, offsetY);
    }
}

/// <summary>
/// A Sprite Instance: a grid of tile IDs from one tilemap that can be changed at runtime (for example by
/// animation) and drawn as one image. Loading copies the Sprite resource; the resource itself never changes.
/// </summary>
public sealed class Sprite
{
    private readonly int[] _tiles;

    /// <summary>Creates a blank sprite (all tiles 0).</summary>
    public Sprite(string tilemap, int width, int height)
    {
        if (width < 1 || height < 1 || width > 32 || height > 32)
        {
            Intrinsics.Fail("Sprite: width and height must be 1..32 tiles");
        }

        Tilemap = tilemap;
        Width = width;
        _tiles = new int[width * height];
    }

    private Sprite(string tilemap, int width, int[] tiles)
    {
        Tilemap = tilemap;
        Width = width;
        _tiles = tiles;
    }

    /// <summary>The tilemap the tiles come from.</summary>
    public string Tilemap { get; }

    /// <summary>Width in tiles.</summary>
    public int Width { get; }

    /// <summary>Height in tiles.</summary>
    public int Height => _tiles.Length / Width;

    /// <summary>Loads a Sprite Instance from the Sprite resource <paramref name="name"/>.</summary>
    public static Sprite Load(string name)
    {
        int count = Sprites.TileCount(name);
        int[] tiles = new int[count];
        for (int i = 0; i < count; i++)
        {
            tiles[i] = Sprites.Tile(name, i);
        }

        return new Sprite(Sprites.Tilemap(name), Sprites.Width(name), tiles);
    }

    /// <summary>Tile ID at grid position (x, y); (0, 0) is the lower-left tile.</summary>
    public int GetTile(int x, int y) => _tiles[Index(x, y)];

    /// <summary>Changes the tile ID at grid position (x, y).</summary>
    public void SetTile(int x, int y, int tileId)
    {
        if (tileId < 0 || tileId > 1023)
        {
            Intrinsics.Fail("Sprite: tile ID must be 0..1023");
        }

        _tiles[Index(x, y)] = tileId;
    }

    /// <summary>Draws the sprite with its lower-left corner at (x, y).</summary>
    public void Draw(int layer, int x, int y) => Draw(layer, x, y, Transform.Identity);

    /// <summary>Draws the sprite with a transform and a per-tile pixel offset.</summary>
    public void Draw(int layer, int x, int y, Transform transform, int offsetX = 0, int offsetY = 0)
    {
        Graphics.DrawTileGrid(layer, Tilemap, _tiles, Width, x, y, transform.Flags, transform.ScaleX, transform.ScaleY,
            transform.Rotation, transform.PivotX, transform.PivotY, offsetX, offsetY);
    }

    private int Index(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            Intrinsics.Fail("Sprite: tile position out of range");
        }

        return (y * Width) + x;
    }
}

/// <summary>Helpers for the text stream's embedded commands.</summary>
public static class TextStyle
{
    /// <summary>The Set Color command: following text uses ColorIndex <paramref name="index"/> (1..31).</summary>
    public static string Color(int index)
    {
        if (index < 0 || index > 31)
        {
            Intrinsics.Fail("TextStyle.Color: ColorIndex must be 0..31");
        }

        char code = (char)index;
        return "\u0001" + code.ToString();
    }
}
