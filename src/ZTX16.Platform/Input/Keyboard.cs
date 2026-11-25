namespace ZTX16.Input;

/// <summary>Physical keyboard gameplay input. Text input is a separate system.</summary>
public sealed class Keyboard
{
    private static readonly int KeyCount = Enum.GetValues<Key>().Length;

    private readonly DigitalInputSet _keys = new(KeyCount);

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

    internal void BeginTick() => _keys.BeginTick();

    internal void SetKey(Key key, bool down)
    {
        if (key != Key.None)
        {
            _keys.SetDown(ToIndex(key), down);
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
