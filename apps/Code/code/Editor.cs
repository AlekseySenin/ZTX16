using ZTX16;
using ZTX16.Authoring;
using ZTX16.Collections;

namespace ZTX16Code;

/// <summary>
/// The ZTX16 code editor: a toolbar (Explorer, Save, Save As, New, Compile, Log, Find, Run) over a C# editor with
/// syntax colouring, line numbers, selection, clipboard, undo, search and compiler errors shown in place.
/// </summary>
/// <remarks>
/// Keys: Ctrl+O explorer, Ctrl+S save, Ctrl+Shift+S save as, Ctrl+N new, F6 compile, F5 compile and run,
/// Ctrl+L log, Ctrl+F find (F3 next), Ctrl+Z/Ctrl+Y undo/redo, Ctrl+A/C/X/V, Tab/Shift+Tab indent,
/// Esc closes a panel or returns to the Console. Host keys: F1 Console, F2 this editor.
/// </remarks>
public sealed class Editor : ICartridge
{
    private const int Layer = 0;
    private const string Font = "small";
    private const int CharWidth = 4;
    private const int LineHeight = 7;
    private const int ToolbarHeight = 11;
    private const int StatusHeight = 8;
    private const int GutterWidth = 18;
    private const int Screen = 256;
    private const int Columns = (Screen - GutterWidth) / CharWidth;
    private const int LogRows = 10;

    // Colours (default palette).
    private const int Background = 0;
    private const int CurrentLine = 18;
    private const int Selection = 26;
    private const int GutterBack = 31;
    private const int GutterText = 3;
    private const int Cursor = 11;
    private const int Bar = 26;
    private const int ButtonBack = 27;
    private const int ButtonHover = 19;
    private const int ButtonText = 5;
    private const int StatusText = 4;
    private const int ErrorColor = 7;
    private const int WarningColor = 11;
    private const int InfoColor = 17;
    private const int PanelBack = 1;
    private const int PanelBorder = 20;
    private const int TitleColor = 15;

    private const int PanelNone = 0, PanelExplorer = 1, PanelPrompt = 2, PanelConfirm = 3, PanelFind = 4;
    private const int ActionNone = 0, ActionOpen = 1, ActionNew = 2;
    private const int PromptSaveAs = 1;

    private static readonly string[] ButtonLabels = { "EXPLORER", "SAVE", "SAVE AS", "NEW", "COMPILE", "LOG", "FIND", "RUN" };

    private readonly Document _doc = new();
    private readonly Highlighter _highlighter = new();
    private readonly int[] _buttonX = new int[8];
    private readonly int[] _buttonWidth = new int[8];

    private string _path = "";
    private int _scrollTop;
    private int _scrollLeft;
    private int _blink;
    private string _status = "";
    private int _statusColor = StatusText;
    private int _statusTicks;
    private bool _dragging;

    // Panels and dialogs.
    private int _panel;
    private bool _logVisible;
    private int _pendingAction;
    private string _pendingPath = "";
    private int _pendingLine = -1;
    private int _pendingColumn;
    private int _afterSave;
    private int _promptPurpose;
    private string _promptTitle = "";
    private string _promptText = "";
    private string _findText = "";

    // Explorer.
    private string _explorerDir = "/";
    private readonly List<string> _entries = new();
    private int _explorerIndex;
    private int _explorerScroll;
    private long _lastClickTick = -100;
    private int _lastClickIndex = -1;

    // Live analysis: problems of the text being edited (0-based positions) and the request in flight.
    private bool _analyzing;
    private string _analysisPath = "";
    private int _analyzedRevision = -1;
    private int _seenRevision = -1;
    private long _lastEditTick;
    private readonly List<int> _pLine = new();
    private readonly List<int> _pColumn = new();
    private readonly List<int> _pEndLine = new();
    private readonly List<int> _pEndColumn = new();
    private readonly List<bool> _pError = new();
    private readonly List<string> _pText = new();

    // Completion popup.
    private const int CompletionRows = 8;
    private bool _completionPending;
    private bool _completionOpen;
    private bool _completionSuppressed;     // after Esc or accepting: no automatic popup until the word ends
    private bool _completionManual;         // opened with Ctrl+Space: shown for any word length
    private int _popupX, _popupY, _popupWidth, _popupHeight, _popupRows;
    private bool _wheelUsed;                // the wheel scrolled the popup this tick, not the text
    private int _completionLine;
    private int _completionStart;
    private int _completionSelected;
    private int _completionScroll;
    private readonly List<string> _cLabel = new();
    private readonly List<int> _cKind = new();
    private readonly List<string> _cDetail = new();
    private readonly List<int> _cShown = new();

    // Build log.
    private readonly List<int> _logKind = new();          // 0 error, 1 warning, 2 info
    private readonly List<string> _logFile = new();
    private readonly List<int> _logLine = new();
    private readonly List<int> _logColumn = new();
    private readonly List<string> _logText = new();
    private int _logIndex;
    private int _logScroll;

    public void Initialize()
    {
        int x = 2;
        for (int i = 0; i < ButtonLabels.Length; i++)
        {
            _buttonX[i] = x;
            _buttonWidth[i] = (ButtonLabels[i].Length * CharWidth) + 5;
            x += _buttonWidth[i] + 2;
        }

        // Reopen the file of the last session, or the Hello World project of a new workspace.
        byte[]? last = Storage.Read("session", "last-file");
        string path = last is null ? "" : new ByteReader(last).ReadString() ?? "";
        if (path.Length == 0 || !Workspace.Exists(path))
        {
            path = DefaultFile;
        }

        if (Workspace.Exists(path))
        {
            OpenFile(path);
        }

        SetStatus("F1 console  F5 run  F6 compile  Ctrl+O open", InfoColor);
    }

    private const string DefaultFile = "/HelloWorld/code/Game.cs";

    // Remembered so the next session reopens it.
    private void RememberFile()
    {
        var writer = new ByteWriter();
        writer.WriteString(_path);
        Storage.Write("session", "last-file", writer.ToArray());
    }

    // ---- tick ---------------------------------------------------------------------------------------------

    public void Tick()
    {
        string request = Apps.Request();
        if (request.Length > 0)
        {
            RequestOpen(request, -1, 0);
        }

        if (_statusTicks > 0)
        {
            _statusTicks--;
        }

        _blink++;
        HandleMouse();
        if (_panel == PanelExplorer)
        {
            ExplorerKeys();
        }
        else if (_panel == PanelPrompt)
        {
            PromptKeys();
        }
        else if (_panel == PanelConfirm)
        {
            ConfirmKeys();
        }
        else
        {
            if (_panel == PanelFind)
            {
                FindKeys();
            }
            else
            {
                EditorKeys();
            }

            GlobalKeys();
        }

        KeepCursorVisible();
        LiveAnalysis();
        PollCompletion();
    }

    // ---- live analysis --------------------------------------------------------------------------------------

    // About 0.3 s after the last change, the text is analysed in the background; results colour names and
    // underline problems.
    private void LiveAnalysis()
    {
        if (_doc.Revision != _seenRevision)
        {
            _seenRevision = _doc.Revision;
            _lastEditTick = Machine.TickCount;
        }

        if (_analyzing && CodeAnalysis.AnalysisReady())
        {
            _analyzing = false;
            if (_analysisPath == _path)
            {
                LoadProblems(CodeAnalysis.Problems());
                _highlighter.SetSemantic(CodeAnalysis.Colors(), _doc);
            }
        }

        if (!_analyzing && _path.Length > 0 && _doc.Revision != _analyzedRevision && Machine.TickCount - _lastEditTick >= 18)
        {
            _analyzedRevision = _doc.Revision;
            if (CodeAnalysis.Analyze(_path, _doc.Text()))
            {
                _analyzing = true;
                _analysisPath = _path;
            }
        }
    }

    private void LoadProblems(string text)
    {
        ClearProblems();
        if (text.Length == 0)
        {
            return;
        }

        List<string> lines = Strings.Split(text, '\n');
        for (int i = 0; i < lines.Count; i++)
        {
            List<string> f = Strings.Split(lines[i], '\t');
            int line, column, endLine, endColumn;
            if (f.Count >= 7 && Strings.TryParseInt(f[1], out line) && Strings.TryParseInt(f[2], out column)
                && Strings.TryParseInt(f[3], out endLine) && Strings.TryParseInt(f[4], out endColumn))
            {
                _pLine.Add(line);
                _pColumn.Add(column);
                _pEndLine.Add(endLine);
                _pEndColumn.Add(endColumn);
                _pError.Add(f[0] == "E");
                _pText.Add(f[5] + ": " + f[6]);
            }
        }
    }

    private void ClearProblems()
    {
        _pLine.Clear();
        _pColumn.Clear();
        _pEndLine.Clear();
        _pEndColumn.Clear();
        _pError.Clear();
        _pText.Clear();
    }

    // The live problem under the cursor (or anywhere on its line), or -1.
    private int LiveProblemAtCursor()
    {
        int onLine = -1;
        for (int i = 0; i < _pLine.Count; i++)
        {
            if (_doc.Line < _pLine[i] || _doc.Line > _pEndLine[i])
            {
                continue;
            }

            bool afterStart = _doc.Line > _pLine[i] || _doc.Column >= _pColumn[i];
            bool beforeEnd = _doc.Line < _pEndLine[i] || _doc.Column <= _pEndColumn[i];
            if (afterStart && beforeEnd)
            {
                return i;
            }

            if (onLine < 0 || (_pError[i] && !_pError[onLine]))
            {
                onLine = i;
            }
        }

        return onLine;
    }

    private int LiveCount(bool errors)
    {
        int n = 0;
        for (int i = 0; i < _pError.Count; i++)
        {
            if (_pError[i] == errors)
            {
                n++;
            }
        }

        return n;
    }

    // ---- completion -----------------------------------------------------------------------------------------

    private void RequestCompletion()
    {
        if (_path.Length == 0 || _completionPending)
        {
            return;
        }

        if (CodeAnalysis.Complete(_path, _doc.Text(), _doc.Line, _doc.Column))
        {
            _completionPending = true;
            _completionLine = _doc.Line;
        }
    }

    private void PollCompletion()
    {
        if (!_completionPending || !CodeAnalysis.CompletionReady())
        {
            return;
        }

        _completionPending = false;
        if (_doc.Line != _completionLine || _panel != PanelNone)
        {
            return;
        }

        string text = CodeAnalysis.Completion();
        _cLabel.Clear();
        _cKind.Clear();
        _cDetail.Clear();
        List<string> lines = Strings.Split(text, '\n');
        int start;
        if (lines.Count < 2 || !Strings.TryParseInt(lines[0], out start))
        {
            CloseCompletion();
            return;
        }

        for (int i = 1; i < lines.Count; i++)
        {
            List<string> f = Strings.Split(lines[i], '\t');
            int kind;
            if (f.Count >= 3 && Strings.TryParseInt(f[0], out kind))
            {
                _cKind.Add(kind);
                _cLabel.Add(f[1]);
                _cDetail.Add(f[2]);
            }
        }

        _completionStart = start;
        _completionOpen = true;
        Refilter();
    }

    // Shows the proposals matching the word typed so far: prefix matches first, then other matches.
    private void Refilter()
    {
        if (!_completionOpen)
        {
            return;
        }

        if (_doc.Line != _completionLine || _doc.Column < _completionStart)
        {
            CloseCompletion();
            return;
        }

        string prefix = Strings.ToLower(Strings.Sub(_doc.Lines[_doc.Line], _completionStart, _doc.Column - _completionStart));
        bool afterDot = _completionStart > 0 && _doc.Lines[_doc.Line][_completionStart - 1] == '.';
        if (!afterDot && !_completionManual && prefix.Length < 3)
        {
            CloseCompletion();       // proposals appear from the third character of a word
            return;
        }
        _cShown.Clear();
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < _cLabel.Count; i++)
            {
                string label = Strings.ToLower(_cLabel[i]);
                bool prefixMatch = Strings.StartsWith(label, prefix);
                if ((pass == 0 && prefixMatch) || (pass == 1 && !prefixMatch && prefix.Length > 1 && Strings.Contains(label, prefix)))
                {
                    _cShown.Add(i);
                }
            }
        }

        if (_cShown.Count == 0 || (_cShown.Count == 1 && Strings.ToLower(_cLabel[_cShown[0]]) == prefix && prefix.Length > 0))
        {
            CloseCompletion();
            return;
        }

        _completionSelected = 0;
        _completionScroll = 0;
    }

    private void CloseCompletion()
    {
        _completionOpen = false;
        _completionManual = false;
        _cShown.Clear();
    }

    // Keys while the popup is open. Returns true if the key was used by the popup.
    private bool CompletionKeys()
    {
        if (!_completionOpen)
        {
            return false;
        }

        bool used = false;
        if (Input.WasKeyTyped(Key.Down))
        {
            _completionSelected = (_completionSelected + 1) % _cShown.Count;
            used = true;
        }

        if (Input.WasKeyTyped(Key.Up))
        {
            _completionSelected = (_completionSelected + _cShown.Count - 1) % _cShown.Count;
            used = true;
        }

        if (Input.WasKeyTyped(Key.PageDown))
        {
            _completionSelected = Document.Min(_cShown.Count - 1, _completionSelected + CompletionRows);
            used = true;
        }

        if (Input.WasKeyTyped(Key.PageUp))
        {
            _completionSelected = Document.Max(0, _completionSelected - CompletionRows);
            used = true;
        }

        if (Input.WasKeyPressed(Key.Tab))
        {
            AcceptCompletion();
            used = true;
        }

        if (Input.WasKeyPressed(Key.Enter) || Input.WasKeyPressed(Key.KeypadEnter))
        {
            // Enter keeps its meaning (a new line); it only closes the popup.
            CloseCompletion();
            _completionSuppressed = true;
        }

        if (Input.WasKeyPressed(Key.Escape))
        {
            CloseCompletion();
            _completionSuppressed = true;
            used = true;
        }

        if (_completionSelected < _completionScroll)
        {
            _completionScroll = _completionSelected;
        }

        if (_completionSelected >= _completionScroll + CompletionRows)
        {
            _completionScroll = _completionSelected - CompletionRows + 1;
        }

        return used;
    }

    private void AcceptCompletion()
    {
        if (!_completionOpen || _cShown.Count == 0)
        {
            return;
        }

        string label = _cLabel[_cShown[_completionSelected]];
        int line = _doc.Line, column = _doc.Column;
        _doc.MoveTo(line, _completionStart, false);
        if (column > _completionStart)
        {
            _doc.MoveTo(line, column, true);
        }

        _doc.Insert(label);
        CloseCompletion();
        _completionSuppressed = true;
    }

    private static string KindLetter(int kind)
    {
        string letters = "?TMPFLANEVKC";
        return kind >= 0 && kind < letters.Length ? letters[kind].ToString() : "?";
    }

    private void GlobalKeys()
    {
        bool ctrl = Ctrl();
        bool shift = Shift();
        if (ctrl && Input.WasKeyPressed(Key.O))
        {
            OpenExplorer();
        }
        else if (ctrl && shift && Input.WasKeyPressed(Key.S))
        {
            SaveAs();
        }
        else if (ctrl && Input.WasKeyPressed(Key.S))
        {
            Save();
        }
        else if (ctrl && Input.WasKeyPressed(Key.N))
        {
            NewFile();
        }
        else if (Input.WasKeyPressed(Key.F6) || (ctrl && Input.WasKeyPressed(Key.B)))
        {
            Compile(false);
        }
        else if (Input.WasKeyPressed(Key.F5))
        {
            Compile(true);
        }
        else if (ctrl && Input.WasKeyPressed(Key.L))
        {
            _logVisible = !_logVisible;
        }
        else if (ctrl && Input.WasKeyPressed(Key.F))
        {
            OpenFind();
        }
        else if (Input.WasKeyPressed(Key.F3))
        {
            FindNext();
        }
    }

    private void EditorKeys()
    {
        bool ctrl = Ctrl();
        bool shift = Shift();

        // While the completion popup is open, Up/Down/PgUp/PgDn/Enter/Tab/Esc belong to it.
        bool popup = _completionOpen;
        CompletionKeys();

        string typed = Input.GetText();
        if (typed.Length > 0 && !ctrl)
        {
            _doc.Type(typed);
            _blink = 0;
            char last = typed[typed.Length - 1];
            if (last == '.')
            {
                CloseCompletion();
                RequestCompletion();
            }
            else if (!IsWord(last))
            {
                CloseCompletion();
                _completionSuppressed = false;            // a new word may start
            }
            else if (_completionOpen)
            {
                Refilter();
            }
            else if (!_completionSuppressed && !IsDigitChar(WordStartChar()) && WordLengthBeforeCursor() >= 3)
            {
                RequestCompletion();
            }
        }

        if (ctrl && Input.WasKeyPressed(Key.Space))
        {
            CloseCompletion();
            _completionManual = true;
            RequestCompletion();
        }

        if (Input.WasKeyTyped(Key.Left))
        {
            if (ctrl)
            {
                WordLeft(shift);
            }
            else
            {
                _doc.Left(shift);
            }

            _blink = 0;
            CloseCompletion();
        }

        if (Input.WasKeyTyped(Key.Right))
        {
            if (ctrl)
            {
                WordRight(shift);
            }
            else
            {
                _doc.Right(shift);
            }

            _blink = 0;
            CloseCompletion();
        }

        if (Input.WasKeyTyped(Key.Up) && !popup)
        {
            _doc.MoveTo(_doc.Line - 1, _doc.Column, shift);
            _blink = 0;
        }

        if (Input.WasKeyTyped(Key.Down) && !popup)
        {
            _doc.MoveTo(_doc.Line + 1, _doc.Column, shift);
            _blink = 0;
        }

        if (Input.WasKeyTyped(Key.PageUp) && !popup)
        {
            _doc.MoveTo(_doc.Line - EditorRows(), _doc.Column, shift);
        }

        if (Input.WasKeyTyped(Key.PageDown) && !popup)
        {
            _doc.MoveTo(_doc.Line + EditorRows(), _doc.Column, shift);
        }

        if (Input.WasKeyPressed(Key.Home))
        {
            if (ctrl)
            {
                _doc.MoveTo(0, 0, shift);
            }
            else
            {
                _doc.Home(shift);
            }
        }

        if (Input.WasKeyPressed(Key.End))
        {
            if (ctrl)
            {
                _doc.MoveTo(_doc.Lines.Count - 1, _doc.Lines[_doc.Lines.Count - 1].Length, shift);
            }
            else
            {
                _doc.MoveTo(_doc.Line, _doc.Lines[_doc.Line].Length, shift);
            }
        }

        if (Input.WasKeyTyped(Key.Enter) || Input.WasKeyTyped(Key.KeypadEnter))
        {
            _doc.NewLine();
        }

        if (Input.WasKeyTyped(Key.Backspace))
        {
            _doc.Backspace();
            _blink = 0;
            Refilter();
        }

        if (Input.WasKeyTyped(Key.Delete))
        {
            _doc.Delete();
        }

        if (Input.WasKeyTyped(Key.Tab) && !popup)
        {
            _doc.Tab(shift);
        }

        if (ctrl && Input.WasKeyPressed(Key.A))
        {
            _doc.SelectAll();
        }

        if (ctrl && Input.WasKeyPressed(Key.C) && _doc.HasSelection)
        {
            Apps.SetClipboard(_doc.SelectedText());
            SetStatus("Copied", InfoColor);
        }

        if (ctrl && Input.WasKeyPressed(Key.X) && _doc.HasSelection)
        {
            Apps.SetClipboard(_doc.SelectedText());
            _doc.DeleteSelection();
        }

        if (ctrl && Input.WasKeyPressed(Key.V))
        {
            string clip = Apps.GetClipboard();
            if (clip.Length > 0)
            {
                _doc.Insert(clip);
            }
        }

        if (ctrl && Input.WasKeyTyped(Key.Z))
        {
            _doc.Undo();
        }

        if (ctrl && Input.WasKeyTyped(Key.Y))
        {
            _doc.Redo();
        }

        if (Input.WasKeyPressed(Key.Escape) && !popup)
        {
            if (_logVisible)
            {
                _logVisible = false;
            }
            else if (_doc.HasSelection)
            {
                _doc.MoveTo(_doc.Line, _doc.Column, false);
            }
            else
            {
                Apps.Back();
            }
        }

        int wheel = Input.MouseWheel;
        if (wheel != 0 && !_wheelUsed)
        {
            if (_logVisible && Input.MouseY < StatusHeight + (LogRows * LineHeight))
            {
                _logScroll = Document.Clamp(_logScroll - wheel, 0, Document.Max(0, _logText.Count - (LogRows - 1)));
            }
            else
            {
                _scrollTop = Document.Clamp(_scrollTop - (wheel * 3), 0, Document.Max(0, _doc.Lines.Count - 1));
            }
        }
    }

    private void WordLeft(bool select)
    {
        string line = _doc.Lines[_doc.Line];
        int c = _doc.Column;
        if (c == 0)
        {
            _doc.Left(select);
            return;
        }

        while (c > 0 && line[c - 1] == ' ')
        {
            c--;
        }

        while (c > 0 && IsWord(line[c - 1]))
        {
            c--;
        }

        if (c == _doc.Column)
        {
            c--;
        }

        _doc.MoveTo(_doc.Line, c, select);
    }

    private void WordRight(bool select)
    {
        string line = _doc.Lines[_doc.Line];
        int c = _doc.Column;
        if (c >= line.Length)
        {
            _doc.Right(select);
            return;
        }

        while (c < line.Length && IsWord(line[c]))
        {
            c++;
        }

        while (c < line.Length && line[c] == ' ')
        {
            c++;
        }

        if (c == _doc.Column)
        {
            c++;
        }

        _doc.MoveTo(_doc.Line, c, select);
    }

    // ---- mouse --------------------------------------------------------------------------------------------

    private void HandleMouse()
    {
        _wheelUsed = false;
        int mx = Input.MouseX, my = Input.MouseY;
        bool pressed = Input.WasMouseButtonPressed(MouseButton.Left);
        bool down = Input.IsMouseButtonDown(MouseButton.Left);
        if (!down)
        {
            _dragging = false;
        }

        if (pressed && my >= Screen - ToolbarHeight)
        {
            for (int i = 0; i < ButtonLabels.Length; i++)
            {
                if (mx >= _buttonX[i] && mx < _buttonX[i] + _buttonWidth[i])
                {
                    Button(i);
                }
            }

            return;
        }

        if (_panel == PanelExplorer)
        {
            ExplorerMouse(mx, my, pressed);
            return;
        }

        if (_panel == PanelPrompt || _panel == PanelConfirm)
        {
            return;
        }

        // The completion popup: a click picks a proposal, the wheel scrolls it.
        if (_completionOpen && mx >= _popupX && mx < _popupX + _popupWidth && my >= _popupY && my < _popupY + _popupHeight)
        {
            int wheel = Input.MouseWheel;
            if (wheel != 0)
            {
                _completionScroll = Document.Clamp(_completionScroll - wheel, 0, Document.Max(0, _cShown.Count - _popupRows));
            }

            if (pressed)
            {
                int row = (_popupY + _popupHeight - 2 - my) / LineHeight;
                int index = _completionScroll + row;
                if (row >= 0 && row < _popupRows && index < _cShown.Count)
                {
                    _completionSelected = index;
                    AcceptCompletion();
                }
            }

            _wheelUsed = true;
            return;
        }

        if (_logVisible && my >= StatusHeight && my < StatusHeight + (LogRows * LineHeight))
        {
            if (pressed)
            {
                int row = ((StatusHeight + ((LogRows - 1) * LineHeight)) - my) / LineHeight;
                int index = _logScroll + row;
                if (row >= 0 && index < _logText.Count)
                {
                    _logIndex = index;
                    JumpToLog(index);
                }
            }

            return;
        }

        if (pressed && my >= StatusHeight && my < EditorTop() && mx >= 0)
        {
            int line, column;
            PositionAt(mx, my, out line, out column);
            _doc.MoveTo(line, column, Shift());
            CloseCompletion();
            _dragging = true;
            _blink = 0;
        }
        else if (_dragging && down)
        {
            int line, column;
            PositionAt(mx, my, out line, out column);
            _doc.MoveTo(line, column, true);
        }
    }

    private void PositionAt(int mx, int my, out int line, out int column)
    {
        int row = (EditorTop() - 1 - my) / LineHeight;
        line = Document.Clamp(_scrollTop + row, 0, _doc.Lines.Count - 1);
        int col = _scrollLeft + ((mx - GutterWidth + 2) / CharWidth);
        column = Document.Clamp(col, 0, _doc.Lines[line].Length);
    }

    private void Button(int index)
    {
        if (index == 0)
        {
            OpenExplorer();
        }
        else if (index == 1)
        {
            Save();
        }
        else if (index == 2)
        {
            SaveAs();
        }
        else if (index == 3)
        {
            NewFile();
        }
        else if (index == 4)
        {
            Compile(false);
        }
        else if (index == 5)
        {
            _logVisible = !_logVisible;
        }
        else if (index == 6)
        {
            OpenFind();
        }
        else
        {
            Compile(true);
        }
    }

    // ---- files ------------------------------------------------------------------------------------------

    /// <summary>Opens a file, asking first to save unsaved changes. Then goes to a line (if ≥ 0).</summary>
    private void RequestOpen(string path, int line, int column)
    {
        if (path == _path && _path.Length > 0)
        {
            GoTo(line, column);
            return;
        }

        if (_doc.Modified)
        {
            _pendingAction = ActionOpen;
            _pendingPath = path;
            _pendingLine = line;
            _pendingColumn = column;
            _panel = PanelConfirm;
            return;
        }

        OpenFile(path);
        GoTo(line, column);
    }

    private void OpenFile(string path)
    {
        ClearProblems();
        _highlighter.ClearSemantic(_doc);
        CloseCompletion();
        _analyzedRevision = -1;
        string? text = Workspace.ReadText(path);
        if (text is null)
        {
            if (Workspace.IsDirectory(path))
            {
                SetStatus("That is a directory", ErrorColor);
                return;
            }

            // A file that does not exist yet: start it empty under that name.
            _doc.Load("");
            _path = path;
            SetStatus("New file " + FileName(path), InfoColor);
        }
        else
        {
            _doc.Load(text);
            _path = path;
            SetStatus("Opened " + FileName(path), InfoColor);
        }

        _scrollTop = 0;
        _scrollLeft = 0;
        _explorerDir = Parent(path);
        RememberFile();
    }

    private void GoTo(int line, int column)
    {
        if (line >= 0)
        {
            _doc.MoveTo(line, column, false);
            _scrollTop = Document.Max(0, line - (EditorRows() / 2));
        }
    }

    private void NewFile()
    {
        if (_doc.Modified)
        {
            _pendingAction = ActionNew;
            _panel = PanelConfirm;
            return;
        }

        DoNew();
    }

    private void DoNew()
    {
        ClearProblems();
        _highlighter.ClearSemantic(_doc);
        CloseCompletion();
        _doc.Load("");
        _path = "";
        _scrollTop = 0;
        _scrollLeft = 0;
        SetStatus("New file: Save to name it", InfoColor);
    }

    private bool Save()
    {
        if (_path.Length == 0)
        {
            SaveAs();
            return false;
        }

        string error = Workspace.WriteText(_path, _doc.Text());
        if (error.Length > 0)
        {
            SetStatus("Save failed: " + error, ErrorColor);
            return false;
        }

        _doc.Modified = false;
        SetStatus("Saved " + FileName(_path), InfoColor);
        RememberFile();
        return true;
    }

    private void SaveAs()
    {
        _promptPurpose = PromptSaveAs;
        _promptTitle = "SAVE AS (workspace path)";
        _promptText = _path.Length > 0 ? _path : Join(_explorerDir, "NewFile.cs");
        _panel = PanelPrompt;
    }

    private void RunPending()
    {
        int action = _pendingAction;
        _pendingAction = ActionNone;
        if (action == ActionOpen)
        {
            OpenFile(_pendingPath);
            GoTo(_pendingLine, _pendingColumn);
        }
        else if (action == ActionNew)
        {
            DoNew();
        }
    }

    // ---- confirm: "Save this file?" -----------------------------------------------------------------------

    private void ConfirmKeys()
    {
        string typed = Strings.ToLower(Input.GetText());
        if (typed == "y" || Input.WasKeyPressed(Key.Enter))
        {
            _panel = PanelNone;
            if (_path.Length == 0)
            {
                _afterSave = _pendingAction;
                SaveAs();
                return;
            }

            if (Save())
            {
                RunPending();
            }
        }
        else if (typed == "n")
        {
            _panel = PanelNone;
            RunPending();
        }
        else if (Input.WasKeyPressed(Key.Escape))
        {
            _panel = PanelNone;
            _pendingAction = ActionNone;
            SetStatus("Cancelled", StatusText);
        }
    }

    // ---- prompt -------------------------------------------------------------------------------------------

    private void PromptKeys()
    {
        string typed = Input.GetText();
        if (typed.Length > 0)
        {
            _promptText += typed;
        }

        if (Input.WasKeyTyped(Key.Backspace) && _promptText.Length > 0)
        {
            _promptText = Strings.Sub(_promptText, 0, _promptText.Length - 1);
        }

        if (Input.WasKeyPressed(Key.Escape))
        {
            _panel = PanelNone;
            _afterSave = ActionNone;
            _pendingAction = ActionNone;
            return;
        }

        if (Input.WasKeyPressed(Key.Enter) && _promptPurpose == PromptSaveAs)
        {
            string path = Strings.Trim(_promptText);
            if (path.Length == 0 || path[0] != '/')
            {
                path = Join(_explorerDir, path);
            }

            _panel = PanelNone;
            string previous = _path;
            _path = path;
            if (!Save())
            {
                _path = previous;
                return;
            }

            _explorerDir = Parent(path);
            if (_afterSave != ActionNone)
            {
                _pendingAction = _afterSave;
                _afterSave = ActionNone;
                RunPending();
            }
        }
    }

    // ---- explorer -----------------------------------------------------------------------------------------

    private void OpenExplorer()
    {
        _panel = PanelExplorer;
        LoadEntries();
    }

    private void LoadEntries()
    {
        _entries.Clear();
        if (_explorerDir != "/")
        {
            _entries.Add("../");
        }

        string? listing = Workspace.List(_explorerDir);
        if (listing is null)
        {
            _explorerDir = "/";
            listing = Workspace.List("/");
        }

        if (listing is not null && listing.Length > 0)
        {
            List<string> all = Strings.Split(listing, '\n');
            for (int i = 0; i < all.Count; i++)
            {
                string entry = all[i];
                string lower = Strings.ToLower(entry);
                if (Strings.EndsWith(entry, "/") || Strings.EndsWith(lower, ".cs") || Strings.EndsWith(lower, ".json")
                    || Strings.EndsWith(lower, ".zcmd") || Strings.EndsWith(lower, ".txt") || Strings.EndsWith(lower, ".md"))
                {
                    if (lower != "bin/" && lower != "obj/")
                    {
                        _entries.Add(entry);
                    }
                }
            }
        }

        _explorerIndex = 0;
        _explorerScroll = 0;
    }

    private void ExplorerKeys()
    {
        if (Input.WasKeyTyped(Key.Up) && _explorerIndex > 0)
        {
            _explorerIndex--;
        }

        if (Input.WasKeyTyped(Key.Down) && _explorerIndex < _entries.Count - 1)
        {
            _explorerIndex++;
        }

        if (Input.WasKeyPressed(Key.Enter))
        {
            ExplorerActivate(_explorerIndex);
        }

        if (Input.WasKeyTyped(Key.Backspace) && _explorerDir != "/")
        {
            _explorerDir = Parent(_explorerDir);
            LoadEntries();
        }

        if (Input.WasKeyPressed(Key.Escape))
        {
            _panel = PanelNone;
        }

        _explorerScroll -= Input.MouseWheel * 3;
        int rows = ExplorerRows();
        if (_explorerIndex < _explorerScroll)
        {
            _explorerScroll = _explorerIndex;
        }

        if (_explorerIndex >= _explorerScroll + rows)
        {
            _explorerScroll = _explorerIndex - rows + 1;
        }

        _explorerScroll = Document.Clamp(_explorerScroll, 0, Document.Max(0, _entries.Count - rows));
    }

    private void ExplorerMouse(int mx, int my, bool pressed)
    {
        if (!pressed)
        {
            return;
        }

        int top = EditorTop() - 14;
        int row = (top - 1 - my) / LineHeight;
        if (my < top && row >= 0 && row < ExplorerRows())
        {
            int index = _explorerScroll + row;
            if (index < _entries.Count)
            {
                if (index == _lastClickIndex && Machine.TickCount - _lastClickTick < 25)
                {
                    ExplorerActivate(index);
                    _lastClickIndex = -1;
                    return;
                }

                _explorerIndex = index;
                _lastClickIndex = index;
                _lastClickTick = Machine.TickCount;
            }
        }
    }

    private void ExplorerActivate(int index)
    {
        if (index < 0 || index >= _entries.Count)
        {
            return;
        }

        string entry = _entries[index];
        if (entry == "../")
        {
            _explorerDir = Parent(_explorerDir);
            LoadEntries();
        }
        else if (Strings.EndsWith(entry, "/"))
        {
            _explorerDir = Join(_explorerDir, Strings.Sub(entry, 0, entry.Length - 1));
            LoadEntries();
        }
        else
        {
            _panel = PanelNone;
            RequestOpen(Join(_explorerDir, entry), -1, 0);
        }
    }

    private int ExplorerRows() => ((EditorTop() - 14 - StatusHeight - 6) / LineHeight);

    // ---- find -------------------------------------------------------------------------------------------

    private void OpenFind()
    {
        _panel = PanelFind;
        if (_doc.HasSelection && _doc.Line == _doc.AnchorLine)
        {
            _findText = _doc.SelectedText();
        }
    }

    private void FindKeys()
    {
        string typed = Input.GetText();
        if (typed.Length > 0 && !Ctrl())
        {
            _findText += typed;
        }

        if (Input.WasKeyTyped(Key.Backspace) && _findText.Length > 0)
        {
            _findText = Strings.Sub(_findText, 0, _findText.Length - 1);
        }

        if (Input.WasKeyPressed(Key.Enter))
        {
            FindNext();
        }

        if (Input.WasKeyPressed(Key.Escape))
        {
            _panel = PanelNone;
        }
    }

    // Finds the next occurrence after the cursor (case-insensitive, wrapping) and selects it.
    private void FindNext()
    {
        if (_findText.Length == 0)
        {
            OpenFind();
            return;
        }

        string needle = Strings.ToLower(_findText);
        int count = _doc.Lines.Count;
        int startLine = _doc.Line;
        int startColumn = _doc.Column;
        for (int step = 0; step <= count; step++)
        {
            int line = (startLine + step) % count;
            string text = Strings.ToLower(_doc.Lines[line]);
            int from = step == 0 ? startColumn : 0;
            int found = Strings.IndexOf(text, needle, from);
            if (step == count && found >= startColumn)
            {
                found = -1;
            }

            if (found >= 0)
            {
                _doc.MoveTo(line, found, false);
                _doc.MoveTo(line, found + needle.Length, true);
                SetStatus("Found at line " + (line + 1).ToString(), InfoColor);
                return;
            }
        }

        SetStatus("Not found: " + _findText, ErrorColor);
    }

    // ---- compile ------------------------------------------------------------------------------------------

    private void Compile(bool run)
    {
        string project = ProjectOf(_path);
        if (project.Length == 0)
        {
            SetStatus(_path.Length == 0 ? "Save the file inside a project first" : "This file is not inside a project", ErrorColor);
            return;
        }

        if (_doc.Modified && !Save())
        {
            return;
        }

        ClearLog();
        bool ok = Projects.Build(project);
        ParseOutput(project, Projects.Output(), ok);
        if (!ok)
        {
            _logVisible = true;
            SetStatus("Build failed: " + Count(0).ToString() + " error(s)", ErrorColor);
            return;
        }

        if (Count(1) > 0)
        {
            _logVisible = true;
        }

        SetStatus("Build succeeded" + (Count(1) > 0 ? ", " + Count(1).ToString() + " warning(s)" : ""), InfoColor);
        if (run)
        {
            string error = Projects.Run(Projects.LastCartridge());
            if (error.Length > 0)
            {
                AddLog(0, "", -1, 0, error);
                _logVisible = true;
            }
        }
    }

    // The nearest directory above the file that holds a project, or "".
    private static string ProjectOf(string path)
    {
        if (path.Length == 0)
        {
            return "";
        }

        string dir = Parent(path);
        while (true)
        {
            if (Projects.IsProject(dir))
            {
                return dir;
            }

            if (dir == "/")
            {
                return "";
            }

            dir = Parent(dir);
        }
    }

    // Lines like "code/Game.cs(12,5): error CS1002: ; expected" become log entries that jump to the place.
    private void ParseOutput(string project, string output, bool ok)
    {
        List<string> lines = Strings.Split(Strings.Replace(output, "\r", ""), '\n');
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (Strings.Trim(line).Length == 0)
            {
                continue;
            }

            int open = Strings.IndexOf(line, '(');
            int close = Strings.IndexOf(line, "):");
            int kind = Strings.Contains(line, ": error ") ? 0 : Strings.Contains(line, ": warning ") ? 1 : (ok ? 2 : 0);
            if (open > 0 && close > open)
            {
                string file = Strings.Sub(line, 0, open);
                List<string> place = Strings.Split(Strings.Sub(line, open + 1, close - open - 1), ',');
                int lineNumber, column;
                if (place.Count == 2 && Strings.TryParseInt(place[0], out lineNumber) && Strings.TryParseInt(place[1], out column))
                {
                    string message = Strings.Trim(Strings.Sub(line, close + 2));
                    AddLog(kind, Strings.StartsWith(file, "<core>") ? "" : Join(project, file), lineNumber - 1, column - 1, message);
                    continue;
                }
            }

            AddLog(kind, "", -1, 0, line);
        }
    }

    private void JumpToLog(int index)
    {
        if (_logFile[index].Length > 0)
        {
            RequestOpen(_logFile[index], _logLine[index], _logColumn[index]);
        }

        SetStatus(_logText[index], _logKind[index] == 0 ? ErrorColor : _logKind[index] == 1 ? WarningColor : InfoColor);
    }

    private void ClearLog()
    {
        _logKind.Clear();
        _logFile.Clear();
        _logLine.Clear();
        _logColumn.Clear();
        _logText.Clear();
        _logIndex = 0;
        _logScroll = 0;
    }

    private void AddLog(int kind, string file, int line, int column, string text)
    {
        _logKind.Add(kind);
        _logFile.Add(file);
        _logLine.Add(line);
        _logColumn.Add(column);
        _logText.Add(text);
    }

    private int Count(int kind)
    {
        int n = 0;
        for (int i = 0; i < _logKind.Count; i++)
        {
            if (_logKind[i] == kind)
            {
                n++;
            }
        }

        return n;
    }

    // Worst problem on a line of the open file: 0 error, 1 warning, -1 none.
    private int ProblemAt(int line)
    {
        int worst = -1;
        for (int i = 0; i < _logKind.Count; i++)
        {
            if (_logLine[i] == line && _logKind[i] < 2 && _logFile[i] == _path && (worst < 0 || _logKind[i] < worst))
            {
                worst = _logKind[i];
            }
        }

        return worst;
    }

    private string ProblemTextAt(int line)
    {
        for (int i = 0; i < _logKind.Count; i++)
        {
            if (_logLine[i] == line && _logKind[i] < 2 && _logFile[i] == _path)
            {
                return _logText[i];
            }
        }

        return "";
    }

    // ---- layout -----------------------------------------------------------------------------------------

    // Top edge (exclusive) of the text area: below the toolbar and the find bar.
    private int EditorTop() => Screen - ToolbarHeight - (_panel == PanelFind ? LineHeight + 2 : 0);

    // Bottom edge of the text area: above the status bar and the log.
    private int EditorBottom() => StatusHeight + (_logVisible ? LogRows * LineHeight : 0);

    private int EditorRows() => (EditorTop() - EditorBottom()) / LineHeight;

    private void KeepCursorVisible()
    {
        int rows = EditorRows();
        if (_doc.Line < _scrollTop)
        {
            _scrollTop = _doc.Line;
        }

        if (_doc.Line >= _scrollTop + rows)
        {
            _scrollTop = _doc.Line - rows + 1;
        }

        if (_doc.Column < _scrollLeft)
        {
            _scrollLeft = _doc.Column;
        }

        if (_doc.Column >= _scrollLeft + Columns - 1)
        {
            _scrollLeft = _doc.Column - Columns + 2;
        }
    }

    // ---- render -------------------------------------------------------------------------------------------

    public void Render()
    {
        Graphics.Clear(Layer);
        _highlighter.Update(_doc);
        UpdateBrackets();
        DrawText();
        DrawCompletion();
        DrawToolbar();
        DrawStatus();
        if (_logVisible)
        {
            DrawLog();
        }

        if (_panel == PanelFind)
        {
            DrawFind();
        }
        else if (_panel == PanelExplorer)
        {
            DrawExplorer();
        }
        else if (_panel == PanelPrompt)
        {
            DrawPrompt();
        }
        else if (_panel == PanelConfirm)
        {
            DrawConfirm();
        }
    }

    private void DrawText()
    {
        int rows = EditorRows();
        int top = EditorTop();
        int selStartLine = 0, selStartColumn = 0, selEndLine = -1, selEndColumn = 0;
        if (_doc.HasSelection)
        {
            _doc.SelectionRange(out selStartLine, out selStartColumn, out selEndLine, out selEndColumn);
        }

        for (int r = 0; r < rows; r++)
        {
            int line = _scrollTop + r;
            if (line >= _doc.Lines.Count)
            {
                break;
            }

            int y = top - ((r + 1) * LineHeight);
            if (line == _doc.Line)
            {
                Graphics.FillRect(Layer, GutterWidth, y, Screen - GutterWidth, LineHeight, CurrentLine);
            }

            if (line >= selStartLine && line <= selEndLine)
            {
                int from = line == selStartLine ? selStartColumn : 0;
                int to = line == selEndLine ? selEndColumn : _doc.Lines[line].Length + 1;
                int x0 = GutterWidth + ((from - _scrollLeft) * CharWidth);
                int x1 = GutterWidth + ((to - _scrollLeft) * CharWidth);
                if (x0 < GutterWidth)
                {
                    x0 = GutterWidth;
                }

                if (x1 > x0)
                {
                    Graphics.FillRect(Layer, x0, y, x1 - x0, LineHeight, Selection);
                }
            }

            DrawBracketBackground(line, y);
            string[] texts = _highlighter.SpanTexts(line);
            int[] starts = _highlighter.SpanStarts(line);
            int[] colors = _highlighter.SpanColors(line);
            for (int s = 0; s < texts.Length; s++)
            {
                int end = starts[s] + texts[s].Length;
                if (end <= _scrollLeft || starts[s] >= _scrollLeft + Columns + 1)
                {
                    continue;
                }

                Text.Draw(Layer, texts[s], GutterWidth + ((starts[s] - _scrollLeft) * CharWidth), y + 1, colors[s], Font);
            }

            DrawUnderlines(line, y);
        }

        // Gutter: line numbers (drawn over any text scrolled under it).
        Graphics.FillRect(Layer, 0, EditorBottom(), GutterWidth - 2, top - EditorBottom(), GutterBack);
        for (int r = 0; r < rows && _scrollTop + r < _doc.Lines.Count; r++)
        {
            int line = _scrollTop + r;
            int y = top - ((r + 1) * LineHeight);
            string number = Strings.PadLeft((line + 1).ToString(), 4);
            int problem = WorstProblem(line);
            int color = problem == 0 ? ErrorColor : problem == 1 ? WarningColor : line == _doc.Line ? Cursor : GutterText;
            Text.Draw(Layer, number, 0, y + 1, color, Font);
        }

        // Cursor.
        if (_panel == PanelNone && (_blink / 24) % 2 == 0)
        {
            int row = _doc.Line - _scrollTop;
            if (row >= 0 && row < rows)
            {
                int x = GutterWidth + ((_doc.Column - _scrollLeft) * CharWidth) - 1;
                if (x >= GutterWidth - 1)
                {
                    Graphics.FillRect(Layer, x, top - ((row + 1) * LineHeight), 1, LineHeight, Cursor);
                }
            }
        }
    }

    // Live problems: a line under the exact range (warnings yellow, errors red, drawn last so they win).
    private void DrawUnderlines(int line, int y)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < _pLine.Count; i++)
            {
                if (_pError[i] != (pass == 1) || line < _pLine[i] || line > _pEndLine[i])
                {
                    continue;
                }

                int from = line == _pLine[i] ? _pColumn[i] : Document.Indent(_doc.Lines[line]);
                int to = line == _pEndLine[i] ? _pEndColumn[i] : _doc.Lines[line].Length;
                if (to <= from)
                {
                    to = from + 1;
                }

                int x0 = GutterWidth + ((from - _scrollLeft) * CharWidth);
                int x1 = GutterWidth + ((to - _scrollLeft) * CharWidth);
                if (x0 < GutterWidth)
                {
                    x0 = GutterWidth;
                }

                // A dotted "squiggle": every other pixel, alternating rows.
                for (int x = x0; x < x1 && x < Screen; x++)
                {
                    Graphics.SetPixel(Layer, x, y + ((x / 2) % 2), _pError[i] ? ErrorColor : WarningColor);
                }
            }
        }
    }

    // ---- bracket pairs ----------------------------------------------------------------------------------

    private const int BracketColor = 27;
    private const int MaxBracketLines = 3000;
    private int _bracketRevision = -1, _bracketCursorLine = -1, _bracketCursorColumn = -1;
    private bool _bracketSelection;
    private int _bracketLine = -1, _bracketColumn, _matchLine = -1, _matchColumn;

    // Finds the bracket next to the cursor (the one after it first, then the one before it; or a selected single
    // bracket) and its partner. Brackets in strings and comments do not count. Cached until cursor or text change.
    private void UpdateBrackets()
    {
        if (_doc.Revision == _bracketRevision && _doc.Line == _bracketCursorLine && _doc.Column == _bracketCursorColumn && _doc.HasSelection == _bracketSelection)
        {
            return;
        }

        _bracketRevision = _doc.Revision;
        _bracketCursorLine = _doc.Line;
        _bracketCursorColumn = _doc.Column;
        _bracketSelection = _doc.HasSelection;
        _bracketLine = -1;
        _matchLine = -1;

        int line = _doc.Line;
        if (_doc.HasSelection)
        {
            int startLine, startColumn, endLine, endColumn;
            _doc.SelectionRange(out startLine, out startColumn, out endLine, out endColumn);
            if (startLine == endLine && endColumn - startColumn == 1 && IsBracket(_doc.Lines[startLine][startColumn]) && IsCode(startLine, startColumn))
            {
                SetBracket(startLine, startColumn);
            }

            return;
        }

        string text = _doc.Lines[line];
        int column = _doc.Column;
        if (column < text.Length && IsBracket(text[column]) && IsCode(line, column))
        {
            SetBracket(line, column);
        }
        else if (column > 0 && IsBracket(text[column - 1]) && IsCode(line, column - 1))
        {
            SetBracket(line, column - 1);
        }
    }

    private void SetBracket(int line, int column)
    {
        _bracketLine = line;
        _bracketColumn = column;
        char c = _doc.Lines[line][column];
        char open = c == ')' ? '(' : c == '}' ? '{' : c == ']' ? '[' : c;
        char close = open == '(' ? ')' : open == '{' ? '}' : ']';
        bool forward = c == open;
        int depth = 0;
        int last = forward ? Document.Min(_doc.Lines.Count - 1, line + MaxBracketLines) : Document.Max(0, line - MaxBracketLines);
        for (int l = line; forward ? l <= last : l >= last; l += forward ? 1 : -1)
        {
            string[] texts = _highlighter.SpanTexts(l);
            int[] starts = _highlighter.SpanStarts(l);
            int[] colors = _highlighter.SpanColors(l);
            for (int si = 0; si < texts.Length; si++)
            {
                int s = forward ? si : texts.Length - 1 - si;
                if (colors[s] != Highlighter.Punctuation)
                {
                    continue;
                }

                string span = texts[s];
                for (int ci = 0; ci < span.Length; ci++)
                {
                    int k = forward ? ci : span.Length - 1 - ci;
                    int col = starts[s] + k;
                    if (l == line && (forward ? col < column : col > column))
                    {
                        continue;
                    }

                    char ch = span[k];
                    if (ch == open)
                    {
                        depth += forward ? 1 : -1;
                    }
                    else if (ch == close)
                    {
                        depth += forward ? -1 : 1;
                    }
                    else
                    {
                        continue;
                    }

                    if (depth == 0)
                    {
                        _matchLine = l;
                        _matchColumn = col;
                        return;
                    }
                }
            }
        }
    }

    // True if the character is outside strings and comments (it belongs to a punctuation span).
    private bool IsCode(int line, int column)
    {
        int[] starts = _highlighter.SpanStarts(line);
        string[] texts = _highlighter.SpanTexts(line);
        int[] colors = _highlighter.SpanColors(line);
        for (int s = 0; s < starts.Length; s++)
        {
            if (column >= starts[s] && column < starts[s] + texts[s].Length)
            {
                return colors[s] == Highlighter.Punctuation;
            }
        }

        return false;
    }

    private static bool IsBracket(char c) => c == '(' || c == ')' || c == '{' || c == '}' || c == '[' || c == ']';

    // Background behind the bracket and its partner (red when the bracket has none).
    private void DrawBracketBackground(int line, int y)
    {
        int color = _matchLine >= 0 ? BracketColor : ErrorColor;
        if (line == _bracketLine)
        {
            DrawCell(_bracketColumn, y, color);
        }

        if (line == _matchLine)
        {
            DrawCell(_matchColumn, y, color);
        }
    }

    private void DrawCell(int column, int y, int color)
    {
        int x = GutterWidth + ((column - _scrollLeft) * CharWidth) - 1;
        if (x >= GutterWidth - 1 && x < Screen)
        {
            Graphics.FillRect(Layer, x, y, CharWidth + 1, LineHeight, color);
        }
    }

    // Worst problem on a line from live analysis or the last build: 0 error, 1 warning, -1 none.
    private int WorstProblem(int line)
    {
        int worst = ProblemAt(line);
        for (int i = 0; i < _pLine.Count; i++)
        {
            if (line >= _pLine[i] && line <= _pEndLine[i])
            {
                int kind = _pError[i] ? 0 : 1;
                if (worst < 0 || kind < worst)
                {
                    worst = kind;
                }
            }
        }

        return worst;
    }

    private void DrawCompletion()
    {
        if (!_completionOpen || _cShown.Count == 0 || _panel != PanelNone)
        {
            return;
        }

        int rows = Document.Min(CompletionRows, _cShown.Count);
        int width = 34 * CharWidth;
        int height = (rows * LineHeight) + 2;
        int cursorRow = _doc.Line - _scrollTop;
        int lineBottom = EditorTop() - ((cursorRow + 1) * LineHeight);
        int x = GutterWidth + ((_completionStart - _scrollLeft) * CharWidth) - CharWidth - 3;
        x = Document.Clamp(x, 0, Screen - width);
        int y = lineBottom - height;                       // below the line…
        if (y < EditorBottom())
        {
            y = lineBottom + LineHeight;                   // …or above it
        }

        Box(x, y, width, height);
        (_popupX, _popupY, _popupWidth, _popupHeight, _popupRows) = (x, y, width, height, rows);
        for (int r = 0; r < rows; r++)
        {
            int index = _completionScroll + r;
            if (index >= _cShown.Count)
            {
                break;
            }

            int item = _cShown[index];
            int rowY = y + height - 1 - ((r + 1) * LineHeight);
            if (index == _completionSelected)
            {
                Graphics.FillRect(Layer, x + 1, rowY, width - 2, LineHeight, Selection);
            }

            int kind = _cKind[item];
            Text.Draw(Layer, KindLetter(kind), x + 2, rowY + 1, kind == 10 ? Highlighter.Keyword : Highlighter.KindColor(kind) == Highlighter.Plain ? GutterText : Highlighter.KindColor(kind), Font);
            string label = _cLabel[item];
            if (label.Length > 31)
            {
                label = Strings.Sub(label, 0, 30) + "..";
            }

            Text.Draw(Layer, label, x + 2 + (2 * CharWidth), rowY + 1, index == _completionSelected ? ButtonText : StatusText, Font);
        }

        if (_cShown.Count > rows)
        {
            // Scroll position bar on the right edge.
            int barHeight = Document.Max(2, (height - 2) * rows / _cShown.Count);
            int barY = y + height - 1 - barHeight - (((height - 2 - barHeight) * _completionScroll) / Document.Max(1, _cShown.Count - rows));
            Graphics.FillRect(Layer, x + width - 2, barY, 1, barHeight, PanelBorder);
        }
    }

    private void DrawToolbar()
    {
        Graphics.FillRect(Layer, 0, Screen - ToolbarHeight, Screen, ToolbarHeight, Bar);
        int mx = Input.MouseX, my = Input.MouseY;
        for (int i = 0; i < ButtonLabels.Length; i++)
        {
            bool hover = my >= Screen - ToolbarHeight && mx >= _buttonX[i] && mx < _buttonX[i] + _buttonWidth[i];
            bool active = (i == 0 && _panel == PanelExplorer) || (i == 5 && _logVisible) || (i == 6 && _panel == PanelFind);
            Graphics.FillRect(Layer, _buttonX[i], Screen - ToolbarHeight + 1, _buttonWidth[i], ToolbarHeight - 2, hover || active ? ButtonHover : ButtonBack);
            Text.Draw(Layer, ButtonLabels[i], _buttonX[i] + 3, Screen - ToolbarHeight + 2, ButtonText, Font);
        }
    }

    private void DrawStatus()
    {
        Graphics.FillRect(Layer, 0, 0, Screen, StatusHeight, Bar);
        string name = (_doc.Modified ? "*" : "") + (_path.Length == 0 ? "untitled" : FileName(_path));
        int errors = LiveCount(true), warnings = LiveCount(false);
        string counts = errors + warnings == 0 ? "" : errors.ToString() + "E " + warnings.ToString() + "W ";
        string position = counts + "L" + (_doc.Line + 1).ToString() + ":C" + (_doc.Column + 1).ToString();
        Text.Draw(Layer, name, 2, 1, TitleColor, Font);
        Text.Draw(Layer, position, Screen - (position.Length * CharWidth) - 2, 1, StatusText, Font);

        string message = _statusTicks > 0 ? _status : "";
        int color = _statusColor;
        int live = LiveProblemAtCursor();
        if (_completionOpen && _cShown.Count > 0)
        {
            message = _cDetail[_cShown[_completionSelected]];
            color = InfoColor;
        }
        else if (_statusTicks == 0 && live >= 0)
        {
            message = _pText[live];
            color = _pError[live] ? ErrorColor : WarningColor;
        }
        else if (_statusTicks == 0)
        {
            message = ProblemTextAt(_doc.Line);
            color = ProblemAt(_doc.Line) == 0 ? ErrorColor : WarningColor;
        }
        int x = 2 + ((name.Length + 2) * CharWidth);
        int room = ((Screen - (position.Length * CharWidth) - 6) - x) / CharWidth;
        if (message.Length > 0 && room > 3)
        {
            Text.Draw(Layer, message.Length > room ? Strings.Sub(message, 0, room - 2) + ".." : message, x, 1, color, Font);
        }
    }

    private void DrawLog()
    {
        int height = LogRows * LineHeight;
        Graphics.FillRect(Layer, 0, StatusHeight, Screen, height, PanelBack);
        Graphics.FillRect(Layer, 0, StatusHeight + height - 1, Screen, 1, PanelBorder);
        int top = StatusHeight + height - 1;
        string title = "LOG  " + Count(0).ToString() + " error(s)  " + Count(1).ToString() + " warning(s)   Esc hides";
        Text.Draw(Layer, title, 2, top - LineHeight + 1, TitleColor, Font);
        for (int r = 0; r < LogRows - 1; r++)
        {
            int index = _logScroll + r;
            if (index >= _logText.Count)
            {
                break;
            }

            int y = top - ((r + 2) * LineHeight);
            if (index == _logIndex)
            {
                Graphics.FillRect(Layer, 0, y, Screen, LineHeight, Selection);
            }

            int kind = _logKind[index];
            string prefix = kind == 0 ? "E " : kind == 1 ? "W " : "  ";
            string place = _logFile[index].Length > 0 ? FileName(_logFile[index]) + "(" + (_logLine[index] + 1).ToString() + ") " : "";
            string text = prefix + place + _logText[index];
            if (text.Length > 63)
            {
                text = Strings.Sub(text, 0, 61) + "..";
            }

            Text.Draw(Layer, text, 2, y + 1, kind == 0 ? ErrorColor : kind == 1 ? WarningColor : InfoColor, Font);
        }
    }

    private void DrawFind()
    {
        int y = Screen - ToolbarHeight - LineHeight - 2;
        Graphics.FillRect(Layer, 0, y, Screen, LineHeight + 2, PanelBack);
        Graphics.FillRect(Layer, 0, y, Screen, 1, PanelBorder);
        string text = "FIND: " + _findText;
        Text.Draw(Layer, text, 2, y + 2, ButtonText, Font);
        if ((_blink / 24) % 2 == 0)
        {
            Graphics.FillRect(Layer, 2 + (text.Length * CharWidth), y + 2, 1, LineHeight - 1, Cursor);
        }

        Text.Draw(Layer, "Enter/F3 next  Esc close", Screen - (24 * CharWidth) - 2, y + 2, GutterText, Font);
    }

    private void DrawExplorer()
    {
        int top = EditorTop() - 4;
        int bottom = StatusHeight + 4;
        Box(8, bottom, Screen - 16, top - bottom);
        Text.Draw(Layer, "EXPLORER  " + _explorerDir, 12, top - LineHeight - 1, TitleColor, Font);
        int rows = ExplorerRows();
        int listTop = EditorTop() - 14;
        for (int r = 0; r < rows; r++)
        {
            int index = _explorerScroll + r;
            if (index >= _entries.Count)
            {
                break;
            }

            int y = listTop - ((r + 1) * LineHeight);
            if (index == _explorerIndex)
            {
                Graphics.FillRect(Layer, 10, y, Screen - 20, LineHeight, Selection);
            }

            string entry = _entries[index];
            bool directory = Strings.EndsWith(entry, "/");
            bool project = directory && entry != "../" && Projects.IsProject(Join(_explorerDir, Strings.Sub(entry, 0, entry.Length - 1)));
            Text.Draw(Layer, (directory ? (project ? "[P] " : "[D] ") : "    ") + entry, 12, y + 1, directory ? Highlighter.Keyword : StatusText, Font);
        }

        Text.Draw(Layer, "Enter open  Backspace up  Esc close", 12, bottom + 2, GutterText, Font);
    }

    private void DrawPrompt()
    {
        Box(16, 108, Screen - 32, 34);
        Text.Draw(Layer, _promptTitle, 20, 132, TitleColor, Font);
        Graphics.FillRect(Layer, 20, 118, Screen - 40, 9, Background);
        string shown = _promptText.Length > 52 ? Strings.Sub(_promptText, _promptText.Length - 52) : _promptText;
        Text.Draw(Layer, shown, 22, 119, ButtonText, Font);
        if ((_blink / 24) % 2 == 0)
        {
            Graphics.FillRect(Layer, 22 + (shown.Length * CharWidth), 119, 1, LineHeight - 1, Cursor);
        }

        Text.Draw(Layer, "Enter save   Esc cancel", 20, 110, GutterText, Font);
    }

    private void DrawConfirm()
    {
        Box(40, 108, Screen - 80, 30);
        string name = _path.Length == 0 ? "untitled" : FileName(_path);
        Text.Draw(Layer, "Save this file?", 46, 128, TitleColor, Font);
        Text.Draw(Layer, name, 46, 120, StatusText, Font);
        Text.Draw(Layer, "Y yes  N no  Esc cancel", 46, 111, WarningColor, Font);
    }

    private void Box(int x, int y, int width, int height)
    {
        Graphics.FillRect(Layer, x, y, width, height, PanelBack);
        Graphics.FillRect(Layer, x, y, width, 1, PanelBorder);
        Graphics.FillRect(Layer, x, y + height - 1, width, 1, PanelBorder);
        Graphics.FillRect(Layer, x, y, 1, height, PanelBorder);
        Graphics.FillRect(Layer, x + width - 1, y, 1, height, PanelBorder);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private void SetStatus(string text, int color)
    {
        _status = text;
        _statusColor = color;
        _statusTicks = 240;
    }

    private static bool Ctrl() => Input.IsKeyDown(Key.LeftControl) || Input.IsKeyDown(Key.RightControl);

    private static bool Shift() => Input.IsKeyDown(Key.LeftShift) || Input.IsKeyDown(Key.RightShift);

    private static bool IsDigitChar(char c) => c >= '0' && c <= '9';

    // Length of the identifier that ends at the cursor.
    private int WordLengthBeforeCursor()
    {
        string line = _doc.Lines[_doc.Line];
        int i = _doc.Column;
        while (i > 0 && IsWord(line[i - 1]))
        {
            i--;
        }

        return _doc.Column - i;
    }

    // First character of the identifier that ends at the cursor (' ' if none).
    private char WordStartChar()
    {
        int length = WordLengthBeforeCursor();
        return length == 0 ? ' ' : _doc.Lines[_doc.Line][_doc.Column - length];
    }

    private static bool IsWord(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    private static string Parent(string path)
    {
        int slash = Strings.LastIndexOf(path, '/');
        return slash <= 0 ? "/" : Strings.Sub(path, 0, slash);
    }

    private static string Join(string directory, string name) => directory == "/" ? "/" + name : directory + "/" + name;

    private static string FileName(string path) => Strings.Sub(path, Strings.LastIndexOf(path, '/') + 1);
}
