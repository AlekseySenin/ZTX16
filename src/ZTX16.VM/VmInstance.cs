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
public sealed class VmInstance
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
    }

    /// <summary>The module being executed.</summary>
    public VmModule Module => _verified.Module;

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

    private void Execute(int functionIndex, int frameBase)
    {
        VmFunction function = _functions[functionIndex];
        int localCount = function.LocalCount;
        int sp = frameBase + localCount;

        if (++_depth > MaxCallDepth)
        {
            throw new VmTrapException("Call stack overflow", function.Name, 0);
        }

        if (sp + _verified.MaxStack[functionIndex] > _stack.Length)
        {
            throw new VmTrapException("Value stack overflow", function.Name, 0);
        }

        // Parameters were placed by the caller; extra locals start zeroed.
        Array.Clear(_stack, frameBase + function.Signature.Parameters.Count, function.Locals.Count);

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
                    {
                        int callee = ReadU16(code, ref pc);
                        VmSignature signature = _functions[callee].Signature;
                        int calleeBase = sp - signature.Parameters.Count;
                        Execute(callee, calleeBase);
                        sp = calleeBase + (signature.Result is null ? 0 : 1);
                        break;
                    }

                case OpCode.Syscall:
                    {
                        int index = ReadU16(code, ref pc);
                        VmImport import = _imports[index];
                        int argCount = import.Signature.Parameters.Count;
                        int argBase = sp - argCount;
                        long result;
                        try
                        {
                            result = _syscalls[index](new ReadOnlySpan<long>(s, argBase, argCount));
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
                    if (function.Signature.Result is not null)
                    {
                        s[frameBase] = s[sp - 1];
                    }

                    _depth--;
                    return;

                default:
                    // Unreachable for verified code.
                    throw new VmTrapException($"Invalid opcode 0x{(byte)op:X2}", function.Name, start);
            }
        }
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
