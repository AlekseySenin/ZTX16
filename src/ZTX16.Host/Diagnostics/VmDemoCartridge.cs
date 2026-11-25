using ZTX16.Input;
using ZTX16.VM;

namespace ZTX16.Host.Diagnostics;

/// <summary>
/// A hand-assembled ZTX16 executable used to exercise the VM path end to end before the
/// C# toolchain exists: globals, calls, branches and syscalls.
/// </summary>
/// <remarks>
/// Behaviour: checkerboard-free solid background, orientation markers, and a square
/// moved with the arrow keys (clamped by a helper function). Escape exits.
/// </remarks>
internal static class VmDemoCartridge
{
    private const VmType I32 = VmType.I32;

    /// <summary>Builds the demo and returns it as executable bytes.</summary>
    public static byte[] BuildExecutable() => ExecutableFormat.Write(Build());

    public static VmModule Build()
    {
        var m = new ModuleBuilder();
        int x = m.AddGlobal(I32);
        int y = m.AddGlobal(I32);

        int exit = m.Import("machine.exit", VmSignature.Void);
        int fill = m.Import("graphics.fill", VmSignature.Of(null, I32, I32));
        int clear = m.Import("graphics.clear", VmSignature.Of(null, I32));
        int fillRect = m.Import("graphics.fill_rect", VmSignature.Of(null, I32, I32, I32, I32, I32, I32));
        int keyDown = m.Import("input.key_down", VmSignature.Of(I32, I32));
        int keyPressed = m.Import("input.key_pressed", VmSignature.Of(I32, I32));

        // clamp(v, lo, hi) -> i32
        FunctionBuilder clamp = m.DefineFunction("clamp", VmSignature.Of(I32, I32, I32, I32));
        {
            Label notBelow = clamp.DefineLabel();
            Label notAbove = clamp.DefineLabel();
            clamp.Emit(OpCode.LoadLocal, 0).Emit(OpCode.LoadLocal, 1).Emit(OpCode.LtI32).Emit(OpCode.BranchIfFalse, notBelow);
            clamp.Emit(OpCode.LoadLocal, 1).Emit(OpCode.Return);
            clamp.Mark(notBelow);
            clamp.Emit(OpCode.LoadLocal, 0).Emit(OpCode.LoadLocal, 2).Emit(OpCode.GtI32).Emit(OpCode.BranchIfFalse, notAbove);
            clamp.Emit(OpCode.LoadLocal, 2).Emit(OpCode.Return);
            clamp.Mark(notAbove);
            clamp.Emit(OpCode.LoadLocal, 0).Emit(OpCode.Return);
        }

        // axis(negativeKey, positiveKey) -> -1/0/+1
        FunctionBuilder axis = m.DefineFunction("axis", VmSignature.Of(I32, I32, I32));
        axis.Emit(OpCode.LoadLocal, 1).Emit(OpCode.Syscall, keyDown)
            .Emit(OpCode.LoadLocal, 0).Emit(OpCode.Syscall, keyDown)
            .Emit(OpCode.SubI32).Emit(OpCode.Return);

        FunctionBuilder initialize = m.DefineFunction("Initialize", VmSignature.Void);
        initialize.ConstI32(120).Emit(OpCode.StoreGlobal, x);
        initialize.ConstI32(120).Emit(OpCode.StoreGlobal, y);
        initialize.ConstI32(0).ConstI32(18).Emit(OpCode.Syscall, fill);
        EmitRect(initialize, fillRect, layer: 1, 0, 0, 24, 4, color: 7);
        EmitRect(initialize, fillRect, layer: 1, 0, 0, 4, 24, color: 7);
        EmitRect(initialize, fillRect, layer: 1, 232, 252, 24, 4, color: 19);
        EmitRect(initialize, fillRect, layer: 1, 252, 232, 4, 24, color: 19);
        initialize.Emit(OpCode.Return);

        FunctionBuilder tick = m.DefineFunction("Tick", VmSignature.Void);
        {
            Label noExit = tick.DefineLabel();
            tick.ConstI32((int)Key.Escape).Emit(OpCode.Syscall, keyPressed).Emit(OpCode.BranchIfFalse, noExit);
            tick.Emit(OpCode.Syscall, exit);
            tick.Mark(noExit);

            EmitMove(tick, clamp, axis, x, (int)Key.Left, (int)Key.Right);
            EmitMove(tick, clamp, axis, y, (int)Key.Down, (int)Key.Up);
            tick.Emit(OpCode.Return);
        }

        FunctionBuilder render = m.DefineFunction("Render", VmSignature.Void);
        render.ConstI32(2).Emit(OpCode.Syscall, clear);
        render.ConstI32(2).Emit(OpCode.LoadGlobal, x).Emit(OpCode.LoadGlobal, y)
            .ConstI32(16).ConstI32(16).ConstI32(11).Emit(OpCode.Syscall, fillRect);
        render.ConstI32(2)
            .Emit(OpCode.LoadGlobal, x).ConstI32(4).Emit(OpCode.AddI32)
            .Emit(OpCode.LoadGlobal, y).ConstI32(4).Emit(OpCode.AddI32)
            .ConstI32(8).ConstI32(8).ConstI32(22).Emit(OpCode.Syscall, fillRect);
        render.Emit(OpCode.Return);

        m.Export(VmModule.LifecycleExports.Initialize, initialize);
        m.Export(VmModule.LifecycleExports.Tick, tick);
        m.Export(VmModule.LifecycleExports.Render, render);
        return m.Build();
    }

    // global = clamp(global + axis(neg, pos) * 2, 0, 240)
    private static void EmitMove(FunctionBuilder f, FunctionBuilder clamp, FunctionBuilder axis, int global, int negativeKey, int positiveKey)
    {
        f.Emit(OpCode.LoadGlobal, global)
            .ConstI32(negativeKey).ConstI32(positiveKey).Call(axis)
            .ConstI32(2).Emit(OpCode.MulI32).Emit(OpCode.AddI32)
            .ConstI32(0).ConstI32(240).Call(clamp)
            .Emit(OpCode.StoreGlobal, global);
    }

    private static void EmitRect(FunctionBuilder f, int fillRect, int layer, int x, int y, int w, int h, int color)
    {
        f.ConstI32(layer).ConstI32(x).ConstI32(y).ConstI32(w).ConstI32(h).ConstI32(color).Emit(OpCode.Syscall, fillRect);
    }
}
