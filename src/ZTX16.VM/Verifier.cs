using System.Buffers.Binary;
using System.Collections.Immutable;

namespace ZTX16.VM;

/// <summary>Thrown when a module fails load-time verification.</summary>
public sealed class VerificationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public VerificationException(string message)
        : base(message)
    {
    }
}

/// <summary>A module that passed verification, with per-function facts the interpreter relies on.</summary>
public sealed class VerifiedModule
{
    internal VerifiedModule(VmModule module, int[] maxStack)
    {
        Module = module;
        MaxStack = maxStack;
    }

    /// <summary>The verified module.</summary>
    public VmModule Module { get; }

    /// <summary>Maximum operand stack depth of each function.</summary>
    internal int[] MaxStack { get; }
}

/// <summary>
/// Load-time verifier. Guarantees that a verified function can execute without runtime
/// type checks: every instruction is well-formed, every branch lands on an instruction,
/// operand types match at every instruction and merge point, locals/globals/calls/syscalls
/// are in range and used with their declared types, and no path falls off the end.
/// </summary>
public static class Verifier
{
    /// <summary>Maximum operand stack depth allowed in one function.</summary>
    public const int MaxStackDepth = 1024;

    /// <summary>Verifies <paramref name="module"/>.</summary>
    /// <exception cref="VerificationException">The module is invalid.</exception>
    public static VerifiedModule Verify(VmModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        foreach (VmType type in module.Globals)
        {
            RequireType(type, "global");
        }

        foreach (VmImport import in module.Imports)
        {
            if (string.IsNullOrEmpty(import.Name))
            {
                throw new VerificationException("Syscall import with an empty name.");
            }

            RequireSignature(import.Signature, $"syscall '{import.Name}'");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (VmFunction function in module.Functions)
        {
            if (!names.Add(function.Name))
            {
                throw new VerificationException($"Duplicate function name '{function.Name}'.");
            }

            RequireSignature(function.Signature, $"function '{function.Name}'");
            foreach (VmType type in function.Locals)
            {
                RequireType(type, $"local of '{function.Name}'");
            }

            if (function.LocalCount > ushort.MaxValue + 1)
            {
                throw new VerificationException($"Function '{function.Name}' has too many locals.");
            }
        }

        foreach ((string name, int index) in module.Exports)
        {
            if ((uint)index >= (uint)module.Functions.Count)
            {
                throw new VerificationException($"Export '{name}' refers to missing function {index}.");
            }
        }

        int[] maxStack = new int[module.Functions.Count];
        for (int i = 0; i < module.Functions.Count; i++)
        {
            maxStack[i] = new FunctionVerifier(module, module.Functions[i]).Run();
        }

        return new VerifiedModule(module, maxStack);
    }

    private static void RequireType(VmType type, string what)
    {
        if (!VmTypes.IsDefined(type))
        {
            throw new VerificationException($"Invalid type {(byte)type} in {what}.");
        }
    }

    private static void RequireSignature(VmSignature signature, string what)
    {
        foreach (VmType type in signature.Parameters)
        {
            RequireType(type, what);
        }

        if (signature.Result is { } result)
        {
            RequireType(result, what);
        }
    }

    private sealed class FunctionVerifier
    {
        private readonly VmModule _module;
        private readonly VmFunction _function;
        private readonly Dictionary<int, int> _instructionAt = [];
        private readonly List<(int Offset, OpCode Op, long Operand)> _instructions = [];

        public FunctionVerifier(VmModule module, VmFunction function)
        {
            _module = module;
            _function = function;
        }

        public int Run()
        {
            Decode();
            if (_instructions.Count == 0)
            {
                throw Fail(0, "function has no code");
            }

            var states = new ImmutableStack<VmType>?[_instructions.Count];
            var depths = new int[_instructions.Count];
            var work = new Stack<int>();
            states[0] = ImmutableStack<VmType>.Empty;
            work.Push(0);
            int maxDepth = 0;

            while (work.TryPop(out int index))
            {
                var stack = new TypeStack(states[index]!, depths[index], this, _instructions[index].Offset);
                (int offset, OpCode op, long operand) = _instructions[index];

                bool fallsThrough = Step(op, operand, ref stack, offset, out int? branchTarget);
                maxDepth = Math.Max(maxDepth, stack.MaxDepth);

                if (branchTarget is int target)
                {
                    if (!_instructionAt.TryGetValue(target, out int targetIndex))
                    {
                        throw Fail(offset, $"branch target {target} is not an instruction boundary");
                    }

                    Merge(targetIndex, stack, states, depths, work, offset);
                }

                if (fallsThrough)
                {
                    if (index + 1 >= _instructions.Count)
                    {
                        throw Fail(offset, "execution can fall off the end of the function");
                    }

                    Merge(index + 1, stack, states, depths, work, offset);
                }
            }

            return maxDepth;
        }

        public VerificationException Fail(int offset, string message) =>
            new($"Function '{_function.Name}' at offset {offset}: {message}.");

        private void Merge(int target, TypeStack stack, ImmutableStack<VmType>?[] states, int[] depths, Stack<int> work, int from)
        {
            if (states[target] is { } existing)
            {
                if (depths[target] != stack.Depth || !existing.SequenceEqual(stack.Items))
                {
                    throw Fail(from, $"stack mismatch at merge point {_instructions[target].Offset}");
                }

                return;
            }

            states[target] = stack.Items;
            depths[target] = stack.Depth;
            work.Push(target);
        }

        private void Decode()
        {
            ReadOnlySpan<byte> code = _function.Code;
            int pc = 0;
            while (pc < code.Length)
            {
                int start = pc;
                var op = (OpCode)code[pc++];
                if (!OpCodeInfo.IsDefined(op))
                {
                    throw Fail(start, $"undefined opcode 0x{(byte)op:X2}");
                }

                OperandKind kind = OpCodeInfo.OperandOf(op);
                int size = OpCodeInfo.OperandSize(kind);
                if (pc + size > code.Length)
                {
                    throw Fail(start, "truncated operand");
                }

                long operand = kind switch
                {
                    OperandKind.None => 0,
                    OperandKind.U16 => BinaryPrimitives.ReadUInt16LittleEndian(code[pc..]),
                    OperandKind.I32 or OperandKind.F32 or OperandKind.BranchTarget => BinaryPrimitives.ReadInt32LittleEndian(code[pc..]),
                    _ => BinaryPrimitives.ReadInt64LittleEndian(code[pc..]),
                };
                pc += size;

                _instructionAt[start] = _instructions.Count;
                _instructions.Add((start, op, operand));
            }
        }

        /// <summary>Applies one instruction's stack effect. Returns whether control can fall through.</summary>
        private bool Step(OpCode op, long operand, ref TypeStack s, int offset, out int? branchTarget)
        {
            branchTarget = null;
            switch (op)
            {
                case OpCode.Nop:
                    return true;

                case OpCode.ConstI32: s.Push(VmType.I32); return true;
                case OpCode.ConstI64: s.Push(VmType.I64); return true;
                case OpCode.ConstF32: s.Push(VmType.F32); return true;
                case OpCode.ConstF64: s.Push(VmType.F64); return true;

                case OpCode.LoadLocal: s.Push(Local(operand, offset)); return true;
                case OpCode.StoreLocal: s.Pop(Local(operand, offset)); return true;
                case OpCode.LoadGlobal: s.Push(Global(operand, offset)); return true;
                case OpCode.StoreGlobal: s.Pop(Global(operand, offset)); return true;

                case OpCode.Dup:
                    {
                        VmType top = s.PopAny();
                        s.Push(top);
                        s.Push(top);
                        return true;
                    }

                case OpCode.Pop:
                    s.PopAny();
                    return true;

                case OpCode.Branch:
                    branchTarget = (int)operand;
                    return false;

                case OpCode.BranchIfTrue:
                case OpCode.BranchIfFalse:
                    s.Pop(VmType.I32);
                    branchTarget = (int)operand;
                    return true;

                case OpCode.Call:
                    {
                        if (operand >= _module.Functions.Count)
                        {
                            throw Fail(offset, $"call to missing function {operand}");
                        }

                        ApplySignature(_module.Functions[(int)operand].Signature, ref s);
                        return true;
                    }

                case OpCode.Syscall:
                    {
                        if (operand >= _module.Imports.Count)
                        {
                            throw Fail(offset, $"syscall to missing import {operand}");
                        }

                        ApplySignature(_module.Imports[(int)operand].Signature, ref s);
                        return true;
                    }

                case OpCode.Return:
                    if (_function.Signature.Result is { } result)
                    {
                        s.Pop(result);
                    }

                    if (s.Depth != 0)
                    {
                        throw Fail(offset, $"return with {s.Depth} extra value(s) on the stack");
                    }

                    return false;

                case OpCode.ShlI64:
                case OpCode.ShrI64:
                case OpCode.ShrUnI64:
                    s.Pop(VmType.I32);
                    s.Pop(VmType.I64);
                    s.Push(VmType.I64);
                    return true;
            }

            if (UnaryOrBinary(op) is var (input, arity, output))
            {
                for (int i = 0; i < arity; i++)
                {
                    s.Pop(input);
                }

                s.Push(output);
                return true;
            }

            throw Fail(offset, $"opcode {op} has no verification rule");
        }

        private static void ApplySignature(VmSignature signature, ref TypeStack s)
        {
            for (int i = signature.Parameters.Count - 1; i >= 0; i--)
            {
                s.Pop(signature.Parameters[i]);
            }

            if (signature.Result is { } result)
            {
                s.Push(result);
            }
        }

        private static (VmType Input, int Arity, VmType Output)? UnaryOrBinary(OpCode op) => op switch
        {
            OpCode.AddI32 or OpCode.SubI32 or OpCode.MulI32 or OpCode.DivI32 or OpCode.RemI32
                or OpCode.AndI32 or OpCode.OrI32 or OpCode.XorI32
                or OpCode.ShlI32 or OpCode.ShrI32 or OpCode.ShrUnI32 => (VmType.I32, 2, VmType.I32),
            OpCode.NegI32 or OpCode.NotI32
                or OpCode.I32ToI8 or OpCode.I32ToU8 or OpCode.I32ToI16 or OpCode.I32ToU16 => (VmType.I32, 1, VmType.I32),
            OpCode.AddI64 or OpCode.SubI64 or OpCode.MulI64 or OpCode.DivI64 or OpCode.RemI64
                or OpCode.AndI64 or OpCode.OrI64 or OpCode.XorI64 => (VmType.I64, 2, VmType.I64),
            OpCode.NegI64 or OpCode.NotI64 => (VmType.I64, 1, VmType.I64),
            OpCode.AddF32 or OpCode.SubF32 or OpCode.MulF32 or OpCode.DivF32 or OpCode.RemF32 => (VmType.F32, 2, VmType.F32),
            OpCode.NegF32 => (VmType.F32, 1, VmType.F32),
            OpCode.AddF64 or OpCode.SubF64 or OpCode.MulF64 or OpCode.DivF64 or OpCode.RemF64 => (VmType.F64, 2, VmType.F64),
            OpCode.NegF64 => (VmType.F64, 1, VmType.F64),

            OpCode.EqI32 or OpCode.LtI32 or OpCode.GtI32 or OpCode.LtUnI32 or OpCode.GtUnI32 => (VmType.I32, 2, VmType.I32),
            OpCode.EqI64 or OpCode.LtI64 or OpCode.GtI64 or OpCode.LtUnI64 or OpCode.GtUnI64 => (VmType.I64, 2, VmType.I32),
            OpCode.EqF32 or OpCode.LtF32 or OpCode.GtF32 or OpCode.LtUnF32 or OpCode.GtUnF32 => (VmType.F32, 2, VmType.I32),
            OpCode.EqF64 or OpCode.LtF64 or OpCode.GtF64 or OpCode.LtUnF64 or OpCode.GtUnF64 => (VmType.F64, 2, VmType.I32),

            OpCode.I32ToI64 or OpCode.U32ToI64 => (VmType.I32, 1, VmType.I64),
            OpCode.I64ToI32 => (VmType.I64, 1, VmType.I32),
            OpCode.I32ToF32 => (VmType.I32, 1, VmType.F32),
            OpCode.I32ToF64 => (VmType.I32, 1, VmType.F64),
            OpCode.I64ToF32 => (VmType.I64, 1, VmType.F32),
            OpCode.I64ToF64 => (VmType.I64, 1, VmType.F64),
            OpCode.F32ToI32 => (VmType.F32, 1, VmType.I32),
            OpCode.F32ToI64 => (VmType.F32, 1, VmType.I64),
            OpCode.F64ToI32 => (VmType.F64, 1, VmType.I32),
            OpCode.F64ToI64 => (VmType.F64, 1, VmType.I64),
            OpCode.F32ToF64 => (VmType.F32, 1, VmType.F64),
            OpCode.F64ToF32 => (VmType.F64, 1, VmType.F32),
            _ => null,
        };

        private VmType Local(long index, int offset)
        {
            if (index >= _function.LocalCount)
            {
                throw Fail(offset, $"local {index} out of range");
            }

            return _function.LocalType((int)index);
        }

        private VmType Global(long index, int offset)
        {
            if (index >= _module.Globals.Count)
            {
                throw Fail(offset, $"global {index} out of range");
            }

            return _module.Globals[(int)index];
        }
    }

    /// <summary>Abstract operand stack of types used during verification.</summary>
    private struct TypeStack
    {
        private readonly FunctionVerifier _owner;
        private readonly int _offset;

        public TypeStack(ImmutableStack<VmType> items, int depth, FunctionVerifier owner, int offset)
        {
            Items = items;
            Depth = depth;
            MaxDepth = depth;
            _owner = owner;
            _offset = offset;
        }

        public ImmutableStack<VmType> Items { get; private set; }

        public int Depth { get; private set; }

        public int MaxDepth { get; private set; }

        public void Push(VmType type)
        {
            if (Depth >= MaxStackDepth)
            {
                throw _owner.Fail(_offset, "operand stack exceeds the maximum depth");
            }

            Items = Items.Push(type);
            Depth++;
            MaxDepth = Math.Max(MaxDepth, Depth);
        }

        public VmType PopAny()
        {
            if (Depth == 0)
            {
                throw _owner.Fail(_offset, "operand stack underflow");
            }

            Items = Items.Pop(out VmType type);
            Depth--;
            return type;
        }

        public void Pop(VmType expected)
        {
            VmType actual = PopAny();
            if (actual != expected)
            {
                throw _owner.Fail(_offset, $"expected {VmTypes.Name(expected)} on the stack, found {VmTypes.Name(actual)}");
            }
        }
    }
}
