namespace ZTX16.Input;

/// <summary>Manufacturer-neutral gamepad buttons.</summary>
/// <remarks>Numeric values are executable ABI: append members only, never reorder.</remarks>
public enum GamepadButton
{
    /// <summary>D-pad up.</summary>
    DPadUp,

    /// <summary>D-pad down.</summary>
    DPadDown,

    /// <summary>D-pad left.</summary>
    DPadLeft,

    /// <summary>D-pad right.</summary>
    DPadRight,

    /// <summary>Bottom face button.</summary>
    Action1,

    /// <summary>Right face button.</summary>
    Action2,

    /// <summary>Left face button.</summary>
    Action3,

    /// <summary>Top face button.</summary>
    Action4,

    /// <summary>Left shoulder.</summary>
    LeftShoulder,

    /// <summary>Right shoulder.</summary>
    RightShoulder,

    /// <summary>Left stick press.</summary>
    LeftStickPress,

    /// <summary>Right stick press.</summary>
    RightStickPress,

    /// <summary>Start.</summary>
    Start,

    /// <summary>Select (never called Back in ZTX16).</summary>
    Select,
}

/// <summary>Gamepad analog axes.</summary>
/// <remarks>Numeric values are executable ABI: append members only, never reorder.</remarks>
public enum GamepadAxis
{
    /// <summary>Left stick X, -1 (left) … +1 (right).</summary>
    LeftStickX,

    /// <summary>Left stick Y, -1 (down) … +1 (up).</summary>
    LeftStickY,

    /// <summary>Right stick X, -1 (left) … +1 (right).</summary>
    RightStickX,

    /// <summary>Right stick Y, -1 (down) … +1 (up).</summary>
    RightStickY,

    /// <summary>Left trigger, 0 … +1.</summary>
    LeftTrigger,

    /// <summary>Right trigger, 0 … +1.</summary>
    RightTrigger,
}

/// <summary>
/// One gamepad device slot. A slot index is the device's current position, not a
/// persistent hardware identity. Values are raw: dead zones belong to game code.
/// </summary>
public sealed class Gamepad
{
    private static readonly int ButtonCount = Enum.GetValues<GamepadButton>().Length;
    private static readonly int AxisCount = Enum.GetValues<GamepadAxis>().Length;

    private readonly DigitalInputSet _buttons = new(ButtonCount);
    private readonly float[] _axes = new float[AxisCount];
    private readonly bool[] _axisChanged = new bool[AxisCount];

    internal Gamepad(int slot)
    {
        Slot = slot;
    }

    /// <summary>Device slot index.</summary>
    public int Slot { get; }

    /// <summary>True while a device occupies this slot.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Returns the current state and transitions of <paramref name="button"/>.</summary>
    public DigitalInput this[GamepadButton button] => _buttons.Get(ToIndex(button, ButtonCount));

    /// <summary>Returns the current value of <paramref name="axis"/>.</summary>
    public float GetAxis(GamepadAxis axis) => _axes[ToIndex(axis, AxisCount)];

    /// <summary>True if <paramref name="axis"/> changed value during this tick.</summary>
    public bool AxisChanged(GamepadAxis axis) => _axisChanged[ToIndex(axis, AxisCount)];

    internal void BeginTick()
    {
        _buttons.BeginTick();
        Array.Clear(_axisChanged);
    }

    internal void SetConnected(bool connected)
    {
        if (!connected)
        {
            _buttons.ReleaseAll();
            for (int i = 0; i < AxisCount; i++)
            {
                SetAxisValue(i, 0f);
            }
        }

        IsConnected = connected;
    }

    internal void SetButton(GamepadButton button, bool down) => _buttons.SetDown(ToIndex(button, ButtonCount), down);

    internal void SetAxis(GamepadAxis axis, float value)
    {
        int index = ToIndex(axis, AxisCount);
        float min = axis is GamepadAxis.LeftTrigger or GamepadAxis.RightTrigger ? 0f : -1f;
        SetAxisValue(index, Math.Clamp(value, min, 1f));
    }

    private void SetAxisValue(int index, float value)
    {
        if (_axes[index] != value)
        {
            _axes[index] = value;
            _axisChanged[index] = true;
        }
    }

    private static int ToIndex(GamepadButton button, int count) => ToIndex((int)button, count, nameof(button));

    private static int ToIndex(GamepadAxis axis, int count) => ToIndex((int)axis, count, nameof(axis));

    private static int ToIndex(int index, int count, string paramName)
    {
        if ((uint)index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(paramName, index, "Unknown gamepad input.");
        }

        return index;
    }
}
