namespace ZTX16.Platform.Input;

/// <summary>
/// The ZTX16 input hardware model: devices, not players. Game code decides how
/// physical inputs map to gameplay.
/// </summary>
/// <remarks>
/// Host events are queued as they arrive and applied atomically at the start of each
/// tick, so cartridge code observes a stable snapshot for the whole tick.
/// </remarks>
public sealed class InputSystem
{
    /// <summary>Number of gamepad device slots.</summary>
    public const int GamepadSlotCount = 4;

    private readonly Gamepad[] _gamepads = new Gamepad[GamepadSlotCount];
    private readonly Queue<InputEvent> _pending = new();

    internal InputSystem()
    {
        for (int i = 0; i < GamepadSlotCount; i++)
        {
            _gamepads[i] = new Gamepad(i);
        }

        Gamepads = Array.AsReadOnly(_gamepads);
    }

    /// <summary>Physical keyboard.</summary>
    public Keyboard Keyboard { get; } = new();

    /// <summary>Mouse.</summary>
    public Mouse Mouse { get; } = new();

    /// <summary>Gamepad slots. Check <see cref="Gamepad.IsConnected"/> before use.</summary>
    public IReadOnlyList<Gamepad> Gamepads { get; }

    /// <summary>Queues a host event for the next tick. Called by the host backend.</summary>
    internal void Enqueue(in InputEvent inputEvent) => _pending.Enqueue(inputEvent);

    /// <summary>
    /// Releases all held inputs on the next tick, e.g. after the host loses focus,
    /// so no key stays stuck down.
    /// </summary>
    internal void EnqueueReleaseAll() => _pending.Enqueue(new InputEvent(InputEventKind.ReleaseAll));

    /// <summary>Input phase of the tick: clears transitions and applies queued events.</summary>
    internal void BeginTick()
    {
        Keyboard.BeginTick();
        Mouse.BeginTick();
        foreach (Gamepad gamepad in _gamepads)
        {
            gamepad.BeginTick();
        }

        while (_pending.TryDequeue(out InputEvent e))
        {
            Apply(e);
        }

        Mouse.EndEvents();
        Keyboard.EndEvents();
    }

    private void Apply(in InputEvent e)
    {
        switch (e.Kind)
        {
            case InputEventKind.Key:
                Keyboard.SetKey(e.Key, e.Down);
                break;
            case InputEventKind.KeyRepeat:
                Keyboard.RepeatKey(e.Key);
                break;
            case InputEventKind.Text:
                Keyboard.AddText(e.Text ?? string.Empty);
                break;
            case InputEventKind.MouseMove:
                Mouse.SetPosition(e.X, e.Y);
                break;
            case InputEventKind.MouseButton:
                Mouse.SetButton(e.MouseButton, e.Down);
                break;
            case InputEventKind.MouseWheel:
                Mouse.AddWheel(e.X, e.Y);
                break;
            case InputEventKind.GamepadConnection:
                GetSlot(e.Slot)?.SetConnected(e.Down);
                break;
            case InputEventKind.GamepadButton:
                GetSlot(e.Slot)?.SetButton(e.GamepadButton, e.Down);
                break;
            case InputEventKind.GamepadAxis:
                GetSlot(e.Slot)?.SetAxis(e.GamepadAxis, e.Value);
                break;
            case InputEventKind.ReleaseAll:
                Keyboard.ReleaseAll();
                Mouse.ReleaseAll();
                break;
            default:
                throw new InvalidOperationException($"Unhandled input event kind {e.Kind}.");
        }
    }

    private Gamepad? GetSlot(int slot) => (uint)slot < GamepadSlotCount ? _gamepads[slot] : null;
}

/// <summary>Kinds of host input events.</summary>
internal enum InputEventKind
{
    Key,
    KeyRepeat,
    Text,
    MouseMove,
    MouseButton,
    MouseWheel,
    GamepadConnection,
    GamepadButton,
    GamepadAxis,
    ReleaseAll,
}

/// <summary>A host input event already translated into ZTX16 terms.</summary>
internal readonly record struct InputEvent(InputEventKind Kind)
{
    public Key Key { get; init; }

    public MouseButton MouseButton { get; init; }

    public GamepadButton GamepadButton { get; init; }

    public GamepadAxis GamepadAxis { get; init; }

    /// <summary>Down/pressed for buttons; connected for gamepad connection events.</summary>
    public bool Down { get; init; }

    public int X { get; init; }

    public int Y { get; init; }

    public int Slot { get; init; }

    public float Value { get; init; }

    public string? Text { get; init; }

    public static InputEvent KeyChange(Key key, bool down) => new(InputEventKind.Key) { Key = key, Down = down };

    public static InputEvent KeyRepeat(Key key) => new(InputEventKind.KeyRepeat) { Key = key };

    public static InputEvent TextTyped(string text) => new(InputEventKind.Text) { Text = text };

    public static InputEvent MouseMoved(int x, int y) => new(InputEventKind.MouseMove) { X = x, Y = y };

    public static InputEvent MouseButtonChange(MouseButton button, bool down) =>
        new(InputEventKind.MouseButton) { MouseButton = button, Down = down };

    public static InputEvent Wheel(int deltaX, int deltaY) => new(InputEventKind.MouseWheel) { X = deltaX, Y = deltaY };

    public static InputEvent GamepadConnection(int slot, bool connected) =>
        new(InputEventKind.GamepadConnection) { Slot = slot, Down = connected };

    public static InputEvent GamepadButtonChange(int slot, GamepadButton button, bool down) =>
        new(InputEventKind.GamepadButton) { Slot = slot, GamepadButton = button, Down = down };

    public static InputEvent GamepadAxisChange(int slot, GamepadAxis axis, float value) =>
        new(InputEventKind.GamepadAxis) { Slot = slot, GamepadAxis = axis, Value = value };
}
