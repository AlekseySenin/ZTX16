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
    public static CartridgeContent Build(string projectRoot, string? coreDirectory = null) => Build(projectRoot, coreDirectory, null);

    /// <summary>Like <see cref="Build(string, string?)"/>, also collecting compiler warnings in the project's own code.</summary>
    public static CartridgeContent Build(string projectRoot, string? coreDirectory, List<string>? warnings)
    {
        Project project = Project.Open(projectRoot);
        if (project.Sources.Count == 0)
        {
            throw new ProjectException($"No C# sources found in '{project.Config.Directories[ProjectConfig.CodeCategory]}/'.");
        }

        byte[] il = Compile(project, coreDirectory ?? DefaultCoreDirectory, warnings);
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

    private static byte[] Compile(Project project, string coreDirectory, List<string>? warnings)
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
        IEnumerable<string> projectWarnings = result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Warning && !d.IsSuppressed && d.Location.SourceTree?.FilePath.StartsWith("<core>", StringComparison.Ordinal) == false)
            .Select(d => d.ToString());
        warnings?.AddRange(projectWarnings);
        if (!result.Success)
        {
            IEnumerable<string> errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            throw new BuildException(string.Join(Environment.NewLine, errors.Concat(projectWarnings)));
        }

        return stream.ToArray();
    }

    private static MetadataReference[] References()
    {
        // In a single-file host the bundled assemblies have no location; tools/publish.ps1 then ships copies in ref/.
        string refDirectory = Path.Combine(AppContext.BaseDirectory, "ref");
#pragma warning disable IL3000 // Empty in a single-file host: handled just below.
        string sdk = typeof(SyscallAttribute).Assembly.Location;
#pragma warning restore IL3000
        if (string.IsNullOrEmpty(sdk) && Directory.Exists(refDirectory))
        {
            return Directory.GetFiles(refDirectory, "*.dll").Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
        }

        string[] trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty).Split(Path.PathSeparator);
        return trusted
            .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll")
            .Append(sdk)
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
        ResourceType.Level => LevelFormat.Encode(LevelFormat.ParseJson(File.ReadAllText(resource.FullPath), resource.RelativePath)),
        ResourceType.Sample => SampleFormat.Encode(SampleFormat.FromWav(File.ReadAllBytes(resource.FullPath), resource.RelativePath)),
        ResourceType.Wavetable => WavetableFormat.Encode(WavetableFormat.ParseJson(File.ReadAllText(resource.FullPath), resource.RelativePath)),
        ResourceType.Instrument => InstrumentFormat.Encode(InstrumentFormat.ParseJson(File.ReadAllText(resource.FullPath), resource.RelativePath)),
        ResourceType.Sequence => SequenceFormat.Encode(SequenceFormat.ParseJson(File.ReadAllText(resource.FullPath), resource.RelativePath)),
        ResourceType.Song => SongFormat.Encode(SongFormat.ParseJson(File.ReadAllText(resource.FullPath), resource.RelativePath)),
        _ => throw new ProjectException($"{resource.RelativePath}: {resource.Type} resources are not supported yet."),
    };

    /// <summary>Sprites, fonts and levels must name Tilemap (and Sprite) resources that exist in the project.</summary>
    private static void CheckReferences(Project project, List<CartridgeResource> resources)
    {
        var tilemaps = resources.Where(r => r.Type == ResourceType.Tilemap).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var sprites = resources.Where(r => r.Type == ResourceType.Sprite).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> Names(ResourceType type) => resources.Where(r => r.Type == type).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> samples = Names(ResourceType.Sample), wavetables = Names(ResourceType.Wavetable);
        Dictionary<string, InstrumentDefinition> instruments = resources.Where(r => r.Type == ResourceType.Instrument)
            .ToDictionary(r => r.Name, r => InstrumentFormat.Decode(r.Data, r.Name), StringComparer.Ordinal);
        Dictionary<string, SequenceDefinition> sequences = resources.Where(r => r.Type == ResourceType.Sequence)
            .ToDictionary(r => r.Name, r => SequenceFormat.Decode(r.Data, r.Name), StringComparer.Ordinal);
        for (int i = 0; i < resources.Count; i++)
        {
            string file = project.Resources[i].RelativePath;
            switch (resources[i].Type)
            {
                case ResourceType.Instrument:
                {
                    InstrumentDefinition instrument = instruments[resources[i].Name];
                    HashSet<string>? pool = instrument.Source switch
                    {
                        InstrumentSourceKind.Sample => samples,
                        InstrumentSourceKind.Wavetable => wavetables,
                        _ => null,
                    };
                    if (pool is not null && !pool.Contains(instrument.Resource!))
                    {
                        throw new ProjectException($"{file}: {instrument.Source} resource '{instrument.Resource}' does not exist.");
                    }

                    continue;
                }

                case ResourceType.Sequence when !instruments.ContainsKey(sequences[resources[i].Name].Instrument):
                    throw new ProjectException($"{file}: Instrument resource '{sequences[resources[i].Name].Instrument}' does not exist.");
                case ResourceType.Song:
                    foreach (SongEvent e in SongFormat.Decode(resources[i].Data, resources[i].Name).Events)
                    {
                        if (e.Kind != 0 || e.Name is null)
                        {
                            continue;
                        }

                        if (!sequences.TryGetValue(e.Name, out SequenceDefinition? sequence))
                        {
                            throw new ProjectException($"{file}: Sequence resource '{e.Name}' does not exist.");
                        }

                        bool synth = instruments[sequence.Instrument].Source == InstrumentSourceKind.Synth;
                        if (synth != (e.P0 < 6))
                        {
                            throw new ProjectException($"{file}: sequence '{e.Name}' cannot play on channel {e.P0} "
                                + "(Synth instruments use channels 0..5, Sample and Wavetable instruments 6..7).");
                        }
                    }

                    continue;
            }

            if (resources[i].Type == ResourceType.Level)
            {
                foreach (LevelLayer layer in LevelFormat.Decode(resources[i].Data, resources[i].Name).Layers)
                {
                    if (layer.Tilemap is not null && !tilemaps.Contains(layer.Tilemap))
                    {
                        throw new ProjectException($"{file}: layer '{layer.Name}': Tilemap resource '{layer.Tilemap}' does not exist.");
                    }

                    foreach (LevelActor actor in layer.Actors)
                    {
                        if (actor.Sprite is not null && !sprites.Contains(actor.Sprite))
                        {
                            throw new ProjectException($"{file}: actor '{actor.Id}': Sprite resource '{actor.Sprite}' does not exist.");
                        }
                    }
                }

                continue;
            }

            string? tilemap = resources[i].Type switch
            {
                ResourceType.Sprite => SpriteFormat.Decode(resources[i].Data, resources[i].Name).Tilemap,
                ResourceType.Font => FontFormat.Decode(resources[i].Data, resources[i].Name).Tilemap,
                _ => null,
            };

            if (tilemap is not null && !tilemaps.Contains(tilemap))
            {
                throw new ProjectException($"{file}: Tilemap resource '{tilemap}' does not exist.");
            }
        }
    }
}
