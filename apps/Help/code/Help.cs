using ZTX16;
using ZTX16.Authoring;
using ZTX16.Collections;

namespace ZTX16Help;

/// <summary>
/// The ZTX16 help app (F1): the API reference of everything a cartridge can use — the SDK and the core library —
/// generated from their documentation. Types on the left, the selected type's members on the right; typing filters
/// by type or member name.
/// </summary>
/// <remarks>Keys: type to search, Backspace edits it, Up/Down choose a type, PgUp/PgDn or the wheel scroll, Esc clears the search or goes back.</remarks>
public sealed class Help : ICartridge
{
    private const int Layer = 0;
    private const string Font = "small";
    private const int Screen = 256;
    private const int CharWidth = 4;
    private const int LineHeight = 7;
    private const int TopBar = 11;
    private const int ListWidth = 76;
    private const int PaneX = ListWidth + 3;
    private const int PaneColumns = (Screen - PaneX - 2) / CharWidth;

    // Colours of the default palette.
    private const int Back = 31, Bar = 26, Ink = 4, Dim = 3, Title = 15, Accent = 11, Type = 17, Select = 18, Search = 5;

    // The reference: per type its name, kind, summary and its members' range.
    private readonly List<string> _typeName = new();
    private readonly List<string> _typeKind = new();
    private readonly List<string> _typeSummary = new();
    private readonly List<int> _memberStart = new();
    private readonly List<int> _memberEnd = new();
    private readonly List<string> _signature = new();
    private readonly List<string> _summary = new();

    // Current view.
    private readonly List<int> _visible = new();         // filtered type indices
    private readonly List<string> _paneLines = new();
    private readonly List<int> _paneColors = new();
    private string _query = "";
    private int _selected;                               // index into _visible
    private int _listScroll;
    private int _paneScroll;

    public void Initialize()
    {
        Load(Reference.Text());
        Filter();
    }

    private void Load(string text)
    {
        List<string> lines = Strings.Split(text, '\n');
        for (int i = 0; i < lines.Count; i++)
        {
            List<string> f = Strings.Split(lines[i], '\t');
            if (f.Count >= 4 && f[0] == "T")
            {
                if (_typeName.Count > 0)
                {
                    _memberEnd.Add(_signature.Count);
                }

                _typeName.Add(f[1]);
                _typeKind.Add(f[2]);
                _typeSummary.Add(f[3]);
                _memberStart.Add(_signature.Count);
            }
            else if (f.Count >= 3 && f[0] == "M" && _typeName.Count > 0)
            {
                _signature.Add(f[1]);
                _summary.Add(f[2]);
            }
        }

        if (_typeName.Count > 0)
        {
            _memberEnd.Add(_signature.Count);
        }
    }

    public void Tick()
    {
        string typed = Input.GetText();
        if (typed.Length > 0)
        {
            _query += typed;
            Filter();
        }

        if (Input.WasKeyTyped(Key.Backspace) && _query.Length > 0)
        {
            _query = Strings.Sub(_query, 0, _query.Length - 1);
            Filter();
        }

        if (Input.WasKeyPressed(Key.Escape))
        {
            if (_query.Length > 0)
            {
                _query = "";
                Filter();
            }
            else
            {
                Apps.Back();
            }
        }

        if (Input.WasKeyTyped(Key.Down) && _selected < _visible.Count - 1)
        {
            _selected++;
            BuildPane();
        }

        if (Input.WasKeyTyped(Key.Up) && _selected > 0)
        {
            _selected--;
            BuildPane();
        }

        int paneRows = (Screen - TopBar - 4) / LineHeight;
        if (Input.WasKeyTyped(Key.PageDown)) _paneScroll += paneRows - 2;
        if (Input.WasKeyTyped(Key.PageUp)) _paneScroll -= paneRows - 2;
        if (Input.WasKeyPressed(Key.Home)) _paneScroll = 0;

        int mx = Input.MouseX, my = Input.MouseY;
        int wheel = Input.MouseWheel;
        if (wheel != 0)
        {
            if (mx < ListWidth)
            {
                _listScroll -= wheel * 3;
            }
            else
            {
                _paneScroll -= wheel * 3;
            }
        }

        if (Input.WasMouseButtonPressed(MouseButton.Left) && mx < ListWidth && my < Screen - TopBar)
        {
            int row = (Screen - TopBar - 2 - my) / LineHeight;
            int index = _listScroll + row;
            if (row >= 0 && index < _visible.Count)
            {
                _selected = index;
                BuildPane();
            }
        }

        int listRows = (Screen - TopBar - 4) / LineHeight;
        _listScroll = Clamp(_listScroll, 0, Max(0, _visible.Count - listRows));
        if (_selected < _listScroll) _listScroll = _selected;
        if (_selected >= _listScroll + listRows) _listScroll = _selected - listRows + 1;
        _paneScroll = Clamp(_paneScroll, 0, Max(0, _paneLines.Count - paneRows));
    }

    // Types whose name, or one of whose members, contains the query (case-insensitive).
    private void Filter()
    {
        string query = Strings.ToLower(_query);
        _visible.Clear();
        for (int t = 0; t < _typeName.Count; t++)
        {
            if (query.Length == 0 || Strings.Contains(Strings.ToLower(_typeName[t]), query) || MemberMatches(t, query))
            {
                _visible.Add(t);
            }
        }

        _selected = 0;
        _listScroll = 0;
        BuildPane();
    }

    private bool MemberMatches(int type, string query)
    {
        for (int m = _memberStart[type]; m < _memberEnd[type]; m++)
        {
            if (Strings.Contains(Strings.ToLower(MemberName(_signature[m])), query))
            {
                return true;
            }
        }

        return false;
    }

    // The name in a signature: the word before '(' (methods) or the last word before '=' / '{' (others).
    private static string MemberName(string signature)
    {
        int end = Strings.IndexOf(signature, '(');
        if (end < 0)
        {
            end = Strings.IndexOf(signature, " {");
        }

        if (end < 0)
        {
            end = Strings.IndexOf(signature, " =");
        }

        if (end < 0)
        {
            end = signature.Length;
        }

        int start = end;
        while (start > 0 && signature[start - 1] != ' ')
        {
            start--;
        }

        return Strings.Sub(signature, start, end - start);
    }

    // The right pane: the selected type and its members (only matching ones while searching), word-wrapped.
    private void BuildPane()
    {
        _paneLines.Clear();
        _paneColors.Clear();
        _paneScroll = 0;
        if (_visible.Count == 0)
        {
            Add("Nothing matches \"" + _query + "\".", Dim);
            return;
        }

        int t = _visible[_selected];
        Add(_typeKind[t] + " " + _typeName[t], Title);
        Wrap(_typeSummary[t], Ink, "");
        Add("", Ink);
        string query = Strings.ToLower(_query);
        bool typeMatches = query.Length == 0 || Strings.Contains(Strings.ToLower(_typeName[t]), query);
        for (int m = _memberStart[t]; m < _memberEnd[t]; m++)
        {
            if (!typeMatches && !Strings.Contains(Strings.ToLower(MemberName(_signature[m])), query))
            {
                continue;
            }

            Wrap(_signature[m], Type, "");
            if (_summary[m].Length > 0)
            {
                Wrap(_summary[m], Dim, "  ");
            }
        }
    }

    private void Add(string line, int color)
    {
        _paneLines.Add(line);
        _paneColors.Add(color);
    }

    // Word wrap to the pane width; continuation lines get 'indent' (plus two spaces).
    private void Wrap(string text, int color, string indent)
    {
        int width = PaneColumns - indent.Length;
        string rest = text;
        bool first = true;
        while (rest.Length > 0)
        {
            int room = first ? width : width - 2;
            string prefix = first ? indent : indent + "  ";
            if (rest.Length <= room)
            {
                Add(prefix + rest, color);
                return;
            }

            int cut = room;
            while (cut > 0 && rest[cut] != ' ' && rest[cut] != ',' && rest[cut] != '(')
            {
                cut--;
            }

            if (cut <= room / 3)
            {
                cut = room;                      // no good break: split the word
            }
            else if (rest[cut] != ' ')
            {
                cut++;                           // keep ',' or '(' on this line
            }

            Add(prefix + Strings.Sub(rest, 0, cut), color);
            rest = Strings.Trim(Strings.Sub(rest, cut));
            first = false;
        }
    }

    public void Render()
    {
        Graphics.Clear(Layer);
        Graphics.FillRect(Layer, 0, 0, Screen, Screen, Back);

        // Top bar: title and search.
        Graphics.FillRect(Layer, 0, Screen - TopBar, Screen, TopBar, Bar);
        Text.Draw(Layer, "ZTX16 API REFERENCE", 3, Screen - TopBar + 2, Title, Font);
        string search = _query.Length > 0 ? "search: " + _query : "type to search";
        Text.Draw(Layer, search, Screen - (search.Length * CharWidth) - 4, Screen - TopBar + 2, _query.Length > 0 ? Search : Dim, Font);

        // Type list.
        Graphics.FillRect(Layer, ListWidth, 0, 1, Screen - TopBar, Bar);
        int listRows = (Screen - TopBar - 4) / LineHeight;
        for (int r = 0; r < listRows; r++)
        {
            int index = _listScroll + r;
            if (index >= _visible.Count)
            {
                break;
            }

            int y = Screen - TopBar - 2 - ((r + 1) * LineHeight);
            if (index == _selected)
            {
                Graphics.FillRect(Layer, 0, y, ListWidth, LineHeight, Select);
            }

            string name = ShortName(_typeName[_visible[index]]);
            if (name.Length > 18)
            {
                name = Strings.Sub(name, 0, 17) + ".";
            }

            Text.Draw(Layer, name, 2, y + 1, index == _selected ? Accent : Ink, Font);
        }

        // The selected type.
        int paneRows = (Screen - TopBar - 4) / LineHeight;
        for (int r = 0; r < paneRows; r++)
        {
            int index = _paneScroll + r;
            if (index >= _paneLines.Count)
            {
                break;
            }

            int y = Screen - TopBar - 2 - ((r + 1) * LineHeight);
            Text.Draw(Layer, _paneLines[index], PaneX, y + 1, _paneColors[index], Font);
        }

        // Scroll position of the pane.
        if (_paneLines.Count > paneRows)
        {
            int track = Screen - TopBar - 4;
            int thumb = Max(6, track * paneRows / _paneLines.Count);
            int position = (track - thumb) * _paneScroll / Max(1, _paneLines.Count - paneRows);
            Graphics.FillRect(Layer, Screen - 2, Screen - TopBar - 2 - position - thumb, 2, thumb, Bar);
        }
    }

    // "ZTX16.Collections.List<T>" → "List<T>"; types of other namespaces keep a short prefix.
    private static string ShortName(string name)
    {
        if (Strings.StartsWith(name, "ZTX16.Authoring."))
        {
            return "Auth." + Strings.Sub(name, 16);
        }

        if (Strings.StartsWith(name, "ZTX16.Collections."))
        {
            return Strings.Sub(name, 18);
        }

        return Strings.StartsWith(name, "ZTX16.") ? Strings.Sub(name, 6) : name;
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

    private static int Max(int a, int b) => a > b ? a : b;
}
