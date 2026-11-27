using ZTX16;
using ZTX16.Authoring;
using ZTX16.Collections;

namespace ZTX16Console;

/// <summary>
/// The ZTX16 Console: an MS-DOS-inspired shell and file manager over the workspace, and the hub of the project
/// workflow — create, build and run projects, launch editors, run command scripts. Type HELP for commands.
/// </summary>
public sealed class Shell : ICartridge
{
    private const int Layer = 0;
    private const int Columns = 32;
    private const int Rows = 32;
    private const int MaxLines = 400;
    private const int MaxHistory = 50;

    // Palette entries of the default palette.
    private const int TextColor = 4;
    private const int PromptColor = 11;
    private const int ErrorColor = 7;
    private const int InfoColor = 17;
    private const int DirectoryColor = 20;
    private const int TitleColor = 15;

    private readonly List<string> _lines = new();
    private readonly List<int> _colors = new();
    private readonly List<string> _history = new();
    private string _input = "";
    private int _cursor;
    private int _historyIndex;
    private int _scroll;
    private string _cwd = "/";
    private int _blink;

    public void Initialize()
    {
        Print("ZTX16 CONSOLE  " + Apps.Version(), TitleColor);
        Print("Type HELP for commands.", TextColor);
        Print("", TextColor);
    }

    public void Tick()
    {
        string message = Apps.Message();
        while (message.Length > 0)
        {
            Print(message, ErrorColor);
            message = Apps.Message();
        }

        string request = Apps.Request();
        if (request.Length > 0)
        {
            Run(request);
        }

        Edit();
        _blink++;
    }

    public void Render()
    {
        Graphics.Clear(Layer);

        // Physical (wrapped) lines of the history plus the input line, newest at the bottom.
        var rows = new List<string>();
        var colors = new List<int>();
        for (int i = 0; i < _lines.Count; i++)
        {
            Wrap(_lines[i], _colors[i], rows, colors);
        }

        int inputStart = rows.Count;
        Wrap(Prompt() + _input, PromptColor, rows, colors);

        int maxScroll = rows.Count > Rows ? rows.Count - Rows : 0;
        if (_scroll > maxScroll)
        {
            _scroll = maxScroll;
        }

        int first = rows.Count - Rows - _scroll;
        if (first < 0)
        {
            first = 0;
        }

        for (int r = 0; r < Rows && first + r < rows.Count; r++)
        {
            int index = first + r;
            int y = Graphics.ScreenSize - ((r + 1) * 8);
            int color = colors[index];
            string text = rows[index];
            if (index >= inputStart && color == PromptColor)
            {
                // The prompt in yellow, the typed text in the normal colour.
                int promptLength = Prompt().Length - ((index - inputStart) * Columns);
                if (promptLength < text.Length && promptLength >= 0)
                {
                    Text.Draw(Layer, Strings.Sub(text, 0, promptLength), 0, y, PromptColor, "");
                    Text.Draw(Layer, Strings.Sub(text, promptLength), promptLength * 8, y, TextColor, "");
                    continue;
                }

                if (promptLength < 0)
                {
                    color = TextColor;
                }
            }

            Text.Draw(Layer, text, 0, y, color, "");
        }

        // Cursor: a blinking block on the input line.
        if (_scroll == 0 && (_blink / 20) % 2 == 0)
        {
            int position = Prompt().Length + _cursor;
            int row = inputStart + (position / Columns) - first;
            if (row >= 0 && row < Rows)
            {
                Graphics.FillRect(Layer, (position % Columns) * 8, Graphics.ScreenSize - ((row + 1) * 8), 8, 2, PromptColor);
            }
        }
    }

    // ---- line editing ---------------------------------------------------------------------------------

    private void Edit()
    {
        string typed = Input.GetText();
        if (typed.Length > 0)
        {
            _input = Strings.Sub(_input, 0, _cursor) + typed + Strings.Sub(_input, _cursor);
            _cursor += typed.Length;
            _scroll = 0;
            _blink = 0;
        }

        if (Input.WasKeyTyped(Key.Backspace) && _cursor > 0)
        {
            _input = Strings.Sub(_input, 0, _cursor - 1) + Strings.Sub(_input, _cursor);
            _cursor--;
            _blink = 0;
        }

        if (Input.WasKeyTyped(Key.Delete) && _cursor < _input.Length)
        {
            _input = Strings.Sub(_input, 0, _cursor) + Strings.Sub(_input, _cursor + 1);
            _blink = 0;
        }

        if (Input.WasKeyTyped(Key.Left) && _cursor > 0)
        {
            _cursor--;
            _blink = 0;
        }

        if (Input.WasKeyTyped(Key.Right) && _cursor < _input.Length)
        {
            _cursor++;
            _blink = 0;
        }

        if (Input.WasKeyPressed(Key.Home))
        {
            _cursor = 0;
        }

        if (Input.WasKeyPressed(Key.End))
        {
            _cursor = _input.Length;
        }

        if (Input.WasKeyPressed(Key.Escape))
        {
            SetInput("");
        }

        if (Input.WasKeyTyped(Key.Up) && _historyIndex > 0)
        {
            _historyIndex--;
            SetInput(_history[_historyIndex]);
        }

        if (Input.WasKeyTyped(Key.Down) && _historyIndex < _history.Count)
        {
            _historyIndex++;
            SetInput(_historyIndex < _history.Count ? _history[_historyIndex] : "");
        }

        if (Input.WasKeyTyped(Key.Tab))
        {
            Complete();
        }

        if (Input.WasKeyTyped(Key.PageUp))
        {
            _scroll += Rows - 2;
        }

        if (Input.WasKeyTyped(Key.PageDown))
        {
            _scroll = _scroll > Rows - 2 ? _scroll - (Rows - 2) : 0;
        }

        _scroll += Input.MouseWheel * 3;
        if (_scroll < 0)
        {
            _scroll = 0;
        }

        if (Input.WasKeyPressed(Key.Enter) || Input.WasKeyPressed(Key.KeypadEnter))
        {
            string line = _input;
            Print(Prompt() + line, PromptColor);
            if (Strings.Trim(line).Length > 0 && (_history.Count == 0 || _history[_history.Count - 1] != line))
            {
                _history.Add(line);
                if (_history.Count > MaxHistory)
                {
                    _history.RemoveAt(0);
                }
            }

            _historyIndex = _history.Count;
            SetInput("");
            _scroll = 0;
            Run(line);
        }
    }

    private void SetInput(string text)
    {
        _input = text;
        _cursor = text.Length;
        _blink = 0;
    }

    // Tab: completes the last word as a path in the workspace.
    private void Complete()
    {
        int start = Strings.LastIndexOf(_input, ' ') + 1;
        string word = Strings.Sub(_input, start);
        int slash = Strings.LastIndexOf(word, '/');
        string folderPart = slash >= 0 ? Strings.Sub(word, 0, slash + 1) : "";
        string namePart = slash >= 0 ? Strings.Sub(word, slash + 1) : word;
        string? listing = Workspace.List(Resolve(folderPart.Length == 0 ? "." : folderPart));
        if (listing is null || listing.Length == 0)
        {
            return;
        }

        var matches = new List<string>();
        List<string> entries = Strings.Split(listing, '\n');
        for (int i = 0; i < entries.Count; i++)
        {
            if (Strings.StartsWith(Strings.ToLower(entries[i]), Strings.ToLower(namePart)))
            {
                matches.Add(entries[i]);
            }
        }

        if (matches.Count == 1)
        {
            SetInput(Strings.Sub(_input, 0, start) + folderPart + matches[0]);
        }
        else if (matches.Count > 1)
        {
            Print(Strings.Join(matches, "  "), DirectoryColor);
        }
    }

    // ---- commands ---------------------------------------------------------------------------------------

    private void Run(string line)
    {
        List<string> words = Strings.Words(line);
        if (words.Count == 0)
        {
            return;
        }

        string command = Strings.ToLower(words[0]);
        string a = words.Count > 1 ? words[1] : "";
        string b = words.Count > 2 ? words[2] : "";

        if (command == "help" || command == "?")
        {
            Help();
        }
        else if (command == "cls" || command == "clear")
        {
            _lines.Clear();
            _colors.Clear();
        }
        else if (command == "ver")
        {
            Print(Apps.Version(), TextColor);
        }
        else if (command == "dir" || command == "ls")
        {
            Dir(a.Length == 0 ? _cwd : Resolve(a));
        }
        else if (command == "cd")
        {
            ChangeDirectory(a);
        }
        else if (command == "md" || command == "mkdir")
        {
            Need(a, "MD <directory>", Workspace.CreateDirectory(Resolve(a)));
        }
        else if (command == "rd" || command == "rmdir")
        {
            Need(a, "RD <directory> [/S]", Workspace.Delete(Resolve(a), Strings.EqualsIgnoreCase(b, "/s")));
        }
        else if (command == "del" || command == "rm")
        {
            Need(a, "DEL <file>", Workspace.IsDirectory(Resolve(a)) ? "it is a directory: use RD" : Workspace.Delete(Resolve(a), false));
        }
        else if (command == "ren" || command == "move" || command == "mv")
        {
            Need2(a, b, "REN <from> <to>", Workspace.Move(Resolve(a), Target(a, b)));
        }
        else if (command == "copy" || command == "cp")
        {
            Need2(a, b, "COPY <from> <to>", Workspace.Copy(Resolve(a), Target(a, b)));
        }
        else if (command == "type" || command == "cat")
        {
            TypeFile(a);
        }
        else if (command == "new")
        {
            NewProject(a);
        }
        else if (command == "build")
        {
            Build(a.Length == 0 ? _cwd : Resolve(a));
        }
        else if (command == "run")
        {
            RunProject(a.Length == 0 ? _cwd : Resolve(a));
        }
        else if (command == "extract")
        {
            Need2(a, b, "EXTRACT <file.ztx> <directory>", Projects.Extract(Resolve(a), Resolve(b)));
        }
        else if (command == "edit")
        {
            EditFile(a);
        }
        else if (command == "apps")
        {
            ListApps();
        }
        else if (command == "do")
        {
            RunScript(a);
        }
        else if (command == "echo")
        {
            Print(Strings.Trim(Strings.Sub(Strings.Trim(line), words[0].Length)), TextColor);
        }
        else if (command == "exit")
        {
            Machine.Exit();
        }
        else if (Strings.EndsWith(Strings.ToLower(words[0]), ".ztx") || Strings.EndsWith(Strings.ToLower(words[0]), ".zcmd"))
        {
            // A cartridge or script typed by name runs it.
            if (Strings.EndsWith(Strings.ToLower(words[0]), ".zcmd"))
            {
                RunScript(words[0]);
            }
            else
            {
                RunProject(Resolve(words[0]));
            }
        }
        else
        {
            Print("Unknown command '" + words[0] + "'. Type HELP.", ErrorColor);
        }
    }

    private void Help()
    {
        Print("FILES", TitleColor);
        Print(" DIR [path]      list (LS)", TextColor);
        Print(" CD path         change dir", TextColor);
        Print(" MD dir / RD dir [/S]", TextColor);
        Print(" DEL file", TextColor);
        Print(" REN a b / COPY a b", TextColor);
        Print(" TYPE file       show text", TextColor);
        Print("PROJECTS", TitleColor);
        Print(" NEW name        create project", TextColor);
        Print(" BUILD [dir]     build .ztx", TextColor);
        Print(" RUN [dir|.ztx]  build and run", TextColor);
        Print(" EXTRACT f.ztx dir", TextColor);
        Print(" EDIT file       open in editor", TextColor);
        Print("SYSTEM", TitleColor);
        Print(" DO file.zcmd    run script", TextColor);
        Print(" APPS  VER  CLS  ECHO  EXIT", TextColor);
        Print("Keys: Tab completes, Up/Down", InfoColor);
        Print("history, PgUp/PgDn scroll,", InfoColor);
        Print("F1 help     F2 console", InfoColor);
        Print("F3 code     F4 graphics", InfoColor);
        Print("F5 sound    F6 fonts", InfoColor);
        Print("(also out of a running game).", InfoColor);
    }

    private void Dir(string path)
    {
        string? listing = Workspace.List(path);
        if (listing is null)
        {
            Print("Not a directory: " + path, ErrorColor);
            return;
        }

        Print(" " + path, InfoColor);
        if (listing.Length == 0)
        {
            Print(" (empty)", TextColor);
            return;
        }

        List<string> entries = Strings.Split(listing, '\n');
        int files = 0, directories = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            string entry = entries[i];
            if (Strings.EndsWith(entry, "/"))
            {
                directories++;
                string marker = Projects.IsProject(Join(path, Strings.Sub(entry, 0, entry.Length - 1))) ? " <PRJ>" : " <DIR>";
                Print(" " + Strings.PadRight(entry, 22) + marker, DirectoryColor);
            }
            else
            {
                files++;
                Print(" " + Strings.PadRight(entry, 22) + Strings.PadLeft(SizeText(Workspace.Size(Join(path, entry))), 8), TextColor);
            }
        }

        Print(" " + directories.ToString() + " dir(s), " + files.ToString() + " file(s)", InfoColor);
    }

    private void ChangeDirectory(string path)
    {
        if (path.Length == 0)
        {
            Print(_cwd, TextColor);
            return;
        }

        string target = Resolve(path);
        if (!Workspace.IsDirectory(target))
        {
            Print("Not a directory: " + target, ErrorColor);
            return;
        }

        _cwd = target;
    }

    private void TypeFile(string path)
    {
        if (path.Length == 0)
        {
            Print("Usage: TYPE <file>", ErrorColor);
            return;
        }

        string? text = Workspace.ReadText(Resolve(path));
        if (text is null)
        {
            Print("Cannot read " + Resolve(path), ErrorColor);
            return;
        }

        List<string> lines = Strings.Split(Strings.Replace(text, "\t", "    "), '\n');
        for (int i = 0; i < lines.Count; i++)
        {
            Print(Strings.Replace(lines[i], "\r", ""), TextColor);
        }
    }

    private void NewProject(string name)
    {
        if (name.Length == 0)
        {
            Print("Usage: NEW <name>", ErrorColor);
            return;
        }

        string path = Resolve(name);
        string error = Projects.Create(path, LastSegment(path));
        if (error.Length > 0)
        {
            Print(error, ErrorColor);
            return;
        }

        Print("Created project " + path, InfoColor);
        Print("Try: CD " + name + "  then RUN", TextColor);
    }

    private void Build(string path)
    {
        if (!Projects.Build(path))
        {
            PrintBlock(Projects.Output(), ErrorColor);
            return;
        }

        Print("Built " + Projects.LastCartridge(), InfoColor);
    }

    private void RunProject(string path)
    {
        string error = Projects.Run(path);
        if (error.Length > 0)
        {
            PrintBlock(error, ErrorColor);
            return;
        }

        Print("Running " + path + "  (F2 to return)", InfoColor);
    }

    private void EditFile(string path)
    {
        if (path.Length == 0)
        {
            Print("Usage: EDIT <file>", ErrorColor);
            return;
        }

        string full = Resolve(path);
        string app = EditorFor(Strings.ToLower(full));
        if (app.Length == 0)
        {
            Print("No editor for this kind of file.", ErrorColor);
            return;
        }

        if (!Apps.Launch(app, full))
        {
            Print("The " + Strings.ToUpper(app) + " editor is not installed yet.", ErrorColor);
        }
    }

    private static string EditorFor(string path)
    {
        if (Strings.EndsWith(path, ".cs") || Strings.EndsWith(path, ".json") || Strings.EndsWith(path, ".zcmd") || Strings.EndsWith(path, ".txt"))
        {
            return "code";
        }

        if (Strings.EndsWith(path, ".png") || Strings.EndsWith(path, ".pal") || Strings.EndsWith(path, ".sprite") || Strings.EndsWith(path, ".anim"))
        {
            return "graphics";
        }

        if (Strings.EndsWith(path, ".font"))
        {
            return "font";
        }

        if (Strings.EndsWith(path, ".instrument") || Strings.EndsWith(path, ".sequence") || Strings.EndsWith(path, ".song"))
        {
            return "sound";
        }

        return Strings.EndsWith(path, ".level") ? "level" : "";
    }

    private void ListApps()
    {
        int count = Apps.Count();
        for (int i = 0; i < count; i++)
        {
            Print(" " + Apps.Name(i), TextColor);
        }
    }

    // Runs the commands of a script, one per line ('#' starts a comment). Scripts cannot nest.
    private void RunScript(string path)
    {
        if (path.Length == 0)
        {
            Print("Usage: DO <file.zcmd>", ErrorColor);
            return;
        }

        string? text = Workspace.ReadText(Resolve(path));
        if (text is null)
        {
            Print("Cannot read " + Resolve(path), ErrorColor);
            return;
        }

        List<string> lines = Strings.Split(text, '\n');
        for (int i = 0; i < lines.Count; i++)
        {
            string line = Strings.Trim(Strings.Replace(lines[i], "\r", ""));
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            List<string> words = Strings.Words(line);
            if (Strings.EqualsIgnoreCase(words[0], "do"))
            {
                Print("Scripts cannot run scripts.", ErrorColor);
                continue;
            }

            Print("> " + line, PromptColor);
            Run(line);
        }
    }

    // ---- paths ------------------------------------------------------------------------------------------

    // An absolute workspace path from one relative to the current directory ('.', '..' and '/' handled).
    private string Resolve(string path)
    {
        string start = path.Length > 0 && path[0] == '/' ? "" : _cwd;
        List<string> parts = Strings.Split(start + "/" + path, '/');
        var result = new List<string>();
        for (int i = 0; i < parts.Count; i++)
        {
            string part = parts[i];
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (result.Count > 0)
                {
                    result.RemoveAt(result.Count - 1);
                }

                continue;
            }

            result.Add(part);
        }

        return "/" + Strings.Join(result, "/");
    }

    // COPY/REN into an existing directory keeps the name.
    private string Target(string from, string to)
    {
        string target = Resolve(to);
        return Workspace.IsDirectory(target) ? Join(target, LastSegment(Resolve(from))) : target;
    }

    private static string Join(string directory, string name) => directory == "/" ? "/" + name : directory + "/" + name;

    private static string LastSegment(string path) => Strings.Sub(path, Strings.LastIndexOf(path, '/') + 1);

    private string Prompt() => _cwd + ">";

    // ---- output -----------------------------------------------------------------------------------------

    private void Need(string argument, string usage, string error)
    {
        if (argument.Length == 0)
        {
            Print("Usage: " + usage, ErrorColor);
        }
        else if (error.Length > 0)
        {
            Print(error, ErrorColor);
        }
    }

    private void Need2(string first, string second, string usage, string error)
    {
        Need(first.Length == 0 || second.Length == 0 ? "" : first, usage, error);
    }

    private void PrintBlock(string text, int color)
    {
        List<string> lines = Strings.Split(text, '\n');
        for (int i = 0; i < lines.Count; i++)
        {
            Print(Strings.Replace(lines[i], "\r", ""), color);
        }
    }

    private void Print(string text, int color)
    {
        _lines.Add(text);
        _colors.Add(color);
        if (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
            _colors.RemoveAt(0);
        }
    }

    private static void Wrap(string text, int color, List<string> rows, List<int> colors)
    {
        if (text.Length == 0)
        {
            rows.Add("");
            colors.Add(color);
            return;
        }

        for (int start = 0; start < text.Length; start += Columns)
        {
            int length = text.Length - start < Columns ? text.Length - start : Columns;
            rows.Add(Strings.Sub(text, start, length));
            colors.Add(color);
        }
    }

    private static string SizeText(long bytes) =>
        bytes < 10000 ? bytes.ToString() : bytes < 10240000 ? (bytes / 1024).ToString() + "K" : (bytes / 1048576).ToString() + "M";
}
