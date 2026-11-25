using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ZTX16.Cartridge;

/// <summary>The Visual Representation of an Actor.</summary>
public enum LevelVisual
{
    /// <summary>No visual (for example a trigger that only has a collider).</summary>
    None = 0,

    /// <summary>One tile of the layer's tilemap.</summary>
    Tile = 1,

    /// <summary>A rectangle of tiles of the layer's tilemap.</summary>
    TileRegion = 2,

    /// <summary>A Sprite resource (which names its own tilemap).</summary>
    Sprite = 3,
}

/// <summary>A collider rectangle relative to its actor's position.</summary>
public sealed record LevelCollider(int X, int Y, int Width, int Height);

/// <summary>An Actor as authored: ID, transform, at most one visual and an optional collider.</summary>
/// <param name="Id">Game-defined ID, unique in the level.</param>
/// <param name="X">Position X (the object origin, where the pivot lands).</param>
/// <param name="Y">Position Y.</param>
/// <param name="FlipX">Mirror horizontally.</param>
/// <param name="FlipY">Mirror vertically.</param>
/// <param name="ScaleX">Horizontal scale.</param>
/// <param name="ScaleY">Vertical scale.</param>
/// <param name="Rotation">Rotation in degrees, counter-clockwise.</param>
/// <param name="PivotX">Normalized pivot X (default 0 = left).</param>
/// <param name="PivotY">Normalized pivot Y (default 0 = bottom).</param>
/// <param name="Visual">The kind of Visual Representation.</param>
/// <param name="TileId">Tile ID for <see cref="LevelVisual.Tile"/>.</param>
/// <param name="RegionX">First tile column for <see cref="LevelVisual.TileRegion"/>.</param>
/// <param name="RegionY">First tile row (0 = bottom) for <see cref="LevelVisual.TileRegion"/>.</param>
/// <param name="RegionWidth">Width in tiles for <see cref="LevelVisual.TileRegion"/>.</param>
/// <param name="RegionHeight">Height in tiles for <see cref="LevelVisual.TileRegion"/>.</param>
/// <param name="Sprite">Sprite resource name for <see cref="LevelVisual.Sprite"/>.</param>
/// <param name="Collider">Collider relative to the position, or null.</param>
public sealed record LevelActor(
    string Id,
    int X,
    int Y,
    bool FlipX = false,
    bool FlipY = false,
    float ScaleX = 1f,
    float ScaleY = 1f,
    float Rotation = 0f,
    float PivotX = 0f,
    float PivotY = 0f,
    LevelVisual Visual = LevelVisual.None,
    int TileId = 0,
    int RegionX = 0,
    int RegionY = 0,
    int RegionWidth = 0,
    int RegionHeight = 0,
    string? Sprite = null,
    LevelCollider? Collider = null);

/// <summary>A logical Level Visual Layer.</summary>
/// <param name="Name">Name, unique in the level.</param>
/// <param name="Tilemap">Tilemap used by Tile and Tile Region actors, or null.</param>
/// <param name="Width">Width in screens (256 pixels).</param>
/// <param name="Height">Height in screens.</param>
/// <param name="Actors">Actors in drawing order.</param>
public sealed record LevelLayer(string Name, string? Tilemap, int Width, int Height, LevelActor[] Actors);

/// <summary>An immutable Level resource: logical layers of actors.</summary>
public sealed record LevelDefinition(LevelLayer[] Layers);

/// <summary>
/// The flat form a Level takes at runtime, read by the Core library through the <c>level.*</c> syscalls:
/// 32-bit values (integers, or float bits) and a string table. See <see cref="LevelFormat"/> for the layout.
/// </summary>
public sealed record LevelStream(int[] Data, string[] Strings);

/// <summary>
/// Level resources. Project side (<c>.level</c>): JSON
/// <code>
/// { "layers": [ { "name": "walls", "tilemap": "art", "width": 1, "height": 1, "actors": [
///     { "id": "floor", "x": 0, "y": 0, "region": [0, 4, 32, 1], "collider": [0, 0, 256, 8] },
///     { "id": "gem", "x": 40, "y": 40, "tile": 5, "rotation": 45, "pivot": [0.5, 0.5] },
///     { "id": "hero", "x": 120, "y": 32, "sprite": "ship", "flipX": true, "scale": [2, 2] },
///     { "id": "exit", "x": 240, "y": 0, "collider": [0, 0, 16, 256] } ] } ] }
/// </code>
/// <c>region</c> is [tile column, tile row (0 = bottom), width, height] in tiles; <c>collider</c> is
/// [x, y, width, height] relative to the actor position; <c>scale</c> may also be a single number.
/// </summary>
/// <remarks>
/// Stream layout: <c>[version = 1, layer count]</c>, then per layer <c>[name, tilemap (-1 = none), width,
/// height, actor count]</c> followed by <see cref="ActorStride"/> values per actor: <c>id, x, y, flags (1 =
/// FlipX, 2 = FlipY), scaleX, scaleY, rotation, pivotX, pivotY (float bits), visual, v0, v1, v2, v3 (tile ID,
/// or region x/y/width/height, or sprite name), has collider, collider x, y, width, height</c>. Names are
/// indices into the string table. Cartridge side: u16 string count and the strings, then i32 value count and
/// the values.
/// </remarks>
public static class LevelFormat
{
    /// <summary>Stream format version.</summary>
    public const int Version = 1;

    /// <summary>Values per actor in the stream.</summary>
    public const int ActorStride = 19;

    /// <summary>Maximum layer width or height in screens.</summary>
    public const int MaxLayerScreens = 16;

    /// <summary>Parses the project JSON.</summary>
    /// <exception cref="ProjectException">The JSON is invalid.</exception>
    public static LevelDefinition ParseJson(string json, string file)
    {
        JsonObject root = SpriteFormat.ParseObject(json, file);
        if (root["layers"] is not JsonArray layers || layers.Count == 0)
        {
            throw new ProjectException($"{file}: 'layers' must be a non-empty array.");
        }

        var result = new List<LevelLayer>();
        for (int l = 0; l < layers.Count; l++)
        {
            string where = $"{file}: layers[{l}]";
            JsonObject layer = layers[l] as JsonObject ?? throw new ProjectException($"{where} must be an object.");
            string name = SpriteFormat.RequiredString(layer, "name", where);
            string? tilemap = layer["tilemap"] is null ? null : SpriteFormat.RequiredString(layer, "tilemap", where);
            int width = SpriteFormat.OptionalInt(layer, "width", 1, where);
            int height = SpriteFormat.OptionalInt(layer, "height", 1, where);
            var actors = new List<LevelActor>();
            if (layer["actors"] is JsonArray list)
            {
                for (int a = 0; a < list.Count; a++)
                {
                    actors.Add(ParseActor(list[a] as JsonObject ?? throw new ProjectException($"{where}.actors[{a}] must be an object."),
                        $"{where}.actors[{a}]"));
                }
            }
            else if (layer["actors"] is not null)
            {
                throw new ProjectException($"{where}: 'actors' must be an array.");
            }

            result.Add(new LevelLayer(name, tilemap, width, height, actors.ToArray()));
        }

        var level = new LevelDefinition(result.ToArray());
        Validate(level, file);
        return level;
    }

    /// <summary>Formats project JSON.</summary>
    public static string FormatJson(LevelDefinition level)
    {
        var text = new StringBuilder("{\n  \"layers\": [\n");
        for (int l = 0; l < level.Layers.Length; l++)
        {
            LevelLayer layer = level.Layers[l];
            text.Append($"    {{\n      \"name\": {Quote(layer.Name)},\n");
            if (layer.Tilemap is not null)
            {
                text.Append($"      \"tilemap\": {Quote(layer.Tilemap)},\n");
            }

            text.Append($"      \"width\": {layer.Width},\n      \"height\": {layer.Height},\n      \"actors\": [\n");
            for (int a = 0; a < layer.Actors.Length; a++)
            {
                text.Append("        ").Append(FormatActor(layer.Actors[a])).Append(a < layer.Actors.Length - 1 ? ",\n" : "\n");
            }

            text.Append("      ]\n    }").Append(l < level.Layers.Length - 1 ? ",\n" : "\n");
        }

        return text.Append("  ]\n}\n").ToString();
    }

    /// <summary>The runtime stream of a level.</summary>
    public static LevelStream ToStream(LevelDefinition level)
    {
        var strings = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var data = new List<int> { Version, level.Layers.Length };
        foreach (LevelLayer layer in level.Layers)
        {
            data.AddRange([String(layer.Name), layer.Tilemap is null ? -1 : String(layer.Tilemap), layer.Width, layer.Height, layer.Actors.Length]);
            foreach (LevelActor actor in layer.Actors)
            {
                int[] visual = actor.Visual switch
                {
                    LevelVisual.Tile => [actor.TileId, 0, 0, 0],
                    LevelVisual.TileRegion => [actor.RegionX, actor.RegionY, actor.RegionWidth, actor.RegionHeight],
                    LevelVisual.Sprite => [String(actor.Sprite!), 0, 0, 0],
                    _ => [0, 0, 0, 0],
                };
                LevelCollider? c = actor.Collider;
                data.AddRange([
                    String(actor.Id), actor.X, actor.Y, (actor.FlipX ? 1 : 0) | (actor.FlipY ? 2 : 0),
                    Bits(actor.ScaleX), Bits(actor.ScaleY), Bits(actor.Rotation), Bits(actor.PivotX), Bits(actor.PivotY),
                    (int)actor.Visual, .. visual,
                    c is null ? 0 : 1, c?.X ?? 0, c?.Y ?? 0, c?.Width ?? 0, c?.Height ?? 0,
                ]);
            }
        }

        return new LevelStream(data.ToArray(), strings.ToArray());

        int String(string value)
        {
            if (!index.TryGetValue(value, out int i))
            {
                i = strings.Count;
                strings.Add(value);
                index[value] = i;
            }

            return i;
        }

        static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    }

    /// <summary>Rebuilds a level from its runtime stream.</summary>
    /// <exception cref="ProjectException">The stream is malformed.</exception>
    public static LevelDefinition FromStream(LevelStream stream, string where)
    {
        int[] d = stream.Data;
        int p = 0;
        if (Next() != Version)
        {
            throw new ProjectException($"{where}: unsupported level data version.");
        }

        var layers = new LevelLayer[Count(Next())];
        for (int l = 0; l < layers.Length; l++)
        {
            string name = Str(Next());
            int tilemap = Next();
            int width = Next(), height = Next();
            var actors = new LevelActor[Count(Next())];
            for (int a = 0; a < actors.Length; a++)
            {
                string id = Str(Next());
                int x = Next(), y = Next(), flags = Next();
                float sx = Float(Next()), sy = Float(Next()), rotation = Float(Next()), px = Float(Next()), py = Float(Next());
                var visual = (LevelVisual)Next();
                int v0 = Next(), v1 = Next(), v2 = Next(), v3 = Next();
                bool hasCollider = Next() != 0;
                int cx = Next(), cy = Next(), cw = Next(), ch = Next();
                actors[a] = new LevelActor(id, x, y, (flags & 1) != 0, (flags & 2) != 0, sx, sy, rotation, px, py, visual,
                    TileId: visual == LevelVisual.Tile ? v0 : 0,
                    RegionX: visual == LevelVisual.TileRegion ? v0 : 0,
                    RegionY: visual == LevelVisual.TileRegion ? v1 : 0,
                    RegionWidth: visual == LevelVisual.TileRegion ? v2 : 0,
                    RegionHeight: visual == LevelVisual.TileRegion ? v3 : 0,
                    Sprite: visual == LevelVisual.Sprite ? Str(v0) : null,
                    Collider: hasCollider ? new LevelCollider(cx, cy, cw, ch) : null);
            }

            layers[l] = new LevelLayer(name, tilemap < 0 ? null : Str(tilemap), width, height, actors);
        }

        if (p != d.Length)
        {
            throw new ProjectException($"{where}: level data has trailing values.");
        }

        var level = new LevelDefinition(layers);
        Validate(level, where);
        return level;

        int Next() => p < d.Length ? d[p++] : throw new ProjectException($"{where}: level data is truncated.");

        int Count(int value) => value is >= 0 and <= 65535 ? value : throw new ProjectException($"{where}: bad count in level data.");

        string Str(int i) => (uint)i < (uint)stream.Strings.Length ? stream.Strings[i] : throw new ProjectException($"{where}: bad string index in level data.");

        static float Float(int bits) => BitConverter.Int32BitsToSingle(bits);
    }

    /// <summary>Encodes cartridge data.</summary>
    public static byte[] Encode(LevelDefinition level)
    {
        LevelStream stream = ToStream(level);
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write((ushort)stream.Strings.Length);
        foreach (string s in stream.Strings)
        {
            FormatHelpers.WriteName(writer, s);
        }

        writer.Write(stream.Data.Length);
        foreach (int value in stream.Data)
        {
            writer.Write(value);
        }

        writer.Flush();
        return memory.ToArray();
    }

    /// <summary>Decodes cartridge data into the runtime stream, validating it.</summary>
    public static LevelStream DecodeStream(ReadOnlySpan<byte> data, string name)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data.ToArray()));
            var strings = new string[reader.ReadUInt16()];
            for (int i = 0; i < strings.Length; i++)
            {
                strings[i] = FormatHelpers.ReadName(reader);
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 4)
            {
                throw new EndOfStreamException();
            }

            int[] values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = reader.ReadInt32();
            }

            var stream = new LevelStream(values, strings);
            FromStream(stream, $"Level '{name}'");
            return stream;
        }
        catch (EndOfStreamException e)
        {
            throw new ProjectException($"Level '{name}' data is truncated.", e);
        }
    }

    /// <summary>Decodes cartridge data.</summary>
    public static LevelDefinition Decode(ReadOnlySpan<byte> data, string name) => FromStream(DecodeStream(data, name), $"Level '{name}'");

    private static LevelActor ParseActor(JsonObject actor, string where)
    {
        string id = SpriteFormat.RequiredString(actor, "id", where);
        int x = SpriteFormat.OptionalInt(actor, "x", 0, where);
        int y = SpriteFormat.OptionalInt(actor, "y", 0, where);
        bool flipX = Bool(actor, "flipX", where), flipY = Bool(actor, "flipY", where);
        float scaleX = 1f, scaleY = 1f;
        if (actor["scale"] is JsonValue single && single.TryGetValue(out float s))
        {
            scaleX = scaleY = s;
        }
        else if (actor["scale"] is not null)
        {
            float[] pair = Floats(actor, "scale", 2, where);
            (scaleX, scaleY) = (pair[0], pair[1]);
        }

        float rotation = actor["rotation"] is null ? 0f : Floats1(actor, "rotation", where);
        float pivotX = 0f, pivotY = 0f;
        if (actor["pivot"] is not null)
        {
            float[] pair = Floats(actor, "pivot", 2, where);
            (pivotX, pivotY) = (pair[0], pair[1]);
        }

        int visuals = (actor["tile"] is null ? 0 : 1) + (actor["region"] is null ? 0 : 1) + (actor["sprite"] is null ? 0 : 1);
        if (visuals > 1)
        {
            throw new ProjectException($"{where}: an actor has at most one of 'tile', 'region' and 'sprite'.");
        }

        LevelVisual visual = LevelVisual.None;
        int tile = 0;
        int[] region = [0, 0, 0, 0];
        string? sprite = null;
        if (actor["tile"] is not null)
        {
            visual = LevelVisual.Tile;
            tile = SpriteFormat.RequiredInt(actor, "tile", where);
        }
        else if (actor["region"] is not null)
        {
            visual = LevelVisual.TileRegion;
            region = Ints(actor, "region", 4, where);
        }
        else if (actor["sprite"] is not null)
        {
            visual = LevelVisual.Sprite;
            sprite = SpriteFormat.RequiredString(actor, "sprite", where);
        }

        LevelCollider? collider = null;
        if (actor["collider"] is not null)
        {
            int[] c = Ints(actor, "collider", 4, where);
            collider = new LevelCollider(c[0], c[1], c[2], c[3]);
        }

        return new LevelActor(id, x, y, flipX, flipY, scaleX, scaleY, rotation, pivotX, pivotY, visual, tile,
            region[0], region[1], region[2], region[3], sprite, collider);
    }

    private static string FormatActor(LevelActor actor)
    {
        var parts = new List<string> { $"\"id\": {Quote(actor.Id)}", $"\"x\": {actor.X}", $"\"y\": {actor.Y}" };
        switch (actor.Visual)
        {
            case LevelVisual.Tile:
                parts.Add($"\"tile\": {actor.TileId}");
                break;
            case LevelVisual.TileRegion:
                parts.Add($"\"region\": [{actor.RegionX}, {actor.RegionY}, {actor.RegionWidth}, {actor.RegionHeight}]");
                break;
            case LevelVisual.Sprite:
                parts.Add($"\"sprite\": {Quote(actor.Sprite!)}");
                break;
        }

        if (actor.FlipX)
        {
            parts.Add("\"flipX\": true");
        }

        if (actor.FlipY)
        {
            parts.Add("\"flipY\": true");
        }

        if (actor.ScaleX != 1f || actor.ScaleY != 1f)
        {
            parts.Add($"\"scale\": [{F(actor.ScaleX)}, {F(actor.ScaleY)}]");
        }

        if (actor.Rotation != 0f)
        {
            parts.Add($"\"rotation\": {F(actor.Rotation)}");
        }

        if (actor.PivotX != 0f || actor.PivotY != 0f)
        {
            parts.Add($"\"pivot\": [{F(actor.PivotX)}, {F(actor.PivotY)}]");
        }

        if (actor.Collider is { } c)
        {
            parts.Add($"\"collider\": [{c.X}, {c.Y}, {c.Width}, {c.Height}]");
        }

        return "{ " + string.Join(", ", parts) + " }";

        static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void Validate(LevelDefinition level, string where)
    {
        if (level.Layers.Length == 0)
        {
            throw new ProjectException($"{where}: a level needs at least one layer.");
        }

        var layerNames = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (LevelLayer layer in level.Layers)
        {
            if (string.IsNullOrEmpty(layer.Name) || !layerNames.Add(layer.Name))
            {
                throw new ProjectException($"{where}: layer names must be non-empty and unique ('{layer.Name}').");
            }

            if (layer.Width is < 1 or > MaxLayerScreens || layer.Height is < 1 or > MaxLayerScreens)
            {
                throw new ProjectException($"{where}: layer '{layer.Name}' must be 1..{MaxLayerScreens} screens wide and high.");
            }

            foreach (LevelActor actor in layer.Actors)
            {
                string at = $"{where}: actor '{actor.Id}'";
                if (string.IsNullOrEmpty(actor.Id) || !ids.Add(actor.Id))
                {
                    throw new ProjectException($"{where}: actor IDs must be non-empty and unique in the level ('{actor.Id}').");
                }

                if (!float.IsFinite(actor.ScaleX) || !float.IsFinite(actor.ScaleY) || !float.IsFinite(actor.Rotation)
                    || !float.IsFinite(actor.PivotX) || !float.IsFinite(actor.PivotY))
                {
                    throw new ProjectException($"{at}: transform values must be finite.");
                }

                switch (actor.Visual)
                {
                    case LevelVisual.None:
                        break;
                    case LevelVisual.Tile when actor.TileId is < 0 or > 1023:
                        throw new ProjectException($"{at}: tile ID must be 0..1023.");
                    case LevelVisual.TileRegion when actor.RegionX < 0 || actor.RegionY < 0 || actor.RegionWidth < 1 || actor.RegionHeight < 1
                        || actor.RegionX + actor.RegionWidth > 32 || actor.RegionY + actor.RegionHeight > 32:
                        throw new ProjectException($"{at}: the tile region must lie inside the 32×32-tile tilemap.");
                    case LevelVisual.Sprite when string.IsNullOrEmpty(actor.Sprite):
                        throw new ProjectException($"{at}: sprite name must be non-empty.");
                    case LevelVisual.Tile or LevelVisual.TileRegion when layer.Tilemap is null:
                        throw new ProjectException($"{at}: tile actors need the layer to name a tilemap.");
                    case LevelVisual.Tile or LevelVisual.TileRegion or LevelVisual.Sprite:
                        break;
                    default:
                        throw new ProjectException($"{at}: unknown visual kind.");
                }

                if (actor.Collider is { } c && (c.Width < 1 || c.Height < 1))
                {
                    throw new ProjectException($"{at}: collider width and height must be at least 1.");
                }
            }
        }
    }

    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static bool Bool(JsonObject o, string key, string where) =>
        o[key] is null ? false
        : o[key] is JsonValue v && v.TryGetValue(out bool b) ? b
        : throw new ProjectException($"{where}: '{key}' must be true or false.");

    private static float Floats1(JsonObject o, string key, string where) =>
        o[key] is JsonValue v && v.TryGetValue(out float f) ? f : throw new ProjectException($"{where}: '{key}' must be a number.");

    private static float[] Floats(JsonObject o, string key, int count, string where)
    {
        if (o[key] is not JsonArray array || array.Count != count)
        {
            throw new ProjectException($"{where}: '{key}' must be an array of {count} numbers.");
        }

        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = array[i] is JsonValue v && v.TryGetValue(out float f) ? f : throw new ProjectException($"{where}: '{key}' must be an array of {count} numbers.");
        }

        return result;
    }

    private static int[] Ints(JsonObject o, string key, int count, string where)
    {
        if (o[key] is not JsonArray array || array.Count != count)
        {
            throw new ProjectException($"{where}: '{key}' must be an array of {count} integers.");
        }

        var result = new int[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = array[i] is JsonValue v && v.TryGetValue(out int n) ? n : throw new ProjectException($"{where}: '{key}' must be an array of {count} integers.");
        }

        return result;
    }
}
