using ZTX16.Collections;

namespace ZTX16;

/// <summary>
/// A rectangle collider: position (lower-left corner), size and an enabled flag. It carries no groups,
/// masks, physics or meaning; game code organizes colliders into <see cref="ColliderSet"/>s.
/// </summary>
/// <remarks>
/// A collider covers the half-open pixel area [X, X + Width) × [Y, Y + Height): rectangles that only touch do
/// not overlap. Disabled colliders keep their state but take part in no query.
/// </remarks>
public sealed class Collider
{
    /// <summary>Creates an enabled collider.</summary>
    public Collider(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>Left edge.</summary>
    public int X { get; set; }

    /// <summary>Bottom edge.</summary>
    public int Y { get; set; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Whether the collider takes part in queries.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>X + Width (exclusive right edge).</summary>
    public int Right => X + Width;

    /// <summary>Y + Height (exclusive top edge).</summary>
    public int Top => Y + Height;

    /// <summary>Moves the collider.</summary>
    public void SetPosition(int x, int y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>A game-defined group of colliders ("solids", "enemies", …). A collider may be in several sets.</summary>
public sealed class ColliderSet
{
    private readonly List<Collider> _colliders = new();

    /// <summary>Number of colliders.</summary>
    public int Count => _colliders.Count;

    /// <summary>The collider at <paramref name="index"/>.</summary>
    public Collider this[int index] => _colliders[index];

    /// <summary>Adds a collider (once).</summary>
    public void Add(Collider collider)
    {
        if (!_colliders.Contains(collider))
        {
            _colliders.Add(collider);
        }
    }

    /// <summary>Removes a collider. Returns false if it was not in the set.</summary>
    public bool Remove(Collider collider) => _colliders.Remove(collider);

    /// <summary>True if the set contains <paramref name="collider"/>.</summary>
    public bool Contains(Collider collider) => _colliders.Contains(collider);

    /// <summary>Removes all colliders.</summary>
    public void Clear() => _colliders.Clear();
}

/// <summary>The result of moving a rectangle through a collider set.</summary>
public struct CollisionHit
{
    /// <summary>True if the motion hits a collider.</summary>
    public bool Hit;

    /// <summary>The first collider hit, or null.</summary>
    public Collider? Collider;

    /// <summary>X of the contact point (on the hit collider's face).</summary>
    public int PointX;

    /// <summary>Y of the contact point (on the hit collider's face).</summary>
    public int PointY;

    /// <summary>Surface normal X of the face that was hit (-1, 0 or 1).</summary>
    public int NormalX;

    /// <summary>Surface normal Y of the face that was hit (-1, 0 or 1).</summary>
    public int NormalY;

    /// <summary>X of the last position before the collision (touching, not overlapping). The target X if nothing was hit.</summary>
    public int SafeX;

    /// <summary>Y of the last position before the collision. The target Y if nothing was hit.</summary>
    public int SafeY;
}

/// <summary>
/// Geometry queries. Collision applies no forces, gravity, sliding or any other physics: game code decides
/// what to do with the results.
/// </summary>
public static class Collision
{
    /// <summary>True if two enabled colliders overlap.</summary>
    public static bool Overlaps(Collider a, Collider b) =>
        a.Enabled && b.Enabled && Overlaps(a.X, a.Y, a.Width, a.Height, b.X, b.Y, b.Width, b.Height);

    /// <summary>True if a rectangle overlaps an enabled collider.</summary>
    public static bool Overlaps(int x, int y, int width, int height, Collider collider) =>
        collider.Enabled && Overlaps(x, y, width, height, collider.X, collider.Y, collider.Width, collider.Height);

    /// <summary>True if an enabled collider contains the point.</summary>
    public static bool Contains(Collider collider, int x, int y) =>
        collider.Enabled && x >= collider.X && x < collider.Right && y >= collider.Y && y < collider.Top;

    /// <summary>
    /// Adds every enabled collider of <paramref name="set"/> that overlaps the rectangle to
    /// <paramref name="results"/> (if given) and returns how many there are.
    /// </summary>
    public static int Overlapping(int x, int y, int width, int height, ColliderSet set, List<Collider>? results = null)
    {
        int count = 0;
        for (int i = 0; i < set.Count; i++)
        {
            Collider c = set[i];
            if (Overlaps(x, y, width, height, c))
            {
                count++;
                results?.Add(c);
            }
        }

        return count;
    }

    /// <summary>Like <see cref="Overlapping(int, int, int, int, ColliderSet, List{Collider}?)"/> for a collider, which never matches itself.</summary>
    public static int Overlapping(Collider collider, ColliderSet set, List<Collider>? results = null)
    {
        int count = 0;
        for (int i = 0; i < set.Count; i++)
        {
            Collider c = set[i];
            if (c != collider && Overlaps(collider, c))
            {
                count++;
                results?.Add(c);
            }
        }

        return count;
    }

    /// <summary>
    /// Considers the motion of <paramref name="moving"/> from its position to (<paramref name="targetX"/>,
    /// <paramref name="targetY"/>) and returns the earliest collision with an enabled collider of
    /// <paramref name="set"/> (never itself). The collider is not moved.
    /// </summary>
    /// <remarks>
    /// If the rectangle already overlaps a collider at its start, that collider is reported at once with a zero
    /// normal and the start as the safe position.
    /// </remarks>
    public static CollisionHit Move(Collider moving, int targetX, int targetY, ColliderSet set)
    {
        CollisionHit result = default;
        result.SafeX = targetX;
        result.SafeY = targetY;

        int x = moving.X, y = moving.Y, w = moving.Width, h = moving.Height;
        double dx = targetX - x, dy = targetY - y;
        double best = 2.0;
        bool bestOnX = false;
        Collider? hit = null;

        for (int i = 0; i < set.Count; i++)
        {
            Collider c = set[i];
            if (c == moving || !c.Enabled)
            {
                continue;
            }

            if (Overlaps(x, y, w, h, c.X, c.Y, c.Width, c.Height))
            {
                result.Hit = true;
                result.Collider = c;
                result.SafeX = x;
                result.SafeY = y;
                result.PointX = Middle(x, x + w, c.X, c.Right);
                result.PointY = Middle(y, y + h, c.Y, c.Top);
                return result;
            }

            // Times (fractions of the motion) at which the rectangle enters and leaves the collider on each axis.
            double xEntry, xExit, yEntry, yExit;
            if (dx > 0)
            {
                xEntry = (c.X - (x + w)) / dx;
                xExit = (c.Right - x) / dx;
            }
            else if (dx < 0)
            {
                xEntry = (c.Right - x) / dx;
                xExit = (c.X - (x + w)) / dx;
            }
            else if (x + w <= c.X || x >= c.Right)
            {
                continue;
            }
            else
            {
                xEntry = double.NegativeInfinity;
                xExit = double.PositiveInfinity;
            }

            if (dy > 0)
            {
                yEntry = (c.Y - (y + h)) / dy;
                yExit = (c.Top - y) / dy;
            }
            else if (dy < 0)
            {
                yEntry = (c.Top - y) / dy;
                yExit = (c.Y - (y + h)) / dy;
            }
            else if (y + h <= c.Y || y >= c.Top)
            {
                continue;
            }
            else
            {
                yEntry = double.NegativeInfinity;
                yExit = double.PositiveInfinity;
            }

            double entry = xEntry > yEntry ? xEntry : yEntry;
            double exit = xExit < yExit ? xExit : yExit;
            if (entry < exit && entry >= 0 && entry < 1 && entry < best)
            {
                best = entry;
                bestOnX = xEntry >= yEntry;
                hit = c;
            }
        }

        if (hit is null)
        {
            return result;
        }

        result.Hit = true;
        result.Collider = hit;
        if (bestOnX)
        {
            result.NormalX = dx > 0 ? -1 : 1;
            result.SafeX = dx > 0 ? hit.X - w : hit.Right;
            result.SafeY = y + (int)(dy * best);
            result.PointX = dx > 0 ? hit.X : hit.Right;
            result.PointY = Middle(result.SafeY, result.SafeY + h, hit.Y, hit.Top);
        }
        else
        {
            result.NormalY = dy > 0 ? -1 : 1;
            result.SafeY = dy > 0 ? hit.Y - h : hit.Top;
            result.SafeX = x + (int)(dx * best);
            result.PointY = dy > 0 ? hit.Y : hit.Top;
            result.PointX = Middle(result.SafeX, result.SafeX + w, hit.X, hit.Right);
        }

        return result;
    }

    private static bool Overlaps(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh) =>
        ax < bx + bw && bx < ax + aw && ay < by + bh && by < ay + ah;

    // Middle of the overlap of [a0, a1) and [b0, b1), or of the closest edge if they do not overlap.
    private static int Middle(int a0, int a1, int b0, int b1)
    {
        int lo = a0 > b0 ? a0 : b0;
        int hi = a1 < b1 ? a1 : b1;
        return lo <= hi ? (lo + hi) / 2 : lo;
    }
}

/// <summary>Debug visualization of colliders. It draws only; it has no effect on collision.</summary>
public static class CollisionDebug
{
    /// <summary>Draws the outline of an enabled collider.</summary>
    public static void Draw(int layer, Collider collider, int color)
    {
        if (!collider.Enabled || collider.Width <= 0 || collider.Height <= 0)
        {
            return;
        }

        Graphics.FillRect(layer, collider.X, collider.Y, collider.Width, 1, color);
        Graphics.FillRect(layer, collider.X, collider.Top - 1, collider.Width, 1, color);
        Graphics.FillRect(layer, collider.X, collider.Y, 1, collider.Height, color);
        Graphics.FillRect(layer, collider.Right - 1, collider.Y, 1, collider.Height, color);
    }

    /// <summary>Draws the outlines of every enabled collider in a set.</summary>
    public static void Draw(int layer, ColliderSet set, int color)
    {
        for (int i = 0; i < set.Count; i++)
        {
            Draw(layer, set[i], color);
        }
    }
}
