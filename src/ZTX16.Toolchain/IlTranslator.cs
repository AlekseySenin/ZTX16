using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ZTX16.Abi;
using ZTX16.VM;

namespace ZTX16.Toolchain;

/// <summary>
/// Translates a compiled cartridge assembly (IL) into a ZTX16 executable module.
/// </summary>
/// <remarks>
/// Supported subset (this iteration): numeric types, <c>bool</c>, <c>char</c>, enums, classes
/// (fields, constructors, single inheritance, virtual/override, abstract), interfaces
/// (implicit and explicit implementations, default methods), one-dimensional arrays, strings
/// (literals, length, indexer, equality, concatenation) and null. Static fields and static
/// constructors are supported. Structs, generics, exceptions, delegates and the BCL beyond
/// the members listed are rejected with a <see cref="TranslationException"/>.
///
/// The cartridge is the single non-abstract class implementing <see cref="ICartridge"/>.
/// Static constructors of every type the cartridge uses run once, in type-definition order,
/// before the cartridge object is created.
/// </remarks>
public static class IlTranslator
{
    /// <summary>Translates the assembly in <paramref name="assembly"/>.</summary>
    /// <exception cref="TranslationException">The cartridge uses unsupported features or violates the contract.</exception>
    public static VmModule Translate(ReadOnlySpan<byte> assembly)
    {
        using var pe = new PEReader(ImmutableArray.Create(assembly));
        if (!pe.HasMetadata)
        {
            throw new TranslationException("Input is not a .NET assembly.");
        }

        return new TranslationContext(pe).Run();
    }

    /// <summary>Translates and serializes to executable bytes.</summary>
    public static byte[] TranslateToExecutable(ReadOnlySpan<byte> assembly) => ExecutableFormat.Write(Translate(assembly));
}

/// <summary>A class or interface registered in the module.</summary>
internal sealed class ClassInfo
{
    public required string Name { get; init; }

    public required ClassBuilder Builder { get; init; }

    public TypeDefinitionHandle? Handle { get; init; }

    public ClassInfo? Base { get; init; }

    public bool IsInterface => Builder.IsInterface;

    public Dictionary<FieldDefinitionHandle, int> Slots { get; } = [];

    public List<ClassInfo> DirectInterfaces { get; } = [];
}

/// <summary>A virtual-method or interface-method slot.</summary>
internal sealed record SelectorInfo(int Index, string Name, string SignatureKey, ClassInfo? Interface);

/// <summary>Module-wide translation state.</summary>
internal sealed class TranslationContext
{
    private const string CartridgeInterface = "ZTX16.ICartridge";

    private readonly PEReader _pe;
    private readonly ModuleBuilder _module = new();
    private readonly Dictionary<MethodDefinitionHandle, FunctionBuilder> _functions = [];
    private readonly Queue<(MethodDefinitionHandle Handle, FunctionBuilder Builder, MethodSignature<ClrType> Signature)> _pending = new();
    private readonly Dictionary<FieldDefinitionHandle, (int Index, VmTypeInfo Type)> _globals = [];
    private readonly Dictionary<FieldDefinitionHandle, (int Index, VmTypeInfo Type)> _instanceFields = [];
    private readonly HashSet<string> _functionNames = new(StringComparer.Ordinal);
    private readonly HashSet<TypeDefinitionHandle> _touchedTypes = [];
    private readonly HashSet<TypeDefinitionHandle> _scheduledConstructors = [];
    private readonly Dictionary<TypeDefinitionHandle, ClassInfo> _classes = [];
    private readonly Dictionary<string, ClassInfo> _sdkInterfaces = new(StringComparer.Ordinal);
    private readonly List<ClassInfo> _classList = [];
    private readonly Dictionary<string, SelectorInfo> _selectors = new(StringComparer.Ordinal);

    public TranslationContext(PEReader pe)
    {
        _pe = pe;
        Reader = pe.GetMetadataReader();
    }

    public MetadataReader Reader { get; }

    public ClrTypeProvider TypeProvider { get; } = new();

    public MethodBodyBlock GetBody(MethodDefinition method) => _pe.GetMethodBody(method.RelativeVirtualAddress);

    // ---- driver -------------------------------------------------------------------------

    public VmModule Run()
    {
        (ClassInfo cartridge, TypeDefinitionHandle cartridgeHandle) = FindCartridgeClass();
        TypeDefinition cartridgeType = Reader.GetTypeDefinition(cartridgeHandle);

        MethodDefinitionHandle constructor = FindMethods(cartridgeType, ".ctor")
            .FirstOrDefault(h => !Reader.GetMethodDefinition(h).IsStatic() && Reader.GetMethodDefinition(h).DecodeSignature(TypeProvider, null).ParameterTypes.Length == 0);
        if (constructor.IsNil)
        {
            throw new TranslationException($"Cartridge class '{cartridge.Name}' must have a public parameterless constructor.");
        }

        FunctionBuilder ctor = GetFunction(constructor, cartridge.Name);
        FunctionBuilder initialize = LifecycleImplementation(cartridge, VmModule.LifecycleExports.Initialize, required: true)!;
        FunctionBuilder tick = LifecycleImplementation(cartridge, VmModule.LifecycleExports.Tick, required: true)!;
        FunctionBuilder render = LifecycleImplementation(cartridge, VmModule.LifecycleExports.Render, required: true)!;
        FunctionBuilder? deinitialize = LifecycleImplementation(cartridge, VmModule.LifecycleExports.Deinitialize, required: false);

        ReachFixpoint();

        int instance = _module.AddGlobal(VmType.Ref);
        FunctionBuilder entry = _module.DefineFunction("<cartridge>.Initialize", VmSignature.Void);
        foreach (FunctionBuilder staticConstructor in StaticConstructorsInOrder())
        {
            entry.Call(staticConstructor);
        }

        entry.New(cartridge.Builder.Index, ctor.Index).Emit(OpCode.StoreGlobal, instance);
        entry.Emit(OpCode.LoadGlobal, instance).Call(initialize).Emit(OpCode.Return);
        _module.Export(VmModule.LifecycleExports.Initialize, entry);
        _module.Export(VmModule.LifecycleExports.Tick, Forward("<cartridge>.Tick", instance, tick));
        _module.Export(VmModule.LifecycleExports.Render, Forward("<cartridge>.Render", instance, render));
        if (deinitialize is not null)
        {
            _module.Export(VmModule.LifecycleExports.Deinitialize, Forward("<cartridge>.Deinitialize", instance, deinitialize));
        }

        VmModule result = _module.Build();
        try
        {
            Verifier.Verify(result);
        }
        catch (VerificationException e)
        {
            throw new TranslationException($"Internal toolchain error: generated code failed verification: {e.Message}");
        }

        return result;
    }

    private FunctionBuilder Forward(string name, int instance, FunctionBuilder target)
    {
        FunctionBuilder f = _module.DefineFunction(name, VmSignature.Void);
        f.Emit(OpCode.LoadGlobal, instance).Call(target).Emit(OpCode.Return);
        return f;
    }

    /// <summary>
    /// Translates until nothing new is reachable: pending methods, static constructors of touched
    /// types, and every implementation the dispatch tables need.
    /// </summary>
    private void ReachFixpoint()
    {
        bool changed;
        do
        {
            while (_pending.TryDequeue(out var item))
            {
                new MethodTranslator(this, Reader.GetMethodDefinition(item.Handle), item.Builder, item.Signature).Translate();
            }

            changed = ScheduleStaticConstructors() | FillDispatchTables();
        }
        while (changed || _pending.Count > 0);
    }

    private bool ScheduleStaticConstructors()
    {
        bool added = false;
        foreach (TypeDefinitionHandle type in _touchedTypes.ToArray())
        {
            if (_scheduledConstructors.Add(type) && FindMethods(Reader.GetTypeDefinition(type), ".cctor").FirstOrDefault() is { IsNil: false } cctor)
            {
                GetFunction(cctor, $"{TypeName(type)} static constructor");
                added = true;
            }
        }

        return added;
    }

    private IEnumerable<FunctionBuilder> StaticConstructorsInOrder() =>
        Reader.TypeDefinitions
            .Where(_scheduledConstructors.Contains)
            .Select(t => FindMethods(Reader.GetTypeDefinition(t), ".cctor").FirstOrDefault())
            .Where(h => !h.IsNil)
            .Select(h => _functions[h]);

    // ---- classes ------------------------------------------------------------------------

    public ClassInfo GetClass(TypeDefinitionHandle handle, string where)
    {
        if (_classes.TryGetValue(handle, out ClassInfo? existing))
        {
            return existing;
        }

        if (TypeProvider.GetTypeFromDefinition(Reader, handle, 0) is not ClassClrType classType)
        {
            ClrType t = TypeProvider.GetTypeFromDefinition(Reader, handle, 0);
            throw new TranslationException($"{where}: type '{t}' cannot be used as a class{(t is UnsupportedClrType u ? $" ({u.Reason})" : string.Empty)}.");
        }

        TypeDefinition definition = Reader.GetTypeDefinition(handle);
        ClassInfo? baseClass = null;
        if (!classType.IsInterface)
        {
            switch (definition.BaseType.Kind)
            {
                case HandleKind.TypeDefinition:
                    baseClass = GetClass((TypeDefinitionHandle)definition.BaseType, where);
                    break;
                case HandleKind.TypeReference when ClrTypeProvider.BaseTypeName(Reader, definition) == "System.Object":
                    break;
                default:
                    throw new TranslationException(
                        $"{where}: class '{classType.FullName}' derives from '{ClrTypeProvider.BaseTypeName(Reader, definition)}'; cartridge classes may only derive from object or other cartridge classes.");
            }
        }

        var fields = new List<VmType>(baseClass?.Builder.Fields ?? []);
        var slots = new Dictionary<FieldDefinitionHandle, int>();
        foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
        {
            FieldDefinition field = Reader.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) != 0)
            {
                continue;
            }

            string fieldName = $"{classType.FullName}.{Reader.GetString(field.Name)}";
            slots[fieldHandle] = fields.Count;
            fields.Add(ClrTypeMapping.MapValue(field.DecodeSignature(TypeProvider, null), $"field '{fieldName}'").Type!.Value);
        }

        var info = new ClassInfo
        {
            Name = TypeName(handle),
            Handle = handle,
            Base = baseClass,
            Builder = _module.DefineClass(TypeName(handle), baseClass?.Builder.Index ?? VmClass.NoBase, classType.IsInterface, fields),
        };
        foreach ((FieldDefinitionHandle fieldHandle, int slot) in slots)
        {
            info.Slots[fieldHandle] = slot;
        }

        _classes[handle] = info;
        _classList.Add(info);
        _touchedTypes.Add(handle);

        foreach (InterfaceImplementationHandle implHandle in definition.GetInterfaceImplementations())
        {
            EntityHandle iface = Reader.GetInterfaceImplementation(implHandle).Interface;
            info.DirectInterfaces.Add(iface.Kind switch
            {
                HandleKind.TypeDefinition => GetClass((TypeDefinitionHandle)iface, where),
                HandleKind.TypeReference when TypeProvider.GetTypeFromReference(Reader, (TypeReferenceHandle)iface, 0) is SdkInterfaceClrType sdk => GetSdkInterface(sdk.FullName),
                _ => throw new TranslationException($"{where}: class '{classType.FullName}' implements '{TypeTokenName(iface)}', which is not supported (only cartridge and ZTX16 SDK interfaces)."),
            });
        }

        return info;
    }

    private ClassInfo GetSdkInterface(string fullName)
    {
        if (!_sdkInterfaces.TryGetValue(fullName, out ClassInfo? info))
        {
            info = new ClassInfo { Name = fullName, Builder = _module.DefineClass(fullName, VmClass.NoBase, isInterface: true, []) };
            _sdkInterfaces[fullName] = info;
            _classList.Add(info);
        }

        return info;
    }

    /// <summary>Returns the class to use for a cast or type test, or null if the cast is a no-op (to object).</summary>
    public ClassInfo? ClassForTypeTest(EntityHandle token, string where) => ResolveTypeToken(token) switch
    {
        ClassClrType c => GetClass(c.Handle, where),
        SdkInterfaceClrType sdk => GetSdkInterface(sdk.FullName),
        ObjectClrType => null,
        var other => throw new TranslationException($"{where}: casts and type tests to '{other}' are not supported yet."),
    };

    public ClrType ResolveTypeToken(EntityHandle token) => token.Kind switch
    {
        HandleKind.TypeDefinition => TypeProvider.GetTypeFromDefinition(Reader, (TypeDefinitionHandle)token, 0),
        HandleKind.TypeReference => TypeProvider.GetTypeFromReference(Reader, (TypeReferenceHandle)token, 0),
        HandleKind.TypeSpecification => TypeProvider.GetTypeFromSpecification(Reader, null, (TypeSpecificationHandle)token, 0),
        _ => new UnsupportedClrType(token.Kind.ToString(), "unsupported type token"),
    };

    private bool FillDispatchTables()
    {
        bool changed = false;
        for (int c = 0; c < _classList.Count; c++)
        {
            ClassInfo cls = _classList[c];
            HashSet<ClassInfo> interfaces = InterfaceClosure(cls);
            foreach (ClassInfo iface in interfaces)
            {
                changed |= cls.Builder.Interfaces.Add(iface.Builder.Index);
            }

            if (cls.IsInterface || cls.Handle is null)
            {
                continue;
            }

            foreach (SelectorInfo selector in _selectors.Values.ToArray())
            {
                if (cls.Builder.Methods.ContainsKey(selector.Index))
                {
                    continue;
                }

                MethodDefinitionHandle? impl = selector.Interface is null
                    ? FindVirtualImplementation(cls, selector.Name, selector.SignatureKey)
                    : interfaces.Contains(selector.Interface)
                        ? FindInterfaceImplementation(cls, selector.Interface, selector.Name, selector.SignatureKey)
                        : null;

                if (impl is { } method)
                {
                    cls.Builder.Methods[selector.Index] = GetFunction(method, $"{cls.Name} dispatch").Index;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static HashSet<ClassInfo> InterfaceClosure(ClassInfo cls)
    {
        var result = new HashSet<ClassInfo>();
        var work = new Stack<ClassInfo>();
        for (ClassInfo? c = cls; c is not null; c = c.Base)
        {
            foreach (ClassInfo i in c.DirectInterfaces)
            {
                work.Push(i);
            }
        }

        while (work.TryPop(out ClassInfo? i))
        {
            if (result.Add(i))
            {
                foreach (ClassInfo parent in i.DirectInterfaces)
                {
                    work.Push(parent);
                }
            }
        }

        return result;
    }

    private MethodDefinitionHandle? FindVirtualImplementation(ClassInfo cls, string name, string signatureKey)
    {
        for (ClassInfo? c = cls; c?.Handle is { } handle; c = c.Base)
        {
            foreach (MethodDefinitionHandle m in FindMethods(Reader.GetTypeDefinition(handle), name))
            {
                MethodDefinition method = Reader.GetMethodDefinition(m);
                if ((method.Attributes & MethodAttributes.Virtual) != 0 && SignatureKey(method) == signatureKey)
                {
                    return (method.Attributes & MethodAttributes.Abstract) != 0 ? null : m;
                }
            }
        }

        return null;
    }

    private MethodDefinitionHandle? FindInterfaceImplementation(ClassInfo cls, ClassInfo iface, string name, string signatureKey)
    {
        // 1. Explicit implementations (MethodImpl rows), most derived first.
        for (ClassInfo? c = cls; c?.Handle is { } handle; c = c.Base)
        {
            foreach (MethodImplementationHandle implHandle in Reader.GetTypeDefinition(handle).GetMethodImplementations())
            {
                MethodImplementation impl = Reader.GetMethodImplementation(implHandle);
                if (DescribeMethod(impl.MethodDeclaration) is var (declaringType, declName, declKey)
                    && declaringType == iface.Name && declName == name && declKey == signatureKey
                    && impl.MethodBody.Kind == HandleKind.MethodDefinition)
                {
                    return (MethodDefinitionHandle)impl.MethodBody;
                }
            }
        }

        // 2. Implicit implementations: public instance methods with the same name and signature.
        for (ClassInfo? c = cls; c?.Handle is { } handle; c = c.Base)
        {
            foreach (MethodDefinitionHandle m in FindMethods(Reader.GetTypeDefinition(handle), name))
            {
                MethodDefinition method = Reader.GetMethodDefinition(m);
                if (!method.IsStatic() && (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public
                    && (method.Attributes & MethodAttributes.Abstract) == 0 && SignatureKey(method) == signatureKey)
                {
                    return m;
                }
            }
        }

        // 3. Default interface methods declared in cartridge interfaces.
        foreach (ClassInfo candidate in InterfaceClosure(cls).Where(i => i.Handle is not null))
        {
            foreach (MethodDefinitionHandle m in FindMethods(Reader.GetTypeDefinition(candidate.Handle!.Value), name))
            {
                MethodDefinition method = Reader.GetMethodDefinition(m);
                if (method.RelativeVirtualAddress != 0 && !method.IsStatic() && SignatureKey(method) == signatureKey)
                {
                    return m;
                }
            }
        }

        return null;
    }

    private (ClassInfo Class, TypeDefinitionHandle Handle) FindCartridgeClass()
    {
        var candidates = new List<TypeDefinitionHandle>();
        foreach (TypeDefinitionHandle handle in Reader.TypeDefinitions)
        {
            TypeDefinition type = Reader.GetTypeDefinition(handle);
            if ((type.Attributes & (TypeAttributes.Interface | TypeAttributes.Abstract)) == 0 && ImplementsCartridge(handle))
            {
                candidates.Add(handle);
            }
        }

        return candidates.Count switch
        {
            0 => throw new TranslationException($"No class implements {CartridgeInterface}. Declare 'public sealed class Game : ICartridge'."),
            1 => (GetClass(candidates[0], "cartridge class"), candidates[0]),
            _ => throw new TranslationException(
                $"More than one class implements {CartridgeInterface}: {string.Join(", ", candidates.Select(TypeName))}."),
        };
    }

    private bool ImplementsCartridge(TypeDefinitionHandle handle)
    {
        TypeDefinition type = Reader.GetTypeDefinition(handle);
        foreach (InterfaceImplementationHandle implHandle in type.GetInterfaceImplementations())
        {
            if (TypeTokenName(Reader.GetInterfaceImplementation(implHandle).Interface) == CartridgeInterface)
            {
                return true;
            }
        }

        return !type.BaseType.IsNil && type.BaseType.Kind == HandleKind.TypeDefinition && ImplementsCartridge((TypeDefinitionHandle)type.BaseType);
    }

    private FunctionBuilder? LifecycleImplementation(ClassInfo cartridge, string name, bool required)
    {
        ClassInfo iface = GetSdkInterface(CartridgeInterface);
        string key = $"{name}():Void";
        if (FindInterfaceImplementation(cartridge, iface, name, key) is { } impl)
        {
            return GetFunction(impl, $"{cartridge.Name}.{name}");
        }

        return required
            ? throw new TranslationException($"Cartridge class '{cartridge.Name}' must implement ICartridge.{name}().")
            : null;
    }

    // ---- members ------------------------------------------------------------------------

    /// <summary>Resolves a call target to its full VM signature (receiver first for instance calls) and the emitter.</summary>
    public (VmSignature Signature, Action<FunctionBuilder> Emit) ResolveCall(EntityHandle target, bool virtualCall, string where)
    {
        switch (target.Kind)
        {
            case HandleKind.MethodDefinition:
                {
                    var handle = (MethodDefinitionHandle)target;
                    MethodDefinition method = Reader.GetMethodDefinition(handle);
                    if (method.IsStatic())
                    {
                        FunctionBuilder callee = GetFunction(handle, where);
                        return (callee.Signature, f => f.Call(callee));
                    }

                    ClassInfo owner = GetClass(method.GetDeclaringType(), where);
                    bool isVirtual = (method.Attributes & MethodAttributes.Virtual) != 0 && (method.Attributes & MethodAttributes.Final) == 0;
                    if (virtualCall && (isVirtual || owner.IsInterface))
                    {
                        MethodSignature<ClrType> signature = method.DecodeSignature(TypeProvider, null);
                        string name = Reader.GetString(method.Name);
                        SelectorInfo selector = GetSelector(name, signature, owner.IsInterface ? owner : null, where);
                        return (ClrTypeMapping.ToVmSignature(signature, where, hasThis: true), f => f.Emit(OpCode.CallVirtual, selector.Index));
                    }

                    FunctionBuilder direct = GetFunction(handle, where);
                    return (direct.Signature, f => f.Call(direct));
                }

            case HandleKind.MemberReference:
                return ResolveMemberReference((MemberReferenceHandle)target, virtualCall, where);

            case HandleKind.MethodSpecification:
                throw new TranslationException($"{where}: generic methods are not supported yet.");

            default:
                throw new TranslationException($"{where}: unsupported call target ({target.Kind}).");
        }
    }

    private (VmSignature, Action<FunctionBuilder>) ResolveMemberReference(MemberReferenceHandle handle, bool virtualCall, string where)
    {
        MemberReference reference = Reader.GetMemberReference(handle);
        string typeName = ParentTypeName(reference.Parent);
        string methodName = Reader.GetString(reference.Name);
        MethodSignature<ClrType> signature = reference.DecodeMethodSignature(TypeProvider, null);
        string display = $"{typeName}.{methodName}";

        if (SdkAbi.FindByMethod(typeName, methodName) is { } syscall)
        {
            VmSignature referenced = ClrTypeMapping.ToVmSignature(signature, display);
            if (!referenced.Matches(syscall.Signature))
            {
                throw new TranslationException($"{where}: '{display}' was compiled against a different SDK version ({referenced} vs {syscall.Signature}).");
            }

            int import = _module.Import(syscall.Name, syscall.Signature);
            return (syscall.Signature, f => f.Emit(OpCode.Syscall, import));
        }

        VmType R = VmType.Ref, I = VmType.I32;
        switch (typeName, methodName, signature.ParameterTypes.Length)
        {
            case ("System.Object", ".ctor", 0):
                return (VmSignature.Of(null, R), f => f.Emit(OpCode.Pop));
            case ("System.String", "get_Length", 0):
                return (VmSignature.Of(I, R), f => f.Emit(OpCode.StringLength));
            case ("System.String", "get_Chars", 1):
                return (VmSignature.Of(I, R, I), f => f.Emit(OpCode.StringCharAt));
            case ("System.String", "op_Equality", 2):
            case ("System.String", "Equals", 2) when !signature.Header.IsInstance:
                return (VmSignature.Of(I, R, R), f => f.Emit(OpCode.StringEquals));
            case ("System.String", "op_Inequality", 2):
                return (VmSignature.Of(I, R, R), f => f.Emit(OpCode.StringEquals).ConstI32(0).Emit(OpCode.EqI32));
            case ("System.String", "Concat", 2) when signature.ParameterTypes.All(p => p is StringClrType):
                return (VmSignature.Of(R, R, R), f => f.Emit(OpCode.StringConcat));
            case ("System.String", "Concat", 3) when signature.ParameterTypes.All(p => p is StringClrType):
                return (VmSignature.Of(R, R, R, R), f => f.Emit(OpCode.StringConcat).Emit(OpCode.StringConcat));
            case ("System.String", "Concat", 4) when signature.ParameterTypes.All(p => p is StringClrType):
                return (VmSignature.Of(R, R, R, R, R), f => f.Emit(OpCode.StringConcat).Emit(OpCode.StringConcat).Emit(OpCode.StringConcat));
            case ("System.String", "IsNullOrEmpty", 1):
                return (VmSignature.Of(I, R), f => EmitIsNullOrEmpty(f));
        }

        if (_sdkInterfaces.ContainsKey(typeName) || SdkAbi.SdkAssembly.GetType(typeName) is { IsInterface: true })
        {
            if (!virtualCall)
            {
                throw new TranslationException($"{where}: non-virtual call to interface method '{display}'.");
            }

            SelectorInfo selector = GetSelector(methodName, signature, GetSdkInterface(typeName), where);
            return (ClrTypeMapping.ToVmSignature(signature, display, hasThis: true), f => f.Emit(OpCode.CallVirtual, selector.Index));
        }

        throw new TranslationException(
            $"{where}: call to '{display}' is not supported. Cartridges may call their own methods, ZTX16 SDK APIs and a small set of string members.");
    }

    // string.IsNullOrEmpty(s): s == null || s.Length == 0, without branches: (s == null) | (len(s ?? "") == 0)
    private void EmitIsNullOrEmpty(FunctionBuilder f)
    {
        f.Emit(OpCode.Dup).Emit(OpCode.ConstNull).Emit(OpCode.EqRef);       // s, isNull
        Label done = f.DefineLabel();
        Label notNull = f.DefineLabel();
        f.Emit(OpCode.BranchIfFalse, notNull);                               // s
        f.Emit(OpCode.Pop).ConstI32(1).Emit(OpCode.Branch, done);
        f.Mark(notNull).Emit(OpCode.StringLength).ConstI32(0).Emit(OpCode.EqI32);
        f.Mark(done);
    }

    /// <summary>Resolves a constructor used by <c>newobj</c>.</summary>
    public (VmSignature Constructor, Action<FunctionBuilder> Emit) ResolveNew(EntityHandle target, string where)
    {
        if (target.Kind != HandleKind.MethodDefinition)
        {
            string name = target.Kind == HandleKind.MemberReference
                ? ParentTypeName(Reader.GetMemberReference((MemberReferenceHandle)target).Parent)
                : target.Kind.ToString();
            throw new TranslationException($"{where}: creating instances of '{name}' is not supported; only cartridge classes can be instantiated.");
        }

        var handle = (MethodDefinitionHandle)target;
        MethodDefinition method = Reader.GetMethodDefinition(handle);
        ClassInfo cls = GetClass(method.GetDeclaringType(), where);
        if ((Reader.GetTypeDefinition(method.GetDeclaringType()).Attributes & TypeAttributes.Abstract) != 0)
        {
            throw new TranslationException($"{where}: cannot instantiate abstract class '{cls.Name}'.");
        }

        FunctionBuilder ctor = GetFunction(handle, where);
        return (ctor.Signature, f => f.New(cls.Builder.Index, ctor.Index));
    }

    public (int Index, VmTypeInfo Type) GetInstanceField(EntityHandle handle, string where)
    {
        if (handle.Kind != HandleKind.FieldDefinition)
        {
            throw new TranslationException($"{where}: only fields declared in the cartridge can be used.");
        }

        var fieldHandle = (FieldDefinitionHandle)handle;
        if (_instanceFields.TryGetValue(fieldHandle, out var existing))
        {
            return existing;
        }

        FieldDefinition field = Reader.GetFieldDefinition(fieldHandle);
        ClassInfo owner = GetClass(field.GetDeclaringType(), where);
        if (!owner.Slots.TryGetValue(fieldHandle, out int slot))
        {
            throw new TranslationException($"{where}: '{owner.Name}.{Reader.GetString(field.Name)}' is not an instance field.");
        }

        VmTypeInfo type = ClrTypeMapping.MapValue(field.DecodeSignature(TypeProvider, null), $"field '{owner.Name}.{Reader.GetString(field.Name)}'");
        var result = (_module.AddField(owner.Builder.Index, slot), type);
        _instanceFields[fieldHandle] = result;
        return result;
    }

    public (int Index, VmTypeInfo Type) GetGlobal(EntityHandle handle, string where)
    {
        if (handle.Kind != HandleKind.FieldDefinition)
        {
            throw new TranslationException($"{where}: only static fields declared in the cartridge can be used.");
        }

        var fieldHandle = (FieldDefinitionHandle)handle;
        if (_globals.TryGetValue(fieldHandle, out var existing))
        {
            return existing;
        }

        FieldDefinition field = Reader.GetFieldDefinition(fieldHandle);
        string name = $"{TypeName(field.GetDeclaringType())}.{Reader.GetString(field.Name)}";
        if ((field.Attributes & FieldAttributes.Static) == 0)
        {
            throw new TranslationException($"{where}: '{name}' is an instance field.");
        }

        VmTypeInfo type = ClrTypeMapping.MapValue(field.DecodeSignature(TypeProvider, null), $"field '{name}'");
        int index = _module.AddGlobal(type.Type!.Value);
        _touchedTypes.Add(field.GetDeclaringType());
        _globals[fieldHandle] = (index, type);
        return (index, type);
    }

    public int AddString(string value) => _module.AddString(value);

    private SelectorInfo GetSelector(string name, MethodSignature<ClrType> signature, ClassInfo? iface, string where)
    {
        string signatureKey = ClrTypeMapping.SignatureKey(name, signature);
        string key = iface is null ? $"V:{signatureKey}" : $"I:{iface.Name}::{signatureKey}";
        if (!_selectors.TryGetValue(key, out SelectorInfo? selector))
        {
            int index = _module.AddSelector(key, ClrTypeMapping.ToVmSignature(signature, where, hasThis: true));
            selector = new SelectorInfo(index, name, signatureKey, iface);
            _selectors[key] = selector;
        }

        return selector;
    }

    private FunctionBuilder GetFunction(MethodDefinitionHandle handle, string where)
    {
        if (_functions.TryGetValue(handle, out FunctionBuilder? existing))
        {
            return existing;
        }

        MethodDefinition method = Reader.GetMethodDefinition(handle);
        string name = $"{TypeName(method.GetDeclaringType())}.{Reader.GetString(method.Name)}";
        if (method.GetGenericParameters().Count > 0)
        {
            throw new TranslationException($"{where}: generic method '{name}' is not supported yet.");
        }

        if ((method.Attributes & MethodAttributes.Abstract) != 0)
        {
            throw new TranslationException($"{where}: abstract method '{name}' has no body.");
        }

        bool hasThis = !method.IsStatic();
        if (hasThis)
        {
            GetClass(method.GetDeclaringType(), where);
        }

        MethodSignature<ClrType> signature = method.DecodeSignature(TypeProvider, null);
        FunctionBuilder builder = _module.DefineFunction(UniqueName(name), ClrTypeMapping.ToVmSignature(signature, name, hasThis));
        _functions[handle] = builder;
        _touchedTypes.Add(method.GetDeclaringType());
        _pending.Enqueue((handle, builder, signature));
        return builder;
    }

    // ---- metadata helpers ---------------------------------------------------------------

    private string SignatureKey(MethodDefinition method) =>
        ClrTypeMapping.SignatureKey(Reader.GetString(method.Name), method.DecodeSignature(TypeProvider, null));

    private (string DeclaringType, string Name, string Key)? DescribeMethod(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                {
                    MethodDefinition method = Reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    string name = Reader.GetString(method.Name);
                    return (TypeName(method.GetDeclaringType()), name, SignatureKey(method));
                }

            case HandleKind.MemberReference:
                {
                    MemberReference reference = Reader.GetMemberReference((MemberReferenceHandle)handle);
                    string name = Reader.GetString(reference.Name);
                    return (ParentTypeName(reference.Parent), name,
                        ClrTypeMapping.SignatureKey(name, reference.DecodeMethodSignature(TypeProvider, null)));
                }

            default:
                return null;
        }
    }

    private IEnumerable<MethodDefinitionHandle> FindMethods(TypeDefinition type, string name) =>
        type.GetMethods().Where(h => Reader.GetString(Reader.GetMethodDefinition(h).Name) == name);

    private string TypeTokenName(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => ParentTypeName(handle),
        HandleKind.TypeDefinition => TypeName((TypeDefinitionHandle)handle),
        _ => ResolveTypeToken(handle).ToString(),
    };

    private string ParentTypeName(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => TypeReferenceName((TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => TypeName((TypeDefinitionHandle)parent),
        HandleKind.TypeSpecification => ResolveTypeToken(parent).ToString(),
        _ => "<unsupported parent>",
    };

    private string TypeReferenceName(TypeReferenceHandle handle)
    {
        TypeReference reference = Reader.GetTypeReference(handle);
        return ClrTypeProvider.FullName(Reader, reference.Namespace, reference.Name);
    }

    private string TypeName(TypeDefinitionHandle handle)
    {
        TypeDefinition type = Reader.GetTypeDefinition(handle);
        string name = ClrTypeProvider.FullName(Reader, type.Namespace, type.Name);
        TypeDefinitionHandle declaring = type.GetDeclaringType();
        return declaring.IsNil ? name : $"{TypeName(declaring)}+{name}";
    }

    private string UniqueName(string name)
    {
        string candidate = name;
        for (int i = 2; !_functionNames.Add(candidate); i++)
        {
            candidate = $"{name}#{i}";
        }

        return candidate;
    }

    public string UserString(int token) => Reader.GetUserString((UserStringHandle)MetadataTokens.Handle(token));
}
