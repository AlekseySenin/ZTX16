namespace ZTX16.Cartridge;

/// <summary>Extracts a cartridge back into an ordinary, editable Project.</summary>
public static class ProjectExtractor
{
    /// <summary>
    /// Recreates the Project Config, the directory structure it describes, the sources and the
    /// project-side representation of every resource in <paramref name="root"/>.
    /// </summary>
    /// <exception cref="ProjectException">The target is not empty, or the cartridge contains unsafe paths or unextractable resources.</exception>
    public static void Extract(CartridgeContent cartridge, string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new ProjectException($"'{root}' is not empty.");
        }

        ProjectConfig config = cartridge.Config;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ProjectConfig.FileName), cartridge.ConfigJson);

        foreach (CartridgeSource source in cartridge.Sources)
        {
            WriteFile(root, source.Path, source.Data);
        }

        foreach (CartridgeResource resource in cartridge.Resources)
        {
            string directory = config.Directories.GetValueOrDefault(ResourceTypes.Category(resource.Type))
                ?? throw new ProjectException($"The config has no directory for {resource.Type} resources.");
            string extension = ResourceTypes.Extension(resource.Type)
                ?? throw new ProjectException($"{resource.Type} resources cannot be extracted yet.");
            if (resource.Name.Length == 0 || resource.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || resource.Name is "." or "..")
            {
                throw new ProjectException($"Resource name '{resource.Name}' is not a valid file name.");
            }

            byte[] data = resource.Type switch
            {
                ResourceType.Palette => System.Text.Encoding.UTF8.GetBytes(PaletteFormat.FormatText(PaletteFormat.Decode(resource.Data, resource.Name))),
                ResourceType.Tilemap => TilemapFormat.ToPng(resource.Data, resource.Name),
                ResourceType.Sprite => System.Text.Encoding.UTF8.GetBytes(SpriteFormat.FormatJson(SpriteFormat.Decode(resource.Data, resource.Name))),
                ResourceType.Font => System.Text.Encoding.UTF8.GetBytes(FontFormat.FormatJson(FontFormat.Decode(resource.Data, resource.Name))),
                ResourceType.Animation => System.Text.Encoding.UTF8.GetBytes(AnimationFormat.FormatJson(AnimationFormat.Decode(resource.Data, resource.Name))),
                ResourceType.Level => System.Text.Encoding.UTF8.GetBytes(LevelFormat.FormatJson(LevelFormat.Decode(resource.Data, resource.Name))),
                _ => throw new ProjectException($"{resource.Type} resources cannot be extracted yet."),
            };

            WriteFile(root, $"{directory}/{resource.Name}{extension}", data);
        }
    }

    // Paths come from the cartridge: never allow them to escape the target directory.
    private static void WriteFile(string root, string relativePath, byte[] data)
    {
        string full = Path.GetFullPath(Path.Combine(root, relativePath));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relativePath) || !full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ProjectException($"Cartridge path '{relativePath}' points outside the project.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
    }
}
