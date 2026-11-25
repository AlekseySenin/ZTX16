using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ZTX16.Cartridge;

namespace ZTX16.Toolchain;

/// <summary>Thrown when a project fails to build; <see cref="Exception.Message"/> lists the errors.</summary>
public sealed class BuildException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BuildException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Builds a Project into a cartridge: reads the Project Config, compiles the mapped C# sources
/// (plus the ZTX16 core library) with Roslyn, translates the IL into a ZTX16 executable, and packages
/// the executable, the mapped resources, the config and the sources.
/// </summary>
public static class ProjectBuilder
{
    /// <summary>C# version used for cartridges (see the implementation notes on params spans).</summary>
    public const LanguageVersion CSharpVersion = LanguageVersion.CSharp12;

    /// <summary>Directory containing the core library sources shipped with the toolchain.</summary>
    public static string DefaultCoreDirectory => Path.Combine(AppContext.BaseDirectory, "core");

    /// <summary>Builds the project at <paramref name="projectRoot"/>.</summary>
    /// <exception cref="ProjectException">The project or a resource file is invalid.</exception>
    /// <exception cref="BuildException">The C# code does not compile.</exception>
    /// <exception cref="TranslationException">The code uses features the ZTX16 toolchain does not support.</exception>
    public static CartridgeContent Build(string projectRoot, string? coreDirectory = null)
    {
        Project project = Project.Open(projectRoot);
        if (project.Sources.Count == 0)
        {
            throw new ProjectException($"No C# sources found in '{project.Config.Directories[ProjectConfig.CodeCategory]}/'.");
        }

        byte[] il = Compile(project, coreDirectory ?? DefaultCoreDirectory);
        byte[] executable = IlTranslator.TranslateToExecutable(il);

        var resources = project.Resources
            .Select(r => new CartridgeResource(r.Type, r.Name, EncodeResource(r)))
            .ToList();
        CheckReferences(project, resources);
        var sources = project.Sources
            .Select(s => new CartridgeSource(s.RelativePath, File.ReadAllBytes(s.FullPath)))
            .ToList();

        return new CartridgeContent(project.ConfigJson, executable, resources, sources);
    }

    private static byte[] Compile(Project project, string coreDirectory)
    {
        if (!Directory.Exists(coreDirectory))
        {
            throw new BuildException($"Core library sources not found at '{coreDirectory}'.");
        }

        var parse = new CSharpParseOptions(CSharpVersion);
        IEnumerable<SyntaxTree> projectTrees = project.Sources.Select(s =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(s.FullPath), parse, path: s.RelativePath, encoding: Encoding.UTF8));
        IEnumerable<SyntaxTree> coreTrees = Directory.EnumerateFiles(coreDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(coreDirectory, p).Replace('\\', '/').Split('/').Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), parse, path: $"<core>/{Path.GetFileName(p)}", encoding: Encoding.UTF8));

        CSharpCompilation compilation = CSharpCompilation.Create(
            "Cartridge",
            projectTrees.Concat(coreTrees),
            References(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable,
                deterministic: true));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            IEnumerable<string> errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            throw new BuildException(string.Join(Environment.NewLine, errors));
        }

        return stream.ToArray();
    }

    private static MetadataReference[] References()
    {
        string[] trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return trusted
            .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll")
            .Append(typeof(SyscallAttribute).Assembly.Location)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }

    private static byte[] EncodeResource(ProjectResource resource) => resource.Type switch
    {
        ResourceType.Palette => PaletteFormat.Encode(PaletteFormat.ParseText(File.ReadAllText(resource.FullPath), resource.RelativePath)),
        ResourceType.Tilemap => TilemapFormat.FromPng(File.ReadAllBytes(resource.FullPath), resource.RelativePath),
        ResourceType.Sprite => SpriteFormat.Encode(SpriteFormat.ParseJson(File.ReadAllText(resource.FullPath), resource.RelativePath)),
        ResourceType.Font => FontFormat.Encode(FontFormat.ParseJson(File.ReadAllText(resource.FullPath), resource.RelativePath)),
        ResourceType.Animation => AnimationFormat.Encode(AnimationFormat.ParseJson(File.ReadAllText(resource.FullPath), resource.RelativePath)),
        _ => throw new ProjectException($"{resource.RelativePath}: {resource.Type} resources are not supported yet."),
    };

    /// <summary>Sprites and fonts must name Tilemap resources that exist in the project.</summary>
    private static void CheckReferences(Project project, List<CartridgeResource> resources)
    {
        var tilemaps = resources.Where(r => r.Type == ResourceType.Tilemap).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < resources.Count; i++)
        {
            string? tilemap = resources[i].Type switch
            {
                ResourceType.Sprite => SpriteFormat.Decode(resources[i].Data, resources[i].Name).Tilemap,
                ResourceType.Font => FontFormat.Decode(resources[i].Data, resources[i].Name).Tilemap,
                _ => null,
            };

            if (tilemap is not null && !tilemaps.Contains(tilemap))
            {
                throw new ProjectException($"{project.Resources[i].RelativePath}: Tilemap resource '{tilemap}' does not exist.");
            }
        }
    }
}
