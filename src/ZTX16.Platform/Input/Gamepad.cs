namespace ZTX16.Platform.Input;

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
