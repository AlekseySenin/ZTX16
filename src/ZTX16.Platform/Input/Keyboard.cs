namespace ZTX16.Platform.Input;

/// <summary>
/// Physical keyboard: key state for gameplay, plus typing — the text entered during a tick and keys
/// "typed" (pressed or auto-repeated by the host), for text editing.
/// </summary>
public sealed class Keyboard
{
    private const int MaxTextPerTick = 256;

    private static readonly int KeyCount = Enum.GetValues<Key>().Length;

    private readonly DigitalInputSet _keys = new(KeyCount);
    private readonly bool[] _typed = new bool[KeyCount];
    private readonly System.Text.StringBuilder _text = new();

    internal Keyboard()
    {
    }

    /// <summary>Returns the current state and transitions of <paramref name="key"/>.</summary>
    public DigitalInput this[Key key] => _keys.Get(ToIndex(key));

    /// <summary>True while <paramref name="key"/> is held.</summary>
    public bool IsDown(Key key) => this[key].IsDown;

    /// <summary>True if <paramref name="key"/> went down during the current tick.</summary>
    public bool WasPressed(Key key) => this[key].Pressed;

    /// <summary>True if <paramref name="key"/> went up during the current tick.</summary>
    public bool WasReleased(Key key) => this[key].Released;

    /// <summary>True if <paramref name="key"/> was pressed or auto-repeated during the current tick.</summary>
    public bool WasTyped(Key key) => _typed[ToIndex(key)];

    /// <summary>Text typed during the current tick (characters, in order; empty if none).</summary>
    public string Text { get; private set; } = string.Empty;

    internal void BeginTick()
    {
        _keys.BeginTick();
        Array.Clear(_typed);
        _text.Clear();
    }

    /// <summary>Publishes the text gathered from this tick's events.</summary>
    internal void EndEvents() => Text = _text.ToString();

    internal void SetKey(Key key, bool down)
    {
        if (key == Key.None)
        {
            return;
        }

        if (down && !_keys.Get(ToIndex(key)).IsDown)
        {
            _typed[ToIndex(key)] = true;
        }

        _keys.SetDown(ToIndex(key), down);
    }

    internal void RepeatKey(Key key)
    {
        if (key != Key.None && _keys.Get(ToIndex(key)).IsDown)
        {
            _typed[ToIndex(key)] = true;
        }
    }

    internal void AddText(string text)
    {
        foreach (char c in text)
        {
            if (_text.Length < MaxTextPerTick && !char.IsControl(c))
            {
                _text.Append(c);
            }
        }
    }

    internal void ReleaseAll() => _keys.ReleaseAll();

    private static int ToIndex(Key key)
    {
        if ((uint)key >= (uint)KeyCount)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown key.");
        }

        return (int)key;
    }
}
