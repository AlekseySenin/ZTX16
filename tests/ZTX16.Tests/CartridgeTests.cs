using ZTX16.Cartridge;
using ZTX16.Platform.Execution;
using ZTX16.Platform.Graphics;
using ZTX16.Platform.Runtime;
using ZTX16.Toolchain;
using ZTX16.VM;

namespace ZTX16.Tests;

/// <summary>A temporary directory deleted after the test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ztx16-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relativePath, string content)
    {
        string full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

internal static class ProjectFixtures
{
    public static string CoreDirectory => System.IO.Path.Combine(CartridgeCompiler.RepositoryRoot, "src", "ZTX16.Core");

    public static string Palette(int r, int g, int b)
    {
        var lines = new List<string> { "# test palette" };
        for (int i = 0; i < 32; i++)
        {
            lines.Add($"{(r + i) % 8} {(g + i) % 8} {(b + i) % 8}  # {i}");
        }

        return string.Join("\n", lines);
    }

    public const string Config = """
        {
          // comments are allowed
          "name": "Test Game",
          "gameId": "dev.test.game",
          "version": "0.1.0",
          "language": { "name": "csharp" },
          "directories": { "code": "code", "palettes": "art/palettes" }
        }
        """;

    public const string Game = """
        using ZTX16;
        public sealed class Game : ICartridge
        {
            public void Initialize()
            {
                int count = Resources.Count(ResourceType.Palette);
                Graphics.UsePalette(Resources.Name(ResourceType.Palette, count - 1));
                Graphics.FillRect(0, 0, 0, 1, 1, count);
                Graphics.FillRect(0, 1, 0, 1, 1, Resources.Name(ResourceType.Palette, 0).Length);
                Graphics.FillRect(0, 2, 0, 1, 1, Resources.Exists(ResourceType.Palette, "warm") ? 1 : 2);
                Graphics.FillRect(0, 3, 0, 1, 1, Resources.Exists(ResourceType.Palette, "missing") ? 1 : 2);
            }
            public void Tick() { if (Input.WasKeyPressed(Key.Space)) Graphics.UsePalette("missing"); }
            public void Render() { }
        }
        """;

    public static TempDirectory CreateProject()
    {
        var dir = new TempDirectory();
        dir.Write(ProjectConfig.FileName, Config);
        dir.Write("code/Game.cs", Game);
        dir.Write("art/palettes/cold.pal", Palette(0, 1, 2));
        dir.Write("art/palettes/nested/warm.pal", Palette(7, 3, 1));
        dir.Write("README.md", "unrelated files are ignored");
        dir.Write("art/palettes/notes.txt", "not a palette");
        dir.Write("code/obj/Generated.cs", "this is not valid C# and must be ignored");
        return dir;
    }
}

public class ProjectConfigTests
{
    [Fact]
    public void ParsesCommentsAndCustomMappings()
    {
        ProjectConfig config = ProjectConfig.Parse(ProjectFixtures.Config);
        Assert.Equal("Test Game", config.Name);
        Assert.Equal("dev.test.game", config.GameId);
        Assert.Equal("art/palettes", config.Directories["palettes"]);
    }

    [Theory]
    [InlineData("""{ "name": "x", "gameId": "Bad Id", "directories": { "code": "code" } }""", "gameId")]
    [InlineData("""{ "name": "", "gameId": "a.b", "directories": { "code": "code" } }""", "name")]
    [InlineData("""{ "name": "x", "gameId": "a.b", "language": { "name": "lua" }, "directories": { "code": "code" } }""", "language")]
    [InlineData("""{ "name": "x", "gameId": "a.b", "directories": { "palettes": "p" } }""", "'code'")]
    [InlineData("""{ "name": "x", "gameId": "a.b", "directories": { "code": "code", "textures": "t" } }""", "unknown directory category")]
    [InlineData("""{ "name": "x", "gameId": "a.b", "directories": { "code": "../outside" } }""", "inside the project")]
    [InlineData("""{ not json""", "invalid JSON")]
    public void RejectsInvalidConfigs(string json, string expected)
    {
        var e = Assert.Throws<ProjectException>(() => ProjectConfig.Parse(json));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void DefaultConfigRoundTrips()
    {
        ProjectConfig config = ProjectConfig.CreateDefault("My Game", "dev.me.mygame");
        ProjectConfig parsed = ProjectConfig.Parse(config.ToJson());
        Assert.Equal(config.Directories, parsed.Directories);
        Assert.Equal("code", parsed.Directories["code"]);
    }
}

public class PaletteFormatTests
{
    [Fact]
    public void TextAndBinaryRoundTrip()
    {
        ushort[] colors = PaletteFormat.ParseText(ProjectFixtures.Palette(1, 2, 3), "test.pal");
        Assert.Equal((ushort)((1 << 6) | (2 << 3) | 3), colors[0]);
        Assert.Equal(colors, PaletteFormat.ParseText(PaletteFormat.FormatText(colors), "again.pal"));
        Assert.Equal(colors, PaletteFormat.Decode(PaletteFormat.Encode(colors), "binary"));
    }

    [Theory]
    [InlineData("1 2", "expected 'R G B'")]
    [InlineData("1 2 8", "0..7")]
    [InlineData("1 2 x", "0..7")]
    public void ReportsBadLinesWithLineNumbers(string badLine, string expected)
    {
        string text = ProjectFixtures.Palette(0, 0, 0) + "\n" + badLine;
        var e = Assert.Throws<ProjectException>(() => PaletteFormat.ParseText(text, "bad.pal"));
        Assert.Contains("bad.pal(34)", e.Message);
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void RequiresExactly32Colors()
    {
        var e = Assert.Throws<ProjectException>(() => PaletteFormat.ParseText("1 1 1\n2 2 2", "short.pal"));
        Assert.Contains("found 2", e.Message);
    }
}

public class ProjectTests
{
    [Fact]
    public void DiscoversMappedFilesAndIgnoresEverythingElse()
    {
        using TempDirectory dir = ProjectFixtures.CreateProject();
        Project project = Project.Open(dir.Path);

        Assert.Equal(["code/Game.cs"], project.Sources.Select(s => s.RelativePath));
        Assert.Equal(["cold", "warm"], project.Resources.Select(r => r.Name));
        Assert.All(project.Resources, r => Assert.Equal(ResourceType.Palette, r.Type));
        Assert.Equal("art/palettes/nested/warm.pal", project.Resources[1].RelativePath);
    }

    [Fact]
    public void DirectoryWithoutConfigIsNotAProject()
    {
        using var dir = new TempDirectory();
        var e = Assert.Throws<ProjectException>(() => Project.Open(dir.Path));
        Assert.Contains(ProjectConfig.FileName, e.Message);
    }

    [Fact]
    public void DuplicateResourceNamesAreRejected()
    {
        using TempDirectory dir = ProjectFixtures.CreateProject();
        dir.Write("art/palettes/other/cold.pal", ProjectFixtures.Palette(0, 0, 0));
        var e = Assert.Throws<ProjectException>(() => Project.Open(dir.Path));
        Assert.Contains("'cold'", e.Message);
    }

    [Fact]
    public void MissingResourceDirectoriesAreNormal()
    {
        using var dir = new TempDirectory();
        dir.Write(ProjectConfig.FileName, ProjectFixtures.Config);
        dir.Write("code/Game.cs", ProjectFixtures.Game);
        Assert.Empty(Project.Open(dir.Path).Resources);
    }
}

public class BuildAndExtractTests
{
    [Fact]
    public void BuildsPackagesRunsAndExtracts()
    {
        using TempDirectory dir = ProjectFixtures.CreateProject();
        CartridgeContent built = ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory);

        // Packaging round trip.
        byte[] bytes = CartridgeFile.Write(built);
        CartridgeContent loaded = CartridgeFile.Read(bytes);
        Assert.Equal(built.Executable, loaded.Executable);
        Assert.Equal(["cold", "warm"], loaded.Resources.Select(r => r.Name));
        Assert.Equal(["code/Game.cs"], loaded.Sources.Select(s => s.Path));
        Assert.Equal("dev.test.game", loaded.Config.GameId);

        // Running: resources are visible to cartridge code through the Resources API.
        var session = new RuntimeSession(VmProgram.Load(loaded.Executable), 48000, new ResourceStore(loaded.Resources));
        session.Initialize();
        GraphicsLayer layer = session.Graphics.Layers[0];
        Assert.Equal(2, layer.GetPixel(0, 0));                 // Count
        Assert.Equal(4, layer.GetPixel(1, 0));                 // "cold".Length
        Assert.Equal(1, layer.GetPixel(2, 0));                 // Exists("warm")
        Assert.Equal(2, layer.GetPixel(3, 0));                 // !Exists("missing")
        Assert.Equal(new Color(7, 3, 1), session.Graphics.Palette.GetColor(0)); // UsePalette("warm")
        Assert.Equal(new Color(0, 4, 2), session.Graphics.Palette.GetColor(1));

        // A missing palette is a cartridge failure with a clear message.
        session.Input.Enqueue(Platform.Input.InputEvent.KeyChange(Key.Space, true));
        var trap = Assert.Throws<VmTrapException>(() => session.RunTick(TimeSpan.Zero));
        Assert.Contains("Palette resource 'missing' not found", trap.Message);

        // Extraction recreates an ordinary project that builds to the same executable.
        using var extracted = new TempDirectory();
        Directory.Delete(extracted.Path);
        ProjectExtractor.Extract(loaded, extracted.Path);
        Assert.True(File.Exists(Path.Combine(extracted.Path, ProjectConfig.FileName)));
        Assert.Equal(ProjectFixtures.Game, File.ReadAllText(Path.Combine(extracted.Path, "code", "Game.cs")));
        Assert.True(File.Exists(Path.Combine(extracted.Path, "art", "palettes", "warm.pal")));

        CartridgeContent rebuilt = ProjectBuilder.Build(extracted.Path, ProjectFixtures.CoreDirectory);
        Assert.Equal(built.Executable, rebuilt.Executable);
        Assert.Equal(built.Resources.Select(r => r.Data), rebuilt.Resources.Select(r => r.Data));
    }

    [Fact]
    public void CompileErrorsNameFileAndLine()
    {
        using TempDirectory dir = ProjectFixtures.CreateProject();
        dir.Write("code/Broken.cs", "public class Broken { int x = ; }");
        var e = Assert.Throws<BuildException>(() => ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory));
        Assert.Contains("code/Broken.cs(1,", e.Message);
    }

    [Fact]
    public void InvalidResourceFilesFailTheBuild()
    {
        using TempDirectory dir = ProjectFixtures.CreateProject();
        dir.Write("art/palettes/bad.pal", "1 2 3");
        var e = Assert.Throws<ProjectException>(() => ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory));
        Assert.Contains("art/palettes/bad.pal", e.Message);
    }

    [Fact]
    public void ExtractionRejectsPathsOutsideTheProject()
    {
        var evil = new CartridgeContent(ProjectFixtures.Config, [], [], [new CartridgeSource("../escaped.cs", [1, 2, 3])]);
        using var dir = new TempDirectory();
        string target = Path.Combine(dir.Path, "project");
        var e = Assert.Throws<ProjectException>(() => ProjectExtractor.Extract(evil, target));
        Assert.Contains("outside the project", e.Message);
        Assert.False(File.Exists(Path.Combine(dir.Path, "escaped.cs")));
    }

    [Fact]
    public void ExtractionRefusesNonEmptyDirectories()
    {
        using TempDirectory dir = ProjectFixtures.CreateProject();
        CartridgeContent built = ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory);
        Assert.Throws<ProjectException>(() => ProjectExtractor.Extract(built, dir.Path));
    }

    [Fact]
    public void CorruptCartridgesAreRejected()
    {
        Assert.Throws<CartridgeFormatException>(() => CartridgeFile.Read([1, 2, 3, 4, 5, 6]));
        byte[] good = CartridgeFile.Write(new CartridgeContent(ProjectFixtures.Config, [9], [], []));
        Assert.Throws<CartridgeFormatException>(() => CartridgeFile.Read(good[..^4]));
    }

    [Fact]
    public void NewProjectTemplateBuilds()
    {
        using var dir = new TempDirectory();
        string root = Path.Combine(dir.Path, "fresh");
        Project.Create(root, "Fresh", "dev.test.fresh", ProjectTemplates.GameSource, ProjectTemplates.DefaultPalette);

        CartridgeContent built = ProjectBuilder.Build(root, ProjectFixtures.CoreDirectory);
        var session = new RuntimeSession(VmProgram.Load(built.Executable), 48000, new ResourceStore(built.Resources));
        session.Initialize();
        session.RunTick(TimeSpan.Zero);
        Assert.Equal(11, session.Graphics.Layers[1].GetPixel(120, 120));
    }
}
