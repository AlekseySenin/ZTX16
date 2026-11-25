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
    private readonly ClrType _returnType;
    private VmTypeInfo[] _locals = [];
    private readonly Dictionary<int, Label> _labels = [];
    private readonly Dictionary<int, VmType[]> _stackAtTarget = [];
    private List<VmType> _stack = [];
    private bool _reachable = true;
    private int _offset;
    private int? _switchTemp;

    public MethodTranslator(TranslationContext context, MethodDefinition method, FunctionBuilder function, MethodSignature<ClrType> signature)
    {
        _context = context;
        _method = method;
        _function = function;
        _name = function.Name;
        IEnumerable<VmTypeInfo> args = signature.ParameterTypes.Select((t, i) => ClrTypeMapping.MapValue(t, $"{_name} parameter {i}"));
        _args = (method.IsStatic() ? args : args.Prepend(new VmTypeInfo(VmType.Ref, null))).ToArray(); // IL arg 0 is 'this'
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
            _locals = signature.DecodeLocalSignature(_context.TypeProvider, null)
                .Select((t, i) => ClrTypeMapping.MapValue(t, $"{_name} local {i}"))
                .ToArray();
            foreach (VmTypeInfo local in _locals)
            {
                _function.AddLocal(local.Type!.Value);
            }
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

        foreach (IlInstruction instruction in instructions)
        {
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

            TranslateInstruction(instruction);
        }

        if (_reachable)
        {
            throw Error("control falls off the end of the method");
        }
    }

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
                    (VmSignature ctor, Action<FunctionBuilder> emit) = _context.ResolveNew(MetadataTokens.EntityHandle((int)ins.Operand), Where);
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
                    (int index, VmTypeInfo type) = _context.GetInstanceField(MetadataTokens.EntityHandle((int)ins.Operand), Where);
                    Pop(VmType.Ref, "field owner");
                    _function.Emit(OpCode.LoadField, index);
                    Push(type.Type!.Value);
                    return;
                }

            case ILOpCode.Stfld:
                {
                    (int index, VmTypeInfo type) = _context.GetInstanceField(MetadataTokens.EntityHandle((int)ins.Operand), Where);
                    Pop(type.Type!.Value, "field value");
                    Pop(VmType.Ref, "field owner");
                    EmitNarrow(type);
                    _function.Emit(OpCode.StoreField, index);
                    return;
                }

            case ILOpCode.Castclass:
            case ILOpCode.Isinst:
                {
                    Pop(VmType.Ref, "cast operand");
                    if (_context.ClassForTypeTest(MetadataTokens.EntityHandle((int)ins.Operand), Where) is { } target)
                    {
                        _function.Emit(op == ILOpCode.Castclass ? OpCode.CastClass : OpCode.IsInstance, target.Builder.Index);
                    }

                    Push(VmType.Ref);
                    return;
                }

            case ILOpCode.Newarr:
                {
                    ClrType element = _context.ResolveTypeToken(MetadataTokens.EntityHandle((int)ins.Operand));
                    VmElementAccess access = ClrTypeMapping.ElementAccess(element, Where);
                    Pop(VmType.I32, "array length");
                    _function.Emit(OpCode.NewArray, (int)VmTypes.StorageOf(access));
                    Push(VmType.Ref);
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
                LoadElement(ClrTypeMapping.ElementAccess(_context.ResolveTypeToken(MetadataTokens.EntityHandle((int)ins.Operand)), Where));
                return;

            case ILOpCode.Stelem_i1: StoreElement(VmElementAccess.I8); return;
            case ILOpCode.Stelem_i2: StoreElement(VmElementAccess.I16); return;
            case ILOpCode.Stelem_i4: StoreElement(VmElementAccess.I32); return;
            case ILOpCode.Stelem_i8: StoreElement(VmElementAccess.I64); return;
            case ILOpCode.Stelem_r4: StoreElement(VmElementAccess.F32); return;
            case ILOpCode.Stelem_r8: StoreElement(VmElementAccess.F64); return;
            case ILOpCode.Stelem_ref: StoreElement(VmElementAccess.Ref); return;
            case ILOpCode.Stelem:
                StoreElement(ClrTypeMapping.ElementAccess(_context.ResolveTypeToken(MetadataTokens.EntityHandle((int)ins.Operand)), Where));
                return;

            case ILOpCode.Ldstr:
                _function.Emit(OpCode.LoadString, _context.AddString(_context.UserString((int)ins.Operand)));
                Push(VmType.Ref);
                return;

            case ILOpCode.Ldsfld:
                {
                    (int index, VmTypeInfo type) = _context.GetGlobal(MetadataTokens.EntityHandle((int)ins.Operand), Where);
                    _function.Emit(OpCode.LoadGlobal, index);
                    Push(type.Type!.Value);
                    return;
                }

            case ILOpCode.Stsfld:
                {
                    (int index, VmTypeInfo type) = _context.GetGlobal(MetadataTokens.EntityHandle((int)ins.Operand), Where);
                    Pop(type.Type!.Value);
                    EmitNarrow(type);
                    _function.Emit(OpCode.StoreGlobal, index);
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
        ILOpCode.Initobj or ILOpCode.Ldobj or ILOpCode.Stobj => " (structs are not supported yet)",
        ILOpCode.Ldelema => " (element addresses and ref locals are not supported)",
        ILOpCode.Ldflda or ILOpCode.Ldloca or ILOpCode.Ldloca_s or ILOpCode.Ldarga or ILOpCode.Ldarga_s or ILOpCode.Ldsflda => " (addresses and ref parameters are not supported)",
        ILOpCode.Throw => " (exceptions are not supported yet)",
        >= ILOpCode.Conv_ovf_i1_un and <= ILOpCode.Conv_ovf_u_un or >= ILOpCode.Conv_ovf_i1 and <= ILOpCode.Conv_ovf_u8
            or ILOpCode.Add_ovf or ILOpCode.Add_ovf_un or ILOpCode.Sub_ovf or ILOpCode.Sub_ovf_un
            or ILOpCode.Mul_ovf or ILOpCode.Mul_ovf_un => " (checked arithmetic is not supported)",
        _ => string.Empty,
    };

    // ---- variables ----------------------------------------------------------------------

    private void LoadArg(int index)
    {
        _function.Emit(OpCode.LoadLocal, CheckArg(index));
        Push(_args[index].Type!.Value);
    }

    private void StoreArg(int index)
    {
        CheckArg(index);
        Pop(_args[index].Type!.Value);
        EmitNarrow(_args[index]);
        _function.Emit(OpCode.StoreLocal, index);
    }

    private void LoadLocal(int index)
    {
        _function.Emit(OpCode.LoadLocal, CheckLocal(index));
        Push(_locals[index].Type!.Value);
    }

    private void StoreLocal(int index)
    {
        int slot = CheckLocal(index);
        Pop(_locals[index].Type!.Value);
        EmitNarrow(_locals[index]);
        _function.Emit(OpCode.StoreLocal, slot);
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
        (VmSignature signature, Action<FunctionBuilder> emit) = _context.ResolveCall(target, virtualCall, Where);
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
