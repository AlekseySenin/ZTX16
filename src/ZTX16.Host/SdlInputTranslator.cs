using SDL;
using ZTX16.Platform.Graphics;
using ZTX16.Platform.Input;
using static SDL.SDL3;

namespace ZTX16.Host;

/// <summary>
/// Translates SDL3 input events into ZTX16 input events. SDL identifiers never cross
/// this boundary: scancodes become <see cref="Key"/> values and SDL joystick IDs become
/// ZTX16 gamepad slots.
/// </summary>
internal sealed unsafe class SdlInputTranslator : IDisposable
{
    private const int DisplayHeight = GraphicsSystem.DisplayHeight;

    private InputSystem _input;
    private readonly SDL_JoystickID[] _slotOwners = new SDL_JoystickID[InputSystem.GamepadSlotCount];
    private readonly IntPtr[] _slotHandles = new IntPtr[InputSystem.GamepadSlotCount];

    /// <summary>Sends input to another session (app switch); connected gamepads are announced to it.</summary>
    public void Retarget(InputSystem input)
    {
        _input = input;
        for (int slot = 0; slot < _slotHandles.Length; slot++)
        {
            if (_slotHandles[slot] != IntPtr.Zero)
            {
                _input.Enqueue(InputEvent.GamepadConnection(slot, true));
            }
        }
    }

    public SdlInputTranslator(InputSystem input)
    {
        _input = input;
    }

    public void Translate(in SDL_Event e)
    {
        switch (e.Type)
        {
            case SDL_EventType.SDL_EVENT_KEY_DOWN:
            case SDL_EventType.SDL_EVENT_KEY_UP:
                if (e.key.repeat)
                {
                    _input.Enqueue(InputEvent.KeyRepeat(MapKey(e.key.scancode)));
                }
                else
                {
                    _input.Enqueue(InputEvent.KeyChange(MapKey(e.key.scancode), e.key.down));
                }

                break;

            case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                if (System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)e.text.text) is { Length: > 0 } text)
                {
                    _input.Enqueue(InputEvent.TextTyped(text));
                }

                break;

            case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                _input.Enqueue(InputEvent.MouseMoved(ToDisplayX(e.motion.x), ToDisplayY(e.motion.y)));
                break;

            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                if (MapMouseButton(e.button.button) is { } button)
                {
                    _input.Enqueue(InputEvent.MouseMoved(ToDisplayX(e.button.x), ToDisplayY(e.button.y)));
                    _input.Enqueue(InputEvent.MouseButtonChange(button, e.button.down));
                }

                break;

            case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                _input.Enqueue(InputEvent.Wheel(e.wheel.integer_x, e.wheel.integer_y));
                break;

            case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                OnGamepadAdded(e.gdevice.which);
                break;

            case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                OnGamepadRemoved(e.gdevice.which);
                break;

            case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN:
            case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP:
                if (FindSlot(e.gbutton.which) is int buttonSlot &&
                    MapGamepadButton((SDL_GamepadButton)e.gbutton.button) is { } padButton)
                {
                    _input.Enqueue(InputEvent.GamepadButtonChange(buttonSlot, padButton, e.gbutton.down));
                }

                break;

            case SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION:
                if (FindSlot(e.gaxis.which) is int axisSlot &&
                    MapAxis((SDL_GamepadAxis)e.gaxis.axis, e.gaxis.value) is var (axis, value))
                {
                    _input.Enqueue(InputEvent.GamepadAxisChange(axisSlot, axis, value));
                }

                break;
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _slotHandles.Length; i++)
        {
            if (_slotHandles[i] != IntPtr.Zero)
            {
                SDL_CloseGamepad((SDL_Gamepad*)_slotHandles[i]);
                _slotHandles[i] = IntPtr.Zero;
            }
        }
    }

    // Render coordinates are top-left based; ZTX16 screen coordinates are lower-left.
    private static int ToDisplayX(float renderX) => (int)MathF.Floor(renderX);

    private static int ToDisplayY(float renderY) => DisplayHeight - 1 - (int)MathF.Floor(renderY);

    private void OnGamepadAdded(SDL_JoystickID id)
    {
        if (FindSlot(id) is not null)
        {
            return;
        }

        int free = Array.FindIndex(_slotHandles, h => h == IntPtr.Zero);
        if (free < 0)
        {
            return; // All slots occupied; the extra device is ignored.
        }

        SDL_Gamepad* gamepad = SDL_OpenGamepad(id);
        if (gamepad == null)
        {
            return;
        }

        _slotHandles[free] = (IntPtr)gamepad;
        _slotOwners[free] = id;
        _input.Enqueue(InputEvent.GamepadConnection(free, true));
    }

    private void OnGamepadRemoved(SDL_JoystickID id)
    {
        if (FindSlot(id) is not int slot)
        {
            return;
        }

        SDL_CloseGamepad((SDL_Gamepad*)_slotHandles[slot]);
        _slotHandles[slot] = IntPtr.Zero;
        _slotOwners[slot] = default;
        _input.Enqueue(InputEvent.GamepadConnection(slot, false));
    }

    private int? FindSlot(SDL_JoystickID id)
    {
        for (int i = 0; i < _slotOwners.Length; i++)
        {
            if (_slotHandles[i] != IntPtr.Zero && _slotOwners[i] == id)
            {
                return i;
            }
        }

        return null;
    }

    private static MouseButton? MapMouseButton(byte sdlButton) => sdlButton switch
    {
        1 => MouseButton.Left,
        2 => MouseButton.Middle,
        3 => MouseButton.Right,
        _ => null,
    };

    private static GamepadButton? MapGamepadButton(SDL_GamepadButton button) => button switch
    {
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => GamepadButton.Action1,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST => GamepadButton.Action2,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST => GamepadButton.Action3,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH => GamepadButton.Action4,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK => GamepadButton.Select,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START => GamepadButton.Start,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK => GamepadButton.LeftStickPress,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK => GamepadButton.RightStickPress,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER => GamepadButton.LeftShoulder,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER => GamepadButton.RightShoulder,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => GamepadButton.DPadUp,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => GamepadButton.DPadDown,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => GamepadButton.DPadLeft,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => GamepadButton.DPadRight,
        _ => null,
    };

    private static (GamepadAxis Axis, float Value)? MapAxis(SDL_GamepadAxis axis, short raw)
    {
        // SDL sticks are -32768..32767 with +Y down; ZTX16 sticks are -1..+1 with +Y up.
        float stick = Math.Max(raw / 32767f, -1f);
        float trigger = Math.Max(raw / 32767f, 0f);
        return axis switch
        {
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX => (GamepadAxis.LeftStickX, stick),
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY => (GamepadAxis.LeftStickY, -stick),
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX => (GamepadAxis.RightStickX, stick),
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY => (GamepadAxis.RightStickY, -stick),
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER => (GamepadAxis.LeftTrigger, trigger),
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER => (GamepadAxis.RightTrigger, trigger),
            _ => null,
        };
    }

    private static Key MapKey(SDL_Scancode scancode)
    {
        // Letters, digits and F1–F12 are contiguous in both enums.
        if (scancode is >= SDL_Scancode.SDL_SCANCODE_A and <= SDL_Scancode.SDL_SCANCODE_Z)
        {
            return Key.A + (scancode - SDL_Scancode.SDL_SCANCODE_A);
        }

        if (scancode is >= SDL_Scancode.SDL_SCANCODE_1 and <= SDL_Scancode.SDL_SCANCODE_0)
        {
            return Key.Digit1 + (scancode - SDL_Scancode.SDL_SCANCODE_1);
        }

        if (scancode is >= SDL_Scancode.SDL_SCANCODE_F1 and <= SDL_Scancode.SDL_SCANCODE_F12)
        {
            return Key.F1 + (scancode - SDL_Scancode.SDL_SCANCODE_F1);
        }

        if (scancode is >= SDL_Scancode.SDL_SCANCODE_KP_1 and <= SDL_Scancode.SDL_SCANCODE_KP_0)
        {
            return Key.Keypad1 + (scancode - SDL_Scancode.SDL_SCANCODE_KP_1);
        }

        return scancode switch
        {
            SDL_Scancode.SDL_SCANCODE_RETURN => Key.Enter,
            SDL_Scancode.SDL_SCANCODE_ESCAPE => Key.Escape,
            SDL_Scancode.SDL_SCANCODE_BACKSPACE => Key.Backspace,
            SDL_Scancode.SDL_SCANCODE_TAB => Key.Tab,
            SDL_Scancode.SDL_SCANCODE_SPACE => Key.Space,
            SDL_Scancode.SDL_SCANCODE_MINUS => Key.Minus,
            SDL_Scancode.SDL_SCANCODE_EQUALS => Key.Equals,
            SDL_Scancode.SDL_SCANCODE_LEFTBRACKET => Key.LeftBracket,
            SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET => Key.RightBracket,
            SDL_Scancode.SDL_SCANCODE_BACKSLASH => Key.Backslash,
            SDL_Scancode.SDL_SCANCODE_SEMICOLON => Key.Semicolon,
            SDL_Scancode.SDL_SCANCODE_APOSTROPHE => Key.Apostrophe,
            SDL_Scancode.SDL_SCANCODE_GRAVE => Key.Grave,
            SDL_Scancode.SDL_SCANCODE_COMMA => Key.Comma,
            SDL_Scancode.SDL_SCANCODE_PERIOD => Key.Period,
            SDL_Scancode.SDL_SCANCODE_SLASH => Key.Slash,
            SDL_Scancode.SDL_SCANCODE_CAPSLOCK => Key.CapsLock,
            SDL_Scancode.SDL_SCANCODE_PRINTSCREEN => Key.PrintScreen,
            SDL_Scancode.SDL_SCANCODE_SCROLLLOCK => Key.ScrollLock,
            SDL_Scancode.SDL_SCANCODE_PAUSE => Key.Pause,
            SDL_Scancode.SDL_SCANCODE_INSERT => Key.Insert,
            SDL_Scancode.SDL_SCANCODE_HOME => Key.Home,
            SDL_Scancode.SDL_SCANCODE_PAGEUP => Key.PageUp,
            SDL_Scancode.SDL_SCANCODE_DELETE => Key.Delete,
            SDL_Scancode.SDL_SCANCODE_END => Key.End,
            SDL_Scancode.SDL_SCANCODE_PAGEDOWN => Key.PageDown,
            SDL_Scancode.SDL_SCANCODE_RIGHT => Key.Right,
            SDL_Scancode.SDL_SCANCODE_LEFT => Key.Left,
            SDL_Scancode.SDL_SCANCODE_DOWN => Key.Down,
            SDL_Scancode.SDL_SCANCODE_UP => Key.Up,
            SDL_Scancode.SDL_SCANCODE_NUMLOCKCLEAR => Key.NumLock,
            SDL_Scancode.SDL_SCANCODE_KP_DIVIDE => Key.KeypadDivide,
            SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY => Key.KeypadMultiply,
            SDL_Scancode.SDL_SCANCODE_KP_MINUS => Key.KeypadMinus,
            SDL_Scancode.SDL_SCANCODE_KP_PLUS => Key.KeypadPlus,
            SDL_Scancode.SDL_SCANCODE_KP_ENTER => Key.KeypadEnter,
            SDL_Scancode.SDL_SCANCODE_KP_PERIOD => Key.KeypadPeriod,
            SDL_Scancode.SDL_SCANCODE_LCTRL => Key.LeftControl,
            SDL_Scancode.SDL_SCANCODE_LSHIFT => Key.LeftShift,
            SDL_Scancode.SDL_SCANCODE_LALT => Key.LeftAlt,
            SDL_Scancode.SDL_SCANCODE_LGUI => Key.LeftMeta,
            SDL_Scancode.SDL_SCANCODE_RCTRL => Key.RightControl,
            SDL_Scancode.SDL_SCANCODE_RSHIFT => Key.RightShift,
            SDL_Scancode.SDL_SCANCODE_RALT => Key.RightAlt,
            SDL_Scancode.SDL_SCANCODE_RGUI => Key.RightMeta,
            SDL_Scancode.SDL_SCANCODE_APPLICATION => Key.Menu,
            _ => Key.None,
        };
    }
}
