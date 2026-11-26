using ZTX16;
using ZTX16.Collections;

namespace ZTX16Code;

/// <summary>
/// C# syntax colouring. Each line is split into coloured spans (text pieces) that are cached and rebuilt only
/// from the first changed line on; block comments carry over between lines.
/// </summary>
public sealed class Highlighter
{
    public const int Plain = 4;
    public const int Keyword = 20;
    public const int TypeName = 17;
    public const int StringColor = 9;
    public const int Comment = 14;
    public const int Number = 23;
    public const int Punctuation = 5;
    public const int Preprocessor = 24;

    private static readonly string[] Keywords =
    {
        "abstract", "as", "base", "break", "case", "catch", "checked", "class", "const", "continue", "default", "delegate",
        "do", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "for", "foreach", "goto", "if",
        "implicit", "in", "interface", "internal", "is", "lock", "namespace", "new", "null", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sealed", "sizeof", "stackalloc", "static",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "unchecked", "unsafe", "using", "virtual", "volatile",
        "while", "var", "get", "set", "init", "value", "record", "when", "yield", "partial", "where", "nameof", "async", "await",
    };

    private static readonly string[] Types =
    {
        "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "sbyte", "short", "string", "uint",
        "ulong", "ushort", "void",
    };

    private readonly Dictionary<string, int> _words = new();
    private readonly List<string[]> _spanText = new();
    private readonly List<int[]> _spanStart = new();
    private readonly List<int[]> _spanColor = new();
    private readonly List<bool> _blockAtEnd = new();

    // Scratch lists reused while splitting a line.
    private readonly List<int> _starts = new();
    private readonly List<int> _lengths = new();
    private readonly List<int> _colors = new();

    public Highlighter()
    {
        for (int i = 0; i < Keywords.Length; i++)
        {
            _words[Keywords[i]] = Keyword;
        }

        for (int i = 0; i < Types.Length; i++)
        {
            _words[Types[i]] = TypeName;
        }
    }

    /// <summary>Brings the cache up to date with the document (from its first dirty line).</summary>
    public void Update(Document document)
    {
        List<string> lines = document.Lines;
        int from = document.DirtyFrom;
        if (from > lines.Count)
        {
            from = lines.Count;
        }

        // Drop cached lines from the first change on, then rebuild them.
        while (_spanText.Count > from)
        {
            int last = _spanText.Count - 1;
            _spanText.RemoveAt(last);
            _spanStart.RemoveAt(last);
            _spanColor.RemoveAt(last);
            _blockAtEnd.RemoveAt(last);
        }

        for (int i = _spanText.Count; i < lines.Count; i++)
        {
            bool inBlock = i > 0 && _blockAtEnd[i - 1];
            _blockAtEnd.Add(Split(lines[i], inBlock));
            int count = _starts.Count;
            string[] texts = new string[count];
            int[] starts = new int[count];
            int[] colors = new int[count];
            for (int s = 0; s < count; s++)
            {
                texts[s] = Strings.Sub(lines[i], _starts[s], _lengths[s]);
                starts[s] = _starts[s];
                colors[s] = _colors[s];
            }

            _spanText.Add(texts);
            _spanStart.Add(starts);
            _spanColor.Add(colors);
        }

        document.DirtyFrom = int.MaxValue;
    }

    public string[] SpanTexts(int line) => _spanText[line];

    public int[] SpanStarts(int line) => _spanStart[line];

    public int[] SpanColors(int line) => _spanColor[line];

    // Splits one line into spans; returns whether a block comment is still open at its end.
    private bool Split(string line, bool inBlock)
    {
        _starts.Clear();
        _lengths.Clear();
        _colors.Clear();
        int i = 0;
        int n = line.Length;
        if (inBlock)
        {
            int close = Strings.IndexOf(line, "*/");
            if (close < 0)
            {
                Add(0, n, Comment);
                return true;
            }

            Add(0, close + 2, Comment);
            i = close + 2;
        }

        int firstNonSpace = Document.Indent(line);
        if (i == 0 && firstNonSpace < n && line[firstNonSpace] == '#')
        {
            Add(firstNonSpace, n - firstNonSpace, Preprocessor);
            return false;
        }

        while (i < n)
        {
            char c = line[i];
            if (c == ' ')
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < n && line[i + 1] == '/')
            {
                Add(i, n - i, Comment);
                return false;
            }

            if (c == '/' && i + 1 < n && line[i + 1] == '*')
            {
                int close = Strings.IndexOf(line, "*/", i + 2);
                if (close < 0)
                {
                    Add(i, n - i, Comment);
                    return true;
                }

                Add(i, close + 2 - i, Comment);
                i = close + 2;
                continue;
            }

            if (c == '"' || ((c == '@' || c == '$') && i + 1 < n && line[i + 1] == '"'))
            {
                bool verbatim = c == '@';
                int start = i;
                i += c == '"' ? 1 : 2;
                while (i < n)
                {
                    if (line[i] == '\\' && !verbatim)
                    {
                        i += 2;
                        continue;
                    }

                    if (line[i] == '"')
                    {
                        if (verbatim && i + 1 < n && line[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                if (i > n)
                {
                    i = n;
                }

                Add(start, i - start, StringColor);
                continue;
            }

            if (c == '\'')
            {
                int start = i;
                i++;
                while (i < n && line[i] != '\'')
                {
                    i += line[i] == '\\' ? 2 : 1;
                }

                i = i + 1 > n ? n : i + 1;
                Add(start, i - start, StringColor);
                continue;
            }

            if (IsDigit(c))
            {
                int start = i;
                while (i < n && (IsWordChar(line[i]) || line[i] == '.'))
                {
                    i++;
                }

                Add(start, i - start, Number);
                continue;
            }

            if (IsWordStart(c))
            {
                int start = i;
                while (i < n && IsWordChar(line[i]))
                {
                    i++;
                }

                string word = Strings.Sub(line, start, i - start);
                int color;
                Add(start, i - start, _words.TryGetValue(word, out color) ? color : Plain);
                continue;
            }

            // Punctuation: a run of symbols in one span.
            int symbolStart = i;
            while (i < n && line[i] != ' ' && !IsWordChar(line[i]) && line[i] != '"' && line[i] != '\'' && !(line[i] == '/' && i + 1 < n && (line[i + 1] == '/' || line[i + 1] == '*')))
            {
                i++;
            }

            if (i == symbolStart)
            {
                i++;
            }

            Add(symbolStart, i - symbolStart, Punctuation);
        }

        return false;
    }

    private void Add(int start, int length, int color)
    {
        if (length <= 0)
        {
            return;
        }

        _starts.Add(start);
        _lengths.Add(length);
        _colors.Add(color);
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static bool IsWordStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c > 127;

    private static bool IsWordChar(char c) => IsWordStart(c) || IsDigit(c);
}
