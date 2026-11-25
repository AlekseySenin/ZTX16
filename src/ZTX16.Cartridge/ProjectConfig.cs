using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZTX16.Cartridge;

/// <summary>Thrown when a project, its config or one of its files is invalid.</summary>
public sealed class ProjectException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ProjectException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The Project Config: the file whose presence makes a directory a ZTX16 Project. It names the
/// project, gives the Game ID that namespaces persistent Storage, selects the source language and
/// maps directories to source/resource categories.
/// </summary>
public sealed partial class ProjectConfig
{
    /// <summary>File name of the Project Config at the project root.</summary>
    public const string FileName = "ztx16.project.json";

    /// <summary>Category key of the source-code directory.</summary>
    public const string CodeCategory = "code";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Stable identifier used as the cartridge's Storage namespace, e.g. <c>dev.example.mygame</c>.</summary>
    public string GameId { get; set; } = string.Empty;

    /// <summary>Version text, e.g. <c>1.0.0</c>.</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>Source language settings.</summary>
    public LanguageConfig Language { get; set; } = new();

    /// <summary>Category → directory relative to the project root.</summary>
    public Dictionary<string, string> Directories { get; set; } = StandardDirectories();

    /// <summary>The standard mapping new projects receive. A convention, not a platform restriction.</summary>
    public static Dictionary<string, string> StandardDirectories() => new(StringComparer.Ordinal)
    {
        [CodeCategory] = "code",
        [ResourceTypes.Category(ResourceType.Palette)] = "palettes",
        [ResourceTypes.Category(ResourceType.Tilemap)] = "tilemaps",
        [ResourceTypes.Category(ResourceType.Sprite)] = "sprites",
        [ResourceTypes.Category(ResourceType.Font)] = "fonts",
        [ResourceTypes.Category(ResourceType.Animation)] = "animations",
        [ResourceTypes.Category(ResourceType.Sample)] = "samples",
        [ResourceTypes.Category(ResourceType.Wavetable)] = "wavetables",
        [ResourceTypes.Category(ResourceType.Instrument)] = "instruments",
        [ResourceTypes.Category(ResourceType.Sequence)] = "sequences",
        [ResourceTypes.Category(ResourceType.Song)] = "songs",
        [ResourceTypes.Category(ResourceType.Level)] = "levels",
    };

    /// <summary>Creates the standard config for a new project.</summary>
    public static ProjectConfig CreateDefault(string name, string gameId) => new() { Name = name, GameId = gameId };

    /// <summary>Parses and validates config JSON.</summary>
    /// <exception cref="ProjectException">The JSON is malformed or the config is invalid.</exception>
    public static ProjectConfig Parse(string json)
    {
        ProjectConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ProjectConfig>(json, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new ProjectException($"{FileName}: invalid JSON: {e.Message}", e);
        }

        if (config is null)
        {
            throw new ProjectException($"{FileName}: empty config.");
        }

        config.Validate();
        return config;
    }

    /// <summary>Serializes the config.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Validates the config.</summary>
    /// <exception cref="ProjectException">A required value is missing or invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ProjectException($"{FileName}: 'name' is required.");
        }

        if (!GameIdPattern().IsMatch(GameId ?? string.Empty))
        {
            throw new ProjectException($"{FileName}: 'gameId' must be lower-case letters, digits, '.', '-' or '_' (for example 'dev.example.mygame').");
        }

        if (Language?.Name != LanguageConfig.CSharp)
        {
            throw new ProjectException($"{FileName}: unsupported language '{Language?.Name}'. Supported: '{LanguageConfig.CSharp}'.");
        }

        Directories ??= StandardDirectories();
        if (!Directories.ContainsKey(CodeCategory))
        {
            throw new ProjectException($"{FileName}: the '{CodeCategory}' directory mapping is required.");
        }

        foreach ((string category, string directory) in Directories)
        {
            if (category != CodeCategory && ResourceTypes.FromCategory(category) is null)
            {
                throw new ProjectException($"{FileName}: unknown directory category '{category}'.");
            }

            if (string.IsNullOrWhiteSpace(directory) || Path.IsPathRooted(directory)
                || directory.Replace('\\', '/').Split('/').Any(part => part == ".."))
            {
                throw new ProjectException($"{FileName}: directory '{directory}' for '{category}' must be a relative path inside the project.");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex GameIdPattern();
}

/// <summary>Source language settings.</summary>
public sealed class LanguageConfig
{
    /// <summary>The C# language identifier.</summary>
    public const string CSharp = "csharp";

    /// <summary>Language identifier.</summary>
    public string Name { get; set; } = CSharp;
}

/// <summary>Names and file extensions of resource types in projects.</summary>
public static class ResourceTypes
{
    /// <summary>Config category key of a resource type, e.g. <c>palettes</c>.</summary>
    public static string Category(ResourceType type) => type switch
    {
        ResourceType.Palette => "palettes",
        ResourceType.Tilemap => "tilemaps",
        ResourceType.Sprite => "sprites",
        ResourceType.Font => "fonts",
        ResourceType.Animation => "animations",
        ResourceType.Sample => "samples",
        ResourceType.Wavetable => "wavetables",
        ResourceType.Instrument => "instruments",
        ResourceType.Sequence => "sequences",
        ResourceType.Song => "songs",
        ResourceType.Level => "levels",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The resource type of a config category, or null.</summary>
    public static ResourceType? FromCategory(string category) =>
        Enum.GetValues<ResourceType>().Select(t => (ResourceType?)t).FirstOrDefault(t => Category(t!.Value) == category);

    /// <summary>
    /// Project-side file extension of a resource type, or null while the type has no project format yet
    /// (files in its directory are then ignored like any unrelated file).
    /// </summary>
    public static string? Extension(ResourceType type) => type switch
    {
        ResourceType.Palette => ".pal",
        _ => null,
    };
}
