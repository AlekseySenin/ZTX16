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
    private readonly List<ClassBuilder> _classes = [];
    private readonly List<VmField> _fields = [];
    private readonly List<VmSelector> _selectors = [];
    private readonly List<string> _strings = [];
    private readonly Dictionary<string, int> _stringIndex = new(StringComparer.Ordinal);

    /// <summary>Defines a class or interface. Its dispatch table and interfaces can be filled in later.</summary>
    public ClassBuilder DefineClass(string name, int baseClass, bool isInterface, IReadOnlyList<VmType> fields)
    {
        var builder = new ClassBuilder(_classes.Count, name, baseClass, isInterface, fields);
        _classes.Add(builder);
        return builder;
    }

    /// <summary>Adds an instance field reference and returns its index.</summary>
    public int AddField(int classIndex, int slot)
    {
        _fields.Add(new VmField(classIndex, slot));
        return _fields.Count - 1;
    }

    /// <summary>Adds a selector and returns its index.</summary>
    public int AddSelector(string name, VmSignature signature)
    {
        _selectors.Add(new VmSelector(name, signature));
        return _selectors.Count - 1;
    }

    /// <summary>Returns the index of a string literal, adding it on first use.</summary>
    public int AddString(string value)
    {
        if (!_stringIndex.TryGetValue(value, out int index))
        {
            index = _strings.Count;
            _strings.Add(value);
            _stringIndex[value] = index;
        }

        return index;
    }

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
        new(_globals, _imports, _functions.Select(f => f.Build()).ToArray(), _exports,
            _classes.Select(c => c.Build()).ToArray(), _fields, _selectors, _strings);
}

/// <summary>Builds one class: layout is fixed at definition, interfaces and dispatch table are filled later.</summary>
public sealed class ClassBuilder
{
    private readonly IReadOnlyList<VmType> _fields;

    internal ClassBuilder(int index, string name, int baseClass, bool isInterface, IReadOnlyList<VmType> fields)
    {
        Index = index;
        Name = name;
        BaseClass = baseClass;
        IsInterface = isInterface;
        _fields = fields.ToArray();
    }

    /// <summary>Class index within the module.</summary>
    public int Index { get; }

    /// <summary>Class name.</summary>
    public string Name { get; }

    /// <summary>Base class index or <see cref="VmClass.NoBase"/>.</summary>
    public int BaseClass { get; }

    /// <summary>Whether this is an interface.</summary>
    public bool IsInterface { get; }

    /// <summary>Instance field slot types (base fields first).</summary>
    public IReadOnlyList<VmType> Fields => _fields;

    /// <summary>All implemented interfaces.</summary>
    public HashSet<int> Interfaces { get; } = [];

    /// <summary>Dispatch table: selector → function.</summary>
    public Dictionary<int, int> Methods { get; } = [];

    /// <summary>Whether this class is a value type (struct).</summary>
    public bool IsValueType { get; set; }

    /// <summary>Slots holding nested value-type instances: slot → class.</summary>
    public Dictionary<int, int> ValueFields { get; } = [];

    internal VmClass Build() => new(Name, BaseClass, IsInterface, _fields, Interfaces.Order().ToArray(), new Dictionary<int, int>(Methods))
    {
        IsValueType = IsValueType,
        ValueFields = new Dictionary<int, int>(ValueFields),
    };
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

    /// <summary>Emits <see cref="OpCode.New"/>: allocate <paramref name="classIndex"/> and run <paramref name="constructor"/>.</summary>
    public FunctionBuilder New(int classIndex, int constructor)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)classIndex, ushort.MaxValue, nameof(classIndex));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)constructor, ushort.MaxValue, nameof(constructor));
        _code.Add((byte)OpCode.New);
        _code.Add((byte)classIndex);
        _code.Add((byte)(classIndex >> 8));
        _code.Add((byte)constructor);
        _code.Add((byte)(constructor >> 8));
        return this;
    }

    /// <summary>Emits an opcode with two u16 operands (<see cref="OpCode.MaterializeLocal"/>, pointers, …).</summary>
    public FunctionBuilder Emit(OpCode op, int first, int second)
    {
        RequireOperand(op, OperandKind.U16Pair);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)first, ushort.MaxValue, nameof(first));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)second, ushort.MaxValue, nameof(second));
        _code.Add((byte)op);
        _code.Add((byte)first);
        _code.Add((byte)(first >> 8));
        _code.Add((byte)second);
        _code.Add((byte)(second >> 8));
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
