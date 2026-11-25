using System.Buffers.Binary;

namespace ZTX16.VM;

/// <summary>A branch target inside a <see cref="FunctionBuilder"/>.</summary>
public readonly record struct Label(int Id);

/// <summary>
/// Builds a <see cref="VmModule"/>. Used for hand-written bytecode and as the emitter
/// behind toolchain translators.
/// </summary>
public sealed class ModuleBuilder
{
    private readonly List<VmType> _globals = [];
    private readonly List<VmImport> _imports = [];
    private readonly Dictionary<string, int> _importIndex = new(StringComparer.Ordinal);
    private readonly List<FunctionBuilder> _functions = [];
    private readonly Dictionary<string, int> _exports = new(StringComparer.Ordinal);

    /// <summary>Adds a global and returns its index.</summary>
    public int AddGlobal(VmType type)
    {
        _globals.Add(type);
        return _globals.Count - 1;
    }

    /// <summary>Returns the index of the syscall import, adding it on first use.</summary>
    /// <exception cref="InvalidOperationException">The name was already imported with a different signature.</exception>
    public int Import(string name, VmSignature signature)
    {
        if (_importIndex.TryGetValue(name, out int existing))
        {
            if (!_imports[existing].Signature.Matches(signature))
            {
                throw new InvalidOperationException($"Syscall '{name}' imported with conflicting signatures.");
            }

            return existing;
        }

        _imports.Add(new VmImport(name, signature));
        _importIndex[name] = _imports.Count - 1;
        return _imports.Count - 1;
    }

    /// <summary>Declares a function. Its body can be emitted later, allowing forward calls.</summary>
    public FunctionBuilder DefineFunction(string name, VmSignature signature)
    {
        var function = new FunctionBuilder(_functions.Count, name, signature);
        _functions.Add(function);
        return function;
    }

    /// <summary>Exports <paramref name="function"/> under <paramref name="name"/>.</summary>
    public void Export(string name, FunctionBuilder function) => _exports[name] = function.Index;

    /// <summary>Builds the module. Every function must be complete.</summary>
    public VmModule Build() =>
        new(_globals, _imports, _functions.Select(f => f.Build()).ToArray(), _exports);
}

/// <summary>Emits the code of one function.</summary>
public sealed class FunctionBuilder
{
    private readonly List<byte> _code = [];
    private readonly List<VmType> _locals = [];
    private readonly List<int> _labelOffsets = [];
    private readonly List<(int PatchAt, Label Label)> _fixups = [];

    internal FunctionBuilder(int index, string name, VmSignature signature)
    {
        Index = index;
        Name = name;
        Signature = signature;
    }

    /// <summary>Function index within the module, used as the <see cref="OpCode.Call"/> operand.</summary>
    public int Index { get; }

    /// <summary>Function name.</summary>
    public string Name { get; }

    /// <summary>Function signature.</summary>
    public VmSignature Signature { get; }

    /// <summary>Returns the local index of parameter <paramref name="parameter"/>.</summary>
    public int Parameter(int parameter)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)parameter, (uint)Signature.Parameters.Count, nameof(parameter));
        return parameter;
    }

    /// <summary>Adds a local after the parameters and returns its local index.</summary>
    public int AddLocal(VmType type)
    {
        _locals.Add(type);
        return Signature.Parameters.Count + _locals.Count - 1;
    }

    /// <summary>Creates a label to be placed later with <see cref="Mark"/>.</summary>
    public Label DefineLabel()
    {
        _labelOffsets.Add(-1);
        return new Label(_labelOffsets.Count - 1);
    }

    /// <summary>Places <paramref name="label"/> at the current position.</summary>
    public FunctionBuilder Mark(Label label)
    {
        if (_labelOffsets[label.Id] >= 0)
        {
            throw new InvalidOperationException("Label is already placed.");
        }

        _labelOffsets[label.Id] = _code.Count;
        return this;
    }

    /// <summary>Emits an opcode without an operand.</summary>
    public FunctionBuilder Emit(OpCode op)
    {
        RequireOperand(op, OperandKind.None);
        _code.Add((byte)op);
        return this;
    }

    /// <summary>Emits <see cref="OpCode.ConstI32"/>.</summary>
    public FunctionBuilder ConstI32(int value) => EmitWithOperand(OpCode.ConstI32, OperandKind.I32, value);

    /// <summary>Emits <see cref="OpCode.ConstI64"/>.</summary>
    public FunctionBuilder ConstI64(long value) => EmitWithOperand(OpCode.ConstI64, OperandKind.I64, value);

    /// <summary>Emits <see cref="OpCode.ConstF32"/>.</summary>
    public FunctionBuilder ConstF32(float value) =>
        EmitWithOperand(OpCode.ConstF32, OperandKind.F32, BitConverter.SingleToInt32Bits(value));

    /// <summary>Emits <see cref="OpCode.ConstF64"/>.</summary>
    public FunctionBuilder ConstF64(double value) =>
        EmitWithOperand(OpCode.ConstF64, OperandKind.F64, BitConverter.DoubleToInt64Bits(value));

    /// <summary>Emits an opcode with a u16 index operand (locals, globals, calls, syscalls).</summary>
    public FunctionBuilder Emit(OpCode op, int index)
    {
        RequireOperand(op, OperandKind.U16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, ushort.MaxValue, nameof(index));
        _code.Add((byte)op);
        _code.Add((byte)index);
        _code.Add((byte)(index >> 8));
        return this;
    }

    /// <summary>Emits a call to <paramref name="function"/>.</summary>
    public FunctionBuilder Call(FunctionBuilder function) => Emit(OpCode.Call, function.Index);

    /// <summary>Emits a branch to <paramref name="target"/>.</summary>
    public FunctionBuilder Emit(OpCode op, Label target)
    {
        RequireOperand(op, OperandKind.BranchTarget);
        _code.Add((byte)op);
        _fixups.Add((_code.Count, target));
        _code.AddRange([0, 0, 0, 0]);
        return this;
    }

    internal VmFunction Build()
    {
        byte[] code = _code.ToArray();
        foreach ((int patchAt, Label label) in _fixups)
        {
            int offset = _labelOffsets[label.Id];
            if (offset < 0)
            {
                throw new InvalidOperationException($"Function '{Name}' branches to a label that was never placed.");
            }

            BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(patchAt), offset);
        }

        return new VmFunction(Name, Signature, _locals.ToArray(), code);
    }

    private FunctionBuilder EmitWithOperand(OpCode op, OperandKind kind, long value)
    {
        _code.Add((byte)op);
        int size = OpCodeInfo.OperandSize(kind);
        for (int i = 0; i < size; i++)
        {
            _code.Add((byte)(value >> (8 * i)));
        }

        return this;
    }

    private static void RequireOperand(OpCode op, OperandKind expected)
    {
        if (!OpCodeInfo.IsDefined(op) || OpCodeInfo.OperandOf(op) != expected)
        {
            throw new ArgumentException($"Opcode {op} does not take a {expected} operand.", nameof(op));
        }
    }
}
