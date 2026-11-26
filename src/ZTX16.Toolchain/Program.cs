using ZTX16.Cartridge;
using ZTX16.Toolchain;
using ZTX16.VM;

// ztx16c — the ZTX16 toolchain.
//   ztx16c new <directory> [--name <name>] [--id <game-id>]   create a project
//   ztx16c build <project> [-o <file.ztx>]                    build a project into a cartridge
//   ztx16c extract <file.ztx> <directory>                     extract a cartridge into a project
//   ztx16c <assembly.dll> [-o <file.ztxe>]                    translate a compiled assembly (cartridge build targets)
const string Usage = """
    Usage:
      ztx16c new <directory> [--name <name>] [--id <game-id>]
      ztx16c build <project-directory> [-o <file.ztx>]
      ztx16c extract <file.ztx> <directory>
      ztx16c <assembly.dll> [-o <file.ztxe>]
    """;

try
{
    return args switch
    {
        ["new", string directory, .. var rest] => New(directory, rest),
        ["build", string project] => Build(project, null),
        ["build", string project, "-o", string output] => Build(project, output),
        ["extract", string cartridge, string directory] => Extract(cartridge, directory),
        [string assembly] when assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) => Translate(assembly, Path.ChangeExtension(assembly, ".ztxe")),
        [string assembly, "-o", string output] => Translate(assembly, output),
        _ => Fail(Usage, 2),
    };
}
catch (Exception e) when (e is ProjectException or BuildException or TranslationException or CartridgeFormatException)
{
    // MSBuild-recognizable error format where a location is known.
    Console.Error.WriteLine(e is BuildException ? e.Message : $"error ZTX0001: {e.Message}");
    return 1;
}
catch (IOException e)
{
    return Fail($"ztx16c: {e.Message}", 2);
}

static int New(string directory, string[] options)
{
    string? name = null;
    string? id = null;
    for (int i = 0; i + 1 < options.Length; i += 2)
    {
        switch (options[i])
        {
            case "--name": name = options[i + 1]; break;
            case "--id": id = options[i + 1]; break;
            default: return Fail($"Unknown option '{options[i]}'.", 2);
        }
    }

    Console.WriteLine($"ztx16c: {ProjectTools.Create(directory, name, id)} in {Path.GetFullPath(directory)}");
    return 0;
}

static int Build(string project, string? output)
{
    Console.WriteLine($"ztx16c: {ProjectTools.Build(project, output).Message}");
    return 0;
}

static int Extract(string cartridge, string directory)
{
    Console.WriteLine($"ztx16c: {ProjectTools.Extract(cartridge, directory)} into {Path.GetFullPath(directory)}");
    return 0;
}

static int Translate(string input, string output)
{
    byte[] executable = IlTranslator.TranslateToExecutable(File.ReadAllBytes(input));
    File.WriteAllBytes(output, executable);
    Console.WriteLine($"ztx16c: {Path.GetFileName(input)} -> {output} ({executable.Length} bytes)");
    return 0;
}

static int Fail(string message, int code)
{
    Console.Error.WriteLine(message);
    return code;
}

namespace ZTX16.Toolchain
{
    /// <summary>Files placed in new projects.</summary>
    internal static class ProjectTemplates
    {
        public const string GameSource = """
            using ZTX16;

            /// <summary>The cartridge: created once, then Tick and Render run 60 times per second.</summary>
            public sealed class Game : ICartridge
            {
                private int x = 120;
                private int y = 120;

                public void Initialize()
                {
                    Graphics.UsePalette("default");
                    Graphics.Fill(0, 18);
                }

                public void Tick()
                {
                    if (Input.IsKeyDown(Key.Left)) x--;
                    if (Input.IsKeyDown(Key.Right)) x++;
                    if (Input.IsKeyDown(Key.Down)) y--;
                    if (Input.IsKeyDown(Key.Up)) y++;
                    if (Input.WasKeyPressed(Key.Escape)) Machine.Exit();
                }

                public void Render()
                {
                    Graphics.Clear(1);
                    Graphics.FillRect(1, x, y, 16, 16, 11);
                }
            }

            """;

        public const string DefaultPalette = """
            # ZTX16 palette: 32 entries of R G B (0..7). Entry 0 is the backdrop.
            0 0 1    # 0  backdrop
            0 0 0    # 1  black
            2 2 2    # 2  dark grey
            4 4 4    # 3  grey
            6 6 6    # 4  light grey
            7 7 7    # 5  white
            4 0 0    # 6  dark red
            7 0 0    # 7  red
            7 4 4    # 8  pink
            7 3 0    # 9  orange
            4 2 0    # 10 brown
            7 7 0    # 11 yellow
            7 7 4    # 12 pale yellow
            0 3 0    # 13 dark green
            0 6 0    # 14 green
            4 7 2    # 15 lime
            0 3 3    # 16 teal
            0 7 7    # 17 cyan
            0 0 4    # 18 navy
            0 2 7    # 19 blue
            3 5 7    # 20 sky
            3 0 4    # 21 purple
            6 0 6    # 22 magenta
            7 4 7    # 23 lavender
            5 4 3    # 24 tan
            3 2 1    # 25 dark brown
            1 2 3    # 26 slate
            2 4 5    # 27 steel
            5 6 7    # 28 ice
            6 5 2    # 29 sand
            2 1 0    # 30 umber
            1 1 1    # 31 near black

            """;
    }
}
