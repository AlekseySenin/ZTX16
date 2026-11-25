using ZTX16.Execution;
using ZTX16.Input;
using ZTX16.Runtime;
using ZTX16.VM;

namespace ZTX16.Tests;

internal static class VmTestHelpers
{
    public const VmType I32 = VmType.I32;

    /// <summary>Builds a single-function module and runs it.</summary>
    public static long Run(VmSignature signature, Action<FunctionBuilder> body, params long[] args)
    {
        var m = new ModuleBuilder();
        FunctionBuilder f = m.DefineFunction("main", signature);
        body(f);
        var instance = new VmInstance(Verifier.Verify(m.Build()), new SyscallTable());
        return instance.Invoke(0, args);
    }

    public static VerificationException Rejects(VmSignature signature, Action<FunctionBuilder> body, Action<ModuleBuilder>? setup = null)
    {
        var m = new ModuleBuilder();
        setup?.Invoke(m);
        FunctionBuilder f = m.DefineFunction("main", signature);
        body(f);
        VmModule module = m.Build();
        return Assert.Throws<VerificationException>(() => Verifier.Verify(module));
    }
}

public class VmInterpreterTests
{
    private const VmType I32 = VmType.I32;
    private const VmType I64 = VmType.I64;
    private const VmType F32 = VmType.F32;

    [Fact]
    public void AddsArguments()
    {
        long result = VmTestHelpers.Run(VmSignature.Of(I32, I32, I32),
            f => f.Emit(OpCode.LoadLocal, 0).Emit(OpCode.LoadLocal, 1).Emit(OpCode.AddI32).Emit(OpCode.Return), 40, 2);
        Assert.Equal(42, result);
    }

    [Fact]
    public void LoopSumsOneToHundred()
    {
        long result = VmTestHelpers.Run(VmSignature.Of(I32), f =>
        {
            int i = f.AddLocal(I32);
            int sum = f.AddLocal(I32);
            Label loop = f.DefineLabel();
            Label done = f.DefineLabel();
            f.ConstI32(1).Emit(OpCode.StoreLocal, i);
            f.Mark(loop);
            f.Emit(OpCode.LoadLocal, i).ConstI32(100).Emit(OpCode.GtI32).Emit(OpCode.BranchIfTrue, done);
            f.Emit(OpCode.LoadLocal, sum).Emit(OpCode.LoadLocal, i).Emit(OpCode.AddI32).Emit(OpCode.StoreLocal, sum);
            f.Emit(OpCode.LoadLocal, i).ConstI32(1).Emit(OpCode.AddI32).Emit(OpCode.StoreLocal, i);
            f.Emit(OpCode.Branch, loop);
            f.Mark(done);
            f.Emit(OpCode.LoadLocal, sum).Emit(OpCode.Return);
        });
        Assert.Equal(5050, result);
    }

    [Fact]
    public void RecursiveFibonacci()
    {
        var m = new ModuleBuilder();
        FunctionBuilder fib = m.DefineFunction("fib", VmSignature.Of(I32, I32));
        Label recurse = fib.DefineLabel();
        fib.Emit(OpCode.LoadLocal, 0).ConstI32(2).Emit(OpCode.LtI32).Emit(OpCode.BranchIfFalse, recurse);
        fib.Emit(OpCode.LoadLocal, 0).Emit(OpCode.Return);
        fib.Mark(recurse);
        fib.Emit(OpCode.LoadLocal, 0).ConstI32(1).Emit(OpCode.SubI32).Call(fib);
        fib.Emit(OpCode.LoadLocal, 0).ConstI32(2).Emit(OpCode.SubI32).Call(fib);
        fib.Emit(OpCode.AddI32).Emit(OpCode.Return);

        var instance = new VmInstance(Verifier.Verify(m.Build()), new SyscallTable());
        Assert.Equal(6765, instance.Invoke(0, [20]));
    }

    [Fact]
    public void FloatArithmeticAndConversions()
    {
        long result = VmTestHelpers.Run(VmSignature.Of(I32), f =>
            f.ConstF32(2.5f).ConstF32(4f).Emit(OpCode.MulF32).Emit(OpCode.F32ToI32).Emit(OpCode.Return));
        Assert.Equal(10, result);

        long f32 = VmTestHelpers.Run(VmSignature.Of(F32), f =>
            f.ConstI32(7).Emit(OpCode.I32ToF32).ConstF32(2f).Emit(OpCode.DivF32).Emit(OpCode.Return));
        Assert.Equal(3.5f, VmValue.ToF32(f32));
    }

    [Fact]
    public void FloatToIntSaturatesAndNaNIsZero()
    {
        Assert.Equal(int.MaxValue, VmTestHelpers.Run(VmSignature.Of(I32), f =>
            f.ConstF32(1e20f).Emit(OpCode.F32ToI32).Emit(OpCode.Return)));
        Assert.Equal(0, VmTestHelpers.Run(VmSignature.Of(I32), f =>
            f.ConstF32(float.NaN).Emit(OpCode.F32ToI32).Emit(OpCode.Return)));
    }

    [Fact]
    public void I32ArithmeticWrapsAndI64IsWide()
    {
        Assert.Equal(int.MinValue, VmTestHelpers.Run(VmSignature.Of(I32), f =>
            f.ConstI32(int.MaxValue).ConstI32(1).Emit(OpCode.AddI32).Emit(OpCode.Return)));
        Assert.Equal(1L << 40, VmTestHelpers.Run(VmSignature.Of(I64), f =>
            f.ConstI64(1).ConstI32(40).Emit(OpCode.ShlI64).Emit(OpCode.Return)));
    }

    [Fact]
    public void NarrowingConversionsMatchCSharp()
    {
        Assert.Equal(-1, VmTestHelpers.Run(VmSignature.Of(I32), f => f.ConstI32(255).Emit(OpCode.I32ToI8).Emit(OpCode.Return)));
        Assert.Equal(0x34, VmTestHelpers.Run(VmSignature.Of(I32), f => f.ConstI32(0x1234).Emit(OpCode.I32ToU8).Emit(OpCode.Return)));
    }

    [Fact]
    public void GlobalsPersistBetweenInvocations()
    {
        var m = new ModuleBuilder();
        int counter = m.AddGlobal(I32);
        FunctionBuilder inc = m.DefineFunction("inc", VmSignature.Of(I32));
        inc.Emit(OpCode.LoadGlobal, counter).ConstI32(1).Emit(OpCode.AddI32).Emit(OpCode.Dup)
            .Emit(OpCode.StoreGlobal, counter).Emit(OpCode.Return);

        var instance = new VmInstance(Verifier.Verify(m.Build()), new SyscallTable());
        instance.Invoke(0, []);
        instance.Invoke(0, []);
        Assert.Equal(3, instance.Invoke(0, []));
    }

    [Fact]
    public void SyscallReceivesArgumentsAndReturnsResult()
    {
        var m = new ModuleBuilder();
        int mul = m.Import("test.mul", VmSignature.Of(I32, I32, I32));
        FunctionBuilder f = m.DefineFunction("main", VmSignature.Of(I32));
        f.ConstI32(6).ConstI32(7).Emit(OpCode.Syscall, mul).Emit(OpCode.Return);

        var table = new SyscallTable().Add("test.mul", VmSignature.Of(I32, I32, I32), a => a[0] * a[1]);
        Assert.Equal(42, new VmInstance(Verifier.Verify(m.Build()), table).Invoke(0, []));
    }
}

public class VmTrapTests
{
    private const VmType I32 = VmType.I32;

    [Fact]
    public void DivisionByZeroTraps()
    {
        var e = Assert.Throws<VmTrapException>(() => VmTestHelpers.Run(VmSignature.Of(I32), f =>
            f.ConstI32(1).ConstI32(0).Emit(OpCode.DivI32).Emit(OpCode.Return)));
        Assert.Equal("main", e.Function);
    }

    [Fact]
    public void MinValueDividedByMinusOneTraps()
    {
        Assert.Throws<VmTrapException>(() => VmTestHelpers.Run(VmSignature.Of(I32), f =>
            f.ConstI32(int.MinValue).ConstI32(-1).Emit(OpCode.DivI32).Emit(OpCode.Return)));
    }

    [Fact]
    public void InfiniteLoopExhaustsBudget()
    {
        var m = new ModuleBuilder();
        FunctionBuilder f = m.DefineFunction("spin", VmSignature.Void);
        Label top = f.DefineLabel();
        f.Mark(top).Emit(OpCode.Branch, top);

        var instance = new VmInstance(Verifier.Verify(m.Build()), new SyscallTable());
        Assert.Throws<VmTrapException>(() => instance.Invoke(0, [], instructionBudget: 1000));
        Assert.Equal(1000, instance.LastInstructionCount);
    }

    [Fact]
    public void UnboundedRecursionTraps()
    {
        var m = new ModuleBuilder();
        FunctionBuilder f = m.DefineFunction("down", VmSignature.Void);
        f.Call(f).Emit(OpCode.Return);

        var instance = new VmInstance(Verifier.Verify(m.Build()), new SyscallTable());
        var e = Assert.Throws<VmTrapException>(() => instance.Invoke(0, []));
        Assert.Contains("Call stack overflow", e.Message);

        // The instance stays usable after a trap.
        Assert.Throws<VmTrapException>(() => instance.Invoke(0, []));
    }

    [Fact]
    public void FailingSyscallBecomesTrap()
    {
        var m = new ModuleBuilder();
        int boom = m.Import("test.boom", VmSignature.Void);
        FunctionBuilder f = m.DefineFunction("main", VmSignature.Void);
        f.Emit(OpCode.Syscall, boom).Emit(OpCode.Return);

        var table = new SyscallTable().Add("test.boom", VmSignature.Void, _ => throw new ArgumentOutOfRangeException("x"));
        var e = Assert.Throws<VmTrapException>(() => new VmInstance(Verifier.Verify(m.Build()), table).Invoke(0, []));
        Assert.IsType<ArgumentOutOfRangeException>(e.InnerException);
    }
}

public class VerifierTests
{
    private const VmType I32 = VmType.I32;
    private const VmType F32 = VmType.F32;

    [Fact]
    public void RejectsStackUnderflow()
    {
        VmTestHelpers.Rejects(VmSignature.Void, f => f.Emit(OpCode.Pop).Emit(OpCode.Return));
    }

    [Fact]
    public void RejectsTypeMismatch()
    {
        var e = VmTestHelpers.Rejects(VmSignature.Of(I32), f =>
            f.ConstI32(1).ConstF32(2f).Emit(OpCode.AddI32).Emit(OpCode.Return));
        Assert.Contains("expected i32", e.Message);
    }

    [Fact]
    public void RejectsWrongReturnType()
    {
        VmTestHelpers.Rejects(VmSignature.Of(I32), f => f.ConstF32(1f).Emit(OpCode.Return));
    }

    [Fact]
    public void RejectsExtraValuesAtReturn()
    {
        VmTestHelpers.Rejects(VmSignature.Void, f => f.ConstI32(1).Emit(OpCode.Return));
    }

    [Fact]
    public void RejectsFallingOffTheEnd()
    {
        VmTestHelpers.Rejects(VmSignature.Void, f => f.Emit(OpCode.Nop));
    }

    [Fact]
    public void RejectsBranchIntoMiddleOfInstruction()
    {
        var module = new VmModule([], [],
            [new VmFunction("main", VmSignature.Void, [], [(byte)OpCode.Branch, 1, 0, 0, 0, (byte)OpCode.Return])],
            new Dictionary<string, int>());
        Assert.Throws<VerificationException>(() => Verifier.Verify(module));
    }

    [Fact]
    public void RejectsMismatchedStacksAtMergePoint()
    {
        VmTestHelpers.Rejects(VmSignature.Of(I32), f =>
        {
            Label other = f.DefineLabel();
            Label join = f.DefineLabel();
            f.ConstI32(1).Emit(OpCode.BranchIfTrue, other);
            f.ConstI32(1).Emit(OpCode.Branch, join);
            f.Mark(other).ConstF32(1f);
            f.Mark(join).Emit(OpCode.Return);
        });
    }

    [Fact]
    public void RejectsLocalUsedWithWrongType()
    {
        VmTestHelpers.Rejects(VmSignature.Void, f =>
        {
            int local = f.AddLocal(F32);
            f.ConstI32(1).Emit(OpCode.StoreLocal, local).Emit(OpCode.Return);
        });
    }

    [Fact]
    public void RejectsOutOfRangeLocalGlobalAndCall()
    {
        VmTestHelpers.Rejects(VmSignature.Void, f => f.Emit(OpCode.LoadLocal, 3).Emit(OpCode.Pop).Emit(OpCode.Return));
        VmTestHelpers.Rejects(VmSignature.Void, f => f.Emit(OpCode.LoadGlobal, 0).Emit(OpCode.Pop).Emit(OpCode.Return));
        VmTestHelpers.Rejects(VmSignature.Void, f => f.Emit(OpCode.Call, 9).Emit(OpCode.Return));
    }

    [Fact]
    public void RejectsUndefinedOpcode()
    {
        var module = new VmModule([], [], [new VmFunction("main", VmSignature.Void, [], [0xFF])], new Dictionary<string, int>());
        Assert.Throws<VerificationException>(() => Verifier.Verify(module));
    }

    [Fact]
    public void ChecksSyscallArgumentTypes()
    {
        VmTestHelpers.Rejects(VmSignature.Void,
            f => f.ConstF32(1f).Emit(OpCode.Syscall, 0).Emit(OpCode.Return),
            m => m.Import("test.take_i32", VmSignature.Of(null, I32)));
    }
}

public class ExecutableFormatTests
{
    private static VmModule Sample()
    {
        var m = new ModuleBuilder();
        m.AddGlobal(VmType.I32);
        m.AddGlobal(VmType.F64);
        int sys = m.Import("graphics.clear", VmSignature.Of(null, VmType.I32));
        FunctionBuilder helper = m.DefineFunction("helper", VmSignature.Of(VmType.F32, VmType.I64));
        helper.AddLocal(VmType.F64);
        helper.ConstF32(1.5f).Emit(OpCode.Return);
        FunctionBuilder main = m.DefineFunction("Тест", VmSignature.Void); // non-ASCII name
        main.ConstI32(0).Emit(OpCode.Syscall, sys).ConstI64(5).Call(helper).Emit(OpCode.Pop).Emit(OpCode.Return);
        m.Export("Initialize", main);
        return m.Build();
    }

    [Fact]
    public void RoundTripsModule()
    {
        VmModule original = Sample();
        byte[] bytes = ExecutableFormat.Write(original);
        VmModule loaded = ExecutableFormat.Read(bytes);

        Assert.Equal(bytes, ExecutableFormat.Write(loaded));
        Assert.Equal(original.Functions.Count, loaded.Functions.Count);
        Assert.Equal(original.Imports.Select(i => i.Name), loaded.Imports.Select(i => i.Name));
        Verifier.Verify(loaded);
    }

    [Fact]
    public void RejectsBadMagicVersionAndTruncation()
    {
        byte[] bytes = ExecutableFormat.Write(Sample());

        byte[] badMagic = (byte[])bytes.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<ExecutableFormatException>(() => ExecutableFormat.Read(badMagic));

        byte[] badVersion = (byte[])bytes.Clone();
        badVersion[4] = 99;
        Assert.Throws<ExecutableFormatException>(() => ExecutableFormat.Read(badVersion));

        Assert.Throws<ExecutableFormatException>(() => ExecutableFormat.Read(bytes.AsSpan(0, bytes.Length - 3)));
    }
}

public class VmProgramTests
{
    private const VmType I32 = VmType.I32;

    private static VmModule LifecycleModule(Action<ModuleBuilder, FunctionBuilder, FunctionBuilder, FunctionBuilder> build)
    {
        var m = new ModuleBuilder();
        FunctionBuilder init = m.DefineFunction("Initialize", VmSignature.Void);
        FunctionBuilder tick = m.DefineFunction("Tick", VmSignature.Void);
        FunctionBuilder render = m.DefineFunction("Render", VmSignature.Void);
        build(m, init, tick, render);
        init.Emit(OpCode.Return);
        tick.Emit(OpCode.Return);
        render.Emit(OpCode.Return);
        m.Export("Initialize", init);
        m.Export("Tick", tick);
        m.Export("Render", render);
        return m.Build();
    }

    [Fact]
    public void CartridgeDrawsThroughSyscalls()
    {
        VmModule module = LifecycleModule((m, init, tick, render) =>
        {
            int fillRect = m.Import("graphics.fill_rect", VmSignature.Of(null, I32, I32, I32, I32, I32, I32));
            render.ConstI32(3).ConstI32(10).ConstI32(20).ConstI32(2).ConstI32(2).ConstI32(9).Emit(OpCode.Syscall, fillRect);
        });

        var session = new RuntimeSession(VmProgram.FromModule(module), 48000);
        session.Initialize();
        session.RunTick(TimeSpan.Zero);

        Assert.Equal(9, session.Graphics.Layers[3].GetPixel(11, 21));
        Assert.Equal(0, session.Graphics.Layers[3].GetPixel(12, 21));
    }

    [Fact]
    public void CartridgeReadsInputAndRequestsExit()
    {
        VmModule module = LifecycleModule((m, init, tick, render) =>
        {
            int pressed = m.Import("input.key_pressed", VmSignature.Of(I32, I32));
            int exit = m.Import("machine.exit", VmSignature.Void);
            Label skip = tick.DefineLabel();
            tick.ConstI32((int)Key.Escape).Emit(OpCode.Syscall, pressed).Emit(OpCode.BranchIfFalse, skip);
            tick.Emit(OpCode.Syscall, exit);
            tick.Mark(skip);
        });

        var session = new RuntimeSession(VmProgram.FromModule(module), 48000);
        session.Initialize();
        session.RunTick(TimeSpan.Zero);
        Assert.False(session.ShouldExit);

        session.Input.Enqueue(InputEvent.KeyChange(Key.Escape, true));
        session.RunTick(TimeSpan.FromSeconds(1.0 / 60));
        Assert.True(session.ShouldExit);
    }

    [Fact]
    public void InvalidSyscallArgumentTrapsTheCartridge()
    {
        VmModule module = LifecycleModule((m, init, tick, render) =>
        {
            int clear = m.Import("graphics.clear", VmSignature.Of(null, I32));
            tick.ConstI32(8).Emit(OpCode.Syscall, clear); // there is no layer 8
        });

        var session = new RuntimeSession(VmProgram.FromModule(module), 48000);
        session.Initialize();
        Assert.Throws<VmTrapException>(() => session.RunTick(TimeSpan.Zero));
    }

    [Fact]
    public void UnknownSyscallFailsToLink()
    {
        VmModule module = LifecycleModule((m, init, tick, render) =>
        {
            int nope = m.Import("host.open_file", VmSignature.Void);
            tick.Emit(OpCode.Syscall, nope);
        });

        var session = new RuntimeSession(VmProgram.FromModule(module), 48000);
        Assert.Throws<LinkException>(session.Initialize);
    }

    [Fact]
    public void MissingLifecycleExportIsRejected()
    {
        var m = new ModuleBuilder();
        FunctionBuilder init = m.DefineFunction("Initialize", VmSignature.Void);
        init.Emit(OpCode.Return);
        m.Export("Initialize", init);

        Assert.Throws<VerificationException>(() => VmProgram.FromModule(m.Build()));
    }
}
