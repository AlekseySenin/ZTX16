namespace ZTX16;

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
