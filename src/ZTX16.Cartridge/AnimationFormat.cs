using System.Text;
using System.Text.Json.Nodes;

namespace ZTX16.Cartridge;

/// <summary>One animation frame: how long it lasts and the sprite tiles it sets.</summary>
/// <param name="Duration">Duration in ticks (1/60 s) at play speed 1.</param>
/// <param name="SpriteTiles">Sprite tile indices (row-major, bottom row first) the frame sets.</param>
/// <param name="TileIds">The tilemap tile ID each corresponding sprite tile shows.</param>
public sealed record AnimationFrame(int Duration, int[] SpriteTiles, int[] TileIds);

/// <summary>An animation: frames of tile swaps. It knows nothing about a particular sprite, speed or looping.</summary>
public sealed record AnimationDefinition(AnimationFrame[] Frames);

/// <summary>
/// Animation resources. Project side (<c>.anim</c>): JSON
/// <c>{ "frames": [ { "duration": 6, "tiles": [[0, 64], [1, 65]] }, … ] }</c> where each pair is
/// <c>[sprite tile index, tile ID]</c>. Each frame describes the full tile state it wants, not a change from
/// the previous frame. Cartridge side: u16 frame count, then per frame u16 duration, u16 swap count and
/// (u16 index, u16 tile) pairs.
/// </summary>
public static class AnimationFormat
{
    /// <summary>Parses the project JSON.</summary>
    /// <exception cref="ProjectException">The JSON is invalid.</exception>
    public static AnimationDefinition ParseJson(string json, string file)
    {
        JsonObject root = SpriteFormat.ParseObject(json, file);
        if (root["frames"] is not JsonArray frames || frames.Count == 0)
        {
            throw new ProjectException($"{file}: 'frames' must be a non-empty array.");
        }

        var result = new List<AnimationFrame>();
        for (int f = 0; f < frames.Count; f++)
        {
            JsonObject frame = frames[f] as JsonObject ?? throw new ProjectException($"{file}: frames[{f}] must be an object.");
            int duration = SpriteFormat.RequiredInt(frame, "duration", file);
            var indices = new List<int>();
            var tiles = new List<int>();
            if (frame["tiles"] is JsonArray swaps)
            {
                foreach (JsonNode? swap in swaps)
                {
                    if (swap is not JsonArray { Count: 2 } pair || pair[0] is not JsonValue a || pair[1] is not JsonValue b
                        || !a.TryGetValue(out int index) || !b.TryGetValue(out int tile))
                    {
                        throw new ProjectException($"{file}: frames[{f}].tiles entries must be [sprite tile index, tile ID] pairs.");
                    }

                    indices.Add(index);
                    tiles.Add(tile);
                }
            }

            result.Add(new AnimationFrame(duration, indices.ToArray(), tiles.ToArray()));
        }

        var animation = new AnimationDefinition(result.ToArray());
        Validate(animation, file);
        return animation;
    }

    /// <summary>Formats project JSON.</summary>
    public static string FormatJson(AnimationDefinition animation)
    {
        var text = new StringBuilder("{\n  \"frames\": [\n");
        for (int f = 0; f < animation.Frames.Length; f++)
        {
            AnimationFrame frame = animation.Frames[f];
            string swaps = string.Join(", ", frame.SpriteTiles.Select((index, i) => $"[{index}, {frame.TileIds[i]}]"));
            text.Append($"    {{ \"duration\": {frame.Duration}, \"tiles\": [{swaps}] }}");
            text.Append(f < animation.Frames.Length - 1 ? ",\n" : "\n");
        }

        return text.Append("  ]\n}\n").ToString();
    }

    /// <summary>Encodes cartridge data.</summary>
    public static byte[] Encode(AnimationDefinition animation)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)animation.Frames.Length);
        foreach (AnimationFrame frame in animation.Frames)
        {
            writer.Write((ushort)frame.Duration);
            writer.Write((ushort)frame.SpriteTiles.Length);
            for (int i = 0; i < frame.SpriteTiles.Length; i++)
            {
                writer.Write((ushort)frame.SpriteTiles[i]);
                writer.Write((ushort)frame.TileIds[i]);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Decodes cartridge data.</summary>
    public static AnimationDefinition Decode(ReadOnlySpan<byte> data, string name)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data.ToArray()));
            var frames = new AnimationFrame[reader.ReadUInt16()];
            for (int f = 0; f < frames.Length; f++)
            {
                int duration = reader.ReadUInt16();
                int count = reader.ReadUInt16();
                int[] indices = new int[count], tiles = new int[count];
                for (int i = 0; i < count; i++)
                {
                    indices[i] = reader.ReadUInt16();
                    tiles[i] = reader.ReadUInt16();
                }

                frames[f] = new AnimationFrame(duration, indices, tiles);
            }

            var animation = new AnimationDefinition(frames);
            Validate(animation, $"Animation '{name}'");
            return animation;
        }
        catch (EndOfStreamException e)
        {
            throw new ProjectException($"Animation '{name}' data is truncated.", e);
        }
    }

    private static void Validate(AnimationDefinition animation, string where)
    {
        if (animation.Frames.Length is 0 or > ushort.MaxValue)
        {
            throw new ProjectException($"{where}: an animation needs at least one frame.");
        }

        for (int f = 0; f < animation.Frames.Length; f++)
        {
            AnimationFrame frame = animation.Frames[f];
            if (frame.Duration is < 1 or > ushort.MaxValue)
            {
                throw new ProjectException($"{where}: frame {f} needs a duration of at least 1 tick.");
            }

            if (frame.SpriteTiles.Any(i => i is < 0 or >= 1024) || frame.TileIds.Any(t => t is < 0 or > 1023))
            {
                throw new ProjectException($"{where}: frame {f} has a sprite tile index or tile ID out of range.");
            }
        }
    }
}
