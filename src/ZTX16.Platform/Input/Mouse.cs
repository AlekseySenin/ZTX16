namespace ZTX16.Input;

/// <summary>Mouse buttons.</summary>
/// <remarks>Numeric values are executable ABI: append members only, never reorder.</remarks>
public enum MouseButton
{
    /// <summary>Left button.</summary>
    Left,

    /// <summary>Right button.</summary>
    Right,

    /// <summary>Middle button.</summary>
    Middle,
}

/// <summary>
/// Mouse input. Positions are ZTX16 screen coordinates: (0,0) is the lower-left pixel
/// of the logical display and +Y is up.
/// </summary>
public sealed class Mouse
{
    private const int ButtonCount = 3;

    private readonly DigitalInputSet _buttons = new(ButtonCount);
    private int _previousX;
    private int _previousY;
    private int _pendingWheelX;
    private int _pendingWheelY;

    internal Mouse()
    {
    }

    /// <summary>Absolute X position in display pixels. May lie outside the display.</summary>
    public int X { get; private set; }

    /// <summary>Absolute Y position in display pixels. May lie outside the display.</summary>
    public int Y { get; private set; }

    /// <summary>X movement since the previous tick.</summary>
    public int DeltaX { get; private set; }

    /// <summary>Y movement since the previous tick (+ is up).</summary>
    public int DeltaY { get; private set; }

    /// <summary>Vertical wheel steps during this tick (+ is away from the user).</summary>
    public int WheelDelta { get; private set; }

    /// <summary>Horizontal wheel steps during this tick (+ is right).</summary>
    public int WheelDeltaX { get; private set; }

    /// <summary>True when the pointer is inside the 256×256 display.</summary>
    public bool IsInsideDisplay => (uint)X < 256u && (uint)Y < 256u;

    /// <summary>Returns the current state and transitions of <paramref name="button"/>.</summary>
    public DigitalInput this[MouseButton button] => _buttons.Get(ToIndex(button));

    internal void BeginTick()
    {
        _buttons.BeginTick();
        _pendingWheelX = 0;
        _pendingWheelY = 0;
    }

    /// <summary>Finalizes per-tick derived values after all events of the tick were applied.</summary>
    internal void EndEvents()
    {
        DeltaX = X - _previousX;
        DeltaY = Y - _previousY;
        _previousX = X;
        _previousY = Y;
        WheelDelta = _pendingWheelY;
        WheelDeltaX = _pendingWheelX;
    }

    internal void SetPosition(int x, int y)
    {
        X = x;
        Y = y;
    }

    internal void AddWheel(int deltaX, int deltaY)
    {
        _pendingWheelX += deltaX;
        _pendingWheelY += deltaY;
    }

    internal void SetButton(MouseButton button, bool down) => _buttons.SetDown(ToIndex(button), down);

    internal void ReleaseAll() => _buttons.ReleaseAll();

    private static int ToIndex(MouseButton button)
    {
        if ((uint)button >= ButtonCount)
        {
            throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button.");
        }

        return (int)button;
    }
}
