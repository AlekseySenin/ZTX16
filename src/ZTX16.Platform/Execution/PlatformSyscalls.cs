using ZTX16.Abi;
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

        var table = new SyscallTable();
        var bound = new HashSet<string>(StringComparer.Ordinal);

        // Machine
        Bind("machine.exit", _ => { machine.Exit(); return 0; });
        Bind("machine.suspend", _ => { machine.Suspend(); return 0; });
        Bind("machine.tick_count", _ => machine.TickCount);

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

        string[] missing = SdkAbi.Syscalls.Keys.Where(name => !bound.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"SDK syscalls without a platform implementation: {string.Join(", ", missing)}.");
        }

        return table;

        void Bind(string name, SyscallHandler handler)
        {
            if (!SdkAbi.Syscalls.TryGetValue(name, out SyscallDescriptor? descriptor))
            {
                throw new InvalidOperationException($"Syscall '{name}' is not declared in the SDK.");
            }

            table.Add(name, descriptor.Signature, handler);
            bound.Add(name);
        }

        GraphicsLayer Layer(long slot) => graphics.Layers[ToI32(slot)];

        DisplayLayer Display(long slot) => graphics.DisplayLayers[ToI32(slot)];

        Gamepad Pad(long slot) => input.Gamepads[ToI32(slot)];
    }
}
