using ZTX16.Abi;
using ZTX16.Cartridge;
using ZTX16.Platform.Runtime;
using ZTX16.Platform.Graphics;
using ZTX16.Platform.Input;
using ZTX16.VM;
using static ZTX16.VM.VmValue;

namespace ZTX16.Platform.Execution;

/// <summary>
/// The platform syscall table: every operation cartridge code can perform, bound to
/// one <see cref="Runtime.RuntimeSession"/>. This list is the cartridge sandbox boundary.
/// </summary>
/// <remarks>
/// Names and signatures come from the SDK's <see cref="SyscallAttribute"/> declarations
/// (<see cref="SdkAbi"/>); this class only supplies implementations. Every declared syscall
/// must be implemented, and nothing undeclared can be added.
/// Arguments are validated by the subsystems themselves; an invalid argument throws,
/// which the VM turns into a trap (an unhandled cartridge failure).
/// </remarks>
internal static class PlatformSyscalls
{
    public static SyscallTable Create(Runtime.RuntimeSession session)
    {
        Machine machine = session.Machine;
        GraphicsSystem graphics = session.Graphics;
        InputSystem input = session.Input;
        ResourceStore resources = session.Resources;

        var table = new SyscallTable();
        var bound = new HashSet<string>(StringComparer.Ordinal);

        // Machine
        Bind("machine.exit", _ => { machine.Exit(); return 0; });
        Bind("machine.suspend", _ => { machine.Suspend(); return 0; });
        Bind("machine.tick_count", _ => machine.TickCount);
        BindWithContext("machine.warn", (c, a) => { machine.Warn(Text(c, a[0])); return 0; });

        // Graphics layers
        Bind("graphics.clear", a => { Layer(a[0]).Clear(); return 0; });
        Bind("graphics.fill", a => { Layer(a[0]).Fill(ToI32(a[1])); return 0; });
        Bind("graphics.set_pixel", a =>
        {
            Layer(a[0]).SetPixel(ToI32(a[1]), ToI32(a[2]), ToI32(a[3]));
            return 0;
        });
        Bind("graphics.get_pixel", a => Layer(a[0]).GetPixel(ToI32(a[1]), ToI32(a[2])));
        Bind("graphics.fill_rect", a =>
        {
            Layer(a[0]).FillRectangle(ToI32(a[1]), ToI32(a[2]), ToI32(a[3]), ToI32(a[4]), ToI32(a[5]));
            return 0;
        });
        Bind("graphics.resize_layer", a =>
        {
            Layer(a[0]).Resize(ToI32(a[1]), ToI32(a[2]));
            return 0;
        });
        Bind("graphics.layer_width", a => Layer(a[0]).Width);
        Bind("graphics.layer_height", a => Layer(a[0]).Height);
        Bind("graphics.clear_layers", _ => { graphics.ClearLayers(); return 0; });

        // Display layers and palette
        Bind("display.set_origin", a =>
        {
            Display(a[0]).SetOrigin(ToI32(a[1]), ToI32(a[2]));
            return 0;
        });
        Bind("display.set_enabled", a => { Display(a[0]).Enabled = ToBool(a[1]); return 0; });
        Bind("display.is_enabled", a => FromBool(Display(a[0]).Enabled));
        Bind("palette.set_color", a =>
        {
            graphics.Palette.SetColor(ToI32(a[0]), new Color(ToI32(a[1]), ToI32(a[2]), ToI32(a[3])));
            return 0;
        });

        // Keyboard
        Bind("input.key_down", a => FromBool(input.Keyboard[(Key)ToI32(a[0])].IsDown));
        Bind("input.key_pressed", a => FromBool(input.Keyboard[(Key)ToI32(a[0])].Pressed));
        Bind("input.key_released", a => FromBool(input.Keyboard[(Key)ToI32(a[0])].Released));

        // Mouse
        Bind("input.mouse_x", _ => input.Mouse.X);
        Bind("input.mouse_y", _ => input.Mouse.Y);
        Bind("input.mouse_wheel", _ => input.Mouse.WheelDelta);
        Bind("input.mouse_button_down", a => FromBool(input.Mouse[(MouseButton)ToI32(a[0])].IsDown));
        Bind("input.mouse_button_pressed", a => FromBool(input.Mouse[(MouseButton)ToI32(a[0])].Pressed));

        // Gamepads
        Bind("input.gamepad_connected", a => FromBool(Pad(a[0]).IsConnected));
        Bind("input.gamepad_button_down", a =>
            FromBool(Pad(a[0])[(GamepadButton)ToI32(a[1])].IsDown));
        Bind("input.gamepad_button_pressed", a =>
            FromBool(Pad(a[0])[(GamepadButton)ToI32(a[1])].Pressed));
        Bind("input.gamepad_axis", a =>
            FromF32(Pad(a[0]).GetAxis((GamepadAxis)ToI32(a[1]))));

        // Resources
        BindWithContext("resources.exists", (c, a) => FromBool(resources.Exists(Type(a[0]), Name(c, a[1]))));
        Bind("resources.count", a => resources.Names(Type(a[0])).Count);
        BindWithContext("resources.name", (c, a) =>
        {
            IReadOnlyList<string> names = resources.Names(Type(a[0]));
            int index = ToI32(a[1]);
            return (uint)index < (uint)names.Count
                ? c.NewString(names[index])
                : throw new ArgumentOutOfRangeException(nameof(index), index, $"There are {names.Count} {Type(a[0])} resources.");
        });
        // Tiles and sprites
        BindWithContext("graphics.draw_tiles", (c, a) =>
        {
            DrawRegion(c, a, DrawTransform.Identity, 0, 0);
            return 0;
        });
        BindWithContext("graphics.draw_tiles_transformed", (c, a) =>
        {
            DrawRegion(c, a, Transform(a, 8), ToI32(a[14]), ToI32(a[15]));
            return 0;
        });
        BindWithContext("graphics.draw_tile_grid", (c, a) =>
        {
            byte[] tilemap = resources.Tilemap(Name(c, a[1]));
            int[] tiles = c.ReadInt32Array(a[2]) ?? throw new ArgumentNullException(nameof(a), "Tile array is null.");
            int gridWidth = ToI32(a[3]);
            if (gridWidth < 1 || tiles.Length == 0 || tiles.Length % gridWidth != 0)
            {
                throw new ArgumentException($"A tile grid needs a width of at least 1 that divides the tile count ({tiles.Length}).");
            }

            int bad = Array.FindIndex(tiles, t => t is < 0 or > 1023);
            if (bad >= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(a), tiles[bad], $"Tile ID at index {bad} must be 0..1023.");
            }

            int gridHeight = tiles.Length / gridWidth;
            Rasterizer.Draw(Layer(a[0]), gridWidth * 8, gridHeight * 8,
                TileSources.Grid(tilemap, tiles, gridWidth, ToI32(a[12]), ToI32(a[13])), ToI32(a[4]), ToI32(a[5]), Transform(a, 6));
            return 0;
        });
        BindWithContext("sprite.tilemap", (c, a) => c.NewString(resources.Sprite(Name(c, a[0])).Tilemap));
        BindWithContext("sprite.width", (c, a) => resources.Sprite(Name(c, a[0])).Width);
        BindWithContext("sprite.tile_count", (c, a) => resources.Sprite(Name(c, a[0])).Tiles.Length);
        BindWithContext("sprite.tile", (c, a) =>
        {
            int[] tiles = resources.Sprite(Name(c, a[0])).Tiles;
            int index = ToI32(a[1]);
            return (uint)index < (uint)tiles.Length ? tiles[index] : throw new ArgumentOutOfRangeException(nameof(index), index, $"The sprite has {tiles.Length} tiles.");
        });

        // Animations
        BindWithContext("animation.frame_count", (c, a) => resources.Animation(Name(c, a[0])).Frames.Length);
        BindWithContext("animation.duration", (c, a) => Frame(c, a).Duration);
        BindWithContext("animation.swap_count", (c, a) => Frame(c, a).SpriteTiles.Length);
        BindWithContext("animation.swap_index", (c, a) => Frame(c, a).SpriteTiles[Swap(c, a)]);
        BindWithContext("animation.swap_tile", (c, a) => Frame(c, a).TileIds[Swap(c, a)]);

        // Levels: the flat stream the Core library decodes (LevelFormat documents the layout)
        BindWithContext("level.value_count", (c, a) => resources.Level(Name(c, a[0])).Data.Length);
        BindWithContext("level.value", (c, a) => LevelValue(c, a));
        BindWithContext("level.float_value", (c, a) => FromF32(BitConverter.Int32BitsToSingle(LevelValue(c, a))));
        BindWithContext("level.string", (c, a) =>
        {
            string[] strings = resources.Level(Name(c, a[0])).Strings;
            int index = ToI32(a[1]);
            return (uint)index < (uint)strings.Length
                ? c.NewString(strings[index])
                : throw new ArgumentOutOfRangeException(nameof(a), index, $"The level has {strings.Length} strings.");
        });

        // Text
        BindWithContext("text.draw", (c, a) =>
        {
            TextRenderer.Draw(Layer(a[0]), Font(c, a[5]), Text(c, a[1]), ToI32(a[2]), ToI32(a[3]), ToI32(a[4]), 0, 0, wordWrap: false);
            return 0;
        });
        BindWithContext("text.draw_box", (c, a) =>
        {
            int width = ToI32(a[4]), height = ToI32(a[5]);
            if (width < 1 || height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(a), "Box width must be positive and height zero or positive.");
            }

            TextRenderer.Draw(Layer(a[0]), Font(c, a[8]), Text(c, a[1]), ToI32(a[2]), ToI32(a[3]), ToI32(a[6]), width, height, ToBool(a[7]));
            return 0;
        });
        BindWithContext("text.width", (c, a) => TextRenderer.Measure(Font(c, a[1]), Text(c, a[0])));
        BindWithContext("text.line_height", (c, a) => Font(c, a[0]).LineHeight);

        BindWithContext("palette.use", (c, a) =>
        {
            string name = Name(c, a[0]);
            ushort[] colors = resources.Palette(name) ?? throw new ArgumentException($"Palette resource '{name}' not found.");
            for (int i = 0; i < colors.Length; i++)
            {
                graphics.Palette.SetColor(i, Color.FromValue(colors[i]));
            }

            return 0;
        });

        string[] missing = SdkAbi.Syscalls.Keys.Where(name => !bound.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"SDK syscalls without a platform implementation: {string.Join(", ", missing)}.");
        }

        return table;

        void Bind(string name, SimpleSyscallHandler handler) => BindWithContext(name, (_, args) => handler(args));

        void BindWithContext(string name, SyscallHandler handler)
        {
            if (!SdkAbi.Syscalls.TryGetValue(name, out SyscallDescriptor? descriptor))
            {
                throw new InvalidOperationException($"Syscall '{name}' is not declared in the SDK.");
            }

            table.Add(name, descriptor.Signature, handler);
            bound.Add(name);
        }

        void DrawRegion(ISyscallContext c, ReadOnlySpan<long> a, DrawTransform transform, int offsetX, int offsetY)
        {
            byte[] tilemap = resources.Tilemap(Name(c, a[1]));
            int tileX = ToI32(a[2]), tileY = ToI32(a[3]), width = ToI32(a[4]), height = ToI32(a[5]);
            if (tileX < 0 || tileY < 0 || width < 1 || height < 1 || tileX + width > 32 || tileY + height > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(a), $"Tile region ({tileX}, {tileY}, {width}×{height}) must lie inside the 32×32 tilemap.");
            }

            Rasterizer.Draw(Layer(a[0]), width * 8, height * 8, TileSources.Region(tilemap, tileX, tileY, offsetX, offsetY),
                ToI32(a[6]), ToI32(a[7]), transform);
        }

        // flags, scaleX, scaleY, rotation, pivotX, pivotY starting at argument 'first'
        static DrawTransform Transform(ReadOnlySpan<long> a, int first)
        {
            int flags = ToI32(a[first]);
            return new DrawTransform((flags & 1) != 0, (flags & 2) != 0, ToF32(a[first + 1]), ToF32(a[first + 2]), ToF32(a[first + 3]),
                ToF32(a[first + 4]), ToF32(a[first + 5]));
        }

        // (name, index) arguments of the level value syscalls
        int LevelValue(ISyscallContext c, ReadOnlySpan<long> a)
        {
            int[] data = resources.Level(Name(c, a[0])).Data;
            int index = ToI32(a[1]);
            return (uint)index < (uint)data.Length
                ? data[index]
                : throw new ArgumentOutOfRangeException(nameof(a), index, $"The level has {data.Length} values.");
        }

        // (name, frame, …) arguments of the animation syscalls
        AnimationFrame Frame(ISyscallContext c, ReadOnlySpan<long> a)
        {
            AnimationFrame[] frames = resources.Animation(Name(c, a[0])).Frames;
            int index = ToI32(a[1]);
            return (uint)index < (uint)frames.Length
                ? frames[index]
                : throw new ArgumentOutOfRangeException(nameof(a), index, $"The animation has {frames.Length} frames.");
        }

        // (name, frame, swap) arguments
        int Swap(ISyscallContext c, ReadOnlySpan<long> a)
        {
            int count = Frame(c, a).SpriteTiles.Length, index = ToI32(a[2]);
            return (uint)index < (uint)count ? index : throw new ArgumentOutOfRangeException(nameof(a), index, $"The frame has {count} tile swaps.");
        }

        RuntimeFont Font(ISyscallContext context, long slot) => resources.Font(context.ReadString(slot) ?? string.Empty);

        static string Text(ISyscallContext context, long slot) => context.ReadString(slot) ?? string.Empty;

        static ResourceType Type(long slot) =>
            Enum.IsDefined((ResourceType)ToI32(slot))
                ? (ResourceType)ToI32(slot)
                : throw new ArgumentOutOfRangeException(nameof(slot), ToI32(slot), "Unknown resource type.");

        static string Name(ISyscallContext context, long slot) =>
            context.ReadString(slot) ?? throw new ArgumentNullException(nameof(slot), "Resource name is null.");

        GraphicsLayer Layer(long slot) => graphics.Layers[ToI32(slot)];

        DisplayLayer Display(long slot) => graphics.DisplayLayers[ToI32(slot)];

        Gamepad Pad(long slot) => input.Gamepads[ToI32(slot)];
    }
}
