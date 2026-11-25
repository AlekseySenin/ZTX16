using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ZTX16.VM;

namespace ZTX16.Toolchain;

/// <summary>
/// Translates one IL method body into ZTX16 bytecode. IL instructions are untyped, so the
/// translator tracks the type of every evaluation-stack slot and picks the typed VM
/// opcode for each instruction. The result is still verified by the VM verifier.
/// </summary>
internal sealed class MethodTranslator
{
    private readonly TranslationContext _context;
    private readonly MethodDefinition _method;
    private readonly FunctionBuilder _function;
    private readonly string _name;
    private readonly VmTypeInfo[] _args;
    private readonly ClrType[] _argTypes;
    private readonly ClrType _returnType;
    private VmTypeInfo[] _locals = [];
    private ClrType[] _localTypes = [];
    private VariableKind[] _localKinds = [];
    private VariableKind[] _argKinds = [];
    private int[] _argCells = [];
    private readonly Dictionary<int, Label> _labels = [];
    private readonly Dictionary<int, VmType[]> _stackAtTarget = [];
    private List<VmType> _stack = [];
    private bool _reachable = true;
    private int _offset;
    private int? _switchTemp;
    private int? _structTemp;
    private VmElementAccess? _lastNewArrayAccess;
    private readonly GenericContext _generic;

    /// <summary>How a local or argument is stored.</summary>
    private enum VariableKind
    {
        /// <summary>A VM local holding the value.</summary>
        Plain,

        /// <summary>A struct: the VM local holds the instance; stores copy into it.</summary>
        Struct,

        /// <summary>A non-struct variable whose address is taken: it lives in a heap cell.</summary>
        Cell,
    }

    public MethodTranslator(TranslationContext context, MethodDefinition method, GenericContext generic, FunctionBuilder function)
    {
        _context = context;
        _method = method;
        _generic = generic;
        _function = function;
        _name = function.Name;
        MethodSignature<ClrType> signature = method.DecodeSignature(context.TypeProvider, generic);
        IEnumerable<ClrType> argTypes = method.IsStatic() ? signature.ParameterTypes : signature.ParameterTypes.Prepend(new ObjectClrType());
        _argTypes = argTypes.ToArray(); // IL arg 0 is 'this' for instance methods (a reference, also for structs)
        _args = _argTypes.Select((t, i) => ClrTypeMapping.MapValue(t, $"{_name} parameter {i}")).ToArray();
        _returnType = signature.ReturnType;
    }

    public void Translate()
    {
        if (_method.RelativeVirtualAddress == 0)
        {
            throw new TranslationException($"{_name}: method has no IL body.");
        }

        MethodBodyBlock body = _context.GetBody(_method);
        if (body.ExceptionRegions.Length > 0)
        {
            throw new TranslationException($"{_name}: try/catch/finally is not supported yet.");
        }

        if (!body.LocalSignature.IsNil)
        {
            StandaloneSignature signature = _context.Reader.GetStandaloneSignature(body.LocalSignature);
            _localTypes = signature.DecodeLocalSignature(_context.TypeProvider, _generic).ToArray();
            _locals = _localTypes.Select((t, i) => ClrTypeMapping.MapValue(t, $"{_name} local {i}")).ToArray();
        }

        List<IlInstruction> instructions = IlDecoder.Decode(body);
        foreach (IlInstruction instruction in instructions)
        {
            if (instruction.OpCode.IsBranch())
            {
                LabelAt((int)instruction.Operand);
            }

            foreach (int target in instruction.SwitchTargets)
            {
                LabelAt(target);
            }
        }

        PlanVariables(instructions);
        EmitPrologue();

        for (int i = 0; i < instructions.Count; i++)
        {
            IlInstruction instruction = instructions[i];
            _offset = instruction.Offset;
            if (_labels.TryGetValue(instruction.Offset, out Label label))
            {
                EnterBranchTarget(instruction.Offset);
                _function.Mark(label);
            }
            else if (!_reachable)
            {
                continue; // dead code that no branch reaches
            }

            switch (FusedPatternAt(instructions, i))
            {
                case FusedPattern.ZeroLocal:
                    // default(T) idiom: ldloca V; initobj T  →  V = default
                    ZeroLocal((int)instruction.Operand);
                    i++;
                    continue;

                case FusedPattern.ArrayInitializer:
                    // new T[] { constants }: newarr; dup; ldtoken <data>; call RuntimeHelpers.InitializeArray
                    EmitArrayInitializer(_context.FieldInitialData(MetadataTokens.EntityHandle((int)instructions[i + 1].Operand), Where));
                    i += 2;
                    continue;

                case FusedPattern.PrimitiveToString:
                    // primitive.ToString() idiom: ldloca/ldarga/ldflda/ldsflda; call T::ToString()  →  load; ToText
                    LoadByAddressInstruction(instruction);
                    _function.Emit(OpCode.ToText, (int)_context.PrimitiveToString(MetadataTokens.EntityHandle((int)instructions[i + 1].Operand))!.Value);
                    PopAny();
                    Push(VmType.Ref);
                    i++;
                    continue;
            }

            TranslateInstruction(instruction);
        }

        if (_reachable)
        {
            throw Error("control falls off the end of the method");
        }
    }

    private enum FusedPattern
    {
        None,
        ZeroLocal,
        PrimitiveToString,
        ArrayInitializer,
    }

    /// <summary>Stores constant initializer data into the array on top of the stack (which stays there).</summary>
    private void EmitArrayInitializer(byte[] data)
    {
        VmElementAccess access = _lastNewArrayAccess ?? throw Error("array initializer without a preceding newarr");
        int size = access switch
        {
            VmElementAccess.I8 or VmElementAccess.U8 => 1,
            VmElementAccess.I16 or VmElementAccess.U16 => 2,
            VmElementAccess.I32 or VmElementAccess.F32 => 4,
            VmElementAccess.I64 or VmElementAccess.F64 => 8,
            _ => throw Error("array initializer data for reference elements"),
        };

        for (int index = 0; index + size <= data.Length; index += size)
        {
            ReadOnlySpan<byte> bytes = data.AsSpan(index, size);
            _function.Emit(OpCode.Dup).ConstI32(index / size);
            switch (access)
            {
                case VmElementAccess.I8: _function.ConstI32((sbyte)bytes[0]); break;
                case VmElementAccess.U8: _function.ConstI32(bytes[0]); break;
                case VmElementAccess.I16: _function.ConstI32(BitConverter.ToInt16(bytes)); break;
                case VmElementAccess.U16: _function.ConstI32(BitConverter.ToUInt16(bytes)); break;
                case VmElementAccess.I32: _function.ConstI32(BitConverter.ToInt32(bytes)); break;
                case VmElementAccess.F32: _function.ConstF32(BitConverter.ToSingle(bytes)); break;
                case VmElementAccess.I64: _function.ConstI64(BitConverter.ToInt64(bytes)); break;
                default: _function.ConstF64(BitConverter.ToDouble(bytes)); break;
            }

            _function.Emit(OpCode.StoreElement, (int)access);
        }
    }

    /// <summary>Two-instruction idioms translated as a unit (they take an address only to use it at once).</summary>
    private FusedPattern FusedPatternAt(List<IlInstruction> instructions, int i)
    {
        if (i + 1 >= instructions.Count || _labels.ContainsKey(instructions[i + 1].Offset))
        {
            return FusedPattern.None;
        }

        ILOpCode op = instructions[i].OpCode;
        IlInstruction next = instructions[i + 1];
        if (op == ILOpCode.Dup && next.OpCode == ILOpCode.Ldtoken
            && i + 2 < instructions.Count && instructions[i + 2].OpCode == ILOpCode.Call && !_labels.ContainsKey(instructions[i + 2].Offset)
            && _context.IsInitializeArray(MetadataTokens.EntityHandle((int)instructions[i + 2].Operand)))
        {
            return FusedPattern.ArrayInitializer;
        }

        if (op is ILOpCode.Ldloca or ILOpCode.Ldloca_s && next.OpCode == ILOpCode.Initobj)
        {
            return FusedPattern.ZeroLocal;
        }

        if (op is ILOpCode.Ldloca or ILOpCode.Ldloca_s or ILOpCode.Ldarga or ILOpCode.Ldarga_s or ILOpCode.Ldflda or ILOpCode.Ldsflda
            && next.OpCode == ILOpCode.Call
            && _context.PrimitiveToString(MetadataTokens.EntityHandle((int)next.Operand)) is not null)
        {
            return FusedPattern.PrimitiveToString;
        }

        return FusedPattern.None;
    }

    /// <summary>
    /// Decides how each local and argument is stored: structs keep their instance in the VM slot,
    /// non-struct variables whose address is taken move into heap cells.
    /// </summary>
    private void PlanVariables(List<IlInstruction> instructions)
    {
        var addressedLocals = new HashSet<int>();
        var addressedArgs = new HashSet<int>();
        var storedArgs = new HashSet<int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            if (FusedPatternAt(instructions, i) != FusedPattern.None)
            {
                i++;
                continue;
            }

            switch (instructions[i].OpCode)
            {
                case ILOpCode.Ldloca or ILOpCode.Ldloca_s: addressedLocals.Add((int)instructions[i].Operand); break;
                case ILOpCode.Ldarga or ILOpCode.Ldarga_s: addressedArgs.Add((int)instructions[i].Operand); break;
                case ILOpCode.Starg or ILOpCode.Starg_s: storedArgs.Add((int)instructions[i].Operand); break;
            }
        }

        _localKinds = new VariableKind[_locals.Length];
        for (int i = 0; i < _locals.Length; i++)
        {
            _localKinds[i] = ClrTypeMapping.IsStruct(_localTypes[i]) ? VariableKind.Struct
                : addressedLocals.Contains(i) ? VariableKind.Cell
                : VariableKind.Plain;
            _function.AddLocal(_localKinds[i] == VariableKind.Plain ? _locals[i].Type!.Value : VmType.Ref);
        }

        _argKinds = new VariableKind[_args.Length];
        _argCells = new int[_args.Length];
        for (int i = 0; i < _args.Length; i++)
        {
            bool isThis = i == 0 && !_method.IsStatic();
            if (isThis || _argTypes[i] is ByRefClrType)
            {
                if (addressedArgs.Contains(i))
                {
                    throw Error($"taking the address of {(isThis ? "'this'" : "a ref parameter")} is not supported");
                }

                _argKinds[i] = VariableKind.Plain;
            }
            else if (ClrTypeMapping.IsStruct(_argTypes[i]))
            {
                _argKinds[i] = VariableKind.Struct;
                _argCells[i] = addressedArgs.Contains(i) || storedArgs.Contains(i) ? 1 : 0; // 1 = copy on entry
            }
            else if (addressedArgs.Contains(i))
            {
                _argKinds[i] = VariableKind.Cell;
                _argCells[i] = _function.AddLocal(VmType.Ref);
            }
        }
    }

    private void EmitPrologue()
    {
        for (int i = 0; i < _locals.Length; i++)
        {
            if (_localKinds[i] == VariableKind.Cell)
            {
                _function.Emit(OpCode.NewCell, (int)CellAccess(_localTypes[i])).Emit(OpCode.StoreLocal, _args.Length + i);
            }
        }

        for (int i = 0; i < _args.Length; i++)
        {
            switch (_argKinds[i])
            {
                case VariableKind.Cell:
                    {
                        VmElementAccess access = CellAccess(_argTypes[i]);
                        _function.Emit(OpCode.NewCell, (int)access).Emit(OpCode.Dup).Emit(OpCode.LoadLocal, i)
                            .Emit(OpCode.StoreIndirect, (int)access).Emit(OpCode.StoreLocal, _argCells[i]);
                        break;
                    }

                case VariableKind.Struct when _argCells[i] == 1:
                    // The caller's value is shared until now; this method modifies its parameter, so copy it.
                    _function.Emit(OpCode.LoadLocal, i).Emit(OpCode.Clone).Emit(OpCode.StoreLocal, i);
                    break;
            }
        }
    }

    private VmElementAccess CellAccess(ClrType type) => ClrTypeMapping.ElementAccess(type, Where);

    private void TranslateInstruction(IlInstruction ins)
    {
        ILOpCode op = ins.OpCode;
        switch (op)
        {
            case ILOpCode.Nop:
                return;

            case ILOpCode.Ldarg_0: LoadArg(0); return;
            case ILOpCode.Ldarg_1: LoadArg(1); return;
            case ILOpCode.Ldarg_2: LoadArg(2); return;
            case ILOpCode.Ldarg_3: LoadArg(3); return;
            case ILOpCode.Ldarg_s or ILOpCode.Ldarg: LoadArg((int)ins.Operand); return;
            case ILOpCode.Starg_s or ILOpCode.Starg: StoreArg((int)ins.Operand); return;

            case ILOpCode.Ldloc_0: LoadLocal(0); return;
            case ILOpCode.Ldloc_1: LoadLocal(1); return;
            case ILOpCode.Ldloc_2: LoadLocal(2); return;
            case ILOpCode.Ldloc_3: LoadLocal(3); return;
            case ILOpCode.Ldloc_s or ILOpCode.Ldloc: LoadLocal((int)ins.Operand); return;
            case ILOpCode.Stloc_0: StoreLocal(0); return;
            case ILOpCode.Stloc_1: StoreLocal(1); return;
            case ILOpCode.Stloc_2: StoreLocal(2); return;
            case ILOpCode.Stloc_3: StoreLocal(3); return;
            case ILOpCode.Stloc_s or ILOpCode.Stloc: StoreLocal((int)ins.Operand); return;

            case >= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4_8:
                PushConstI32((int)op - (int)ILOpCode.Ldc_i4_0); // ILOpCode is ushort-based: subtract as int so ldc.i4.m1 is -1
                return;
            case ILOpCode.Ldc_i4_s or ILOpCode.Ldc_i4:
                PushConstI32((int)ins.Operand);
                return;
            case ILOpCode.Ldc_i8:
                _function.ConstI64(ins.Operand);
                Push(VmType.I64);
                return;
            case ILOpCode.Ldc_r4:
                _function.ConstF32(BitConverter.Int32BitsToSingle((int)ins.Operand));
                Push(VmType.F32);
                return;
            case ILOpCode.Ldc_r8:
                _function.ConstF64(BitConverter.Int64BitsToDouble(ins.Operand));
                Push(VmType.F64);
                return;

            case ILOpCode.Dup:
                {
                    VmType top = Peek();
                    _function.Emit(OpCode.Dup);
                    Push(top);
                    return;
                }

            case ILOpCode.Pop:
                PopAny();
                _function.Emit(OpCode.Pop);
                return;

            case ILOpCode.Ret:
                {
                    VmType? result = ClrTypeMapping.Map(_returnType, $"{_name} return type").Type;
                    if (result is { } r)
                    {
                        Pop(r);
                    }

                    if (_stack.Count != 0)
                    {
                        throw Error("evaluation stack is not empty at return");
                    }

                    _function.Emit(OpCode.Return);
                    _reachable = false;
                    return;
                }

            case ILOpCode.Add or ILOpCode.Sub or ILOpCode.Mul or ILOpCode.Div or ILOpCode.Div_un
                or ILOpCode.Rem or ILOpCode.Rem_un or ILOpCode.And or ILOpCode.Or or ILOpCode.Xor:
                Binary(op);
                return;

            case ILOpCode.Shl or ILOpCode.Shr or ILOpCode.Shr_un:
                Shift(op);
                return;

            case ILOpCode.Neg or ILOpCode.Not:
                Unary(op);
                return;

            case ILOpCode.Ceq or ILOpCode.Cgt or ILOpCode.Cgt_un or ILOpCode.Clt or ILOpCode.Clt_un:
                {
                    VmType type = PopPair();
                    if (type == VmType.Ref)
                    {
                        // ceq: identity; cgt.un: C#'s "x != null" idiom.
                        _function.Emit(OpCode.EqRef);
                        if (op == ILOpCode.Cgt_un)
                        {
                            _function.ConstI32(0).Emit(OpCode.EqI32);
                        }
                        else if (op != ILOpCode.Ceq)
                        {
                            throw Error($"comparison '{op}' is not defined for references");
                        }
                    }
                    else
                    {
                        _function.Emit(CompareOp(op, type));
                    }

                    Push(VmType.I32);
                    return;
                }

            case ILOpCode.Conv_i1 or ILOpCode.Conv_u1 or ILOpCode.Conv_i2 or ILOpCode.Conv_u2
                or ILOpCode.Conv_i4 or ILOpCode.Conv_u4 or ILOpCode.Conv_i8 or ILOpCode.Conv_u8
                or ILOpCode.Conv_r4 or ILOpCode.Conv_r8 or ILOpCode.Conv_r_un:
                Convert(op);
                return;

            case ILOpCode.Br or ILOpCode.Br_s:
                BranchTo((int)ins.Operand, OpCode.Branch);
                _reachable = false;
                return;

            case ILOpCode.Brtrue or ILOpCode.Brtrue_s:
            case ILOpCode.Brfalse or ILOpCode.Brfalse_s:
                {
                    bool ifTrue = op is ILOpCode.Brtrue or ILOpCode.Brtrue_s;
                    VmType condition = PopAny();
                    if (condition == VmType.I64)
                    {
                        // "x != 0" for 64-bit values: compare with zero and invert the branch.
                        _function.ConstI64(0).Emit(OpCode.EqI64);
                        ifTrue = !ifTrue;
                    }
                    else if (condition == VmType.Ref)
                    {
                        // "x != null": compare with null and invert the branch.
                        _function.Emit(OpCode.ConstNull).Emit(OpCode.EqRef);
                        ifTrue = !ifTrue;
                    }
                    else if (condition != VmType.I32)
                    {
                        throw Error($"branch condition must be an integer, found {VmTypes.Name(condition)}");
                    }

                    BranchTo((int)ins.Operand, ifTrue ? OpCode.BranchIfTrue : OpCode.BranchIfFalse);
                    return;
                }

            case ILOpCode.Beq or ILOpCode.Beq_s or ILOpCode.Bne_un or ILOpCode.Bne_un_s
                or ILOpCode.Bge or ILOpCode.Bge_s or ILOpCode.Bge_un or ILOpCode.Bge_un_s
                or ILOpCode.Bgt or ILOpCode.Bgt_s or ILOpCode.Bgt_un or ILOpCode.Bgt_un_s
                or ILOpCode.Ble or ILOpCode.Ble_s or ILOpCode.Ble_un or ILOpCode.Ble_un_s
                or ILOpCode.Blt or ILOpCode.Blt_s or ILOpCode.Blt_un or ILOpCode.Blt_un_s:
                CompareAndBranch(op, (int)ins.Operand);
                return;

            case ILOpCode.Switch:
                Switch(ins.SwitchTargets);
                return;

            case ILOpCode.Call:
            case ILOpCode.Callvirt:
                Call(MetadataTokens.EntityHandle((int)ins.Operand), virtualCall: op == ILOpCode.Callvirt);
                return;

            case ILOpCode.Newobj:
                {
                    (VmSignature ctor, Action<FunctionBuilder> emit) = _context.ResolveNew(MetadataTokens.EntityHandle((int)ins.Operand), _generic, Where);
                    for (int i = ctor.Parameters.Count - 1; i >= 1; i--)
                    {
                        Pop(ctor.Parameters[i], $"constructor argument {i - 1}");
                    }

                    emit(_function);
                    Push(VmType.Ref);
                    return;
                }

            case ILOpCode.Ldnull:
                _function.Emit(OpCode.ConstNull);
                Push(VmType.Ref);
                return;

            case ILOpCode.Ldfld:
                {
                    (int index, VmTypeInfo type) = _context.GetInstanceField(MetadataTokens.EntityHandle((int)ins.Operand), _generic, Where);
                    Pop(VmType.Ref, "field owner");
                    _function.Emit(OpCode.LoadField, index);
                    Push(type.Type!.Value);
                    return;
                }

            case ILOpCode.Stfld:
                {
                    FieldRef field = _context.GetInstanceField(MetadataTokens.EntityHandle((int)ins.Operand), _generic, Where);
                    Pop(field.Type.Type!.Value, "field value");
                    Pop(VmType.Ref, "field owner");
                    if (ClrTypeMapping.IsStruct(field.Clr))
                    {
                        // [owner, value] → copy the value into the field's own instance.
                        _function.Emit(OpCode.Swap).Emit(OpCode.MaterializeField, field.Index, _context.StructClass(field.Clr, Where))
                            .Emit(OpCode.Swap).Emit(OpCode.CopyInto);
                        return;
                    }

                    EmitNarrow(field.Type);
                    _function.Emit(OpCode.StoreField, field.Index);
                    return;
                }

            case ILOpCode.Ldflda:
                {
                    FieldRef field = _context.GetInstanceField(MetadataTokens.EntityHandle((int)ins.Operand), _generic, Where);
                    Pop(VmType.Ref, "field owner");
                    if (ClrTypeMapping.IsStruct(field.Clr))
                    {
                        _function.Emit(OpCode.MaterializeField, field.Index, _context.StructClass(field.Clr, Where));
                    }
                    else
                    {
                        _function.Emit(OpCode.FieldPointer, field.Index, (int)CellAccess(field.Clr));
                    }

                    Push(VmType.Ref);
                    return;
                }

            case ILOpCode.Ldsflda:
                {
                    FieldRef global = _context.GetGlobal(MetadataTokens.EntityHandle((int)ins.Operand), _generic, Where);
                    if (ClrTypeMapping.IsStruct(global.Clr))
                    {
                        _function.Emit(OpCode.MaterializeGlobal, global.Index, _context.StructClass(global.Clr, Where));
                    }
                    else
                    {
                        _function.Emit(OpCode.GlobalPointer, global.Index, (int)CellAccess(global.Clr));
                    }

                    Push(VmType.Ref);
                    return;
                }

            case ILOpCode.Ldloca or ILOpCode.Ldloca_s:
                AddressOfLocal((int)ins.Operand);
                return;

            case ILOpCode.Ldarga or ILOpCode.Ldarga_s:
                AddressOfArg((int)ins.Operand);
                return;

            case ILOpCode.Ldelema:
                {
                    ClrType element = _context.ResolveTypeToken(MetadataTokens.EntityHandle((int)ins.Operand), _generic);
                    Pop(VmType.I32, "array index");
                    Pop(VmType.Ref, "array");
                    if (ClrTypeMapping.IsStruct(element))
                    {
                        _function.Emit(OpCode.MaterializeElement, _context.StructClass(element, Where));
                    }
                    else
                    {
                        _function.Emit(OpCode.ElementPointer, (int)ClrTypeMapping.ElementAccess(element, Where));
                    }

                    Push(VmType.Ref);
                    return;
                }

            case ILOpCode.Ldind_i1: LoadIndirect(VmElementAccess.I8); return;
            case ILOpCode.Ldind_u1: LoadIndirect(VmElementAccess.U8); return;
            case ILOpCode.Ldind_i2: LoadIndirect(VmElementAccess.I16); return;
            case ILOpCode.Ldind_u2: LoadIndirect(VmElementAccess.U16); return;
            case ILOpCode.Ldind_i4 or ILOpCode.Ldind_u4: LoadIndirect(VmElementAccess.I32); return;
            case ILOpCode.Ldind_i8: LoadIndirect(VmElementAccess.I64); return;
            case ILOpCode.Ldind_r4: LoadIndirect(VmElementAccess.F32); return;
            case ILOpCode.Ldind_r8: LoadIndirect(VmElementAccess.F64); return;
            case ILOpCode.Ldind_ref: LoadIndirect(VmElementAccess.Ref); return;
            case ILOpCode.Stind_i1: StoreIndirect(VmElementAccess.I8); return;
            case ILOpCode.Stind_i2: StoreIndirect(VmElementAccess.I16); return;
            case ILOpCode.Stind_i4: StoreIndirect(VmElementAccess.I32); return;
            case ILOpCode.Stind_i8: StoreIndirect(VmElementAccess.I64); return;
            case ILOpCode.Stind_r4: StoreIndirect(VmElementAccess.F32); return;
            case ILOpCode.Stind_r8: StoreIndirect(VmElementAccess.F64); return;
            case ILOpCode.Stind_ref: StoreIndirect(VmElementAccess.Ref); return;

            case ILOpCode.Ldobj:
                {
                    ClrType type = _context.ResolveTypeToken(MetadataTokens.EntityHandle((int)ins.Operand), _generic);
                    if (!ClrTypeMapping.IsStruct(type))
                    {
                        LoadIndirect(ClrTypeMapping.ElementAccess(type, Where));
                    }

                    // A struct address is its instance, which is also its value (stores copy).
                    return;
                }

            case ILOpCode.Stobj:
                {
                    ClrType type = _context.ResolveTypeToken(MetadataTokens.EntityHandle((int)ins.Operand), _generic);
                    if (ClrTypeMapping.IsStruct(type))
                    {
                        EmitStructStoreThroughAddress();
                    }
                    else
                    {
                        StoreIndirect(ClrTypeMapping.ElementAccess(type, Where));
                    }

                    return;
                }

            case ILOpCode.Initobj:
                {
                    ClrType type = _context.ResolveTypeToken(MetadataTokens.EntityHandle((int)ins.Operand), _generic);
                    if (ClrTypeMapping.IsStruct(type))
                    {
                        _function.Emit(OpCode.ConstNull); // copying the default value zeroes the instance
                        Push(VmType.Ref);
                        EmitStructStoreThroughAddress();
                    }
                    else
                    {
                        EmitZero(ClrTypeMapping.MapValue(type, Where).Type!.Value);
                        StoreIndirect(ClrTypeMapping.ElementAccess(type, Where));
                    }

                    return;
                }

            case ILOpCode.Castclass:
            case ILOpCode.Isinst:
                {
                    Pop(VmType.Ref, "cast operand");
                    if (_context.ClassForTypeTest(MetadataTokens.EntityHandle((int)ins.Operand), _generic, Where) is { } target)
                    {
                        _function.Emit(op == ILOpCode.Castclass ? OpCode.CastClass : OpCode.IsInstance, target.Builder.Index);
                    }

                    Push(VmType.Ref);
                    return;
                }

            case ILOpCode.Newarr:
                {
                    ClrType element = _context.ResolveTypeToken(MetadataTokens.EntityHandle((int)ins.Operand), _generic);
                    VmElementAccess access = ClrTypeMapping.ElementAccess(element, Where);
                    Pop(VmType.I32, "array length");
                    _function.Emit(OpCode.NewArray, (int)VmTypes.StorageOf(access));
                    Push(VmType.Ref);
                    _lastNewArrayAccess = access;
                    return;
                }

            case ILOpCode.Ldlen:
                Pop(VmType.Ref, "array");
                _function.Emit(OpCode.ArrayLength);
                Push(VmType.I32);
                return;

            case ILOpCode.Ldelem_i1: LoadElement(VmElementAccess.I8); return;
            case ILOpCode.Ldelem_u1: LoadElement(VmElementAccess.U8); return;
            case ILOpCode.Ldelem_i2: LoadElement(VmElementAccess.I16); return;
            case ILOpCode.Ldelem_u2: LoadElement(VmElementAccess.U16); return;
            case ILOpCode.Ldelem_i4 or ILOpCode.Ldelem_u4: LoadElement(VmElementAccess.I32); return;
            case ILOpCode.Ldelem_i8: LoadElement(VmElementAccess.I64); return;
            case ILOpCode.Ldelem_r4: LoadElement(VmElementAccess.F32); return;
            case ILOpCode.Ldelem_r8: LoadElement(VmElementAccess.F64); return;
            case ILOpCode.Ldelem_ref: LoadElement(VmElementAccess.Ref); return;
            case ILOpCode.Ldelem:
                LoadElement(ClrTypeMapping.ElementAccess(_context.ResolveTypeToken(MetadataTokens.EntityHandle((int)ins.Operand), _generic), Where));
                return;

            case ILOpCode.Stelem_i1: StoreElement(VmElementAccess.I8); return;
            case ILOpCode.Stelem_i2: StoreElement(VmElementAccess.I16); return;
            case ILOpCode.Stelem_i4: StoreElement(VmElementAccess.I32); return;
            case ILOpCode.Stelem_i8: StoreElement(VmElementAccess.I64); return;
            case ILOpCode.Stelem_r4: StoreElement(VmElementAccess.F32); return;
            case ILOpCode.Stelem_r8: StoreElement(VmElementAccess.F64); return;
            case ILOpCode.Stelem_ref: StoreElement(VmElementAccess.Ref); return;
            case ILOpCode.Stelem:
                {
                    ClrType element = _context.ResolveTypeToken(MetadataTokens.EntityHandle((int)ins.Operand), _generic);
                    if (ClrTypeMapping.IsStruct(element))
                    {
                        // [array, index, value] → copy the value into the element's own instance.
                        Pop(VmType.Ref, "array element");
                        Pop(VmType.I32, "array index");
                        Pop(VmType.Ref, "array");
                        int temp = StructTemp();
                        _function.Emit(OpCode.StoreLocal, temp)
                            .Emit(OpCode.MaterializeElement, _context.StructClass(element, Where))
                            .Emit(OpCode.LoadLocal, temp)
                            .Emit(OpCode.CopyInto);
                        return;
                    }

                    StoreElement(ClrTypeMapping.ElementAccess(element, Where));
                    return;
                }

            case ILOpCode.Ldstr:
                _function.Emit(OpCode.LoadString, _context.AddString(_context.UserString((int)ins.Operand)));
                Push(VmType.Ref);
                return;

            case ILOpCode.Ldsfld:
                {
                    (int index, VmTypeInfo type) = _context.GetGlobal(MetadataTokens.EntityHandle((int)ins.Operand), _generic, Where);
                    _function.Emit(OpCode.LoadGlobal, index);
                    Push(type.Type!.Value);
                    return;
                }

            case ILOpCode.Stsfld:
                {
                    FieldRef global = _context.GetGlobal(MetadataTokens.EntityHandle((int)ins.Operand), _generic, Where);
                    Pop(global.Type.Type!.Value);
                    if (ClrTypeMapping.IsStruct(global.Clr))
                    {
                        _function.Emit(OpCode.MaterializeGlobal, global.Index, _context.StructClass(global.Clr, Where))
                            .Emit(OpCode.Swap).Emit(OpCode.CopyInto);
                        return;
                    }

                    EmitNarrow(global.Type);
                    _function.Emit(OpCode.StoreGlobal, global.Index);
                    return;
                }

            default:
                throw Error($"IL instruction '{op.ToString().ToLowerInvariant().Replace('_', '.')}' is not supported yet{UnsupportedHint(op)}");
        }
    }

    private static string UnsupportedHint(ILOpCode op) => op switch
    {
        ILOpCode.Box or ILOpCode.Unbox or ILOpCode.Unbox_any => " (boxing is not supported; avoid passing value types as object)",
        ILOpCode.Ldftn or ILOpCode.Ldvirtftn => " (delegates and lambdas are not supported yet)",
        ILOpCode.Constrained => " (calls through 'constrained.', e.g. ToString()/Equals() on a generic T or a struct, are not supported)",
        ILOpCode.Ldind_i or ILOpCode.Stind_i or ILOpCode.Ldelem_i or ILOpCode.Stelem_i => " (native integers are not part of the ZTX16 model)",
        ILOpCode.Throw => " (exceptions are not supported yet)",
        >= ILOpCode.Conv_ovf_i1_un and <= ILOpCode.Conv_ovf_u_un or >= ILOpCode.Conv_ovf_i1 and <= ILOpCode.Conv_ovf_u8
            or ILOpCode.Add_ovf or ILOpCode.Add_ovf_un or ILOpCode.Sub_ovf or ILOpCode.Sub_ovf_un
            or ILOpCode.Mul_ovf or ILOpCode.Mul_ovf_un => " (checked arithmetic is not supported)",
        _ => string.Empty,
    };

    // ---- variables ----------------------------------------------------------------------

    private void LoadArg(int index)
    {
        CheckArg(index);
        LoadVariable(_argKinds[index], index, _argCells[index], _args[index], _argTypes[index]);
    }

    private void StoreArg(int index)
    {
        CheckArg(index);
        StoreVariable(_argKinds[index], index, _argCells[index], _args[index], _argTypes[index]);
    }

    private void AddressOfArg(int index)
    {
        CheckArg(index);
        AddressOfVariable(_argKinds[index], index, _argCells[index], _argTypes[index], "argument");
    }

    private void LoadLocal(int index)
    {
        int slot = CheckLocal(index);
        LoadVariable(_localKinds[index], slot, slot, _locals[index], _localTypes[index]);
    }

    private void StoreLocal(int index)
    {
        int slot = CheckLocal(index);
        StoreVariable(_localKinds[index], slot, slot, _locals[index], _localTypes[index]);
    }

    private void AddressOfLocal(int index)
    {
        int slot = CheckLocal(index);
        AddressOfVariable(_localKinds[index], slot, slot, _localTypes[index], "local");
    }

    // slot: the VM local holding the value or struct instance; cell: the VM local holding the cell (Cell kind).
    private void LoadVariable(VariableKind kind, int slot, int cell, VmTypeInfo type, ClrType clr)
    {
        if (kind == VariableKind.Cell)
        {
            _function.Emit(OpCode.LoadLocal, cell).Emit(OpCode.LoadIndirect, (int)CellAccess(clr));
        }
        else
        {
            _function.Emit(OpCode.LoadLocal, slot); // a struct loads as its instance; stores copy, so sharing is safe
        }

        Push(type.Type!.Value);
    }

    private void StoreVariable(VariableKind kind, int slot, int cell, VmTypeInfo type, ClrType clr)
    {
        Pop(type.Type!.Value);
        switch (kind)
        {
            case VariableKind.Struct:
                // Copy the value into the variable's own instance (created on first use).
                _function.Emit(OpCode.MaterializeLocal, slot, _context.StructClass(clr, Where)).Emit(OpCode.Swap).Emit(OpCode.CopyInto);
                break;
            case VariableKind.Cell:
                _function.Emit(OpCode.LoadLocal, cell).Emit(OpCode.Swap).Emit(OpCode.StoreIndirect, (int)CellAccess(clr));
                break;
            default:
                EmitNarrow(type);
                _function.Emit(OpCode.StoreLocal, slot);
                break;
        }
    }

    private void AddressOfVariable(VariableKind kind, int slot, int cell, ClrType clr, string what)
    {
        switch (kind)
        {
            case VariableKind.Struct:
                _function.Emit(OpCode.MaterializeLocal, slot, _context.StructClass(clr, Where));
                break;
            case VariableKind.Cell:
                _function.Emit(OpCode.LoadLocal, cell);
                break;
            default:
                throw Error($"internal toolchain error: address of a plain {what}");
        }

        Push(VmType.Ref);
    }

    /// <summary>Stores the struct value on top of the stack into the instance address below it.</summary>
    private void EmitStructStoreThroughAddress()
    {
        Pop(VmType.Ref, "struct value");
        Pop(VmType.Ref, "struct address");
        _function.Emit(OpCode.CopyInto);
    }

    private int StructTemp() => _structTemp ??= _function.AddLocal(VmType.Ref);

    /// <summary>Loads the value whose address an ldloca/ldarga/ldflda/ldsflda instruction would take.</summary>
    private void LoadByAddressInstruction(IlInstruction ins)
    {
        switch (ins.OpCode)
        {
            case ILOpCode.Ldloca or ILOpCode.Ldloca_s:
                LoadLocal((int)ins.Operand);
                break;
            case ILOpCode.Ldarga or ILOpCode.Ldarga_s:
                LoadArg((int)ins.Operand);
                break;
            case ILOpCode.Ldflda:
                {
                    (int index, VmTypeInfo type) = _context.GetInstanceField(MetadataTokens.EntityHandle((int)ins.Operand), _generic, Where);
                    Pop(VmType.Ref, "field owner");
                    _function.Emit(OpCode.LoadField, index);
                    Push(type.Type!.Value);
                    break;
                }

            default:
                {
                    (int index, VmTypeInfo type) = _context.GetGlobal(MetadataTokens.EntityHandle((int)ins.Operand), _generic, Where);
                    _function.Emit(OpCode.LoadGlobal, index);
                    Push(type.Type!.Value);
                    break;
                }
        }
    }

    private void ZeroLocal(int index)
    {
        CheckLocal(index);
        EmitZero(_locals[index].Type!.Value);
        StoreLocal(index);
    }

    /// <summary>Pushes the default value of a VM type (null is also the default struct).</summary>
    private void EmitZero(VmType type)
    {
        switch (type)
        {
            case VmType.I32: _function.ConstI32(0); break;
            case VmType.I64: _function.ConstI64(0); break;
            case VmType.F32: _function.ConstF32(0); break;
            case VmType.F64: _function.ConstF64(0); break;
            default: _function.Emit(OpCode.ConstNull); break;
        }

        Push(type);
    }

    private int CheckArg(int index) =>
        (uint)index < (uint)_args.Length ? index : throw Error($"argument {index} out of range");

    private int CheckLocal(int index) =>
        (uint)index < (uint)_locals.Length ? _args.Length + index : throw Error($"local {index} out of range");

    // Storing into a sub-32-bit location truncates, as the CLI specifies.
    private void EmitNarrow(VmTypeInfo target)
    {
        if (target.Narrow is { } narrow)
        {
            _function.Emit(narrow);
        }
    }

    private void PushConstI32(int value)
    {
        _function.ConstI32(value);
        Push(VmType.I32);
    }

    // ---- arithmetic ---------------------------------------------------------------------

    private void Binary(ILOpCode op)
    {
        VmType type = PopPair();
        OpCode? vmOp = (op, type) switch
        {
            (ILOpCode.Add, VmType.I32) => OpCode.AddI32,
            (ILOpCode.Add, VmType.I64) => OpCode.AddI64,
            (ILOpCode.Add, VmType.F32) => OpCode.AddF32,
            (ILOpCode.Add, VmType.F64) => OpCode.AddF64,
            (ILOpCode.Sub, VmType.I32) => OpCode.SubI32,
            (ILOpCode.Sub, VmType.I64) => OpCode.SubI64,
            (ILOpCode.Sub, VmType.F32) => OpCode.SubF32,
            (ILOpCode.Sub, VmType.F64) => OpCode.SubF64,
            (ILOpCode.Mul, VmType.I32) => OpCode.MulI32,
            (ILOpCode.Mul, VmType.I64) => OpCode.MulI64,
            (ILOpCode.Mul, VmType.F32) => OpCode.MulF32,
            (ILOpCode.Mul, VmType.F64) => OpCode.MulF64,
            (ILOpCode.Div, VmType.I32) => OpCode.DivI32,
            (ILOpCode.Div, VmType.I64) => OpCode.DivI64,
            (ILOpCode.Div, VmType.F32) => OpCode.DivF32,
            (ILOpCode.Div, VmType.F64) => OpCode.DivF64,
            (ILOpCode.Div_un, VmType.I32) => OpCode.DivUnI32,
            (ILOpCode.Div_un, VmType.I64) => OpCode.DivUnI64,
            (ILOpCode.Rem, VmType.I32) => OpCode.RemI32,
            (ILOpCode.Rem, VmType.I64) => OpCode.RemI64,
            (ILOpCode.Rem, VmType.F32) => OpCode.RemF32,
            (ILOpCode.Rem, VmType.F64) => OpCode.RemF64,
            (ILOpCode.Rem_un, VmType.I32) => OpCode.RemUnI32,
            (ILOpCode.Rem_un, VmType.I64) => OpCode.RemUnI64,
            (ILOpCode.And, VmType.I32) => OpCode.AndI32,
            (ILOpCode.And, VmType.I64) => OpCode.AndI64,
            (ILOpCode.Or, VmType.I32) => OpCode.OrI32,
            (ILOpCode.Or, VmType.I64) => OpCode.OrI64,
            (ILOpCode.Xor, VmType.I32) => OpCode.XorI32,
            (ILOpCode.Xor, VmType.I64) => OpCode.XorI64,
            _ => null,
        };

        _function.Emit(vmOp ?? throw Error($"'{op}' is not defined for {VmTypes.Name(type)}"));
        Push(type);
    }

    private void Shift(ILOpCode op)
    {
        Pop(VmType.I32, "shift count");
        VmType type = PopAny();
        OpCode vmOp = (op, type) switch
        {
            (ILOpCode.Shl, VmType.I32) => OpCode.ShlI32,
            (ILOpCode.Shr, VmType.I32) => OpCode.ShrI32,
            (ILOpCode.Shr_un, VmType.I32) => OpCode.ShrUnI32,
            (ILOpCode.Shl, VmType.I64) => OpCode.ShlI64,
            (ILOpCode.Shr, VmType.I64) => OpCode.ShrI64,
            (ILOpCode.Shr_un, VmType.I64) => OpCode.ShrUnI64,
            _ => throw Error($"cannot shift a {VmTypes.Name(type)} value"),
        };
        _function.Emit(vmOp);
        Push(type);
    }

    private void Unary(ILOpCode op)
    {
        VmType type = PopAny();
        OpCode vmOp = (op, type) switch
        {
            (ILOpCode.Neg, VmType.I32) => OpCode.NegI32,
            (ILOpCode.Neg, VmType.I64) => OpCode.NegI64,
            (ILOpCode.Neg, VmType.F32) => OpCode.NegF32,
            (ILOpCode.Neg, VmType.F64) => OpCode.NegF64,
            (ILOpCode.Not, VmType.I32) => OpCode.NotI32,
            (ILOpCode.Not, VmType.I64) => OpCode.NotI64,
            _ => throw Error($"'{op}' is not defined for {VmTypes.Name(type)}"),
        };
        _function.Emit(vmOp);
        Push(type);
    }

    private OpCode CompareOp(ILOpCode op, VmType type)
    {
        bool isFloat = type is VmType.F32 or VmType.F64;
        return (op, type) switch
        {
            (ILOpCode.Ceq, VmType.I32) => OpCode.EqI32,
            (ILOpCode.Ceq, VmType.I64) => OpCode.EqI64,
            (ILOpCode.Ceq, VmType.F32) => OpCode.EqF32,
            (ILOpCode.Ceq, VmType.F64) => OpCode.EqF64,
            (ILOpCode.Cgt, VmType.I32) => OpCode.GtI32,
            (ILOpCode.Cgt, VmType.I64) => OpCode.GtI64,
            (ILOpCode.Cgt, VmType.F32) => OpCode.GtF32,
            (ILOpCode.Cgt, VmType.F64) => OpCode.GtF64,
            (ILOpCode.Clt, VmType.I32) => OpCode.LtI32,
            (ILOpCode.Clt, VmType.I64) => OpCode.LtI64,
            (ILOpCode.Clt, VmType.F32) => OpCode.LtF32,
            (ILOpCode.Clt, VmType.F64) => OpCode.LtF64,
            // .un: unsigned for integers, "or unordered" for floats.
            (ILOpCode.Cgt_un, VmType.I32) => OpCode.GtUnI32,
            (ILOpCode.Cgt_un, VmType.I64) => OpCode.GtUnI64,
            (ILOpCode.Cgt_un, VmType.F32) => OpCode.GtUnF32,
            (ILOpCode.Cgt_un, VmType.F64) => OpCode.GtUnF64,
            (ILOpCode.Clt_un, VmType.I32) => OpCode.LtUnI32,
            (ILOpCode.Clt_un, VmType.I64) => OpCode.LtUnI64,
            (ILOpCode.Clt_un, VmType.F32) => OpCode.LtUnF32,
            (ILOpCode.Clt_un, VmType.F64) => OpCode.LtUnF64,
            _ => throw Error($"comparison '{op}' is not defined for {(isFloat ? "floats" : VmTypes.Name(type))}"),
        };
    }

    private void Convert(ILOpCode op)
    {
        VmType source = PopAny();
        switch (op)
        {
            case ILOpCode.Conv_i1: ToI32(source); _function.Emit(OpCode.I32ToI8); Push(VmType.I32); return;
            case ILOpCode.Conv_u1: ToI32(source); _function.Emit(OpCode.I32ToU8); Push(VmType.I32); return;
            case ILOpCode.Conv_i2: ToI32(source); _function.Emit(OpCode.I32ToI16); Push(VmType.I32); return;
            case ILOpCode.Conv_u2: ToI32(source); _function.Emit(OpCode.I32ToU16); Push(VmType.I32); return;
            case ILOpCode.Conv_i4: ToI32(source); Push(VmType.I32); return;

            case ILOpCode.Conv_u4:
                EmitIf(source switch
                {
                    VmType.I64 => OpCode.I64ToI32,
                    VmType.F32 => OpCode.F32ToU32,
                    VmType.F64 => OpCode.F64ToU32,
                    _ => null,
                });
                Push(VmType.I32);
                return;

            case ILOpCode.Conv_i8:
                EmitIf(source switch
                {
                    VmType.I32 => OpCode.I32ToI64,
                    VmType.F32 => OpCode.F32ToI64,
                    VmType.F64 => OpCode.F64ToI64,
                    _ => null,
                });
                Push(VmType.I64);
                return;

            case ILOpCode.Conv_u8:
                EmitIf(source switch
                {
                    VmType.I32 => OpCode.U32ToI64,
                    VmType.F32 => OpCode.F32ToU64,
                    VmType.F64 => OpCode.F64ToU64,
                    _ => null,
                });
                Push(VmType.I64);
                return;

            case ILOpCode.Conv_r4:
                EmitIf(source switch
                {
                    VmType.I32 => OpCode.I32ToF32,
                    VmType.I64 => OpCode.I64ToF32,
                    VmType.F64 => OpCode.F64ToF32,
                    _ => null,
                });
                Push(VmType.F32);
                return;

            case ILOpCode.Conv_r8:
                EmitIf(source switch
                {
                    VmType.I32 => OpCode.I32ToF64,
                    VmType.I64 => OpCode.I64ToF64,
                    VmType.F32 => OpCode.F32ToF64,
                    _ => null,
                });
                Push(VmType.F64);
                return;

            case ILOpCode.Conv_r_un:
                switch (source)
                {
                    case VmType.I32:
                        _function.Emit(OpCode.U32ToI64).Emit(OpCode.I64ToF64);
                        Push(VmType.F64);
                        return;
                    case VmType.I64:
                        _function.Emit(OpCode.U64ToF64);
                        Push(VmType.F64);
                        return;
                }

                throw Error("conv.r.un requires an integer operand");
        }

        throw Error($"conversion '{op}' is not supported");
    }

    private void ToI32(VmType source) => EmitIf(source switch
    {
        VmType.I64 => OpCode.I64ToI32,
        VmType.F32 => OpCode.F32ToI32,
        VmType.F64 => OpCode.F64ToI32,
        _ => null,
    });

    private void EmitIf(OpCode? op)
    {
        if (op is { } value)
        {
            _function.Emit(value);
        }
    }

    // ---- control flow -------------------------------------------------------------------

    private void CompareAndBranch(ILOpCode op, int target)
    {
        VmType type = PopPair();
        bool isFloat = type is VmType.F32 or VmType.F64;
        ILOpCode normalized = op switch
        {
            ILOpCode.Beq_s => ILOpCode.Beq,
            ILOpCode.Bne_un_s => ILOpCode.Bne_un,
            ILOpCode.Bge_s => ILOpCode.Bge,
            ILOpCode.Bge_un_s => ILOpCode.Bge_un,
            ILOpCode.Bgt_s => ILOpCode.Bgt,
            ILOpCode.Bgt_un_s => ILOpCode.Bgt_un,
            ILOpCode.Ble_s => ILOpCode.Ble,
            ILOpCode.Ble_un_s => ILOpCode.Ble_un,
            ILOpCode.Blt_s => ILOpCode.Blt,
            ILOpCode.Blt_un_s => ILOpCode.Blt_un,
            _ => op,
        };

        // Each IL compare-branch becomes a VM comparison plus a conditional branch.
        // Negated forms are chosen so that NaN behaves as the IL specifies.
        (ILOpCode compare, bool branchIfTrue) = normalized switch
        {
            ILOpCode.Beq => (ILOpCode.Ceq, true),
            ILOpCode.Bne_un => (ILOpCode.Ceq, false),
            ILOpCode.Bgt => (ILOpCode.Cgt, true),
            ILOpCode.Blt => (ILOpCode.Clt, true),
            ILOpCode.Bge => (isFloat ? ILOpCode.Clt_un : ILOpCode.Clt, false),
            ILOpCode.Ble => (isFloat ? ILOpCode.Cgt_un : ILOpCode.Cgt, false),
            ILOpCode.Bgt_un => (ILOpCode.Cgt_un, true),
            ILOpCode.Blt_un => (ILOpCode.Clt_un, true),
            ILOpCode.Bge_un => (isFloat ? ILOpCode.Clt : ILOpCode.Clt_un, false),
            ILOpCode.Ble_un => (isFloat ? ILOpCode.Cgt : ILOpCode.Cgt_un, false),
            _ => throw Error($"unexpected branch '{op}'"),
        };

        if (type == VmType.Ref)
        {
            if (compare != ILOpCode.Ceq)
            {
                throw Error($"ordered comparison '{op}' of references is not supported");
            }

            _function.Emit(OpCode.EqRef);
            BranchTo(target, branchIfTrue ? OpCode.BranchIfTrue : OpCode.BranchIfFalse);
            return;
        }

        _function.Emit(CompareOp(compare, type));
        BranchTo(target, branchIfTrue ? OpCode.BranchIfTrue : OpCode.BranchIfFalse);
    }

    private void Switch(int[] targets)
    {
        Pop(VmType.I32, "switch value");
        _switchTemp ??= _function.AddLocal(VmType.I32);
        _function.Emit(OpCode.StoreLocal, _switchTemp.Value);
        for (int i = 0; i < targets.Length; i++)
        {
            _function.Emit(OpCode.LoadLocal, _switchTemp.Value).ConstI32(i).Emit(OpCode.EqI32);
            BranchTo(targets[i], OpCode.BranchIfTrue);
        }
    }

    private void BranchTo(int target, OpCode branch)
    {
        if (_stackAtTarget.TryGetValue(target, out VmType[]? expected))
        {
            if (!expected.SequenceEqual(_stack))
            {
                throw Error($"evaluation stack differs at branch target IL_{target:X4}");
            }
        }
        else
        {
            _stackAtTarget[target] = _stack.ToArray();
        }

        _function.Emit(branch, _labels[target]);
    }

    private void EnterBranchTarget(int offset)
    {
        if (_stackAtTarget.TryGetValue(offset, out VmType[]? recorded))
        {
            if (_reachable && !recorded.SequenceEqual(_stack))
            {
                throw Error("evaluation stack differs between fall-through and branch");
            }

            _stack = [.. recorded];
        }
        else
        {
            // Reached only by a later backward branch (or fall-through): C# guarantees
            // an empty stack at such statement boundaries; the verifier re-checks.
            if (!_reachable)
            {
                _stack = [];
            }

            _stackAtTarget[offset] = _stack.ToArray();
        }

        _reachable = true;
    }

    private void LabelAt(int offset)
    {
        if (!_labels.ContainsKey(offset))
        {
            _labels[offset] = _function.DefineLabel();
        }
    }

    // ---- calls --------------------------------------------------------------------------

    private void Call(EntityHandle target, bool virtualCall)
    {
        (VmSignature signature, Action<FunctionBuilder> emit) = _context.ResolveCall(target, virtualCall, _generic, Where);
        for (int i = signature.Parameters.Count - 1; i >= 0; i--)
        {
            Pop(signature.Parameters[i], $"argument {i}");
        }

        emit(_function);
        if (signature.Result is { } result)
        {
            Push(result);
        }
    }

    // ---- arrays -------------------------------------------------------------------------

    private void LoadElement(VmElementAccess access)
    {
        Pop(VmType.I32, "array index");
        Pop(VmType.Ref, "array");
        _function.Emit(OpCode.LoadElement, (int)access);
        Push(VmTypes.OfAccess(access));
    }

    private void LoadIndirect(VmElementAccess access)
    {
        Pop(VmType.Ref, "address");
        _function.Emit(OpCode.LoadIndirect, (int)access);
        Push(VmTypes.OfAccess(access));
    }

    private void StoreIndirect(VmElementAccess access)
    {
        Pop(VmTypes.OfAccess(access), "value");
        Pop(VmType.Ref, "address");
        _function.Emit(OpCode.StoreIndirect, (int)access);
    }

    private void StoreElement(VmElementAccess access)
    {
        Pop(VmTypes.OfAccess(access), "array element");
        Pop(VmType.I32, "array index");
        Pop(VmType.Ref, "array");
        _function.Emit(OpCode.StoreElement, (int)access);
    }

    // ---- type stack ---------------------------------------------------------------------

    private void Push(VmType type) => _stack.Add(type);

    private VmType Peek() => _stack.Count > 0 ? _stack[^1] : throw Error("evaluation stack underflow");

    private VmType PopAny()
    {
        VmType top = Peek();
        _stack.RemoveAt(_stack.Count - 1);
        return top;
    }

    private void Pop(VmType expected, string what = "value")
    {
        VmType actual = PopAny();
        if (actual != expected)
        {
            throw Error($"{what}: expected {VmTypes.Name(expected)}, found {VmTypes.Name(actual)}");
        }
    }

    /// <summary>Pops two operands of the same type and returns that type.</summary>
    private VmType PopPair()
    {
        VmType right = PopAny();
        VmType left = PopAny();
        if (left != right)
        {
            throw Error($"operands have different types ({VmTypes.Name(left)} and {VmTypes.Name(right)})");
        }

        return left;
    }

    private string Where => $"{_name} at IL_{_offset:X4}";

    private TranslationException Error(string message) => new($"{Where}: {message}.");
}

/// <summary>Helpers for method metadata.</summary>
internal static class MethodDefinitionExtensions
{
    public static bool IsStatic(this MethodDefinition method) => (method.Attributes & MethodAttributes.Static) != 0;
}
