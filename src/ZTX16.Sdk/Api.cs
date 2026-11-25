namespace ZTX16;

// Platform API for cartridge code. Every member is a syscall declaration; bodies only
// throw because they never execute on .NET. Names and parameter types are ABI.
// Provisional flat shape while the VM has no reference types; object-style APIs
// (Graphics.Layers[i].Clear()) follow once the heap exists.

/// <summary>Machine: time, exit and suspend requests.</summary>
public static class Machine
{
    /// <summary>Number of simulation ticks since session start.</summary>
    public static long TickCount
    {
        [Syscall("machine.tick_count")]
        get => throw new NotInVmException();
    }

    /// <summary>Requests session termination.</summary>
    [Syscall("machine.exit")]
    public static void Exit() => throw new NotInVmException();

    /// <summary>Requests suspension; the runtime may ignore it.</summary>
    [Syscall("machine.suspend")]
    public static void Suspend() => throw new NotInVmException();
}

/// <summary>Graphics: layers, display layers and the palette.</summary>
public static class Graphics
{
    /// <summary>Number of physical layers.</summary>
    public const int LayerCount = 8;

    /// <summary>Display width and height in pixels.</summary>
    public const int ScreenSize = 256;

    /// <summary>Clears a Graphics Layer to ColorIndex 0.</summary>
    [Syscall("graphics.clear")]
    public static void Clear(int layer) => throw new NotInVmException();

    /// <summary>Fills a Graphics Layer with one color.</summary>
    [Syscall("graphics.fill")]
    public static void Fill(int layer, int color) => throw new NotInVmException();

    /// <summary>Writes one pixel; ColorIndex 0 erases.</summary>
    [Syscall("graphics.set_pixel")]
    public static void SetPixel(int layer, int x, int y, int color) => throw new NotInVmException();

    /// <summary>Reads one pixel.</summary>
    [Syscall("graphics.get_pixel")]
    public static int GetPixel(int layer, int x, int y) => throw new NotInVmException();

    /// <summary>Fills a rectangle whose lower-left corner is (x, y); clipped to the layer.</summary>
    [Syscall("graphics.fill_rect")]
    public static void FillRect(int layer, int x, int y, int width, int height, int color) => throw new NotInVmException();

    /// <summary>Recreates a layer with a size in 256×256 screens; the new buffer is cleared.</summary>
    [Syscall("graphics.resize_layer")]
    public static void ResizeLayer(int layer, int widthInScreens, int heightInScreens) => throw new NotInVmException();

    /// <summary>Layer width in pixels.</summary>
    [Syscall("graphics.layer_width")]
    public static int LayerWidth(int layer) => throw new NotInVmException();

    /// <summary>Layer height in pixels.</summary>
    [Syscall("graphics.layer_height")]
    public static int LayerHeight(int layer) => throw new NotInVmException();

    /// <summary>Clears every Graphics Layer.</summary>
    [Syscall("graphics.clear_layers")]
    public static void ClearLayers() => throw new NotInVmException();

    /// <summary>Moves the visible 256×256 window of a Display Layer; it wraps around the layer.</summary>
    [Syscall("display.set_origin")]
    public static void SetDisplayOrigin(int layer, int x, int y) => throw new NotInVmException();

    /// <summary>Includes or excludes a Display Layer from composition.</summary>
    [Syscall("display.set_enabled")]
    public static void SetDisplayEnabled(int layer, bool enabled) => throw new NotInVmException();

    /// <summary>Whether a Display Layer takes part in composition.</summary>
    [Syscall("display.is_enabled")]
    public static bool IsDisplayEnabled(int layer) => throw new NotInVmException();

    /// <summary>Replaces the whole palette with the Palette resource <paramref name="name"/>; stops the cartridge if it does not exist.</summary>
    [Syscall("palette.use")]
    public static void UsePalette(string name) => throw new NotInVmException();

    /// <summary>Sets a palette entry from RGB components 0..7.</summary>
    [Syscall("palette.set_color")]
    public static void SetPaletteColor(int index, int r, int g, int b) => throw new NotInVmException();
}

/// <summary>Input: keyboard, mouse and gamepads. Devices, not players.</summary>
public static class Input
{
    /// <summary>True while the key is held.</summary>
    [Syscall("input.key_down")]
    public static bool IsKeyDown(Key key) => throw new NotInVmException();

    /// <summary>True if the key went down this tick.</summary>
    [Syscall("input.key_pressed")]
    public static bool WasKeyPressed(Key key) => throw new NotInVmException();

    /// <summary>True if the key went up this tick.</summary>
    [Syscall("input.key_released")]
    public static bool WasKeyReleased(Key key) => throw new NotInVmException();

    /// <summary>Mouse X in display pixels.</summary>
    public static int MouseX
    {
        [Syscall("input.mouse_x")]
        get => throw new NotInVmException();
    }

    /// <summary>Mouse Y in display pixels (+Y up).</summary>
    public static int MouseY
    {
        [Syscall("input.mouse_y")]
        get => throw new NotInVmException();
    }

    /// <summary>Vertical wheel steps this tick.</summary>
    public static int MouseWheel
    {
        [Syscall("input.mouse_wheel")]
        get => throw new NotInVmException();
    }

    /// <summary>True while the mouse button is held.</summary>
    [Syscall("input.mouse_button_down")]
    public static bool IsMouseButtonDown(MouseButton button) => throw new NotInVmException();

    /// <summary>True if the mouse button went down this tick.</summary>
    [Syscall("input.mouse_button_pressed")]
    public static bool WasMouseButtonPressed(MouseButton button) => throw new NotInVmException();

    /// <summary>True while a device occupies the gamepad slot.</summary>
    [Syscall("input.gamepad_connected")]
    public static bool IsGamepadConnected(int slot) => throw new NotInVmException();

    /// <summary>True while the gamepad button is held.</summary>
    [Syscall("input.gamepad_button_down")]
    public static bool IsGamepadButtonDown(int slot, GamepadButton button) => throw new NotInVmException();

    /// <summary>True if the gamepad button went down this tick.</summary>
    [Syscall("input.gamepad_button_pressed")]
    public static bool WasGamepadButtonPressed(int slot, GamepadButton button) => throw new NotInVmException();

    /// <summary>Raw axis value: sticks -1..1 (+Y up), triggers 0..1.</summary>
    [Syscall("input.gamepad_axis")]
    public static float GetGamepadAxis(int slot, GamepadAxis axis) => throw new NotInVmException();
}

/// <summary>Resource families stored in a cartridge.</summary>
/// <remarks>Numeric values are executable ABI: append members only, never reorder.</remarks>
public enum ResourceType
{
    /// <summary>32-entry color palette.</summary>
    Palette = 1,

    /// <summary>32×32-tile bitmap.</summary>
    Tilemap = 2,

    /// <summary>Composition of tiles.</summary>
    Sprite = 3,

    /// <summary>Bitmap font.</summary>
    Font = 4,

    /// <summary>Tile-swap animation.</summary>
    Animation = 5,

    /// <summary>PCM sample.</summary>
    Sample = 6,

    /// <summary>64-sample wavetable.</summary>
    Wavetable = 7,

    /// <summary>Instrument definition.</summary>
    Instrument = 8,

    /// <summary>Monophonic sequence.</summary>
    Sequence = 9,

    /// <summary>Song.</summary>
    Song = 10,

    /// <summary>Level.</summary>
    Level = 11,
}

/// <summary>Read-only access to the resources packaged in the cartridge, addressed by type and name.</summary>
public static class Resources
{
    /// <summary>True if a resource of <paramref name="type"/> named <paramref name="name"/> exists.</summary>
    [Syscall("resources.exists")]
    public static bool Exists(ResourceType type, string name) => throw new NotInVmException();

    /// <summary>Number of resources of <paramref name="type"/>.</summary>
    [Syscall("resources.count")]
    public static int Count(ResourceType type) => throw new NotInVmException();

    /// <summary>Name of resource <paramref name="index"/> (0..Count-1) of <paramref name="type"/>, in name order.</summary>
    [Syscall("resources.name")]
    public static string Name(ResourceType type, int index) => throw new NotInVmException();
}
