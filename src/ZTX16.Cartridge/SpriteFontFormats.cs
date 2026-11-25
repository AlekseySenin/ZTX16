using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZTX16.Cartridge;

/// <summary>A sprite: a grid of tile IDs from one tilemap, bottom row first.</summary>
/// <param name="Tilemap">Name of the Tilemap resource the tiles come from.</param>
/// <param name="Width">Grid width in tiles.</param>
/// <param name="Tiles">Tile IDs (0..1023), row-major, bottom row first.</param>
public sealed record SpriteDefinition(string Tilemap, int Width, int[] Tiles)
{
    /// <summary>Grid height in tiles.</summary>
    public int Height => Tiles.Length / Width;
}

/// <summary>
/// Sprite resources. Project side (<c>.sprite</c>): JSON
/// <c>{ "tilemap": "hero", "width": 2, "tiles": [0, 1, 32, 33] }</c>, tiles listed bottom row first.
/// Cartridge side: tilemap name, width and u16 tile IDs.
/// </summary>
public static class SpriteFormat
{
    /// <summary>Largest grid side in tiles.</summary>
    public const int MaxSide = 32;

    /// <summary>Parses the project JSON.</summary>
    /// <exception cref="ProjectException">The JSON is invalid.</exception>
    public static SpriteDefinition ParseJson(string json, string file)
    {
        JsonObject root = ParseObject(json, file);
        string tilemap = RequiredString(root, "tilemap", file);
        int width = RequiredInt(root, "width", file);
        int[] tiles = root["tiles"] is JsonArray array
            ? array.Select((n, i) => n is JsonValue v && v.TryGetValue(out int id) ? id : throw new ProjectException($"{file}: tiles[{i}] must be an integer.")).ToArray()
            : throw new ProjectException($"{file}: 'tiles' must be an array of tile IDs.");
        var sprite = new SpriteDefinition(tilemap, width, tiles);
        Validate(sprite, file);
        return sprite;
    }

    /// <summary>Formats project JSON.</summary>
    public static string FormatJson(SpriteDefinition sprite)
    {
        var text = new StringBuilder();
        text.Append("{\n  \"tilemap\": ").Append(JsonSerializer.Serialize(sprite.Tilemap)).Append(",\n");
        text.Append("  \"width\": ").Append(sprite.Width).Append(",\n");
        text.Append("  \"tiles\": [\n");
        for (int row = 0; row < sprite.Height; row++)
        {
            text.Append("    ").Append(string.Join(", ", sprite.Tiles.Skip(row * sprite.Width).Take(sprite.Width)));
            text.Append(row < sprite.Height - 1 ? ",\n" : "\n");
        }

        return text.Append("  ]\n}\n").ToString();
    }

    /// <summary>Encodes cartridge data.</summary>
    public static byte[] Encode(SpriteDefinition sprite)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        FormatHelpers.WriteName(writer, sprite.Tilemap);
        writer.Write((ushort)sprite.Width);
        writer.Write((ushort)sprite.Tiles.Length);
        foreach (int tile in sprite.Tiles)
        {
            writer.Write((ushort)tile);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Decodes cartridge data.</summary>
    public static SpriteDefinition Decode(ReadOnlySpan<byte> data, string name)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data.ToArray()), Encoding.UTF8);
            string tilemap = FormatHelpers.ReadName(reader);
            int width = reader.ReadUInt16();
            var tiles = new int[reader.ReadUInt16()];
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i] = reader.ReadUInt16();
            }

            var sprite = new SpriteDefinition(tilemap, width, tiles);
            Validate(sprite, $"Sprite '{name}'");
            return sprite;
        }
        catch (EndOfStreamException e)
        {
            throw new ProjectException($"Sprite '{name}' data is truncated.", e);
        }
    }

    private static void Validate(SpriteDefinition sprite, string where)
    {
        if (sprite.Width < 1 || sprite.Width > MaxSide || sprite.Tiles.Length == 0 || sprite.Tiles.Length % sprite.Width != 0
            || sprite.Height > MaxSide)
        {
            throw new ProjectException($"{where}: a sprite needs 1..{MaxSide} tiles per side and a tile count that is a multiple of 'width'.");
        }

        int bad = Array.FindIndex(sprite.Tiles, t => t is < 0 or > 1023);
        if (bad >= 0)
        {
            throw new ProjectException($"{where}: tile ID {sprite.Tiles[bad]} is out of range (0..1023).");
        }
    }

    internal static JsonObject ParseObject(string json, string file)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new ProjectException($"{file}: expected a JSON object.");
        }
        catch (JsonException e)
        {
            throw new ProjectException($"{file}: invalid JSON: {e.Message}", e);
        }
    }

    internal static string RequiredString(JsonObject root, string key, string file) =>
        root[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s)
            ? s
            : throw new ProjectException($"{file}: '{key}' must be a non-empty string.");

    internal static int RequiredInt(JsonObject root, string key, string file) =>
        root[key] is JsonValue v && v.TryGetValue(out int i) ? i : throw new ProjectException($"{file}: '{key}' must be an integer.");

    internal static int OptionalInt(JsonObject root, string key, int fallback, string file) =>
        root[key] is null ? fallback : RequiredInt(root, key, file);
}

/// <summary>A bitmap font: glyphs are regions of a tilemap; every character has a glyph (or none) and an advance.</summary>
/// <param name="Tilemap">Name of the Tilemap resource holding the glyph bitmaps.</param>
/// <param name="GlyphWidth">Glyph width in tiles.</param>
/// <param name="GlyphHeight">Glyph height in tiles.</param>
/// <param name="LineHeight">Distance between lines in pixels.</param>
/// <param name="GlyphTiles">For each of the 256 character codes, the tile ID of its glyph's lower-left tile, or -1.</param>
/// <param name="Advances">For each character code, the pen advance in pixels.</param>
public sealed record FontDefinition(string Tilemap, int GlyphWidth, int GlyphHeight, int LineHeight, int[] GlyphTiles, int[] Advances);

/// <summary>
/// Font resources. Project side (<c>.font</c>): JSON with <c>tilemap</c>, <c>glyphWidth</c>/<c>glyphHeight</c>
/// (tiles), <c>lineHeight</c> and <c>advance</c> (pixels), <c>ranges</c> of consecutive character codes laid
/// out on the sheet, and per-character <c>characters</c> overrides.
/// </summary>
/// <remarks>
/// A range <c>{ "first": 32, "last": 126, "tile": 992 }</c> places glyphs left to right starting at tile 992
/// (the top-left tile of the sheet), wrapping at the right edge to the next glyph row below, the way font
/// sheets are drawn. Override example: <c>{ "char": "i", "advance": 4 }</c> or <c>{ "code": 200, "tile": 5 }</c>.
/// Character codes are the 256-code ZTX16 character set.
/// </remarks>
public static class FontFormat
{
    /// <summary>Number of character codes.</summary>
    public const int Codes = 256;

    /// <summary>Parses the project JSON.</summary>
    /// <exception cref="ProjectException">The JSON is invalid.</exception>
    public static FontDefinition ParseJson(string json, string file)
    {
        JsonObject root = SpriteFormat.ParseObject(json, file);
        string tilemap = SpriteFormat.RequiredString(root, "tilemap", file);
        int glyphWidth = SpriteFormat.OptionalInt(root, "glyphWidth", 1, file);
        int glyphHeight = SpriteFormat.OptionalInt(root, "glyphHeight", 1, file);
        int lineHeight = SpriteFormat.OptionalInt(root, "lineHeight", (glyphHeight * 8) + 1, file);
        int advance = SpriteFormat.OptionalInt(root, "advance", glyphWidth * 8, file);

        int[] tiles = Enumerable.Repeat(-1, Codes).ToArray();
        int[] advances = Enumerable.Repeat(advance, Codes).ToArray();

        if (root["ranges"] is JsonArray ranges)
        {
            foreach (JsonNode? node in ranges)
            {
                JsonObject range = node as JsonObject ?? throw new ProjectException($"{file}: each range must be an object.");
                int first = SpriteFormat.RequiredInt(range, "first", file);
                int last = SpriteFormat.RequiredInt(range, "last", file);
                int tile = SpriteFormat.RequiredInt(range, "tile", file);
                CheckCode(first, file);
                CheckCode(last, file);
                int column = tile % 32, row = tile / 32;
                for (int code = first; code <= last; code++)
                {
                    tiles[code] = (row * 32) + column;
                    column += glyphWidth;
                    if (column + glyphWidth > 32)
                    {
                        column = 0;
                        row -= glyphHeight;
                    }
                }
            }
        }

        if (root["characters"] is JsonArray characters)
        {
            foreach (JsonNode? node in characters)
            {
                JsonObject entry = node as JsonObject ?? throw new ProjectException($"{file}: each character entry must be an object.");
                int code = entry["char"] is JsonValue c && c.TryGetValue(out string? text) && text.Length == 1
                    ? text[0]
                    : SpriteFormat.RequiredInt(entry, "code", file);
                CheckCode(code, file);
                tiles[code] = SpriteFormat.OptionalInt(entry, "tile", tiles[code], file);
                advances[code] = SpriteFormat.OptionalInt(entry, "advance", advances[code], file);
            }
        }

        var font = new FontDefinition(tilemap, glyphWidth, glyphHeight, lineHeight, tiles, advances);
        Validate(font, file);
        return font;
    }

    /// <summary>Formats project JSON (every mapped character listed explicitly, so the result is lossless).</summary>
    public static string FormatJson(FontDefinition font)
    {
        int commonAdvance = font.Advances.GroupBy(a => a).OrderByDescending(g => g.Count()).First().Key;
        var text = new StringBuilder();
        text.Append("{\n  \"tilemap\": ").Append(JsonSerializer.Serialize(font.Tilemap)).Append(",\n");
        text.Append($"  \"glyphWidth\": {font.GlyphWidth},\n  \"glyphHeight\": {font.GlyphHeight},\n");
        text.Append($"  \"lineHeight\": {font.LineHeight},\n  \"advance\": {commonAdvance},\n  \"characters\": [\n");
        var entries = new List<string>();
        for (int code = 0; code < Codes; code++)
        {
            if (font.GlyphTiles[code] < 0 && font.Advances[code] == commonAdvance)
            {
                continue;
            }

            string tile = font.GlyphTiles[code] >= 0 ? $", \"tile\": {font.GlyphTiles[code]}" : string.Empty;
            string advance = font.Advances[code] != commonAdvance ? $", \"advance\": {font.Advances[code]}" : string.Empty;
            entries.Add($"    {{ \"code\": {code}{tile}{advance} }}");
        }

        return text.Append(string.Join(",\n", entries)).Append("\n  ]\n}\n").ToString();
    }

    /// <summary>Encodes cartridge data.</summary>
    public static byte[] Encode(FontDefinition font)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        FormatHelpers.WriteName(writer, font.Tilemap);
        writer.Write((byte)font.GlyphWidth);
        writer.Write((byte)font.GlyphHeight);
        writer.Write((byte)font.LineHeight);
        for (int code = 0; code < Codes; code++)
        {
            writer.Write((short)font.GlyphTiles[code]);
            writer.Write((byte)font.Advances[code]);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Decodes cartridge data.</summary>
    public static FontDefinition Decode(ReadOnlySpan<byte> data, string name)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data.ToArray()), Encoding.UTF8);
            string tilemap = FormatHelpers.ReadName(reader);
            int glyphWidth = reader.ReadByte(), glyphHeight = reader.ReadByte(), lineHeight = reader.ReadByte();
            int[] tiles = new int[Codes], advances = new int[Codes];
            for (int code = 0; code < Codes; code++)
            {
                tiles[code] = reader.ReadInt16();
                advances[code] = reader.ReadByte();
            }

            var font = new FontDefinition(tilemap, glyphWidth, glyphHeight, lineHeight, tiles, advances);
            Validate(font, $"Font '{name}'");
            return font;
        }
        catch (EndOfStreamException e)
        {
            throw new ProjectException($"Font '{name}' data is truncated.", e);
        }
    }

    private static void CheckCode(int code, string file)
    {
        if ((uint)code >= Codes)
        {
            throw new ProjectException($"{file}: character code {code} is outside the ZTX16 character set (0..255).");
        }
    }

    private static void Validate(FontDefinition font, string where)
    {
        if (font.GlyphWidth is < 1 or > 4 || font.GlyphHeight is < 1 or > 4)
        {
            throw new ProjectException($"{where}: glyphWidth and glyphHeight must be 1..4 tiles.");
        }

        if (font.LineHeight is < 1 or > 255)
        {
            throw new ProjectException($"{where}: lineHeight must be 1..255 pixels.");
        }

        for (int code = 0; code < Codes; code++)
        {
            int tile = font.GlyphTiles[code];
            if (font.Advances[code] is < 0 or > 255)
            {
                throw new ProjectException($"{where}: advance of character {code} must be 0..255.");
            }

            if (tile == -1)
            {
                continue;
            }

            if (tile is < 0 or > 1023 || (tile % 32) + font.GlyphWidth > 32 || (tile / 32) + font.GlyphHeight > 32)
            {
                throw new ProjectException($"{where}: the glyph of character {code} (tile {tile}) does not fit in the tilemap.");
            }
        }
    }
}

internal static class FormatHelpers
{
    public static void WriteName(BinaryWriter writer, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    public static string ReadName(BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        byte[] bytes = reader.ReadBytes(length);
        return bytes.Length == length ? Encoding.UTF8.GetString(bytes) : throw new EndOfStreamException();
    }
}
