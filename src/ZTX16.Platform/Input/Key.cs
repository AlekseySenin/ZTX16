namespace ZTX16.Input;

/// <summary>
/// Physical keyboard keys, named by their position on a US-QWERTY layout.
/// A key identifies a physical position, not the character the host layout produces.
/// </summary>
/// <remarks>Numeric values are executable ABI (passed to syscalls as i32): append members only, never reorder.</remarks>
// Member names are self-describing physical key positions; per-member XML docs add nothing.
#pragma warning disable CS1591
public enum Key
{
    /// <summary>No key / unmapped host key.</summary>
    None = 0,

    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9, Digit0,

    Enter, Escape, Backspace, Tab, Space,
    Minus, Equals, LeftBracket, RightBracket, Backslash,
    Semicolon, Apostrophe, Grave, Comma, Period, Slash, CapsLock,

    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

    PrintScreen, ScrollLock, Pause, Insert, Home, PageUp, Delete, End, PageDown,
    Right, Left, Down, Up,

    NumLock, KeypadDivide, KeypadMultiply, KeypadMinus, KeypadPlus, KeypadEnter,
    Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9, Keypad0,
    KeypadPeriod,

    LeftControl, LeftShift, LeftAlt, LeftMeta,
    RightControl, RightShift, RightAlt, RightMeta,
    Menu,
}
#pragma warning restore CS1591
