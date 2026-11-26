using ZTX16.Cartridge;
using ZTX16.Platform.Authoring;
using ZTX16.Platform.Execution;
using ZTX16.Platform.Input;
using ZTX16.Platform.Runtime;
using ZTX16.Platform.Storage;
using ZTX16.Toolchain;
using ZTX16.VM;

namespace ZTX16.Tests;

public class WorkspaceTests
{
    [Theory]
    [InlineData("relative/path")]
    [InlineData("/a/../b")]
    [InlineData("/./a")]
    [InlineData("/C:/Windows")]
    [InlineData("/a\\b")]
    [InlineData("")]
    [InlineData("/name.")]
    public void BadPathsAreRejected(string path)
    {
        using var dir = new TempDirectory();
        var workspace = new Workspace(dir.Path);
        Assert.Null(workspace.Resolve(path, out string error));
        Assert.Contains("bad path", error);
        Assert.NotEqual(string.Empty, workspace.WriteText(path, "x"));     // reported, not thrown
        Assert.Null(workspace.ReadText(path));
    }

    [Fact]
    public void FileOperationsStayInsideTheRoot()
    {
        using var dir = new TempDirectory();
        var workspace = new Workspace(Path.Combine(dir.Path, "ws"));
        Assert.Equal("", workspace.WriteText("/Game/code/Main.cs", "class A {}"));
        Assert.Equal("", workspace.CreateDirectory("/Game/assets"));
        Assert.Equal("", workspace.WriteText("/Game/readme.txt", "hi"));
        Assert.Equal("assets/\ncode/\nreadme.txt", workspace.List("/Game"));
        Assert.Equal("Game/", workspace.List("/"));
        Assert.True(workspace.IsDirectory("/Game/code"));
        Assert.Equal(2, workspace.Size("/Game/readme.txt"));

        Assert.Equal("", workspace.Copy("/Game", "/Copy"));
        Assert.Equal("class A {}", workspace.ReadText("/Copy/code/Main.cs"));
        Assert.Equal("", workspace.Move("/Copy/readme.txt", "/Copy/notes.txt"));
        Assert.Equal("the target already exists", workspace.Move("/Copy/notes.txt", "/Game/readme.txt"));
        Assert.Equal("a directory cannot move into itself", workspace.Move("/Copy", "/Copy/inside"));
        Assert.Equal("the directory is not empty", workspace.Delete("/Copy", recursive: false));
        Assert.Equal("", workspace.Delete("/Copy", recursive: true));
        Assert.Equal("the workspace root cannot be deleted", workspace.Delete("/", recursive: true));
        Assert.False(workspace.Exists("/Copy"));
        Assert.Equal("/Game/code", workspace.ToVirtual(Path.Combine(dir.Path, "ws", "Game", "code")));
        Assert.Null(workspace.ToVirtual(dir.Path));
        Assert.False(File.Exists(Path.Combine(dir.Path, "readme.txt")));
    }
}

public class AuthoringCapabilityTests
{
    [Fact]
    public void ACartridgeCannotUseAuthoringSyscalls()
    {
        var e = Assert.Throws<LinkException>(() => CartridgeCompiler.Start("""
            using ZTX16;
            using ZTX16.Authoring;
            public sealed class Game : ICartridge
            {
                public void Initialize() { Graphics.SetPixel(0, 0, 0, Workspace.List("/") == null ? 1 : 2); }
                public void Tick() { }
                public void Render() { }
            }
            """, optimize: true));
        Assert.Contains("workspace.list", e.Message);
    }
}

/// <summary>Runs the environment headless like the host loop does: tick the active app, then AfterTick.</summary>
internal sealed class EnvironmentDriver : IDisposable
{
    private static readonly Lazy<byte[]> ConsoleCartridge = new(() =>
        CartridgeFile.Write(ProjectBuilder.Build(Path.Combine(CartridgeCompiler.RepositoryRoot, "apps", "Console"), ProjectFixtures.CoreDirectory)));

    private readonly TempDirectory _root = new();

    public EnvironmentDriver()
    {
        Workspace = new Workspace(Path.Combine(_root.Path, "workspace"));
        Environment = new AppEnvironment(Workspace, new Tools(), ["console"], _ => Load(ConsoleCartridge.Value), path => Load(File.ReadAllBytes(path)), 48000);
        Environment.Start();
    }

    public Workspace Workspace { get; }

    public AppEnvironment Environment { get; }

    public AppSession Active => Environment.Active!;

    public void Tick(int count = 1)
    {
        for (int i = 0; i < count && Environment.Active is { } active; i++)
        {
            try
            {
                active.Session.RunTick(TimeSpan.Zero);
            }
            catch (VmTrapException e)
            {
                Environment.Fail(e.Message);
                continue;
            }

            Environment.AfterTick();
        }
    }

    /// <summary>Types a line into the active app and presses Enter.</summary>
    public void Type(string line)
    {
        InputSystem input = Active.Session.Input;
        input.Enqueue(InputEvent.TextTyped(line));
        Tick();
        input.Enqueue(InputEvent.KeyChange(Key.Enter, true));
        Tick();
        input.Enqueue(InputEvent.KeyChange(Key.Enter, false));
        Tick();
    }

    public void Dispose()
    {
        Environment.Shutdown();
        _root.Dispose();
    }

    private static LoadedProgram Load(byte[] cartridgeBytes)
    {
        CartridgeContent cartridge = CartridgeFile.Read(cartridgeBytes);
        return new LoadedProgram(VmProgram.Load(cartridge.Executable), new ResourceStore(cartridge.Resources), cartridge.Config.Name, new StorageSystem());
    }

    private sealed class Tools : IProjectTools
    {
        public string Create(string directory, string name) => ProjectTools.Create(directory, name);

        public (string Path, string Message) Build(string directory) => ProjectTools.Build(directory, coreDirectory: ProjectFixtures.CoreDirectory);

        public string Extract(string cartridgePath, string directory) => ProjectTools.Extract(cartridgePath, directory);

        public bool IsProject(string directory) => File.Exists(Path.Combine(directory, ProjectConfig.FileName));
    }
}

public class ConsoleEnvironmentTests
{
    [Fact]
    public void ConsoleCreatesBuildsAndRunsAProjectThenGetsControlBack()
    {
        using var env = new EnvironmentDriver();
        Assert.Equal("console", env.Active.Name);
        Assert.True(env.Active.IsSystem);

        env.Type("new Game");
        Assert.True(env.Workspace.Exists("/Game/ztx16.project.json"));

        env.Type("cd Game");
        env.Type("build");
        Assert.True(env.Workspace.Exists("/Game/bin/Game.ztx"));

        env.Type("run");
        Assert.False(env.Active.IsSystem);                  // the game is active now
        Assert.Equal("Game.ztx", env.Active.Name);
        env.Tick(5);
        Assert.False(env.Active.IsSystem);

        env.Environment.ReturnToShell();                    // F12
        env.Environment.AfterTick();
        Assert.Equal("console", env.Active.Name);

        // The template game exits on Escape: control returns to the Console that ran it.
        env.Type("run");
        AppSession game = env.Active;
        game.Session.Input.Enqueue(InputEvent.KeyChange(Key.Escape, true));
        env.Tick(2);
        Assert.Equal("console", env.Active.Name);
    }

    [Fact]
    public void FileCommandsAndScripts()
    {
        using var env = new EnvironmentDriver();
        env.Type("md tools");
        env.Type("cd tools");
        env.Workspace.WriteText("/tools/setup.zcmd", "# make a layout\nmd /out\nmd /out/a\ncopy /out/a /out/b\nren /out/b /out/c\n");
        env.Type("do setup.zcmd");
        Assert.True(env.Workspace.IsDirectory("/out/a"));
        Assert.True(env.Workspace.IsDirectory("/out/c"));
        Assert.False(env.Workspace.Exists("/out/b"));

        env.Type("rd /out");                               // not empty: refused
        Assert.True(env.Workspace.Exists("/out"));
        env.Type("rd /out /s");
        Assert.False(env.Workspace.Exists("/out"));

        env.Type("dir");
        env.Type("type setup.zcmd");
        env.Type("edit setup.zcmd");                       // no code editor yet: stays in the Console
        env.Type("nonsense command");
        Assert.Equal("console", env.Active.Name);
        env.Tick(10);
    }

    [Fact]
    public void AFailingCartridgeReturnsToTheConsole()
    {
        using var env = new EnvironmentDriver();
        env.Type("new Crash");
        env.Workspace.WriteText("/Crash/code/Game.cs", """
            using ZTX16;
            public sealed class Game : ICartridge
            {
                private int zero;
                public void Initialize() { }
                public void Tick() { Graphics.SetPixel(0, 0, 0, 1 / zero); }
                public void Render() { }
            }
            """);
        env.Type("run Crash");                              // the game fails on its first tick (inside Type)
        env.Tick(3);
        Assert.Equal("console", env.Active.Name);           // back, with the message printed by the Console
        Assert.True(env.Active.IsSystem);
    }

    [Fact]
    public void ConsoleExitEndsTheEnvironment()
    {
        using var env = new EnvironmentDriver();
        env.Type("exit");
        Assert.Null(env.Environment.Active);
    }
}
