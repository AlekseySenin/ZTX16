using ZTX16.Collections;
using ZTX16.Core;

namespace ZTX16;

/// <summary>The Visual Representation an <see cref="Actor"/> currently has.</summary>
public enum ActorVisual
{
    /// <summary>Nothing is drawn.</summary>
    None = 0,

    /// <summary>One tile of the layer's tilemap.</summary>
    Tile = 1,

    /// <summary>A rectangle of tiles of the layer's tilemap.</summary>
    TileRegion = 2,

    /// <summary>A Sprite Instance (with its own tilemap).</summary>
    Sprite = 3,
}

/// <summary>
/// Runtime state of a loaded Level resource: logical layers of actors. Loading copies the resource; changes
/// to actors never modify it, and loading again gives a fresh copy.
/// </summary>
/// <remarks>
/// Level is not a game engine: actors have no types, behaviour, hierarchy or physics. Game code finds actors
/// by ID or layer, changes them and decides when to <see cref="LevelLayer.Rasterize"/> layers into physical
/// Graphics Layers.
/// </remarks>
public sealed class Level
{
    private const int StreamVersion = 1;

    private readonly List<LevelLayer> _layers = new();
    private readonly Dictionary<string, Actor> _actors = new();

    private Level(string name)
    {
        Name = name;
    }

    /// <summary>The resource name.</summary>
    public string Name { get; }

    /// <summary>Number of logical layers.</summary>
    public int LayerCount => _layers.Count;

    /// <summary>Colliders of every actor in the level.</summary>
    public ColliderSet Colliders { get; } = new();

    /// <summary>Loads the Level resource <paramref name="name"/>, creating its runtime state.</summary>
    public static Level Load(string name)
    {
        var level = new Level(name);
        var reader = new LevelReader(name);
        if (reader.Next() != StreamVersion)
        {
            Intrinsics.Fail("Level: unsupported level data version");
        }

        int layers = reader.Next();
        for (int l = 0; l < layers; l++)
        {
            string layerName = reader.NextString();
            int tilemapIndex = reader.Next();
            string? tilemap = tilemapIndex < 0 ? null : reader.String(tilemapIndex);
            int width = reader.Next() * Graphics.ScreenSize;
            int height = reader.Next() * Graphics.ScreenSize;
            var layer = new LevelLayer(level, l, layerName, tilemap, width, height);
            level._layers.Add(layer);

            int actors = reader.Next();
            for (int a = 0; a < actors; a++)
            {
                Actor actor = ReadActor(reader, layer);
                layer.AddLoaded(actor);
                level._actors[actor.Id] = actor;
            }
        }

        return level;
    }

    /// <summary>The layer at <paramref name="index"/> (authoring order).</summary>
    public LevelLayer GetLayer(int index) => _layers[index];

    /// <summary>The layer named <paramref name="name"/>, or null.</summary>
    public LevelLayer? FindLayer(string name)
    {
        for (int i = 0; i < _layers.Count; i++)
        {
            if (_layers[i].Name == name)
            {
                return _layers[i];
            }
        }

        return null;
    }

    /// <summary>The actor with ID <paramref name="id"/>, or null.</summary>
    public Actor? FindActor(string id) => _actors.TryGetValue(id, out Actor actor) ? actor : null;

    private static Actor ReadActor(LevelReader reader, LevelLayer layer)
    {
        string id = reader.NextString();
        int x = reader.Next();
        int y = reader.Next();
        int flags = reader.Next();
        Transform transform = Transform.Identity;
        transform.FlipX = (flags & 1) != 0;
        transform.FlipY = (flags & 2) != 0;
        transform.ScaleX = reader.NextFloat();
        transform.ScaleY = reader.NextFloat();
        transform.Rotation = reader.NextFloat();
        transform.PivotX = reader.NextFloat();
        transform.PivotY = reader.NextFloat();

        var actor = new Actor(id, layer, x, y, transform);
        int visual = reader.Next();
        int v0 = reader.Next(), v1 = reader.Next(), v2 = reader.Next(), v3 = reader.Next();
        if (visual == (int)ActorVisual.Tile)
        {
            actor.SetTile(v0);
        }
        else if (visual == (int)ActorVisual.TileRegion)
        {
            actor.SetTileRegion(v0, v1, v2, v3);
        }
        else if (visual == (int)ActorVisual.Sprite)
        {
            actor.SetSprite(Sprite.Load(reader.String(v0)));
        }

        bool hasCollider = reader.Next() != 0;
        int cx = reader.Next(), cy = reader.Next(), cw = reader.Next(), ch = reader.Next();
        if (hasCollider)
        {
            actor.Collider = new Collider(x + cx, y + cy, cw, ch);
        }

        return actor;
    }

    private sealed class LevelReader
    {
        private readonly string _name;
        private readonly int _count;
        private int _position;

        public LevelReader(string name)
        {
            _name = name;
            _count = Levels.ValueCount(name);
        }

        public int Next()
        {
            if (_position >= _count)
            {
                Intrinsics.Fail("Level: level data is truncated");
            }

            return Levels.Value(_name, _position++);
        }

        public float NextFloat()
        {
            if (_position >= _count)
            {
                Intrinsics.Fail("Level: level data is truncated");
            }

            return Levels.FloatValue(_name, _position++);
        }

        public string NextString() => String(Next());

        public string String(int index) => Levels.String(_name, index);
    }
}

/// <summary>
/// A logical Level Visual Layer: its own tilemap, size (a whole number of screens) and actors in drawing
/// order. Logical layers are independent from the 8 physical Graphics Layers; any number of them can be
/// rasterized into any Graphics Layer.
/// </summary>
public sealed class LevelLayer
{
    private readonly List<Actor> _actors = new();

    internal LevelLayer(Level level, int index, string name, string? tilemap, int width, int height)
    {
        Level = level;
        Index = index;
        Name = name;
        Tilemap = tilemap;
        Width = width;
        Height = height;
    }

    /// <summary>The level the layer belongs to.</summary>
    public Level Level { get; }

    /// <summary>Position in the level's layer list.</summary>
    public int Index { get; }

    /// <summary>Layer name.</summary>
    public string Name { get; }

    /// <summary>Tilemap used by Tile and Tile Region actors, or null.</summary>
    public string? Tilemap { get; }

    /// <summary>Width in pixels (a multiple of 256).</summary>
    public int Width { get; }

    /// <summary>Height in pixels (a multiple of 256).</summary>
    public int Height { get; }

    /// <summary>Number of actors.</summary>
    public int ActorCount => _actors.Count;

    /// <summary>Colliders of the actors in this layer.</summary>
    public ColliderSet Colliders { get; } = new();

    /// <summary>The actor at <paramref name="index"/> (drawing order).</summary>
    public Actor GetActor(int index) => _actors[index];

    /// <summary>
    /// Draws every visible actor, in order, into Graphics Layer <paramref name="graphicsLayer"/> with the layer's
    /// origin at (<paramref name="offsetX"/>, <paramref name="offsetY"/>). The target is not cleared, so several
    /// logical layers can be composed into one Graphics Layer. Drawing is clipped to the target; a logical layer
    /// larger than the target produces a warning.
    /// </summary>
    public void Rasterize(int graphicsLayer, int offsetX = 0, int offsetY = 0)
    {
        int targetWidth = Graphics.LayerWidth(graphicsLayer);
        int targetHeight = Graphics.LayerHeight(graphicsLayer);
        if (Width > targetWidth || Height > targetHeight)
        {
            Machine.Warn("Level '" + Level.Name + "': layer '" + Name + "' (" + Width.ToString() + "x" + Height.ToString()
                + ") is larger than Graphics Layer " + graphicsLayer.ToString() + " (" + targetWidth.ToString() + "x"
                + targetHeight.ToString() + ") and is clipped");
        }

        for (int i = 0; i < _actors.Count; i++)
        {
            _actors[i].Draw(graphicsLayer, offsetX, offsetY);
        }
    }

    internal void AddLoaded(Actor actor) => _actors.Add(actor);
}

/// <summary>
/// An Actor Runtime Reference: the current transform, Visual Representation and collider of one actor. It
/// has a game-defined ID and no predefined meaning.
/// </summary>
/// <remarks>
/// Actor and collider are independent: an actor may have a collider without a visual or a visual without a
/// collider. <see cref="SetPosition"/> moves the collider with the actor; the collider itself can also be
/// changed, replaced or removed.
/// </remarks>
public sealed class Actor
{
    private Collider? _collider;

    internal Actor(string id, LevelLayer layer, int x, int y, Transform transform)
    {
        Id = id;
        Layer = layer;
        X = x;
        Y = y;
        Transform = transform;
    }

    /// <summary>Game-defined ID, unique in the level.</summary>
    public string Id { get; }

    /// <summary>The logical layer the actor belongs to.</summary>
    public LevelLayer Layer { get; }

    /// <summary>Position X (the object origin, where the transform's pivot lands).</summary>
    public int X { get; private set; }

    /// <summary>Position Y.</summary>
    public int Y { get; private set; }

    /// <summary>Flip, scale, rotation and pivot used when drawing.</summary>
    public Transform Transform { get; set; }

    /// <summary>Whether <see cref="LevelLayer.Rasterize"/> draws the actor.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>The current kind of Visual Representation.</summary>
    public ActorVisual Visual { get; private set; }

    /// <summary>Tile ID when <see cref="Visual"/> is <see cref="ActorVisual.Tile"/>.</summary>
    public int TileId { get; private set; }

    /// <summary>First tile column of the region.</summary>
    public int RegionX { get; private set; }

    /// <summary>First tile row (0 = bottom) of the region.</summary>
    public int RegionY { get; private set; }

    /// <summary>Region width in tiles.</summary>
    public int RegionWidth { get; private set; }

    /// <summary>Region height in tiles.</summary>
    public int RegionHeight { get; private set; }

    /// <summary>The Sprite Instance when <see cref="Visual"/> is <see cref="ActorVisual.Sprite"/>, or null.</summary>
    public Sprite? Sprite { get; private set; }

    /// <summary>
    /// The actor's collider, or null. Setting it keeps the layer's and level's <c>Colliders</c> sets in step.
    /// </summary>
    public Collider? Collider
    {
        get => _collider;
        set
        {
            if (_collider == value)
            {
                return;
            }

            if (_collider is not null)
            {
                Layer.Colliders.Remove(_collider);
                Layer.Level.Colliders.Remove(_collider);
            }

            _collider = value;
            if (value is not null)
            {
                Layer.Colliders.Add(value);
                Layer.Level.Colliders.Add(value);
            }
        }
    }

    /// <summary>Moves the actor; its collider moves by the same amount.</summary>
    public void SetPosition(int x, int y)
    {
        if (_collider is not null)
        {
            _collider.SetPosition(_collider.X + (x - X), _collider.Y + (y - Y));
        }

        X = x;
        Y = y;
    }

    /// <summary>Shows one tile of the layer's tilemap.</summary>
    public void SetTile(int tileId)
    {
        if (tileId < 0 || tileId > 1023)
        {
            Intrinsics.Fail("Actor: tile ID must be 0..1023");
        }

        RequireTilemap();
        ClearVisual();
        Visual = ActorVisual.Tile;
        TileId = tileId;
    }

    /// <summary>Shows a rectangle of tiles of the layer's tilemap (column, row from the bottom, size in tiles).</summary>
    public void SetTileRegion(int tileX, int tileY, int width, int height)
    {
        if (tileX < 0 || tileY < 0 || width < 1 || height < 1 || tileX + width > 32 || tileY + height > 32)
        {
            Intrinsics.Fail("Actor: the tile region must lie inside the 32x32-tile tilemap");
        }

        RequireTilemap();
        ClearVisual();
        Visual = ActorVisual.TileRegion;
        RegionX = tileX;
        RegionY = tileY;
        RegionWidth = width;
        RegionHeight = height;
    }

    /// <summary>Shows a Sprite Instance (for example one an <see cref="AnimationPlayback"/> animates).</summary>
    public void SetSprite(Sprite sprite)
    {
        ClearVisual();
        Visual = ActorVisual.Sprite;
        Sprite = sprite;
    }

    /// <summary>Removes the Visual Representation.</summary>
    public void ClearVisual()
    {
        Visual = ActorVisual.None;
        TileId = 0;
        RegionX = 0;
        RegionY = 0;
        RegionWidth = 0;
        RegionHeight = 0;
        Sprite = null;
    }

    /// <summary>Draws the visual (if any and visible) into a Graphics Layer, offset by (<paramref name="offsetX"/>, <paramref name="offsetY"/>).</summary>
    public void Draw(int graphicsLayer, int offsetX = 0, int offsetY = 0)
    {
        if (!Visible)
        {
            return;
        }

        int x = X + offsetX, y = Y + offsetY;
        if (Visual == ActorVisual.Tile)
        {
            Tiles.Draw(graphicsLayer, Layer.Tilemap!, TileId % 32, TileId / 32, 1, 1, x, y, Transform);
        }
        else if (Visual == ActorVisual.TileRegion)
        {
            Tiles.Draw(graphicsLayer, Layer.Tilemap!, RegionX, RegionY, RegionWidth, RegionHeight, x, y, Transform);
        }
        else if (Visual == ActorVisual.Sprite && Sprite is not null)
        {
            Sprite.Draw(graphicsLayer, x, y, Transform);
        }
    }

    private void RequireTilemap()
    {
        if (Layer.Tilemap is null)
        {
            Intrinsics.Fail("Actor: tile visuals need the layer to name a tilemap");
        }
    }
}
