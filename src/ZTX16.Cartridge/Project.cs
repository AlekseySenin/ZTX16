namespace ZTX16.Cartridge;

/// <summary>A source file of a project.</summary>
/// <param name="RelativePath">Path relative to the project root, with '/' separators.</param>
/// <param name="FullPath">Absolute path on disk.</param>
public sealed record ProjectSource(string RelativePath, string FullPath);

/// <summary>A resource file of a project.</summary>
public sealed record ProjectResource(ResourceType Type, string Name, string RelativePath, string FullPath);

/// <summary>
/// An opened Project: its config plus the source and resource files found through the config's
/// directory mapping. Files outside mapped directories, or with unrecognized extensions, are
/// ignored and never reach a cartridge.
/// </summary>
public sealed class Project
{
    private Project(string root, ProjectConfig config, string configJson, List<ProjectSource> sources, List<ProjectResource> resources)
    {
        Root = root;
        Config = config;
        ConfigJson = configJson;
        Sources = sources;
        Resources = resources;
    }

    /// <summary>Absolute project root.</summary>
    public string Root { get; }

    /// <summary>The parsed config.</summary>
    public ProjectConfig Config { get; }

    /// <summary>The config file text as written by the developer (packaged verbatim).</summary>
    public string ConfigJson { get; }

    /// <summary>Source files of the configured language, in path order.</summary>
    public IReadOnlyList<ProjectSource> Sources { get; }

    /// <summary>Resource files, in type then name order.</summary>
    public IReadOnlyList<ProjectResource> Resources { get; }

    /// <summary>Opens the project at <paramref name="root"/>.</summary>
    /// <exception cref="ProjectException">The directory is not a valid ZTX16 project.</exception>
    public static Project Open(string root)
    {
        root = Path.GetFullPath(root);
        string configPath = Path.Combine(root, ProjectConfig.FileName);
        if (!File.Exists(configPath))
        {
            throw new ProjectException($"'{root}' is not a ZTX16 project: {ProjectConfig.FileName} not found.");
        }

        string configJson = File.ReadAllText(configPath);
        ProjectConfig config = ProjectConfig.Parse(configJson);

        var sources = FilesIn(root, config.Directories[ProjectConfig.CodeCategory], ".cs")
            .Select(path => new ProjectSource(Relative(root, path), path))
            .ToList();

        var resources = new List<ProjectResource>();
        foreach ((string category, string directory) in config.Directories)
        {
            if (ResourceTypes.FromCategory(category) is not { } type || ResourceTypes.Extension(type) is not { } extension)
            {
                continue;
            }

            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in FilesIn(root, directory, extension))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                string relative = Relative(root, path);
                if (names.TryGetValue(name, out string? other))
                {
                    throw new ProjectException($"Two {type} resources are named '{name}': '{other}' and '{relative}'. Names must be unique per type.");
                }

                names[name] = relative;
                resources.Add(new ProjectResource(type, name, relative, path));
            }
        }

        resources.Sort((a, b) => a.Type != b.Type ? a.Type.CompareTo(b.Type) : string.CompareOrdinal(a.Name, b.Name));
        return new Project(root, config, configJson, sources, resources);
    }

    /// <summary>Creates a new project with the standard config and structure.</summary>
    /// <exception cref="ProjectException">The target directory exists and is not empty.</exception>
    public static void Create(string root, string name, string gameId, string gameSource, string paletteText)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new ProjectException($"'{root}' is not empty.");
        }

        ProjectConfig config = ProjectConfig.CreateDefault(name, gameId);
        config.Validate();
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ProjectConfig.FileName), config.ToJson());

        string code = Path.Combine(root, config.Directories[ProjectConfig.CodeCategory]);
        Directory.CreateDirectory(code);
        File.WriteAllText(Path.Combine(code, "Game.cs"), gameSource);

        string palettes = Path.Combine(root, config.Directories[ResourceTypes.Category(ResourceType.Palette)]);
        Directory.CreateDirectory(palettes);
        File.WriteAllText(Path.Combine(palettes, "default.pal"), paletteText);

        // Other resource directories are created on demand.
    }

    // Missing directories are normal: they are created on demand.
    private static IEnumerable<string> FilesIn(string root, string directory, string extension)
    {
        string full = Path.GetFullPath(Path.Combine(root, directory));
        if (!Directory.Exists(full))
        {
            return [];
        }

        return Directory.EnumerateFiles(full, "*" + extension, SearchOption.AllDirectories)
            .Where(p => string.Equals(Path.GetExtension(p), extension, StringComparison.OrdinalIgnoreCase))
            .Where(p => !Relative(full, p).Split('/').Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
