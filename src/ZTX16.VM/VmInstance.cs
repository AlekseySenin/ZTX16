using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ZTX16.VM;

/// <summary>
/// Thrown when executing code hits a runtime fault: division by zero, exhausted
/// instruction budget, call stack overflow, or a failing syscall. A trap is an
/// unhandled failure of the cartridge.
/// </summary>
public sealed class VmTrapException : Exception
{
    /// <summary>Creates the exception.</summary>
    public VmTrapException(string message, string? function = null, int offset = -1, Exception? inner = null)
        : base(function is null ? message : $"{message} (in '{function}' at offset {offset})", inner)
    {
        Function = function;
        Offset = offset;
    }

    /// <summary>Function where the trap occurred, if known.</summary>
    public string? Function { get; }

    /// <summary>Code offset of the faulting instruction, or -1.</summary>
    public int Offset { get; }
}

/// <summary>
/// An instantiated module: verified code, bound syscalls and global state. Executes
/// functions with the interpreter.
/// </summary>
/// <remarks>
/// Values live in one shared slot array. A call frame is [locals | operand stack];
/// a callee's parameters are the caller's top operands, so arguments are never copied.
/// All values are raw 64-bit slots; the verifier guarantees their types.
/// </remarks>
public sealed class VmInstance : ISyscallContext
{
    /// <summary>Maximum nesting of calls. Provisional implementation limit.</summary>
    public const int MaxCallDepth = 256;

    /// <summary>Total value slots available to all frames. Provisional implementation limit.</summary>
    public const int StackSlots = 64 * 1024;

    private readonly VerifiedModule _verified;
    private readonly VmFunction[] _functions;
    private readonly SyscallHandler[] _syscalls;
    private readonly VmImport[] _imports;
    private readonly long[] _globals;
    private readonly long[] _stack = new long[StackSlots];
    private readonly VmHeap _heap;
    private readonly long[] _stringHandles;
    private readonly HashSet<int>[] _assignableTo;
    private readonly int[] _frameFunction = new int[MaxCallDepth + 2];
    private readonly int[] _frameBase = new int[MaxCallDepth + 2];
    private readonly int[] _framePc = new int[MaxCallDepth + 2];
    private readonly int[] _frameResumePc = new int[MaxCallDepth + 2];
    private readonly long[] _frameConstructed = new long[MaxCallDepth + 2];
    private readonly List<long> _temporaryRoots = [];
    private VmFunction? _syscallFunction;
    private int _syscallOffset;
    private long _budget = long.MaxValue;
    private int _depth;

    /// <summary>Instantiates <paramref name="module"/> and links its imports against <paramref name="syscalls"/>.</summary>
    /// <exception cref="LinkException">An import cannot be resolved.</exception>
    public VmInstance(VerifiedModule module, SyscallTable syscalls)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(syscalls);

        _verified = module;
        _functions = module.Module.Functions.ToArray();
        _imports = module.Module.Imports.ToArray();
        _syscalls = _imports.Select(syscalls.Resolve).ToArray();
        _globals = new long[module.Module.Globals.Count];
        _heap = new VmHeap(module.Module);

        // String literals are allocated once and stay alive for the instance's lifetime.
        _stringHandles = module.Module.Strings.Select(s => _heap.Add(new StringObject(s))).ToArray();
        _assignableTo = BuildAssignability(module.Module);
    }

    /// <summary>The module being executed.</summary>
    public VmModule Module => _verified.Module;

    /// <summary>Number of live heap objects (including string literals).</summary>
    public int HeapObjectCount => _heap.ObjectCount;

    /// <summary>Approximate bytes held by heap objects.</summary>
    public long HeapBytes => _heap.LiveBytes;

    /// <summary>Number of garbage collections performed.</summary>
    public int GarbageCollections => _heap.Collections;

    /// <summary>Runs a full collection. Only valid between invocations.</summary>
    public void CollectGarbage()
    {
        if (_depth != 0)
        {
            throw new InvalidOperationException("Cannot collect explicitly while code is running.");
        }

        Collect();
    }

    /// <summary>Instructions executed by the most recent <see cref="Invoke"/> call.</summary>
    public long LastInstructionCount { get; private set; }

    /// <summary>
    /// Invokes function <paramref name="functionIndex"/>. At most <paramref name="instructionBudget"/>
    /// instructions may execute before the call traps.
    /// </summary>
    /// <returns>The raw result slot, or 0 for functions without a result.</returns>
    /// <exception cref="VmTrapException">Execution faulted.</exception>
    public long Invoke(int functionIndex, ReadOnlySpan<long> args, long instructionBudget = long.MaxValue)
    {
        if ((uint)functionIndex >= (uint)_functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionIndex));
        }

        VmFunction function = _functions[functionIndex];
        if (args.Length != function.Signature.Parameters.Count)
        {
            throw new ArgumentException($"Function '{function.Name}' takes {function.Signature.Parameters.Count} argument(s).", nameof(args));
        }

        if (_depth != 0)
        {
            throw new InvalidOperationException("Re-entrant invocation is not supported.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(instructionBudget);
        _budget = instructionBudget;
        args.CopyTo(_stack);
        try
        {
            Execute(functionIndex, 0);
            return function.Signature.Result is null ? 0 : _stack[0];
        }
        finally
        {
            LastInstructionCount = instructionBudget - _budget;
            _depth = 0;
        }
    }

    /// <summary>
    /// Pushes a call frame for <paramref name="functionIndex"/> whose parameters already occupy the
    /// slots starting at <paramref name="frameBase"/>. Returns the frame's initial operand stack pointer.
    /// </summary>
    private int PushFrame(int functionIndex, int frameBase, long constructedObject)
    {
        VmFunction function = _functions[functionIndex];
        if (_depth + 1 > MaxCallDepth)
        {
            throw new VmTrapException("Call stack overflow", function.Name, 0);
        }

        int sp = frameBase + function.LocalCount;
        if (sp + _verified.MaxStack[functionIndex] > _stack.Length)
        {
            throw new VmTrapException("Value stack overflow", function.Name, 0);
        }

        int frame = ++_depth;
        _frameFunction[frame] = functionIndex;
        _frameBase[frame] = frameBase;
        _frameConstructed[frame] = constructedObject;

        // Parameters were placed by the caller; extra locals start zeroed.
        Array.Clear(_stack, frameBase + function.Signature.Parameters.Count, function.Locals.Count);
        return sp;
    }

    /// <summary>
    /// Runs <paramref name="entryFunction"/> to completion. The interpreter is not recursive: VM calls
    /// push frames onto explicit frame arrays, so VM call depth never consumes the host stack.
    /// </summary>
    private void Execute(int entryFunction, int entryBase)
    {
        int entryDepth = _depth;
        int functionIndex = entryFunction;
        int frameBase = entryBase;
        int sp = PushFrame(functionIndex, frameBase, 0);
        int frame = _depth;
        VmFunction function = _functions[functionIndex];

        long[] s = _stack;
        ReadOnlySpan<byte> code = function.Code;
        int pc = 0;

        while (true)
        {
            int start = pc;
            if (--_budget < 0)
            {
                _budget = 0;
                throw new VmTrapException("Instruction budget exhausted", function.Name, start);
            }

            var op = (OpCode)code[pc++];
            switch (op)
            {
                case OpCode.Nop:
                    break;

                case OpCode.ConstI32:
                    s[sp++] = BinaryPrimitives.ReadInt32LittleEndian(code[pc..]);
                    pc += 4;
                    break;
                case OpCode.ConstI64:
                    s[sp++] = BinaryPrimitives.ReadInt64LittleEndian(code[pc..]);
                    pc += 8;
                    break;
                case OpCode.ConstF32:
                    s[sp++] = BinaryPrimitives.ReadInt32LittleEndian(code[pc..]);
                    pc += 4;
                    break;
                case OpCode.ConstF64:
                    s[sp++] = BinaryPrimitives.ReadInt64LittleEndian(code[pc..]);
                    pc += 8;
                    break;

                case OpCode.LoadLocal:
                    s[sp++] = s[frameBase + ReadU16(code, ref pc)];
                    break;
                case OpCode.StoreLocal:
                    s[frameBase + ReadU16(code, ref pc)] = s[--sp];
                    break;
                case OpCode.LoadGlobal:
                    s[sp++] = _globals[ReadU16(code, ref pc)];
                    break;
                case OpCode.StoreGlobal:
                    _globals[ReadU16(code, ref pc)] = s[--sp];
                    break;

                case OpCode.Dup:
                    s[sp] = s[sp - 1];
                    sp++;
                    break;
                case OpCode.Pop:
                    sp--;
                    break;

                // i32
                case OpCode.AddI32: sp--; s[sp - 1] = unchecked(I32(s[sp - 1]) + I32(s[sp])); break;
                case OpCode.SubI32: sp--; s[sp - 1] = unchecked(I32(s[sp - 1]) - I32(s[sp])); break;
                case OpCode.MulI32: sp--; s[sp - 1] = unchecked(I32(s[sp - 1]) * I32(s[sp])); break;
                case OpCode.DivI32:
                    {
                        sp--;
                        int a = I32(s[sp - 1]), b = I32(s[sp]);
                        if (b == 0)
                        {
                            throw new VmTrapException("Integer division by zero", function.Name, start);
                        }

                        if (a == int.MinValue && b == -1)
                        {
                            throw new VmTrapException("Integer division overflow", function.Name, start);
                        }

                        s[sp - 1] = a / b;
                        break;
                    }

                case OpCode.RemI32:
                    {
                        sp--;
                        int a = I32(s[sp - 1]), b = I32(s[sp]);
                        if (b == 0)
                        {
                            throw new VmTrapException("Integer division by zero", function.Name, start);
                        }

                        s[sp - 1] = b == -1 ? 0 : a % b;
                        break;
                    }

                case OpCode.NegI32: s[sp - 1] = unchecked(-I32(s[sp - 1])); break;
                case OpCode.AndI32: sp--; s[sp - 1] = I32(s[sp - 1]) & I32(s[sp]); break;
                case OpCode.OrI32: sp--; s[sp - 1] = I32(s[sp - 1]) | I32(s[sp]); break;
                case OpCode.XorI32: sp--; s[sp - 1] = I32(s[sp - 1]) ^ I32(s[sp]); break;
                case OpCode.NotI32: s[sp - 1] = ~I32(s[sp - 1]); break;
                case OpCode.ShlI32: sp--; s[sp - 1] = I32(s[sp - 1]) << (I32(s[sp]) & 31); break;
                case OpCode.ShrI32: sp--; s[sp - 1] = I32(s[sp - 1]) >> (I32(s[sp]) & 31); break;
                case OpCode.ShrUnI32: sp--; s[sp - 1] = (int)((uint)I32(s[sp - 1]) >> (I32(s[sp]) & 31)); break;
                case OpCode.DivUnI32:
                case OpCode.RemUnI32:
                    {
                        sp--;
                        uint a = (uint)I32(s[sp - 1]), b = (uint)I32(s[sp]);
                        if (b == 0)
                        {
                            throw new VmTrapException("Integer division by zero", function.Name, start);
                        }

                        s[sp - 1] = (int)(op == OpCode.DivUnI32 ? a / b : a % b);
                        break;
                    }

                case OpCode.DivUnI64:
                case OpCode.RemUnI64:
                    {
                        sp--;
                        ulong a = (ulong)s[sp - 1], b = (ulong)s[sp];
                        if (b == 0)
                        {
                            throw new VmTrapException("Integer division by zero", function.Name, start);
                        }

                        s[sp - 1] = (long)(op == OpCode.DivUnI64 ? a / b : a % b);
                        break;
                    }

                // i64
                case OpCode.AddI64: sp--; s[sp - 1] = unchecked(s[sp - 1] + s[sp]); break;
                case OpCode.SubI64: sp--; s[sp - 1] = unchecked(s[sp - 1] - s[sp]); break;
                case OpCode.MulI64: sp--; s[sp - 1] = unchecked(s[sp - 1] * s[sp]); break;
                case OpCode.DivI64:
                    {
                        sp--;
                        long a = s[sp - 1], b = s[sp];
                        if (b == 0)
                        {
                            throw new VmTrapException("Integer division by zero", function.Name, start);
                        }

                        if (a == long.MinValue && b == -1)
                        {
                            throw new VmTrapException("Integer division overflow", function.Name, start);
                        }

                        s[sp - 1] = a / b;
                        break;
                    }

                case OpCode.RemI64:
                    {
                        sp--;
                        long a = s[sp - 1], b = s[sp];
                        if (b == 0)
                        {
                            throw new VmTrapException("Integer division by zero", function.Name, start);
                        }

                        s[sp - 1] = b == -1 ? 0 : a % b;
                        break;
                    }

                case OpCode.NegI64: s[sp - 1] = unchecked(-s[sp - 1]); break;
                case OpCode.AndI64: sp--; s[sp - 1] &= s[sp]; break;
                case OpCode.OrI64: sp--; s[sp - 1] |= s[sp]; break;
                case OpCode.XorI64: sp--; s[sp - 1] ^= s[sp]; break;
                case OpCode.NotI64: s[sp - 1] = ~s[sp - 1]; break;
                case OpCode.ShlI64: sp--; s[sp - 1] <<= I32(s[sp]) & 63; break;
                case OpCode.ShrI64: sp--; s[sp - 1] >>= I32(s[sp]) & 63; break;
                case OpCode.ShrUnI64: sp--; s[sp - 1] = (long)((ulong)s[sp - 1] >> (I32(s[sp]) & 63)); break;

                // f32
                case OpCode.AddF32: sp--; s[sp - 1] = EF32(F32(s[sp - 1]) + F32(s[sp])); break;
                case OpCode.SubF32: sp--; s[sp - 1] = EF32(F32(s[sp - 1]) - F32(s[sp])); break;
                case OpCode.MulF32: sp--; s[sp - 1] = EF32(F32(s[sp - 1]) * F32(s[sp])); break;
                case OpCode.DivF32: sp--; s[sp - 1] = EF32(F32(s[sp - 1]) / F32(s[sp])); break;
                case OpCode.RemF32: sp--; s[sp - 1] = EF32(F32(s[sp - 1]) % F32(s[sp])); break;
                case OpCode.NegF32: s[sp - 1] = EF32(-F32(s[sp - 1])); break;

                // f64
                case OpCode.AddF64: sp--; s[sp - 1] = EF64(F64(s[sp - 1]) + F64(s[sp])); break;
                case OpCode.SubF64: sp--; s[sp - 1] = EF64(F64(s[sp - 1]) - F64(s[sp])); break;
                case OpCode.MulF64: sp--; s[sp - 1] = EF64(F64(s[sp - 1]) * F64(s[sp])); break;
                case OpCode.DivF64: sp--; s[sp - 1] = EF64(F64(s[sp - 1]) / F64(s[sp])); break;
                case OpCode.RemF64: sp--; s[sp - 1] = EF64(F64(s[sp - 1]) % F64(s[sp])); break;
                case OpCode.NegF64: s[sp - 1] = EF64(-F64(s[sp - 1])); break;

                // comparisons
                case OpCode.EqI32: sp--; s[sp - 1] = B(I32(s[sp - 1]) == I32(s[sp])); break;
                case OpCode.LtI32: sp--; s[sp - 1] = B(I32(s[sp - 1]) < I32(s[sp])); break;
                case OpCode.GtI32: sp--; s[sp - 1] = B(I32(s[sp - 1]) > I32(s[sp])); break;
                case OpCode.LtUnI32: sp--; s[sp - 1] = B((uint)I32(s[sp - 1]) < (uint)I32(s[sp])); break;
                case OpCode.GtUnI32: sp--; s[sp - 1] = B((uint)I32(s[sp - 1]) > (uint)I32(s[sp])); break;
                case OpCode.EqI64: sp--; s[sp - 1] = B(s[sp - 1] == s[sp]); break;
                case OpCode.LtI64: sp--; s[sp - 1] = B(s[sp - 1] < s[sp]); break;
                case OpCode.GtI64: sp--; s[sp - 1] = B(s[sp - 1] > s[sp]); break;
                case OpCode.LtUnI64: sp--; s[sp - 1] = B((ulong)s[sp - 1] < (ulong)s[sp]); break;
                case OpCode.GtUnI64: sp--; s[sp - 1] = B((ulong)s[sp - 1] > (ulong)s[sp]); break;
                case OpCode.EqF32: sp--; s[sp - 1] = B(F32(s[sp - 1]) == F32(s[sp])); break;
                case OpCode.LtF32: sp--; s[sp - 1] = B(F32(s[sp - 1]) < F32(s[sp])); break;
                case OpCode.GtF32: sp--; s[sp - 1] = B(F32(s[sp - 1]) > F32(s[sp])); break;
                case OpCode.LtUnF32: sp--; s[sp - 1] = B(!(F32(s[sp - 1]) >= F32(s[sp]))); break;
                case OpCode.GtUnF32: sp--; s[sp - 1] = B(!(F32(s[sp - 1]) <= F32(s[sp]))); break;
                case OpCode.EqF64: sp--; s[sp - 1] = B(F64(s[sp - 1]) == F64(s[sp])); break;
                case OpCode.LtF64: sp--; s[sp - 1] = B(F64(s[sp - 1]) < F64(s[sp])); break;
                case OpCode.GtF64: sp--; s[sp - 1] = B(F64(s[sp - 1]) > F64(s[sp])); break;
                case OpCode.LtUnF64: sp--; s[sp - 1] = B(!(F64(s[sp - 1]) >= F64(s[sp]))); break;
                case OpCode.GtUnF64: sp--; s[sp - 1] = B(!(F64(s[sp - 1]) <= F64(s[sp]))); break;

                // conversions (.NET 9+ float→int conversions saturate and map NaN to 0 on every platform)
                case OpCode.I32ToI64: s[sp - 1] = I32(s[sp - 1]); break;
                case OpCode.U32ToI64: s[sp - 1] = (uint)I32(s[sp - 1]); break;
                case OpCode.I64ToI32: s[sp - 1] = unchecked((int)s[sp - 1]); break;
                case OpCode.I32ToF32: s[sp - 1] = EF32(I32(s[sp - 1])); break;
                case OpCode.I32ToF64: s[sp - 1] = EF64(I32(s[sp - 1])); break;
                case OpCode.I64ToF32: s[sp - 1] = EF32(s[sp - 1]); break;
                case OpCode.I64ToF64: s[sp - 1] = EF64(s[sp - 1]); break;
                case OpCode.F32ToI32: s[sp - 1] = (int)F32(s[sp - 1]); break;
                case OpCode.F32ToI64: s[sp - 1] = (long)F32(s[sp - 1]); break;
                case OpCode.F64ToI32: s[sp - 1] = (int)F64(s[sp - 1]); break;
                case OpCode.F64ToI64: s[sp - 1] = (long)F64(s[sp - 1]); break;
                case OpCode.F32ToF64: s[sp - 1] = EF64(F32(s[sp - 1])); break;
                case OpCode.F64ToF32: s[sp - 1] = EF32((float)F64(s[sp - 1])); break;
                case OpCode.I32ToI8: s[sp - 1] = unchecked((sbyte)I32(s[sp - 1])); break;
                case OpCode.I32ToU8: s[sp - 1] = unchecked((byte)I32(s[sp - 1])); break;
                case OpCode.I32ToI16: s[sp - 1] = unchecked((short)I32(s[sp - 1])); break;
                case OpCode.I32ToU16: s[sp - 1] = unchecked((ushort)I32(s[sp - 1])); break;
                case OpCode.U64ToF64: s[sp - 1] = EF64((ulong)s[sp - 1]); break;
                case OpCode.F32ToU64: s[sp - 1] = unchecked((long)(ulong)F32(s[sp - 1])); break;
                case OpCode.F64ToU64: s[sp - 1] = unchecked((long)(ulong)F64(s[sp - 1])); break;
                case OpCode.F32ToU32: s[sp - 1] = unchecked((int)(uint)F32(s[sp - 1])); break;
                case OpCode.F64ToU32: s[sp - 1] = unchecked((int)(uint)F64(s[sp - 1])); break;

                // control flow
                case OpCode.Branch:
                    pc = BinaryPrimitives.ReadInt32LittleEndian(code[pc..]);
                    break;
                case OpCode.BranchIfTrue:
                    pc = I32(s[--sp]) != 0 ? BinaryPrimitives.ReadInt32LittleEndian(code[pc..]) : pc + 4;
                    break;
                case OpCode.BranchIfFalse:
                    pc = I32(s[--sp]) == 0 ? BinaryPrimitives.ReadInt32LittleEndian(code[pc..]) : pc + 4;
                    break;

                case OpCode.Call:
                case OpCode.CallVirtual:
                case OpCode.New:
                    {
                        _framePc[frame] = start;
                        int callee;
                        int calleeBase;
                        long constructed = 0;
                        if (op == OpCode.Call)
                        {
                            callee = ReadU16(code, ref pc);
                            calleeBase = sp - _functions[callee].Signature.Parameters.Count;
                        }
                        else if (op == OpCode.CallVirtual)
                        {
                            int selector = ReadU16(code, ref pc);
                            calleeBase = sp - Module.Selectors[selector].Signature.Parameters.Count;
                            InstanceObject receiver = Instance(s[calleeBase], function, start);
                            if (!Module.Classes[receiver.Class].Methods.TryGetValue(selector, out callee))
                            {
                                throw new VmTrapException(
                                    $"Class '{Module.Classes[receiver.Class].Name}' has no implementation of '{Module.Selectors[selector].Name}'",
                                    function.Name,
                                    start);
                            }
                        }
                        else
                        {
                            int classIndex = ReadU16(code, ref pc);
                            callee = ReadU16(code, ref pc);
                            int argCount = _functions[callee].Signature.Parameters.Count - 1;
                            constructed = Allocate(new InstanceObject(classIndex, Module.Classes[classIndex].Fields.Count), function, start);

                            // Insert the new object below the constructor arguments as 'this'.
                            calleeBase = sp - argCount;
                            System.Array.Copy(s, calleeBase, s, calleeBase + 1, argCount);
                            s[calleeBase] = constructed;
                        }

                        _frameResumePc[frame] = pc;
                        sp = PushFrame(callee, calleeBase, constructed);
                        frame = _depth;
                        functionIndex = callee;
                        frameBase = calleeBase;
                        function = _functions[callee];
                        code = function.Code;
                        pc = 0;
                        break;
                    }

                case OpCode.ConstNull:
                    s[sp++] = 0;
                    break;

                case OpCode.EqRef:
                    sp--;
                    s[sp - 1] = B(s[sp - 1] == s[sp]);
                    break;

                case OpCode.LoadField:
                    {
                        int field = ReadU16(code, ref pc);
                        VmField f = Module.Fields[field];

                        // A null value-type instance is the default value: every field reads as zero.
                        s[sp - 1] = s[sp - 1] == 0 && Module.Classes[f.Class].IsValueType
                            ? 0
                            : FieldOwner(s[sp - 1], field, function, start).Fields[f.Slot];
                        break;
                    }

                case OpCode.Swap:
                    (s[sp - 1], s[sp - 2]) = (s[sp - 2], s[sp - 1]);
                    break;

                case OpCode.Clone:
                    _framePc[frame] = start;
                    s[sp - 1] = CloneValue(s[sp - 1], function, start);
                    break;

                case OpCode.CopyInto:
                    _framePc[frame] = start;
                    sp -= 2;
                    CopyValue(Instance(s[sp], function, start), s[sp + 1], function, start);
                    break;

                case OpCode.MaterializeLocal:
                    {
                        _framePc[frame] = start;
                        int local = ReadU16(code, ref pc);
                        int valueClass = ReadU16(code, ref pc);
                        if (s[frameBase + local] == 0)
                        {
                            s[frameBase + local] = NewDefault(valueClass, function, start);
                        }

                        s[sp++] = s[frameBase + local];
                        break;
                    }

                case OpCode.MaterializeField:
                    {
                        _framePc[frame] = start;
                        int field = ReadU16(code, ref pc);
                        int valueClass = ReadU16(code, ref pc);
                        InstanceObject owner = FieldOwner(s[sp - 1], field, function, start);
                        int slot = Module.Fields[field].Slot;
                        if (owner.Fields[slot] == 0)
                        {
                            long created = NewDefault(valueClass, function, start);
                            owner.Fields[slot] = created;
                        }

                        s[sp - 1] = owner.Fields[slot];
                        break;
                    }

                case OpCode.MaterializeGlobal:
                    {
                        _framePc[frame] = start;
                        int global = ReadU16(code, ref pc);
                        int valueClass = ReadU16(code, ref pc);
                        if (_globals[global] == 0)
                        {
                            _globals[global] = NewDefault(valueClass, function, start);
                        }

                        s[sp++] = _globals[global];
                        break;
                    }

                case OpCode.MaterializeElement:
                    {
                        _framePc[frame] = start;
                        int valueClass = ReadU16(code, ref pc);
                        sp--;
                        ArrayObject structArray = ArrayOf(s[sp - 1], VmElementAccess.Ref, function, start);
                        int index = I32(s[sp]);
                        CheckIndex(structArray, index, function, start);
                        long[] elements = (long[])structArray.Data;
                        if (elements[index] == 0)
                        {
                            long created = NewDefault(valueClass, function, start);
                            elements[index] = created;
                        }

                        s[sp - 1] = elements[index];
                        break;
                    }

                case OpCode.NewCell:
                    {
                        _framePc[frame] = start;
                        var access = (VmElementAccess)ReadU16(code, ref pc);
                        s[sp++] = Allocate(new PointerObject { Kind = PointerKind.Cell, Access = access }, function, start);
                        break;
                    }

                case OpCode.FieldPointer:
                    {
                        _framePc[frame] = start;
                        int field = ReadU16(code, ref pc);
                        var access = (VmElementAccess)ReadU16(code, ref pc);
                        long target = s[sp - 1];
                        FieldOwner(target, field, function, start);
                        s[sp - 1] = Allocate(new PointerObject { Kind = PointerKind.Field, Target = target, Index = Module.Fields[field].Slot, Access = access }, function, start);
                        break;
                    }

                case OpCode.ElementPointer:
                    {
                        _framePc[frame] = start;
                        var access = (VmElementAccess)ReadU16(code, ref pc);
                        sp--;
                        long target = s[sp - 1];
                        int index = I32(s[sp]);
                        CheckIndex(ArrayOf(target, access, function, start), index, function, start);
                        s[sp - 1] = Allocate(new PointerObject { Kind = PointerKind.Element, Target = target, Index = index }, function, start);
                        break;
                    }

                case OpCode.GlobalPointer:
                    {
                        _framePc[frame] = start;
                        int global = ReadU16(code, ref pc);
                        var access = (VmElementAccess)ReadU16(code, ref pc);
                        s[sp] = Allocate(new PointerObject { Kind = PointerKind.Global, Index = global, Access = access }, function, start);
                        sp++;
                        break;
                    }

                case OpCode.LoadIndirect:
                    {
                        var access = (VmElementAccess)ReadU16(code, ref pc);
                        s[sp - 1] = LoadIndirect(Pointer(s[sp - 1], function, start), access, function, start);
                        break;
                    }

                case OpCode.StoreIndirect:
                    {
                        var access = (VmElementAccess)ReadU16(code, ref pc);
                        sp -= 2;
                        StoreIndirect(Pointer(s[sp], function, start), s[sp + 1], access, function, start);
                        break;
                    }

                case OpCode.StoreField:
                    {
                        int field = ReadU16(code, ref pc);
                        sp -= 2;
                        FieldOwner(s[sp], field, function, start).Fields[Module.Fields[field].Slot] = s[sp + 1];
                        break;
                    }

                case OpCode.IsInstance:
                    {
                        int target = ReadU16(code, ref pc);
                        if (s[sp - 1] != 0 && !IsInstanceOf(s[sp - 1], target))
                        {
                            s[sp - 1] = 0;
                        }

                        break;
                    }

                case OpCode.CastClass:
                    {
                        int target = ReadU16(code, ref pc);
                        if (s[sp - 1] != 0 && !IsInstanceOf(s[sp - 1], target))
                        {
                            throw new VmTrapException($"Invalid cast to '{Module.Classes[target].Name}'", function.Name, start);
                        }

                        break;
                    }

                case OpCode.NewArray:
                    {
                        _framePc[frame] = start;
                        var kind = (VmArrayKind)ReadU16(code, ref pc);
                        int length = I32(s[sp - 1]);
                        if (length < 0)
                        {
                            throw new VmTrapException($"Negative array length {length}", function.Name, start);
                        }

                        s[sp - 1] = Allocate(new ArrayObject(kind, length), function, start);
                        break;
                    }

                case OpCode.LoadElement:
                    {
                        var access = (VmElementAccess)ReadU16(code, ref pc);
                        sp--;
                        s[sp - 1] = LoadElement(ArrayOf(s[sp - 1], access, function, start), I32(s[sp]), access, function, start);
                        break;
                    }

                case OpCode.StoreElement:
                    {
                        var access = (VmElementAccess)ReadU16(code, ref pc);
                        sp -= 3;
                        StoreElement(ArrayOf(s[sp], access, function, start), I32(s[sp + 1]), s[sp + 2], access, function, start);
                        break;
                    }

                case OpCode.ArrayLength:
                    s[sp - 1] = Deref(s[sp - 1], function, start) is ArrayObject array
                        ? array.Length
                        : throw new VmTrapException("Not an array", function.Name, start);
                    break;

                case OpCode.LoadString:
                    s[sp++] = _stringHandles[ReadU16(code, ref pc)];
                    break;

                case OpCode.StringLength:
                    s[sp - 1] = StringOf(s[sp - 1], function, start).Length;
                    break;

                case OpCode.StringCharAt:
                    {
                        sp--;
                        string value = StringOf(s[sp - 1], function, start);
                        int index = I32(s[sp]);
                        if ((uint)index >= (uint)value.Length)
                        {
                            throw new VmTrapException($"String index {index} out of range (length {value.Length})", function.Name, start);
                        }

                        s[sp - 1] = value[index];
                        break;
                    }

                case OpCode.StringConcat:
                    {
                        _framePc[frame] = start;
                        string left = s[sp - 2] == 0 ? string.Empty : StringOf(s[sp - 2], function, start);
                        string right = s[sp - 1] == 0 ? string.Empty : StringOf(s[sp - 1], function, start);
                        long handle = Allocate(new StringObject(left + right), function, start);
                        sp--;
                        s[sp - 1] = handle;
                        break;
                    }

                case OpCode.Hash:
                    {
                        var type = (VmType)ReadU16(code, ref pc);
                        long v = s[sp - 1];
                        s[sp - 1] = type switch
                        {
                            VmType.I32 or VmType.F32 => I32(v),
                            VmType.I64 or VmType.F64 => unchecked((int)v ^ (int)(v >> 32)),
                            _ => ReferenceHash(v),
                        };
                        break;
                    }

                case OpCode.ValueEquals:
                    {
                        sp--;
                        long a = s[sp - 1], b = s[sp];
                        s[sp - 1] = B(ReferenceValueEquals(a, b));
                        break;
                    }

                case OpCode.ToText:
                    {
                        _framePc[frame] = start;
                        var kind = (VmFormatKind)ReadU16(code, ref pc);
                        long v = s[sp - 1];
                        var invariant = System.Globalization.CultureInfo.InvariantCulture;
                        string text = kind switch
                        {
                            VmFormatKind.Int32 => I32(v).ToString(invariant),
                            VmFormatKind.UInt32 => ((uint)I32(v)).ToString(invariant),
                            VmFormatKind.Int64 => v.ToString(invariant),
                            VmFormatKind.UInt64 => ((ulong)v).ToString(invariant),
                            VmFormatKind.Single => F32(v).ToString(invariant),
                            VmFormatKind.Double => F64(v).ToString(invariant),
                            VmFormatKind.Char => ((char)I32(v)).ToString(),
                            _ => I32(v) != 0 ? "True" : "False",
                        };
                        s[sp - 1] = Allocate(new StringObject(text), function, start);
                        break;
                    }

                case OpCode.Trap:
                    {
                        long message = s[--sp];
                        throw new VmTrapException(message != 0 && _heap.Get(message) is StringObject m ? m.Value : "Trap", function.Name, start);
                    }

                case OpCode.StringConcatArray:
                    {
                        _framePc[frame] = start;
                        if (Deref(s[sp - 1], function, start) is not ArrayObject { Kind: VmArrayKind.Ref } parts)
                        {
                            throw new VmTrapException("Not an array of strings", function.Name, start);
                        }

                        var builder = new System.Text.StringBuilder();
                        foreach (long part in (long[])parts.Data)
                        {
                            if (part != 0)
                            {
                                builder.Append(StringOf(part, function, start));
                            }
                        }

                        s[sp - 1] = Allocate(new StringObject(builder.ToString()), function, start);
                        break;
                    }

                case OpCode.StringEquals:
                    {
                        sp--;
                        long a = s[sp - 1], b = s[sp];
                        s[sp - 1] = B(a == b || (a != 0 && b != 0 && StringOf(a, function, start) == StringOf(b, function, start)));
                        break;
                    }

                case OpCode.Syscall:
                    {
                        _framePc[frame] = start;
                        int index = ReadU16(code, ref pc);
                        VmImport import = _imports[index];
                        int argCount = import.Signature.Parameters.Count;
                        int argBase = sp - argCount;
                        long result;
                        try
                        {
                            _syscallFunction = function;
                            _syscallOffset = start;
                            result = _syscalls[index](this, new ReadOnlySpan<long>(s, argBase, argCount));
                        }
                        catch (Exception e) when (e is not VmTrapException)
                        {
                            throw new VmTrapException($"Syscall '{import.Name}' failed: {e.Message}", function.Name, start, e);
                        }

                        sp = argBase;
                        if (import.Signature.Result is not null)
                        {
                            s[sp++] = result;
                        }

                        break;
                    }

                case OpCode.Return:
                    {
                        bool hasResult = function.Signature.Result is not null;
                        if (hasResult)
                        {
                            s[frameBase] = s[sp - 1];
                        }

                        long constructed = _frameConstructed[frame];
                        int returnedBase = frameBase;
                        _depth--;
                        if (_depth == entryDepth)
                        {
                            return;
                        }

                        // Resume the caller.
                        frame = _depth;
                        functionIndex = _frameFunction[frame];
                        frameBase = _frameBase[frame];
                        function = _functions[functionIndex];
                        code = function.Code;
                        pc = _frameResumePc[frame];
                        if (constructed != 0)
                        {
                            s[returnedBase] = constructed; // 'new' leaves the object on the caller's stack
                            sp = returnedBase + 1;
                        }
                        else
                        {
                            sp = returnedBase + (hasResult ? 1 : 0);
                        }

                        break;
                    }

                default:
                    // Unreachable for verified code.
                    throw new VmTrapException($"Invalid opcode 0x{(byte)op:X2}", function.Name, start);
            }
        }
    }

    // ---- heap ---------------------------------------------------------------------------

    // ---- syscall context ------------------------------------------------------------------

    /// <inheritdoc />
    string? ISyscallContext.ReadString(long handle)
    {
        if (handle == 0)
        {
            return null;
        }

        return _heap.Get(handle) is StringObject s ? s.Value : throw new ArgumentException("Argument is not a string.");
    }

    /// <inheritdoc />
    int[]? ISyscallContext.ReadInt32Array(long handle)
    {
        if (handle == 0)
        {
            return null;
        }

        return _heap.Get(handle) is ArrayObject { Kind: VmArrayKind.Bits32 } array
            ? ((int[])array.Data).ToArray()
            : throw new ArgumentException("Argument is not an int[].");
    }

    /// <inheritdoc />
    byte[]? ISyscallContext.ReadByteArray(long handle)
    {
        if (handle == 0)
        {
            return null;
        }

        return _heap.Get(handle) is ArrayObject { Kind: VmArrayKind.Bits8 } array
            ? ((byte[])array.Data).ToArray()
            : throw new ArgumentException("Argument is not a byte[].");
    }

    /// <inheritdoc />
    long ISyscallContext.NewByteArray(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var array = new ArrayObject(VmArrayKind.Bits8, value.Length);
        value.CopyTo((byte[])array.Data, 0);
        return Allocate(array, _syscallFunction ?? _functions[0], _syscallOffset);
    }

    /// <inheritdoc />
    long ISyscallContext.NewString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // A syscall is a safepoint: its arguments are rooted by the stack map, so collecting here is safe.
        return Allocate(new StringObject(value), _syscallFunction ?? _functions[0], _syscallOffset);
    }

    // ---- value types and pointers ---------------------------------------------------------

    private long NewDefault(int valueClass, VmFunction function, int offset) =>
        Allocate(new InstanceObject(valueClass, Module.Classes[valueClass].Fields.Count), function, offset);

    /// <summary>Deep copy of a value-type instance: nested value-type fields are copied too. Null stays null (default).</summary>
    private long CloneValue(long handle, VmFunction function, int offset)
    {
        if (handle == 0)
        {
            return 0;
        }

        InstanceObject source = Instance(handle, function, offset);
        long copyHandle = NewDefault(source.Class, function, offset);

        // The copy is reachable only from here while nested values are cloned (which may collect).
        _temporaryRoots.Add(copyHandle);
        try
        {
            CopyValue((InstanceObject)_heap.Get(copyHandle), handle, function, offset);
        }
        finally
        {
            _temporaryRoots.RemoveAt(_temporaryRoots.Count - 1);
        }

        return copyHandle;
    }

    /// <summary>Copies the fields of value <paramref name="sourceHandle"/> (null = default) into <paramref name="destination"/>.</summary>
    private void CopyValue(InstanceObject destination, long sourceHandle, VmFunction function, int offset)
    {
        if (sourceHandle != 0 && ReferenceEquals(_heap.Get(sourceHandle), destination))
        {
            return;
        }

        VmClass cls = Module.Classes[destination.Class];
        InstanceObject? source = sourceHandle == 0 ? null : Instance(sourceHandle, function, offset);
        if (source is not null && source.Class != destination.Class)
        {
            throw new VmTrapException($"Cannot copy '{Module.Classes[source.Class].Name}' into '{cls.Name}'", function.Name, offset);
        }

        for (int i = 0; i < destination.Fields.Length; i++)
        {
            long value = source?.Fields[i] ?? 0;
            if (cls.ValueFields.ContainsKey(i))
            {
                long nested = destination.Fields[i];
                if (nested != 0)
                {
                    CopyValue((InstanceObject)_heap.Get(nested), value, function, offset);
                    continue;
                }

                value = CloneValue(value, function, offset);
            }

            destination.Fields[i] = value;
        }
    }

    private int ReferenceHash(long handle)
    {
        if (handle == 0)
        {
            return 0;
        }

        switch (_heap.Get(handle))
        {
            case StringObject str:
                return StableStringHash(str.Value);
            case InstanceObject obj when Module.Classes[obj.Class].IsValueType:
                {
                    VmClass cls = Module.Classes[obj.Class];
                    int hash = obj.Class;
                    for (int i = 0; i < obj.Fields.Length; i++)
                    {
                        int part = cls.Fields[i] == VmType.Ref ? ReferenceHash(obj.Fields[i]) : unchecked((int)obj.Fields[i] ^ (int)(obj.Fields[i] >> 32));
                        hash = unchecked((hash * 31) + part);
                    }

                    return hash;
                }

            default:
                return unchecked((int)handle * -1640531535);
        }
    }

    /// <summary>Identity, except strings compare by content and value types field by field (null = default).</summary>
    private bool ReferenceValueEquals(long a, long b)
    {
        if (a == b)
        {
            return true;
        }

        HeapObject? x = a == 0 ? null : _heap.Get(a);
        HeapObject? y = b == 0 ? null : _heap.Get(b);
        if (x is StringObject sx && y is StringObject sy)
        {
            return sx.Value == sy.Value;
        }

        InstanceObject? ix = x as InstanceObject, iy = y as InstanceObject;
        if ((ix is null && iy is null) || (ix is not null && !Module.Classes[ix.Class].IsValueType)
            || (iy is not null && !Module.Classes[iy.Class].IsValueType) || (ix is not null && iy is not null && ix.Class != iy.Class))
        {
            return false;
        }

        VmClass cls = Module.Classes[(ix ?? iy)!.Class];
        for (int i = 0; i < cls.Fields.Count; i++)
        {
            long fx = ix?.Fields[i] ?? 0, fy = iy?.Fields[i] ?? 0;
            bool equal = cls.Fields[i] == VmType.Ref ? ReferenceValueEquals(fx, fy) : fx == fy;
            if (!equal)
            {
                return false;
            }
        }

        return true;
    }

    private PointerObject Pointer(long handle, VmFunction function, int offset) =>
        Deref(handle, function, offset) as PointerObject ?? throw new VmTrapException("Not a pointer", function.Name, offset);

    private long LoadIndirect(PointerObject pointer, VmElementAccess access, VmFunction function, int offset) => pointer.Kind switch
    {
        PointerKind.Cell => pointer.Value,
        PointerKind.Field => ((InstanceObject)_heap.Get(pointer.Target)).Fields[pointer.Index],
        PointerKind.Global => _globals[pointer.Index],
        _ => LoadElement(ArrayOf(pointer.Target, access, function, offset), pointer.Index, access, function, offset),
    };

    private void StoreIndirect(PointerObject pointer, long value, VmElementAccess access, VmFunction function, int offset)
    {
        // Slots hold sub-32-bit integers already narrowed, as direct stores do; the narrowing follows the
        // location's declared type (stind.i1 is used for both byte and sbyte).
        long narrowed = pointer.Access switch
        {
            VmElementAccess.I8 => unchecked((sbyte)value),
            VmElementAccess.U8 => unchecked((byte)value),
            VmElementAccess.I16 => unchecked((short)value),
            VmElementAccess.U16 => unchecked((ushort)value),
            _ => value,
        };

        switch (pointer.Kind)
        {
            case PointerKind.Cell: pointer.Value = narrowed; break;
            case PointerKind.Field: ((InstanceObject)_heap.Get(pointer.Target)).Fields[pointer.Index] = narrowed; break;
            case PointerKind.Global: _globals[pointer.Index] = narrowed; break;
            default: StoreElement(ArrayOf(pointer.Target, access, function, offset), pointer.Index, value, access, function, offset); break;
        }
    }

    // FNV-1a over UTF-16 code units: deterministic across runs and hosts, unlike string.GetHashCode.
    private static int StableStringHash(string value)
    {
        uint hash = 2166136261;
        foreach (char c in value)
        {
            hash = unchecked((hash ^ c) * 16777619);
        }

        return unchecked((int)hash);
    }

    private long Allocate(HeapObject obj, VmFunction function, int offset)
    {
        if (_heap.ShouldCollectBefore(obj.Size))
        {
            Collect();
            if (!_heap.Fits(obj.Size))
            {
                throw new VmTrapException($"Out of memory (heap limit {VmHeap.LimitBytes / (1024 * 1024)} MB)", function.Name, offset);
            }
        }

        return _heap.Add(obj);
    }

    /// <summary>
    /// Marks every root precisely: string literals, reference globals, and in each active frame
    /// the reference-typed locals plus the reference entries of the verifier's stack map.
    /// </summary>
    private void Collect()
    {
        foreach (long handle in _stringHandles)
        {
            _heap.MarkRoot(handle);
        }

        foreach (long handle in _temporaryRoots)
        {
            _heap.MarkRoot(handle);
        }

        for (int g = 0; g < _globals.Length; g++)
        {
            if (Module.Globals[g] == VmType.Ref)
            {
                _heap.MarkRoot(_globals[g]);
            }
        }

        for (int frame = 1; frame <= _depth; frame++)
        {
            VmFunction function = _functions[_frameFunction[frame]];
            int frameBase = _frameBase[frame];
            for (int i = 0; i < function.LocalCount; i++)
            {
                if (function.LocalType(i) == VmType.Ref)
                {
                    _heap.MarkRoot(_stack[frameBase + i]);
                }
            }

            int pc = _framePc[frame];
            VmType[] map = _verified.StackMaps[_frameFunction[frame]][pc];

            // In a calling frame, the top operands became the callee's parameters and are
            // scanned as the callee's locals.
            int count = map.Length - (frame < _depth ? CalleeOwnedOperands(function, pc) : 0);
            int operandBase = frameBase + function.LocalCount;
            for (int i = 0; i < count; i++)
            {
                if (map[i] == VmType.Ref)
                {
                    _heap.MarkRoot(_stack[operandBase + i]);
                }
            }
        }

        _heap.TraceAndSweep();
    }

    private int CalleeOwnedOperands(VmFunction function, int pc)
    {
        var op = (OpCode)function.Code[pc];
        int operand = BinaryPrimitives.ReadUInt16LittleEndian(function.Code.AsSpan(pc + 1));
        return op switch
        {
            OpCode.Call => _functions[operand].Signature.Parameters.Count,
            OpCode.CallVirtual => Module.Selectors[operand].Signature.Parameters.Count,
            OpCode.New => _functions[BinaryPrimitives.ReadUInt16LittleEndian(function.Code.AsSpan(pc + 3))].Signature.Parameters.Count - 1,
            _ => 0,
        };
    }

    private HeapObject Deref(long handle, VmFunction function, int offset) =>
        handle != 0 ? _heap.Get(handle) : throw new VmTrapException("Null reference", function.Name, offset);

    private InstanceObject Instance(long handle, VmFunction function, int offset) =>
        Deref(handle, function, offset) as InstanceObject ?? throw new VmTrapException("Not an object instance", function.Name, offset);

    private InstanceObject FieldOwner(long handle, int field, VmFunction function, int offset)
    {
        InstanceObject obj = Instance(handle, function, offset);
        int owner = Module.Fields[field].Class;
        return _assignableTo[obj.Class].Contains(owner)
            ? obj
            : throw new VmTrapException($"Field of '{Module.Classes[owner].Name}' accessed on '{Module.Classes[obj.Class].Name}'", function.Name, offset);
    }

    private string StringOf(long handle, VmFunction function, int offset) =>
        Deref(handle, function, offset) is StringObject s ? s.Value : throw new VmTrapException("Not a string", function.Name, offset);

    private ArrayObject ArrayOf(long handle, VmElementAccess access, VmFunction function, int offset)
    {
        if (Deref(handle, function, offset) is not ArrayObject array)
        {
            throw new VmTrapException("Not an array", function.Name, offset);
        }

        return array.Kind == VmTypes.StorageOf(access)
            ? array
            : throw new VmTrapException($"Array element access {access} does not match array storage {array.Kind}", function.Name, offset);
    }

    private static long LoadElement(ArrayObject array, int index, VmElementAccess access, VmFunction function, int offset)
    {
        CheckIndex(array, index, function, offset);
        return access switch
        {
            VmElementAccess.I8 => (sbyte)((byte[])array.Data)[index],
            VmElementAccess.U8 => ((byte[])array.Data)[index],
            VmElementAccess.I16 => (short)((ushort[])array.Data)[index],
            VmElementAccess.U16 => ((ushort[])array.Data)[index],
            VmElementAccess.I32 => ((int[])array.Data)[index],
            VmElementAccess.F32 => EF32(((float[])array.Data)[index]),
            VmElementAccess.F64 => EF64(((double[])array.Data)[index]),
            _ => ((long[])array.Data)[index],
        };
    }

    private static void StoreElement(ArrayObject array, int index, long value, VmElementAccess access, VmFunction function, int offset)
    {
        CheckIndex(array, index, function, offset);
        switch (access)
        {
            case VmElementAccess.I8 or VmElementAccess.U8: ((byte[])array.Data)[index] = unchecked((byte)value); break;
            case VmElementAccess.I16 or VmElementAccess.U16: ((ushort[])array.Data)[index] = unchecked((ushort)value); break;
            case VmElementAccess.I32: ((int[])array.Data)[index] = unchecked((int)value); break;
            case VmElementAccess.F32: ((float[])array.Data)[index] = F32(value); break;
            case VmElementAccess.F64: ((double[])array.Data)[index] = F64(value); break;
            default: ((long[])array.Data)[index] = value; break;
        }
    }

    private static void CheckIndex(ArrayObject array, int index, VmFunction function, int offset)
    {
        if ((uint)index >= (uint)array.Length)
        {
            throw new VmTrapException($"Array index {index} out of range (length {array.Length})", function.Name, offset);
        }
    }

    private bool IsInstanceOf(long handle, int classIndex) =>
        _heap.Get(handle) is InstanceObject obj && _assignableTo[obj.Class].Contains(classIndex);

    private static HashSet<int>[] BuildAssignability(VmModule module)
    {
        var result = new HashSet<int>[module.Classes.Count];
        for (int i = 0; i < module.Classes.Count; i++)
        {
            var set = new HashSet<int> { i };
            VmClass c = module.Classes[i];
            if (c.BaseClass != VmClass.NoBase)
            {
                set.UnionWith(result[c.BaseClass]); // bases precede derived classes
            }

            set.UnionWith(c.Interfaces);
            result[i] = set;
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadU16(ReadOnlySpan<byte> code, ref int pc)
    {
        int value = BinaryPrimitives.ReadUInt16LittleEndian(code[pc..]);
        pc += 2;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int I32(long slot) => (int)slot;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float F32(long slot) => BitConverter.Int32BitsToSingle((int)slot);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long EF32(float value) => BitConverter.SingleToInt32Bits(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double F64(long slot) => BitConverter.Int64BitsToDouble(slot);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long EF64(double value) => BitConverter.DoubleToInt64Bits(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long B(bool value) => value ? 1 : 0;
}
