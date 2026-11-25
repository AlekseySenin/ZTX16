namespace ZTX16.Input;

/// <summary>
/// State of one digital input for the current tick: whether it is held, and whether it
/// was pressed or released during the tick. A press and release inside the same tick
/// report both transitions.
/// </summary>
public readonly record struct DigitalInput(bool IsDown, bool Pressed, bool Released)
{
    /// <summary>True when the input is not held.</summary>
    public bool IsUp => !IsDown;
}

/// <summary>Tracks a fixed set of digital inputs and their per-tick transitions.</summary>
internal sealed class DigitalInputSet
{
    private readonly bool[] _down;
    private readonly bool[] _pressed;
    private readonly bool[] _released;

    public DigitalInputSet(int count)
    {
        _down = new bool[count];
        _pressed = new bool[count];
        _released = new bool[count];
    }

    public int Count => _down.Length;

    public DigitalInput Get(int index) => new(_down[index], _pressed[index], _released[index]);

    /// <summary>Clears transitions at the start of a tick; held state persists.</summary>
    public void BeginTick()
    {
        Array.Clear(_pressed);
        Array.Clear(_released);
    }

    public void SetDown(int index, bool down)
    {
        if (_down[index] == down)
        {
            return;
        }

        _down[index] = down;
        if (down)
        {
            _pressed[index] = true;
        }
        else
        {
            _released[index] = true;
        }
    }

    /// <summary>Releases every held input, recording Released transitions.</summary>
    public void ReleaseAll()
    {
        for (int i = 0; i < _down.Length; i++)
        {
            SetDown(i, false);
        }
    }
}
