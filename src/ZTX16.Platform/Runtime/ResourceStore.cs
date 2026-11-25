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
