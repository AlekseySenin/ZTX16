using ZTX16;
using ZTX16.Collections;

namespace ZTX16Code;

/// <summary>
/// The text being edited: lines, cursor, selection and undo/redo. Positions are (line, column) with columns
/// counted in characters. Tabs are expanded to spaces when text comes in.
/// </summary>
public sealed class Document
{
    private const int MaxUndo = 100;
    private const int TabSize = 4;

    private readonly List<string> _undoText = new();
    private readonly List<int> _undoLine = new();
    private readonly List<int> _undoColumn = new();
    private readonly List<string> _redoText = new();
    private readonly List<int> _redoLine = new();
    private readonly List<int> _redoColumn = new();
    private bool _typing;

    public Document()
    {
        Lines.Add("");
    }

    /// <summary>The text, one string per line (never empty: at least one line).</summary>
    public List<string> Lines { get; } = new();

    /// <summary>Cursor line.</summary>
    public int Line { get; private set; }

    /// <summary>Cursor column.</summary>
    public int Column { get; private set; }

    /// <summary>Selection anchor line (the other end is the cursor).</summary>
    public int AnchorLine { get; private set; }

    /// <summary>Selection anchor column.</summary>
    public int AnchorColumn { get; private set; }

    /// <summary>True while a selection exists.</summary>
    public bool HasSelection { get; private set; }

    /// <summary>True if the text changed since it was loaded or saved.</summary>
    public bool Modified { get; set; }

    /// <summary>First line whose highlighting is out of date (int.MaxValue if none). The highlighter resets it.</summary>
    public int DirtyFrom { get; set; }

    /// <summary>Replaces the whole text (undo history cleared).</summary>
    public void Load(string text)
    {
        Lines.Clear();
        List<string> parts = Strings.Split(Strings.Replace(Strings.Replace(text, "\r", ""), "\t", "    "), '\n');
        for (int i = 0; i < parts.Count; i++)
        {
            Lines.Add(parts[i]);
        }

        Line = 0;
        Column = 0;
        HasSelection = false;
        Modified = false;
        DirtyFrom = 0;
        _undoText.Clear();
        _undoLine.Clear();
        _undoColumn.Clear();
        _redoText.Clear();
        _redoLine.Clear();
        _redoColumn.Clear();
    }

    /// <summary>The whole text with '\n' line ends.</summary>
    public string Text() => Strings.Join(Lines, "\n");

    // ---- cursor ---------------------------------------------------------------------------------------

    /// <summary>Moves the cursor; with <paramref name="select"/> the selection extends, otherwise it ends.</summary>
    public void MoveTo(int line, int column, bool select)
    {
        if (select && !HasSelection)
        {
            AnchorLine = Line;
            AnchorColumn = Column;
            HasSelection = true;
        }
        else if (!select)
        {
            HasSelection = false;
        }

        Line = Clamp(line, 0, Lines.Count - 1);
        Column = Clamp(column, 0, Lines[Line].Length);
        if (HasSelection && Line == AnchorLine && Column == AnchorColumn)
        {
            HasSelection = false;
        }

        _typing = false;
    }

    public void Left(bool select)
    {
        if (HasSelection && !select)
        {
            int startLine, startColumn, endLine, endColumn;
            SelectionRange(out startLine, out startColumn, out endLine, out endColumn);
            MoveTo(startLine, startColumn, false);
        }
        else if (Column > 0)
        {
            MoveTo(Line, Column - 1, select);
        }
        else if (Line > 0)
        {
            MoveTo(Line - 1, Lines[Line - 1].Length, select);
        }
    }

    public void Right(bool select)
    {
        if (HasSelection && !select)
        {
            int startLine, startColumn, endLine, endColumn;
            SelectionRange(out startLine, out startColumn, out endLine, out endColumn);
            MoveTo(endLine, endColumn, false);
        }
        else if (Column < Lines[Line].Length)
        {
            MoveTo(Line, Column + 1, select);
        }
        else if (Line < Lines.Count - 1)
        {
            MoveTo(Line + 1, 0, select);
        }
    }

    /// <summary>Home: first non-space character, or column 0 if already there.</summary>
    public void Home(bool select)
    {
        int indent = Indent(Lines[Line]);
        MoveTo(Line, Column == indent ? 0 : indent, select);
    }

    public void SelectAll()
    {
        MoveTo(0, 0, false);
        MoveTo(Lines.Count - 1, Lines[Lines.Count - 1].Length, true);
    }

    /// <summary>The selection as (start, end) in text order.</summary>
    public void SelectionRange(out int startLine, out int startColumn, out int endLine, out int endColumn)
    {
        bool anchorFirst = AnchorLine < Line || (AnchorLine == Line && AnchorColumn < Column);
        startLine = anchorFirst ? AnchorLine : Line;
        startColumn = anchorFirst ? AnchorColumn : Column;
        endLine = anchorFirst ? Line : AnchorLine;
        endColumn = anchorFirst ? Column : AnchorColumn;
    }

    /// <summary>The selected text ("" if none).</summary>
    public string SelectedText()
    {
        if (!HasSelection)
        {
            return "";
        }

        int startLine, startColumn, endLine, endColumn;
        SelectionRange(out startLine, out startColumn, out endLine, out endColumn);
        if (startLine == endLine)
        {
            return Strings.Sub(Lines[startLine], startColumn, endColumn - startColumn);
        }

        var parts = new List<string>();
        parts.Add(Strings.Sub(Lines[startLine], startColumn));
        for (int i = startLine + 1; i < endLine; i++)
        {
            parts.Add(Lines[i]);
        }

        parts.Add(Strings.Sub(Lines[endLine], 0, endColumn));
        return Strings.Join(parts, "\n");
    }

    // ---- editing ----------------------------------------------------------------------------------------

    /// <summary>Types text at the cursor (replacing the selection); consecutive typing undoes as one step.</summary>
    public void Type(string text)
    {
        if (!_typing || HasSelection)
        {
            Snapshot();
        }

        DeleteSelectionNoUndo();
        if (text == "}" && Strings.Trim(Lines[Line]).Length == 0 && Column >= TabSize && Column == Lines[Line].Length)
        {
            // A closing brace on an empty line moves back one indentation level.
            Lines[Line] = Strings.Sub(Lines[Line], 0, Column - TabSize);
            Column -= TabSize;
        }

        InsertNoUndo(text);
        _typing = true;
    }

    /// <summary>Inserts text (a paste: may span lines) as one undo step.</summary>
    public void Insert(string text)
    {
        Snapshot();
        DeleteSelectionNoUndo();
        InsertNoUndo(Strings.Replace(Strings.Replace(text, "\r", ""), "\t", "    "));
    }

    /// <summary>Enter: splits the line, keeping the indentation (one level deeper after '{').</summary>
    public void NewLine()
    {
        Snapshot();
        DeleteSelectionNoUndo();
        string line = Lines[Line];
        string indent = Strings.Repeat(' ', Indent(line));
        string before = Strings.Sub(line, 0, Column);
        string after = Strings.Sub(line, Column);
        if (Strings.EndsWith(Strings.Trim(before), "{"))
        {
            indent += "    ";
        }

        Lines[Line] = before;
        Lines.Insert(Line + 1, indent + Strings.Trim(after));
        Line++;
        Column = indent.Length;
        Changed(Line - 1);
    }

    public void Backspace()
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }

        if (Column == 0 && Line == 0)
        {
            return;
        }

        if (!_typing)
        {
            Snapshot();
        }

        if (Column == 0)
        {
            string previous = Lines[Line - 1];
            Lines[Line - 1] = previous + Lines[Line];
            Lines.RemoveAt(Line);
            Line--;
            Column = previous.Length;
        }
        else
        {
            string line = Lines[Line];
            int remove = 1;
            if (Strings.Trim(Strings.Sub(line, 0, Column)).Length == 0 && Column % TabSize == 0)
            {
                remove = TabSize;     // in the indentation: one level at a time
            }

            Lines[Line] = Strings.Sub(line, 0, Column - remove) + Strings.Sub(line, Column);
            Column -= remove;
        }

        Changed(Line);
        _typing = true;
    }

    public void Delete()
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }

        string line = Lines[Line];
        if (Column == line.Length && Line == Lines.Count - 1)
        {
            return;
        }

        Snapshot();
        if (Column == line.Length)
        {
            Lines[Line] = line + Lines[Line + 1];
            Lines.RemoveAt(Line + 1);
        }
        else
        {
            Lines[Line] = Strings.Sub(line, 0, Column) + Strings.Sub(line, Column + 1);
        }

        Changed(Line);
    }

    public void DeleteSelection()
    {
        if (!HasSelection)
        {
            return;
        }

        Snapshot();
        DeleteSelectionNoUndo();
    }

    /// <summary>Tab: spaces to the next tab stop, or indents every selected line.</summary>
    public void Tab(bool outdent)
    {
        if (!HasSelection && !outdent)
        {
            Type(Strings.Repeat(' ', TabSize - (Column % TabSize)));
            _typing = false;
            return;
        }

        Snapshot();
        int startLine = Line, endLine = Line;
        if (HasSelection)
        {
            int startColumn, endColumn;
            SelectionRange(out startLine, out startColumn, out endLine, out endColumn);
            if (endColumn == 0 && endLine > startLine)
            {
                endLine--;
            }
        }

        for (int i = startLine; i <= endLine; i++)
        {
            string line = Lines[i];
            if (outdent)
            {
                int remove = Min(Indent(line), TabSize);
                Lines[i] = Strings.Sub(line, remove);
                if (i == Line)
                {
                    Column = Max(0, Column - remove);
                }

                if (HasSelection && i == AnchorLine)
                {
                    AnchorColumn = Max(0, AnchorColumn - remove);
                }
            }
            else if (line.Length > 0)
            {
                Lines[i] = "    " + line;
                if (i == Line)
                {
                    Column += TabSize;
                }

                if (HasSelection && i == AnchorLine && AnchorColumn > 0)
                {
                    AnchorColumn += TabSize;
                }
            }
        }

        Column = Clamp(Column, 0, Lines[Line].Length);
        Changed(startLine);
    }

    // ---- undo -------------------------------------------------------------------------------------------

    public bool CanUndo => _undoText.Count > 0;

    public void Undo() => Swap(_undoText, _undoLine, _undoColumn, _redoText, _redoLine, _redoColumn);

    public void Redo() => Swap(_redoText, _redoLine, _redoColumn, _undoText, _undoLine, _undoColumn);

    private void Swap(List<string> fromText, List<int> fromLine, List<int> fromColumn, List<string> toText, List<int> toLine, List<int> toColumn)
    {
        if (fromText.Count == 0)
        {
            return;
        }

        toText.Add(Text());
        toLine.Add(Line);
        toColumn.Add(Column);
        int last = fromText.Count - 1;
        string text = fromText[last];
        int line = fromLine[last], column = fromColumn[last];
        fromText.RemoveAt(last);
        fromLine.RemoveAt(last);
        fromColumn.RemoveAt(last);

        Lines.Clear();
        List<string> parts = Strings.Split(text, '\n');
        for (int i = 0; i < parts.Count; i++)
        {
            Lines.Add(parts[i]);
        }

        HasSelection = false;
        Line = Clamp(line, 0, Lines.Count - 1);
        Column = Clamp(column, 0, Lines[Line].Length);
        Modified = true;
        DirtyFrom = 0;
        _typing = false;
    }

    private void Snapshot()
    {
        _undoText.Add(Text());
        _undoLine.Add(Line);
        _undoColumn.Add(Column);
        if (_undoText.Count > MaxUndo)
        {
            _undoText.RemoveAt(0);
            _undoLine.RemoveAt(0);
            _undoColumn.RemoveAt(0);
        }

        _redoText.Clear();
        _redoLine.Clear();
        _redoColumn.Clear();
    }

    // ---- internals ----------------------------------------------------------------------------------------

    private void InsertNoUndo(string text)
    {
        string line = Lines[Line];
        string before = Strings.Sub(line, 0, Column);
        string after = Strings.Sub(line, Column);
        List<string> parts = Strings.Split(text, '\n');
        if (parts.Count == 1)
        {
            Lines[Line] = before + text + after;
            Column += text.Length;
        }
        else
        {
            Lines[Line] = before + parts[0];
            for (int i = 1; i < parts.Count; i++)
            {
                Lines.Insert(Line + i, parts[i]);
            }

            int last = Line + parts.Count - 1;
            Column = Lines[last].Length;
            Lines[last] = Lines[last] + after;
            Changed(Line);
            Line = last;
        }

        Changed(Line);
    }

    private void DeleteSelectionNoUndo()
    {
        if (!HasSelection)
        {
            return;
        }

        int startLine, startColumn, endLine, endColumn;
        SelectionRange(out startLine, out startColumn, out endLine, out endColumn);
        Lines[startLine] = Strings.Sub(Lines[startLine], 0, startColumn) + Strings.Sub(Lines[endLine], endColumn);
        for (int i = endLine; i > startLine; i--)
        {
            Lines.RemoveAt(i);
        }

        Line = startLine;
        Column = startColumn;
        HasSelection = false;
        Changed(startLine);
    }

    private void Changed(int fromLine)
    {
        Modified = true;
        if (fromLine < DirtyFrom)
        {
            DirtyFrom = fromLine;
        }
    }

    public static int Indent(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] == ' ')
        {
            n++;
        }

        return n;
    }

    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    public static int Min(int a, int b) => a < b ? a : b;

    public static int Max(int a, int b) => a > b ? a : b;
}
