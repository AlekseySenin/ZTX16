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

    /// <summary>Reports a non-fatal problem to the host (shown once per distinct message).</summary>
    [Syscall("machine.warn")]
    public static void Warn(string message) => throw new NotInVmException();
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

    /// <summary>
    /// Draws a rectangle of tiles from a Tilemap resource. (<paramref name="tileX"/>, <paramref name="tileY"/>)
    /// is the region's lower-left tile (0..31, row 0 at the bottom); (<paramref name="x"/>, <paramref name="y"/>)
    /// is where its lower-left pixel lands. ColorIndex 0 is transparent.
    /// </summary>
    [Syscall("graphics.draw_tiles")]
    public static void DrawTiles(int layer, string tilemap, int tileX, int tileY, int widthInTiles, int heightInTiles, int x, int y) => throw new NotInVmException();

    /// <summary>
    /// Draws a rectangle of tiles with a transform: <paramref name="flags"/> 1 = FlipX, 2 = FlipY; scale; rotation in
    /// degrees counter-clockwise; normalized pivot (0,0 lower-left … 1,1 upper-right) that lands on (x, y); and a
    /// cyclic pixel offset applied inside every tile. Order: Flip → Scale → Rotation.
    /// </summary>
    [Syscall("graphics.draw_tiles_transformed")]
    public static void DrawTilesTransformed(int layer, string tilemap, int tileX, int tileY, int widthInTiles, int heightInTiles, int x, int y,
        int flags, float scaleX, float scaleY, float rotation, float pivotX, float pivotY, int offsetX, int offsetY) => throw new NotInVmException();

    /// <summary>
    /// Draws a grid of arbitrary tile IDs (row-major, bottom row first, <paramref name="gridWidth"/> tiles per row) with a
    /// transform, as one image. Used by <c>Sprite</c>.
    /// </summary>
    [Syscall("graphics.draw_tile_grid")]
    public static void DrawTileGrid(int layer, string tilemap, int[] tiles, int gridWidth, int x, int y,
        int flags, float scaleX, float scaleY, float rotation, float pivotX, float pivotY, int offsetX, int offsetY) => throw new NotInVmException();

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

/// <summary>Sprite resources: tile grids defined in the project. Use the <c>Sprite</c> class to draw and modify them.</summary>
public static class Sprites
{
    /// <summary>Name of the Tilemap resource the sprite's tiles come from.</summary>
    [Syscall("sprite.tilemap")]
    public static string Tilemap(string sprite) => throw new NotInVmException();

    /// <summary>Grid width in tiles.</summary>
    [Syscall("sprite.width")]
    public static int Width(string sprite) => throw new NotInVmException();

    /// <summary>Number of tiles in the grid.</summary>
    [Syscall("sprite.tile_count")]
    public static int TileCount(string sprite) => throw new NotInVmException();

    /// <summary>Tile ID at <paramref name="index"/> (row-major, bottom row first).</summary>
    [Syscall("sprite.tile")]
    public static int Tile(string sprite, int index) => throw new NotInVmException();
}

/// <summary>
/// Bitmap text. A <c>font</c> name of "" selects the built-in 8×8 system font. The text stream
/// understands '\n' (new line) and '\u0001' followed by a character whose code is a ColorIndex (set color).
/// The position is the lower-left corner of the first line; further lines go down.
/// </summary>
public static class Text
{
    /// <summary>Draws text without wrapping.</summary>
    [Syscall("text.draw")]
    public static void Draw(int layer, string text, int x, int y, int color, string font) => throw new NotInVmException();

    /// <summary>
    /// Draws text inside a box whose lower-left corner is (x, y). Lines are limited to <paramref name="width"/> pixels
    /// (whole words kept together when <paramref name="wordWrap"/>); the first line sits at the top of the box and
    /// drawing stops at the bottom edge. A <paramref name="height"/> of 0 means no bottom edge (lines then start at y).
    /// </summary>
    [Syscall("text.draw_box")]
    public static void DrawBox(int layer, string text, int x, int y, int width, int height, int color, bool wordWrap, string font) => throw new NotInVmException();

    /// <summary>Width in pixels of the widest line of the text.</summary>
    [Syscall("text.width")]
    public static int Width(string text, string font) => throw new NotInVmException();

    /// <summary>Line height of the font in pixels.</summary>
    [Syscall("text.line_height")]
    public static int LineHeight(string font) => throw new NotInVmException();
}

/// <summary>Animation resources: frames of sprite tile swaps. Use <c>Animation</c> and <c>AnimationPlayback</c> to play them.</summary>
public static class Animations
{
    /// <summary>Number of frames.</summary>
    [Syscall("animation.frame_count")]
    public static int FrameCount(string animation) => throw new NotInVmException();

    /// <summary>Duration of a frame in ticks.</summary>
    [Syscall("animation.duration")]
    public static int Duration(string animation, int frame) => throw new NotInVmException();

    /// <summary>Number of tile swaps in a frame.</summary>
    [Syscall("animation.swap_count")]
    public static int SwapCount(string animation, int frame) => throw new NotInVmException();

    /// <summary>Sprite tile index set by a swap (row-major, bottom row first).</summary>
    [Syscall("animation.swap_index")]
    public static int SwapIndex(string animation, int frame, int swap) => throw new NotInVmException();

    /// <summary>Tile ID set by a swap.</summary>
    [Syscall("animation.swap_tile")]
    public static int SwapTile(string animation, int frame, int swap) => throw new NotInVmException();
}

/// <summary>
/// Low-level audio hardware: eight fixed channels (0–5 Synth, 6–7 Sample/Wavetable), each with one ADSR and a
/// finalizing mixer volume; two global LFOs, two Filters and two FX units; master volume and a safety limiter.
/// </summary>
/// <remarks>
/// Changes made during a tick take effect together in that tick's Audio Update phase (changes made in
/// Initialize or Render take effect in the next one). Reads return the state after the last audio rendered.
/// </remarks>
public static class Audio
{
    /// <summary>Number of physical channels.</summary>
    public const int ChannelCount = 8;

    /// <summary>First Sample/Wavetable channel.</summary>
    public const int FirstSampleChannel = 6;

    /// <summary>Processor target meaning "not assigned".</summary>
    public const int Unassigned = -1;

    /// <summary>Processor target meaning "global, after mixing".</summary>
    public const int Global = 8;

    /// <summary>Waveform of a Synth channel.</summary>
    [Syscall("audio.set_waveform")]
    public static void SetWaveform(int channel, Waveform waveform) => throw new NotInVmException();

    /// <summary>Base frequency in Hz (0.01..20000) of a Synth or Wavetable channel.</summary>
    [Syscall("audio.set_frequency")]
    public static void SetFrequency(int channel, float hz) => throw new NotInVmException();

    /// <summary>Base volume 0..1.</summary>
    [Syscall("audio.set_volume")]
    public static void SetVolume(int channel, float volume) => throw new NotInVmException();

    /// <summary>Pan −1 (left) .. 1 (right).</summary>
    [Syscall("audio.set_pan")]
    public static void SetPan(int channel, float pan) => throw new NotInVmException();

    /// <summary>ADSR: attack, decay and release in seconds (0..10), sustain level 0..1.</summary>
    [Syscall("audio.set_adsr")]
    public static void SetAdsr(int channel, float attack, float decay, float sustain, float release) => throw new NotInVmException();

    /// <summary>Starts or retriggers the attack.</summary>
    [Syscall("audio.note_on")]
    public static void NoteOn(int channel) => throw new NotInVmException();

    /// <summary>Begins the release.</summary>
    [Syscall("audio.note_off")]
    public static void NoteOff(int channel) => throw new NotInVmException();

    /// <summary>Silences the channel at once, without release.</summary>
    [Syscall("audio.stop")]
    public static void Stop(int channel) => throw new NotInVmException();

    /// <summary>Mutes or unmutes the channel's output (it keeps running).</summary>
    [Syscall("audio.set_muted")]
    public static void SetMuted(int channel, bool muted) => throw new NotInVmException();

    /// <summary>Makes a Sample/Wavetable channel play a Sample resource (one-shot, whole sample).</summary>
    [Syscall("audio.use_sample")]
    public static void UseSample(int channel, string sample) => throw new NotInVmException();

    /// <summary>Makes a Sample/Wavetable channel play a Wavetable resource.</summary>
    [Syscall("audio.use_wavetable")]
    public static void UseWavetable(int channel, string wavetable) => throw new NotInVmException();

    /// <summary>Sample looping: loop or one-shot, loop start and end in sample frames (end −1 = sample end).</summary>
    [Syscall("audio.set_sample_loop")]
    public static void SetSampleLoop(int channel, bool loop, int start, int end) => throw new NotInVmException();

    /// <summary>Sample Playback Rate multiplier (nominal 1).</summary>
    [Syscall("audio.set_playback_rate")]
    public static void SetPlaybackRate(int channel, float rate) => throw new NotInVmException();

    /// <summary>Finalizing mixer volume 0..1 of a channel (after its insert processing).</summary>
    [Syscall("audio.set_mixer_volume")]
    public static void SetMixerVolume(int channel, float volume) => throw new NotInVmException();

    /// <summary>Master volume 0..1 (before the safety limiter).</summary>
    [Syscall("audio.set_master_volume")]
    public static void SetMasterVolume(float volume) => throw new NotInVmException();

    /// <summary>Waveform and frequency (0.01..60 Hz) of LFO 0 or 1.</summary>
    [Syscall("audio.set_lfo")]
    public static void SetLfo(int lfo, LfoWaveform waveform, float hz) => throw new NotInVmException();

    /// <summary>Connects an LFO to a channel parameter with an amount (0 disconnects).</summary>
    [Syscall("audio.route_lfo")]
    public static void RouteLfo(int lfo, int channel, ModulationDestination destination, float amount) => throw new NotInVmException();

    /// <summary>Connects an LFO to a Filter or FX parameter with an amount (0 disconnects).</summary>
    [Syscall("audio.route_lfo_to_processor")]
    public static void RouteLfoToProcessor(int lfo, ProcessorUnit unit, ProcessorParameter parameter, float amount) => throw new NotInVmException();

    /// <summary>Assigns the channel's ADSR to its one destination (default Volume, amount 1).</summary>
    [Syscall("audio.route_adsr")]
    public static void RouteAdsr(int channel, ModulationDestination destination, float amount) => throw new NotInVmException();

    /// <summary>Configures Filter 0 or 1: mode, cutoff in Hz (20..20000), resonance 0..1.</summary>
    [Syscall("audio.set_filter")]
    public static void SetFilter(int filter, FilterMode mode, float cutoff, float resonance) => throw new NotInVmException();

    /// <summary>Configures FX unit 0 or 1 and its three parameters (see <see cref="EffectType"/>).</summary>
    [Syscall("audio.set_effect")]
    public static void SetEffect(int fx, EffectType type, float parameter1, float parameter2, float mix) => throw new NotInVmException();

    /// <summary>
    /// Assigns a processing unit: <see cref="Unassigned"/>, a channel 0..7 (insert) or <see cref="Global"/>.
    /// Reassigning replaces the previous assignment.
    /// </summary>
    [Syscall("audio.assign_processor")]
    public static void AssignProcessor(ProcessorUnit unit, int target) => throw new NotInVmException();

    /// <summary>Current voice state of a channel.</summary>
    [Syscall("audio.channel_state")]
    public static ChannelState GetChannelState(int channel) => throw new NotInVmException();

    /// <summary>Effective frequency in Hz (Synth/Wavetable) or effective Playback Rate (Sample).</summary>
    [Syscall("audio.effective_pitch")]
    public static float GetEffectivePitch(int channel) => throw new NotInVmException();

    /// <summary>Effective volume 0..1 after modulation.</summary>
    [Syscall("audio.effective_volume")]
    public static float GetEffectiveVolume(int channel) => throw new NotInVmException();

    /// <summary>Current output of LFO 0 or 1 (−1..1).</summary>
    [Syscall("audio.lfo_value")]
    public static float GetLfoValue(int lfo) => throw new NotInVmException();

    /// <summary>Current ADSR output of a channel (0..1).</summary>
    [Syscall("audio.adsr_value")]
    public static float GetAdsrValue(int channel) => throw new NotInVmException();

    /// <summary>Length of a Sample resource in sample frames (44.1 kHz).</summary>
    [Syscall("audio.sample_length")]
    public static int SampleLength(string sample) => throw new NotInVmException();
}

/// <summary>
/// Persistent Storage: Storage Entries (type, name, opaque binary data) in the running cartridge's own Game ID
/// namespace. Entries survive the cartridge ending and starting again. Storage does not interpret the data;
/// use <c>ByteWriter</c>/<c>ByteReader</c> (or any other serialization) to build it.
/// </summary>
/// <remarks>
/// Types and names are 1..64 characters without control characters. Limits: an entry up to 1 MiB, at most
/// 1024 entries and 16 MiB per game. Breaking a rule or a limit stops the cartridge with an error.
/// </remarks>
public static class Storage
{
    /// <summary>Creates or replaces an entry.</summary>
    [Syscall("storage.write")]
    public static void Write(string type, string name, byte[] data) => throw new NotInVmException();

    /// <summary>The entry's data, or null if there is no such entry.</summary>
    [Syscall("storage.read")]
    public static byte[]? Read(string type, string name) => throw new NotInVmException();

    /// <summary>True if the entry exists.</summary>
    [Syscall("storage.exists")]
    public static bool Exists(string type, string name) => throw new NotInVmException();

    /// <summary>Removes an entry. Returns false if it did not exist.</summary>
    [Syscall("storage.delete")]
    public static bool Delete(string type, string name) => throw new NotInVmException();

    /// <summary>Number of entry types in use.</summary>
    [Syscall("storage.type_count")]
    public static int TypeCount() => throw new NotInVmException();

    /// <summary>An entry type in use (sorted).</summary>
    [Syscall("storage.type_name")]
    public static string TypeName(int index) => throw new NotInVmException();

    /// <summary>Number of entries of a type.</summary>
    [Syscall("storage.count")]
    public static int Count(string type) => throw new NotInVmException();

    /// <summary>The name of an entry of a type (sorted).</summary>
    [Syscall("storage.name")]
    public static string Name(string type, int index) => throw new NotInVmException();

    /// <summary>True if entries are kept on persistent storage (false when the host keeps them in memory only).</summary>
    [Syscall("storage.is_persistent")]
    public static bool IsPersistent() => throw new NotInVmException();
}

/// <summary>
/// Instrument, Sequence and Song resources in their flat runtime form (32-bit values and a string table).
/// Use <c>Instrument.Load</c>, <c>Sequence.Load</c> and <c>Song.Load</c>, which decode them.
/// </summary>
public static class ResourceData
{
    /// <summary>Number of values in the resource stream.</summary>
    [Syscall("data.value_count")]
    public static int ValueCount(ResourceType type, string name) => throw new NotInVmException();

    /// <summary>A value of the stream as an integer.</summary>
    [Syscall("data.value")]
    public static int Value(ResourceType type, string name, int index) => throw new NotInVmException();

    /// <summary>A value of the stream reinterpreted as a float.</summary>
    [Syscall("data.float_value")]
    public static float FloatValue(ResourceType type, string name, int index) => throw new NotInVmException();

    /// <summary>An entry of the resource's string table.</summary>
    [Syscall("data.string")]
    public static string String(ResourceType type, string name, int index) => throw new NotInVmException();
}

/// <summary>
/// Level resources in their flat runtime form (32-bit values and a string table). Use <c>Level.Load</c>,
/// which decodes them into layers and actors.
/// </summary>
public static class Levels
{
    /// <summary>Number of values in the level stream.</summary>
    [Syscall("level.value_count")]
    public static int ValueCount(string level) => throw new NotInVmException();

    /// <summary>A value of the level stream as an integer.</summary>
    [Syscall("level.value")]
    public static int Value(string level, int index) => throw new NotInVmException();

    /// <summary>A value of the level stream reinterpreted as a float.</summary>
    [Syscall("level.float_value")]
    public static float FloatValue(string level, int index) => throw new NotInVmException();

    /// <summary>An entry of the level's string table.</summary>
    [Syscall("level.string")]
    public static string String(string level, int index) => throw new NotInVmException();
}
