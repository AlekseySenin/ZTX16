using ZTX16.Graphics;
using ZTX16.Input;
using ZTX16.VM;
using static ZTX16.VM.VmValue;

namespace ZTX16.Execution;

/// <summary>
/// The platform syscall table: every operation cartridge code can perform, bound to
/// one <see cref="Runtime.RuntimeSession"/>. This list is the cartridge sandbox boundary.
/// </summary>
/// <remarks>
/// Names are stable ABI. Arguments are validated by the subsystems themselves; an
/// invalid argument throws, which the VM turns into a trap (an unhandled cartridge failure).
/// Enum-typed arguments (keys, buttons, axes) are passed as i32 and use the numeric
/// values of the platform enums, which are therefore frozen.
/// </remarks>
internal static class PlatformSyscalls
{
    private const VmType I32 = VmType.I32;
    private const VmType I64 = VmType.I64;
    private const VmType F32 = VmType.F32;

    public static SyscallTable Create(Runtime.RuntimeSession session)
    {
        Machine machine = session.Machine;
        GraphicsSystem graphics = session.Graphics;
        InputSystem input = session.Input;

        var table = new SyscallTable();

        // Machine
        table.Add("machine.exit", VmSignature.Void, _ => { machine.Exit(); return 0; });
        table.Add("machine.suspend", VmSignature.Void, _ => { machine.Suspend(); return 0; });
        table.Add("machine.tick_count", VmSignature.Of(I64), _ => machine.TickCount);

        // Graphics layers
        table.Add("graphics.clear", VmSignature.Of(null, I32), a => { Layer(a[0]).Clear(); return 0; });
        table.Add("graphics.fill", VmSignature.Of(null, I32, I32), a => { Layer(a[0]).Fill(ToI32(a[1])); return 0; });
        table.Add("graphics.set_pixel", VmSignature.Of(null, I32, I32, I32, I32), a =>
        {
            Layer(a[0]).SetPixel(ToI32(a[1]), ToI32(a[2]), ToI32(a[3]));
            return 0;
        });
        table.Add("graphics.get_pixel", VmSignature.Of(I32, I32, I32, I32), a => Layer(a[0]).GetPixel(ToI32(a[1]), ToI32(a[2])));
        table.Add("graphics.fill_rect", VmSignature.Of(null, I32, I32, I32, I32, I32, I32), a =>
        {
            Layer(a[0]).FillRectangle(ToI32(a[1]), ToI32(a[2]), ToI32(a[3]), ToI32(a[4]), ToI32(a[5]));
            return 0;
        });
        table.Add("graphics.resize_layer", VmSignature.Of(null, I32, I32, I32), a =>
        {
            Layer(a[0]).Resize(ToI32(a[1]), ToI32(a[2]));
            return 0;
        });
        table.Add("graphics.layer_width", VmSignature.Of(I32, I32), a => Layer(a[0]).Width);
        table.Add("graphics.layer_height", VmSignature.Of(I32, I32), a => Layer(a[0]).Height);
        table.Add("graphics.clear_layers", VmSignature.Void, _ => { graphics.ClearLayers(); return 0; });

        // Display layers and palette
        table.Add("display.set_origin", VmSignature.Of(null, I32, I32, I32), a =>
        {
            Display(a[0]).SetOrigin(ToI32(a[1]), ToI32(a[2]));
            return 0;
        });
        table.Add("display.set_enabled", VmSignature.Of(null, I32, I32), a => { Display(a[0]).Enabled = ToBool(a[1]); return 0; });
        table.Add("display.is_enabled", VmSignature.Of(I32, I32), a => FromBool(Display(a[0]).Enabled));
        table.Add("palette.set_color", VmSignature.Of(null, I32, I32, I32, I32), a =>
        {
            graphics.Palette.SetColor(ToI32(a[0]), new Color(ToI32(a[1]), ToI32(a[2]), ToI32(a[3])));
            return 0;
        });

        // Keyboard
        table.Add("input.key_down", VmSignature.Of(I32, I32), a => FromBool(input.Keyboard[(Key)ToI32(a[0])].IsDown));
        table.Add("input.key_pressed", VmSignature.Of(I32, I32), a => FromBool(input.Keyboard[(Key)ToI32(a[0])].Pressed));
        table.Add("input.key_released", VmSignature.Of(I32, I32), a => FromBool(input.Keyboard[(Key)ToI32(a[0])].Released));

        // Mouse
        table.Add("input.mouse_x", VmSignature.Of(I32), _ => input.Mouse.X);
        table.Add("input.mouse_y", VmSignature.Of(I32), _ => input.Mouse.Y);
        table.Add("input.mouse_wheel", VmSignature.Of(I32), _ => input.Mouse.WheelDelta);
        table.Add("input.mouse_button_down", VmSignature.Of(I32, I32), a => FromBool(input.Mouse[(MouseButton)ToI32(a[0])].IsDown));
        table.Add("input.mouse_button_pressed", VmSignature.Of(I32, I32), a => FromBool(input.Mouse[(MouseButton)ToI32(a[0])].Pressed));

        // Gamepads
        table.Add("input.gamepad_connected", VmSignature.Of(I32, I32), a => FromBool(Pad(a[0]).IsConnected));
        table.Add("input.gamepad_button_down", VmSignature.Of(I32, I32, I32), a =>
            FromBool(Pad(a[0])[(GamepadButton)ToI32(a[1])].IsDown));
        table.Add("input.gamepad_button_pressed", VmSignature.Of(I32, I32, I32), a =>
            FromBool(Pad(a[0])[(GamepadButton)ToI32(a[1])].Pressed));
        table.Add("input.gamepad_axis", VmSignature.Of(F32, I32, I32), a =>
            FromF32(Pad(a[0]).GetAxis((GamepadAxis)ToI32(a[1]))));

        return table;

        GraphicsLayer Layer(long slot) => graphics.Layers[ToI32(slot)];

        DisplayLayer Display(long slot) => graphics.DisplayLayers[ToI32(slot)];

        Gamepad Pad(long slot) => input.Gamepads[ToI32(slot)];
    }
}
