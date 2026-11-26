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
    private static readonly Lazy<byte[]> GraphicsCartridge = new(() =>
        CartridgeFile.Write(ProjectBuilder.Build(Path.Combine(CartridgeCompiler.RepositoryRoot, "apps", "Graphics"), ProjectFixtures.CoreDirectory)));

    private static readonly Lazy<byte[]> CodeCartridge = new(() =>
        CartridgeFile.Write(ProjectBuilder.Build(Path.Combine(CartridgeCompiler.RepositoryRoot, "apps", "Code"), ProjectFixtures.CoreDirectory)));

    private static readonly Lazy<byte[]> ConsoleCartridge = new(() =>
        CartridgeFile.Write(ProjectBuilder.Build(Path.Combine(CartridgeCompiler.RepositoryRoot, "apps", "Console"), ProjectFixtures.CoreDirectory)));

    private readonly TempDirectory _root = new();

    public EnvironmentDriver()
    {
        Workspace = new Workspace(Path.Combine(_root.Path, "workspace"));
        Environment = new AppEnvironment(Workspace, new Tools(), ["code", "console", "graphics"],
            name => Load(name == "code" ? CodeCartridge.Value : name == "graphics" ? GraphicsCartridge.Value : ConsoleCartridge.Value),
            path => Load(File.ReadAllBytes(path)), 48000);
        Environment.Dialogs = Dialogs;
        Environment.CodeAnalysis = new TestCodeAnalysis();
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

    /// <summary>Types text into the active app (no Enter).</summary>
    public void Text(string text)
    {
        Active.Session.Input.Enqueue(InputEvent.TextTyped(text));
        Tick();
    }

    /// <summary>Presses and releases a key, optionally with Ctrl and/or Shift held.</summary>
    public void Press(Key key, bool ctrl = false, bool shift = false)
    {
        InputSystem input = Active.Session.Input;
        if (ctrl)
        {
            input.Enqueue(InputEvent.KeyChange(Key.LeftControl, true));
        }

        if (shift)
        {
            input.Enqueue(InputEvent.KeyChange(Key.LeftShift, true));
        }

        input.Enqueue(InputEvent.KeyChange(key, true));
        Tick();
        input = Active.Session.Input;
        input.Enqueue(InputEvent.KeyChange(key, false));
        input.Enqueue(InputEvent.KeyChange(Key.LeftControl, false));
        input.Enqueue(InputEvent.KeyChange(Key.LeftShift, false));
        Tick();
    }

    /// <summary>Ticks (letting background work finish) until <paramref name="done"/> holds; false on timeout.</summary>
    public bool TickUntil(Func<bool> done, int maxTicks = 400)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            if (done())
            {
                return true;
            }

            Thread.Sleep(5);
            Tick();
        }

        return done();
    }

    private sealed class TestCodeAnalysis : ICodeAnalysis
    {
        public CodeAnalysisInfo Analyze(string projectDirectory, string file, string text)
        {
            CodeAnalysisResult r = CodeIntelligence.Analyze(projectDirectory, file, text, ProjectFixtures.CoreDirectory);
            return new CodeAnalysisInfo(
                r.Problems.Select(p => new CodeProblemInfo(p.Line, p.Column, p.EndLine, p.EndColumn, p.IsError, p.Id, p.Message)).ToList(),
                r.Colors.Select(c => new CodeColorInfo(c.Line, c.Column, c.Length, (int)c.Kind)).ToList());
        }

        public CodeCompletionInfo Complete(string projectDirectory, string file, string text, int line, int column)
        {
            CodeCompletionResult r = CodeIntelligence.Complete(projectDirectory, file, text, line, column, ProjectFixtures.CoreDirectory);
            return new CodeCompletionInfo(r.StartColumn, r.Items.Select(i => new CodeCompletionEntry(i.Label, (int)i.Kind, i.Detail)).ToList());
        }
    }

    /// <summary>Answers host file dialogs with a preset path (null = cancelled).</summary>
    public FakeDialogs Dialogs { get; } = new();

    /// <summary>Clicks at (x, y) with a mouse button (press and release on separate ticks).</summary>
    public void Click(int x, int y, MouseButton button = MouseButton.Left)
    {
        InputSystem input = Active.Session.Input;
        input.Enqueue(InputEvent.MouseMoved(x, y));
        input.Enqueue(InputEvent.MouseButtonChange(button, true));
        Tick();
        input = Active.Session.Input;
        input.Enqueue(InputEvent.MouseButtonChange(button, false));
        Tick();
    }

    /// <summary>Drags with the left button from one point to another through the given steps.</summary>
    public void Drag(int x0, int y0, int x1, int y1, int steps = 4)
    {
        InputSystem input = Active.Session.Input;
        input.Enqueue(InputEvent.MouseMoved(x0, y0));
        input.Enqueue(InputEvent.MouseButtonChange(MouseButton.Left, true));
        Tick();
        for (int i = 1; i <= steps; i++)
        {
            input.Enqueue(InputEvent.MouseMoved(x0 + ((x1 - x0) * i / steps), y0 + ((y1 - y0) * i / steps)));
            Tick();
        }

        input.Enqueue(InputEvent.MouseButtonChange(MouseButton.Left, false));
        Tick();
    }

    public sealed class FakeDialogs : IHostDialogs
    {
        public string? Answer { get; set; }

        public void Open(string[] extensions, Action<string?> done) => done(Answer);

        public void Save(string defaultName, string[] extensions, Action<string?> done) => done(Answer);
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
        env.Type("edit setup.zcmd");                       // opens the code editor
        Assert.Equal("code", env.Active.Name);
        env.Press(Key.Escape);                             // back to the Console
        Assert.Equal("console", env.Active.Name);
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

public class CodeEditorTests
{
    private static EnvironmentDriver StartWithProject()
    {
        var env = new EnvironmentDriver();
        env.Type("new Game");
        env.Type("edit /Game/code/Game.cs");
        Assert.Equal("code", env.Active.Name);
        return env;
    }

    private static int Pixel(EnvironmentDriver env, int x, int y) => env.Active.Session.Graphics.Layers[0].GetPixel(x, y);

    [Fact]
    public void TypesSavesAndCopies()
    {
        using EnvironmentDriver env = StartWithProject();
        string original = env.Workspace.ReadText("/Game/code/Game.cs")!;

        env.Text("// written in ZTX16");
        env.Press(Key.Enter);
        env.Press(Key.S, ctrl: true);
        string saved = env.Workspace.ReadText("/Game/code/Game.cs")!;
        Assert.Equal("// written in ZTX16\n" + original.Replace("\r", ""), saved);

        env.Press(Key.A, ctrl: true);
        env.Press(Key.C, ctrl: true);
        Assert.Equal(saved, env.Environment.Clipboard.GetText());

        // Undo removes the typing (two steps: the line break and the text).
        env.Press(Key.Z, ctrl: true);
        env.Press(Key.Z, ctrl: true);
        env.Press(Key.S, ctrl: true);
        Assert.Equal(original.Replace("\r", ""), env.Workspace.ReadText("/Game/code/Game.cs"));
    }

    [Fact]
    public void CompileErrorsOpenTheLog()
    {
        using EnvironmentDriver env = StartWithProject();
        Assert.Equal(0, Pixel(env, 250, 30));              // no log panel yet
        env.Press(Key.A, ctrl: true);
        env.Text("public sealed class Game : ZTX16.ICartridge { void Broken( }");
        env.Press(Key.F6);                                 // compile (saves first)
        Assert.Contains("Broken(", env.Workspace.ReadText("/Game/code/Game.cs"));
        Assert.Equal(1, Pixel(env, 250, 30));              // the log panel is shown
        Assert.Equal(7, Pixel(env, 13, 240));              // line number 1 in the gutter is red: the error is there
    }

    [Fact]
    public void SwitchingFilesAsksToSaveUnsavedChanges()
    {
        using EnvironmentDriver env = StartWithProject();
        env.Workspace.WriteText("/Game/code/Other.cs", "// other");
        string original = env.Workspace.ReadText("/Game/code/Game.cs")!;

        // N: the change is discarded and the other file opens.
        env.Text("// change one");
        env.Press(Key.Escape);                             // to the Console (the editor keeps its state)
        env.Type("edit /Game/code/Other.cs");
        Assert.Equal("code", env.Active.Name);
        Assert.Equal(20, Pixel(env, 100, 108));            // "Save this file?" dialog
        env.Text("n");
        Assert.Equal(original, env.Workspace.ReadText("/Game/code/Game.cs"));
        Assert.NotEqual(20, Pixel(env, 100, 108));

        // Esc cancels: still on Other.cs with its change.
        env.Text("// change two");
        env.Press(Key.Escape);
        env.Type("edit /Game/code/Game.cs");
        Assert.Equal(20, Pixel(env, 100, 108));
        env.Press(Key.Escape);
        Assert.Equal("// other", env.Workspace.ReadText("/Game/code/Other.cs"));

        // Y: saved, then switched.
        env.Press(Key.Escape);
        env.Type("edit /Game/code/Game.cs");
        env.Text("y");
        Assert.Equal("// change two// other", env.Workspace.ReadText("/Game/code/Other.cs"));
    }

    [Fact]
    public void RunStartsTheGameAndReturnsToTheEditor()
    {
        using EnvironmentDriver env = StartWithProject();
        env.Press(Key.F5);
        Assert.False(env.Active.IsSystem);
        env.Active.Session.Input.Enqueue(Platform.Input.InputEvent.KeyChange(Key.Escape, true));
        env.Tick(2);
        Assert.Equal("code", env.Active.Name);             // the game's launcher
    }

    [Fact]
    public void SaveAsAndNew()
    {
        using EnvironmentDriver env = StartWithProject();
        env.Press(Key.N, ctrl: true);                      // new, nothing to save
        env.Text("// fresh file");
        env.Press(Key.S, ctrl: true);                      // untitled: asks for a name
        for (int i = 0; i < 40; i++)
        {
            env.Press(Key.Backspace);
        }

        env.Text("/Game/code/Fresh.cs");
        env.Press(Key.Enter);
        Assert.Equal("// fresh file", env.Workspace.ReadText("/Game/code/Fresh.cs"));
    }
}

public class AppSwitchingTests
{
    [Fact]
    public void F1AndF2SwitchBetweenConsoleAndEditorAndStopAGame()
    {
        using var env = new EnvironmentDriver();
        env.Type("new Game");
        Assert.True(env.Environment.SwitchTo("code"));      // F2
        env.Environment.AfterTick();
        Assert.Equal("code", env.Active.Name);

        env.Environment.SwitchTo("console");                // F1
        env.Environment.AfterTick();
        Assert.Equal("console", env.Active.Name);

        env.Type("run Game");
        Assert.False(env.Active.IsSystem);
        env.Environment.SwitchTo("code");                   // F2 from a running game stops it
        env.Environment.AfterTick();
        Assert.Equal("code", env.Active.Name);
        Assert.False(env.Environment.SwitchTo("nonexistent"));
    }

    [Fact]
    public void EditorOpensHelloWorldThenRemembersTheLastFile()
    {
        using var env = new EnvironmentDriver();
        env.Type("new HelloWorld");
        env.Workspace.WriteText("/HelloWorld/code/Extra.cs", "// extra");
        env.Environment.SwitchTo("code");
        env.Environment.AfterTick();
        env.Tick();

        // It opened /HelloWorld/code/Game.cs: typing and saving changes that file.
        env.Text("// hi");
        env.Press(Key.S, ctrl: true);
        Assert.StartsWith("// hi", env.Workspace.ReadText("/HelloWorld/code/Game.cs"));

        env.Press(Key.Escape);
        env.Type("edit /HelloWorld/code/Extra.cs");
        Assert.Equal("code", env.Active.Name);
        AppSession editor = env.Active;
        Assert.Equal("/HelloWorld/code/Extra.cs", LastFile(editor));   // the next session reopens it
    }

    private static string LastFile(AppSession editor)
    {
        byte[] data = editor.Session.Storage.Read("session", "last-file")!;
        int length = BitConverter.ToInt32(data, 0);
        return System.Text.Encoding.Unicode.GetString(data, 4, length * 2);
    }
}

public class LiveCodeTests
{
    private static EnvironmentDriver Start()
    {
        var env = new EnvironmentDriver();
        env.Type("new Game");
        env.Type("edit /Game/code/Game.cs");
        return env;
    }

    private static int Pixel(EnvironmentDriver env, int x, int y) => env.Active.Session.Graphics.Layers[0].GetPixel(x, y);

    // The completion popup draws a frame (colour 20) somewhere in the editor area.
    private static bool PopupVisible(EnvironmentDriver env)
    {
        // The selected row: a long run of the selection colour (26) inside the editor area.
        for (int y = 12; y < 240; y++)
        {
            int run = 0;
            for (int x = 18; x < 256; x++)
            {
                run = Pixel(env, x, y) == 26 ? run + 1 : 0;
                if (run > 100)
                {
                    return true;
                }
            }
        }

        return false;
    }
    private static string Line(EnvironmentDriver env, int index) =>
        env.Workspace.ReadText("/Game/code/Game.cs")!.Replace("\r", "").Split('\n')[index];

    [Fact]
    public void ErrorsAreMarkedWhileTypingWithoutCompiling()
    {
        using EnvironmentDriver env = Start();
        env.Text("x");                                     // "xusing ZTX16;" is not C#
        Assert.True(env.TickUntil(() => Pixel(env, 13, 240) == 7), "line 1 should turn red");
        env.Press(Key.Backspace);
        Assert.True(env.TickUntil(() => Pixel(env, 13, 240) != 7), "the error should go away");
    }

    [Fact]
    public void NamesAreColouredByWhatTheyAre()
    {
        using EnvironmentDriver env = Start();
        // Line 14 is "        Graphics.UsePalette(...)": the G of Graphics, lexically plain (4), becomes a type (17).
        Assert.Equal(4, Pixel(env, 51, 149));
        Assert.True(env.TickUntil(() => Pixel(env, 51, 149) == 17), "Graphics should be coloured as a type");
    }

    [Fact]
    public void CompletionAfterADotFiltersAndInserts()
    {
        using EnvironmentDriver env = Start();
        env.Press(Key.Down);                                // line 2 (empty)
        env.Text("Graphics.");
        env.Text("FillR");                                 // Graphics has Fill and FillRect
        Assert.True(env.TickUntil(() => PopupVisible(env)), "the completion popup should open");
        env.Press(Key.Tab);                                 // Tab accepts the selected proposal
        env.Press(Key.S, ctrl: true);
        Assert.Equal("Graphics.FillRect", Line(env, 1));
    }

    [Fact]
    public void CompletionOfANameInScope()
    {
        using EnvironmentDriver env = Start();
        env.Press(Key.Down);
        env.Text("Inp");
        Assert.True(env.TickUntil(() => PopupVisible(env)), "the completion popup should open");
        env.Press(Key.Tab);                                 // Tab accepts too
        env.Press(Key.S, ctrl: true);
        Assert.Equal("Input", Line(env, 1));
    }
}

public class CompletionAndBracketTests
{
    private static EnvironmentDriver Start()
    {
        var env = new EnvironmentDriver();
        env.Type("new Game");
        env.Type("edit /Game/code/Game.cs");
        return env;
    }

    private static int Pixel(EnvironmentDriver env, int x, int y) => env.Active.Session.Graphics.Layers[0].GetPixel(x, y);

    // The popup's selected row: a long run of the selection colour (26) in the editor area; returns its y or -1.
    private static int PopupRow(EnvironmentDriver env)
    {
        for (int y = 12; y < 240; y++)
        {
            int run = 0;
            for (int x = 18; x < 256; x++)
            {
                run = Pixel(env, x, y) == 26 ? run + 1 : 0;
                if (run > 100)
                {
                    return y;
                }
            }
        }

        return -1;
    }

    private static string Line(EnvironmentDriver env, int index)
    {
        env.Press(Key.S, ctrl: true);
        return env.Workspace.ReadText("/Game/code/Game.cs")!.Replace("\r", "").Split('\n')[index];
    }

    [Fact]
    public void PopupOpensFromTheFirstCharacter()
    {
        using EnvironmentDriver env = Start();
        env.Press(Key.Down);
        env.Text("I");
        Assert.True(env.TickUntil(() => PopupRow(env) >= 0), "one character opens the popup");
    }

    [Fact]
    public void CompletionStillWorksAfterEnterClosedThePopup()
    {
        // Regression: Enter used to switch the automatic popup off until a space or symbol was typed.
        using EnvironmentDriver env = Start();

        // Inside Initialize: the end of line 15 ("        Graphics.Fill(0, 18);"), then a new line.
        for (int i = 0; i < 14; i++)
        {
            env.Press(Key.Down);
        }

        env.Press(Key.End);
        env.Press(Key.Enter);
        env.Text("Inp");
        Assert.True(env.TickUntil(() => PopupRow(env) >= 0));
        env.Press(Key.Enter);                               // inserts "Input" and closes the popup
        Assert.Equal(-1, PopupRow(env));
        env.Press(Key.Enter);                               // a new line
        env.Text("Gra");
        Assert.True(env.TickUntil(() => PopupRow(env) >= 0), "the next word must open the popup again");
        env.Press(Key.Enter);
        Assert.Equal("Input", Line(env, 15).Trim());
        Assert.Equal("Graphics", Line(env, 16).Trim());
    }

    [Fact]
    public void FindNextWithF3AndPreviousWithF2()
    {
        using EnvironmentDriver env = Start();
        env.Press(Key.F, ctrl: true);
        env.Text("Graphics");
        env.Press(Key.Enter);                               // first: line 14 "Graphics.UsePalette"
        env.Press(Key.F3);                                  // next: line 15 "Graphics.Fill"
        env.Press(Key.F2);                                  // previous: back to line 14
        env.Press(Key.Escape);                              // close the find bar; the match stays selected
        env.Text("X");                                      // replaces the selected match
        Assert.Equal("        X.UsePalette(\"default\");", Line(env, 13));
        Assert.Equal("        Graphics.Fill(0, 18);", Line(env, 14));
    }

    [Fact]
    public void EnterInsertsTheProposalLikeTab()
    {
        using EnvironmentDriver env = Start();
        env.Press(Key.Down);
        env.Text("Inp");
        Assert.True(env.TickUntil(() => PopupRow(env) >= 0));
        env.Press(Key.Enter);
        Assert.Equal(-1, PopupRow(env));
        Assert.Equal("Input", Line(env, 1));                // inserted, no new line
        Assert.StartsWith("///", Line(env, 2));

        env.Press(Key.Enter);                               // without a popup Enter starts a new line
        Assert.Equal("", Line(env, 2).Trim());
    }

    [Fact]
    public void ClickingAProposalInsertsIt()
    {
        using EnvironmentDriver env = Start();
        env.Press(Key.Down);
        env.Text("Graphics.");
        env.Text("Clea");
        Assert.True(env.TickUntil(() => PopupRow(env) >= 0));
        int y = PopupRow(env) + 3;
        var input = env.Active.Session.Input;
        input.Enqueue(Platform.Input.InputEvent.MouseMoved(80, y));
        input.Enqueue(Platform.Input.InputEvent.MouseButtonChange(MouseButton.Left, true));
        env.Tick();
        input.Enqueue(Platform.Input.InputEvent.MouseButtonChange(MouseButton.Left, false));
        env.Tick();
        Assert.Equal("Graphics.Clear", Line(env, 1));
    }

    [Fact]
    public void BracketPairsAreHighlighted()
    {
        using EnvironmentDriver env = Start();
        // Line 12 is "    public void Initialize()": the cursor at its end is right after ')'.
        for (int i = 0; i < 11; i++)
        {
            env.Press(Key.Down);
        }

        env.Press(Key.End);
        int y = 245 - (12 * 7);                             // row 11 (0-based)
        int open = 18 + (26 * 4) + 1, close = 18 + (27 * 4) + 1;   // the bottom pixel row of a cell is background
        Assert.Equal(27, Pixel(env, open, y));
        Assert.Equal(27, Pixel(env, close, y));

        env.Press(Key.Home);                                // away from brackets: no highlight
        Assert.NotEqual(27, Pixel(env, open, y));
    }

    [Fact]
    public void BracketsInStringsDoNotCount()
    {
        using EnvironmentDriver env = Start();
        env.Press(Key.Down);
        env.Text("f(\")\");");                              // f(")");  — the ')' inside the string is not the partner
        env.Press(Key.Home);
        env.Press(Key.Right);                               // cursor before '(' at column 1
        int y = 245 - (2 * 7);
        Assert.Equal(27, Pixel(env, 18 + (1 * 4) + 3, y)); // '('
        Assert.Equal(27, Pixel(env, 18 + (5 * 4) + 3, y)); // the real ')' at column 5
        Assert.NotEqual(27, Pixel(env, 18 + (3 * 4) + 3, y));
    }
}
