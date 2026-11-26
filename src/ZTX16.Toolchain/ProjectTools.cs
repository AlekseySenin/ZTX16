using ZTX16.Cartridge;

namespace ZTX16.Toolchain;

/// <summary>Project operations shared by the <c>ztx16c</c> command line and the ZTX16 environment (Console).</summary>
public static class ProjectTools
{
    /// <summary>Creates a project in <paramref name="directory"/> from the standard template. Returns a message.</summary>
    /// <exception cref="ProjectException">The directory already holds a project, or the name or ID is invalid.</exception>
    public static string Create(string directory, string? name = null, string? gameId = null)
    {
        name ??= Path.GetFileName(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar));
        gameId ??= DefaultGameId(name);
        Project.Create(directory, name, gameId, ProjectTemplates.GameSource, ProjectTemplates.DefaultPalette);
        return $"created project '{name}' ({gameId})";
    }

    /// <summary>A game ID derived from a project name: <c>dev.local.&lt;letters and digits&gt;</c>.</summary>
    public static string DefaultGameId(string name) =>
        "dev.local." + new string(name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()).PadRight(1, 'x');

    /// <summary>
    /// Builds a project into a cartridge file (default <c>bin/&lt;name&gt;.ztx</c> inside the project) and returns its
    /// path and a summary message.
    /// </summary>
    /// <exception cref="ProjectException">The project is invalid.</exception>
    /// <exception cref="BuildException">The code does not compile.</exception>
    /// <exception cref="TranslationException">The code uses something the VM does not support.</exception>
    public static (string Path, string Message) Build(string projectDirectory, string? output = null, string? coreDirectory = null)
    {
        var warnings = new List<string>();
        CartridgeContent cartridge = ProjectBuilder.Build(projectDirectory, coreDirectory, warnings);
        output ??= Path.Combine(Path.GetFullPath(projectDirectory), "bin", SafeFileName(cartridge.Config.Name) + CartridgeFile.Extension);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        byte[] bytes = CartridgeFile.Write(cartridge);
        File.WriteAllBytes(output, bytes);
        string summary = $"{cartridge.Config.Name} -> {output} ({bytes.Length} bytes; {cartridge.Resources.Count} resources, {cartridge.Sources.Count} sources)";
        return (output, warnings.Count == 0 ? summary : summary + Environment.NewLine + string.Join(Environment.NewLine, warnings));
    }

    /// <summary>Extracts a cartridge into a project directory. Returns a message.</summary>
    public static string Extract(string cartridgePath, string directory)
    {
        CartridgeContent content = CartridgeFile.Read(File.ReadAllBytes(cartridgePath));
        ProjectExtractor.Extract(content, directory);
        return $"extracted {content.Config.Name}";
    }

    /// <summary>A file name made from a display name (invalid characters and spaces become '_').</summary>
    public static string SafeFileName(string name) =>
        new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c).ToArray());
}
