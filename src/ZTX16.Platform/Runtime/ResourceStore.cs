using ZTX16.Cartridge;

namespace ZTX16.Platform.Runtime;

/// <summary>
/// The immutable resources of the running cartridge, addressed by type and name. Runtime state
/// derived from them (for example the active palette) never modifies the resources themselves.
/// </summary>
public sealed class ResourceStore
{
    private readonly Dictionary<(ResourceType, string), byte[]> _data = [];
    private readonly Dictionary<ResourceType, string[]> _names;
    private readonly Dictionary<string, ushort[]> _palettes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _tilemaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpriteDefinition> _sprites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimationDefinition> _animations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Graphics.RuntimeFont> _fonts = new(StringComparer.Ordinal);

    /// <summary>Creates a store from cartridge resources.</summary>
    public ResourceStore(IEnumerable<CartridgeResource> resources)
    {
        foreach (CartridgeResource resource in resources)
        {
            if (!_data.TryAdd((resource.Type, resource.Name), resource.Data))
            {
                throw new ArgumentException($"Duplicate {resource.Type} resource '{resource.Name}'.", nameof(resources));
            }
        }

        _names = _data.Keys
            .GroupBy(k => k.Item1)
            .ToDictionary(g => g.Key, g => g.Select(k => k.Item2).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>A store without resources.</summary>
    public static ResourceStore Empty { get; } = new([]);

    /// <summary>True if the resource exists.</summary>
    public bool Exists(ResourceType type, string name) => _data.ContainsKey((type, name));

    /// <summary>Names of all resources of <paramref name="type"/>, in ordinal order.</summary>
    public IReadOnlyList<string> Names(ResourceType type) => _names.GetValueOrDefault(type) ?? [];

    /// <summary>ColorIndex pixels of a Tilemap resource (256×256, row 0 at the bottom).</summary>
    /// <exception cref="ArgumentException">The tilemap does not exist.</exception>
    public byte[] Tilemap(string name)
    {
        if (_tilemaps.TryGetValue(name, out byte[]? cached))
        {
            return cached;
        }

        byte[] data = _data.GetValueOrDefault((ResourceType.Tilemap, name))
            ?? throw new ArgumentException($"Tilemap resource '{name}' not found.");
        byte[] pixels = TilemapFormat.Pixels(data, name);
        _tilemaps[name] = pixels;
        return pixels;
    }

    /// <summary>A Sprite resource definition.</summary>
    /// <exception cref="ArgumentException">The sprite does not exist.</exception>
    public SpriteDefinition Sprite(string name)
    {
        if (_sprites.TryGetValue(name, out SpriteDefinition? cached))
        {
            return cached;
        }

        byte[] data = _data.GetValueOrDefault((ResourceType.Sprite, name))
            ?? throw new ArgumentException($"Sprite resource '{name}' not found.");
        SpriteDefinition sprite = SpriteFormat.Decode(data, name);
        _sprites[name] = sprite;
        return sprite;
    }

    /// <summary>An Animation resource definition.</summary>
    /// <exception cref="ArgumentException">The animation does not exist.</exception>
    public AnimationDefinition Animation(string name)
    {
        if (_animations.TryGetValue(name, out AnimationDefinition? cached))
        {
            return cached;
        }

        byte[] data = _data.GetValueOrDefault((ResourceType.Animation, name))
            ?? throw new ArgumentException($"Animation resource '{name}' not found.");
        AnimationDefinition animation = AnimationFormat.Decode(data, name);
        _animations[name] = animation;
        return animation;
    }

    /// <summary>A font ready for rendering. An empty name selects the built-in system font.</summary>
    /// <exception cref="ArgumentException">The font (or its tilemap) does not exist.</exception>
    public Graphics.RuntimeFont Font(string name)
    {
        if (name.Length == 0)
        {
            return Graphics.RuntimeFont.System;
        }

        if (_fonts.TryGetValue(name, out Graphics.RuntimeFont? cached))
        {
            return cached;
        }

        byte[] data = _data.GetValueOrDefault((ResourceType.Font, name))
            ?? throw new ArgumentException($"Font resource '{name}' not found.");
        FontDefinition definition = FontFormat.Decode(data, name);
        var font = new Graphics.RuntimeFont(definition, Tilemap(definition.Tilemap));
        _fonts[name] = font;
        return font;
    }

    /// <summary>Packed 9-bit colors of a Palette resource, or null if it does not exist.</summary>
    public ushort[]? Palette(string name)
    {
        if (_palettes.TryGetValue(name, out ushort[]? cached))
        {
            return cached;
        }

        if (!_data.TryGetValue((ResourceType.Palette, name), out byte[]? data))
        {
            return null;
        }

        ushort[] colors = PaletteFormat.Decode(data, name);
        _palettes[name] = colors;
        return colors;
    }
}
