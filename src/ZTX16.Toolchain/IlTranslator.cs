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
/// Supported subset: numeric types, <c>bool</c>, <c>char</c>, enums, classes (fields,
/// constructors, single inheritance, virtual/override/abstract), interfaces (implicit, explicit
/// and default implementations), generic classes, interfaces and methods (monomorphized per
/// set of type arguments), one-dimensional arrays, strings (literals, length, indexer,
/// equality, concatenation) and null. Static fields and static constructors are supported.
/// Structs, delegates, exceptions and the BCL beyond the members listed are rejected with a
/// <see cref="TranslationException"/>.
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

/// <summary>A class or interface registered in the module (one per generic instantiation).</summary>
internal sealed class ClassInfo
{
    public required string Name { get; init; }

    public required ClassBuilder Builder { get; init; }

    /// <summary>The cartridge type, or null for an SDK interface.</summary>
    public ClassClrType? Type { get; init; }

    public TypeDefinitionHandle? Handle => Type?.Handle;

    public GenericContext Context => Type?.Context ?? GenericContext.Empty;

    public ClassInfo? Base { get; init; }

    public bool IsInterface => Builder.IsInterface;

    public Dictionary<FieldDefinitionHandle, int> Slots { get; } = [];

    public List<ClassInfo> DirectInterfaces { get; } = [];
}

/// <summary>A resolved field: VM index (field or global), storage type and CLR type.</summary>
internal sealed record FieldRef(int Index, VmTypeInfo Type, ClrType Clr)
{
    /// <summary>Deconstructs into index and storage type.</summary>
    public void Deconstruct(out int index, out VmTypeInfo type)
    {
        index = Index;
        type = Type;
    }
}

/// <summary>A virtual-method or interface-method slot.</summary>
internal sealed record SelectorInfo(int Index, string Name, string SignatureKey, ClassInfo? Interface);

/// <summary>A resolved reference to a method body in a specific generic context.</summary>
internal readonly record struct MethodTarget(MethodDefinitionHandle Handle, GenericContext Context);

/// <summary>Module-wide translation state.</summary>
internal sealed class TranslationContext
{
    private const string CartridgeInterface = "ZTX16.ICartridge";
    private const string IntrinsicsType = "ZTX16.Core.Intrinsics";

    private readonly PEReader _pe;
    private readonly ModuleBuilder _module = new();
    private readonly Dictionary<string, FunctionBuilder> _functions = new(StringComparer.Ordinal);
    private readonly Queue<(MethodDefinitionHandle Handle, GenericContext Context, FunctionBuilder Builder)> _pending = new();
    private readonly Dictionary<string, FieldRef> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FieldRef> _instanceFields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _functionNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (TypeDefinitionHandle Handle, GenericContext Context)> _touchedTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionBuilder?> _staticConstructors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClassInfo> _classes = new(StringComparer.Ordinal);
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
        ClassInfo cartridge = FindCartridgeClass();
        TypeDefinition cartridgeType = Reader.GetTypeDefinition(cartridge.Handle!.Value);

        MethodDefinitionHandle constructor = FindMethods(cartridgeType, ".ctor")
            .FirstOrDefault(h => !Reader.GetMethodDefinition(h).IsStatic() && Reader.GetMethodDefinition(h).DecodeSignature(TypeProvider, null).ParameterTypes.Length == 0);
        if (constructor.IsNil)
        {
            throw new TranslationException($"Cartridge class '{cartridge.Name}' must have a public parameterless constructor.");
        }

        FunctionBuilder ctor = GetFunction(new MethodTarget(constructor, GenericContext.Empty), cartridge.Name);
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
                new MethodTranslator(this, Reader.GetMethodDefinition(item.Handle), item.Context, item.Builder).Translate();
            }

            changed = ScheduleStaticConstructors() | FillDispatchTables();
        }
        while (changed || _pending.Count > 0);
    }

    /// <summary>Records that a type (instantiation) is used, so its static constructor runs.</summary>
    private void Touch(TypeDefinitionHandle handle, GenericContext context) =>
        _touchedTypes.TryAdd(TypeKey(handle, context), (handle, context with { MethodArgs = [] }));

    private string TypeKey(TypeDefinitionHandle handle, GenericContext context) =>
        $"{ClrTypeProvider.DefinitionName(Reader, handle)}{(context.TypeArgs.IsDefaultOrEmpty ? "" : $"<{string.Join(",", context.TypeArgs)}>")}";

    private bool ScheduleStaticConstructors()
    {
        bool added = false;
        foreach ((string key, (TypeDefinitionHandle handle, GenericContext context)) in _touchedTypes.ToArray())
        {
            if (_staticConstructors.ContainsKey(key))
            {
                continue;
            }

            MethodDefinitionHandle cctor = FindMethods(Reader.GetTypeDefinition(handle), ".cctor").FirstOrDefault();
            _staticConstructors[key] = cctor.IsNil ? null : GetFunction(new MethodTarget(cctor, context), $"{key} static constructor");
            added |= !cctor.IsNil;
        }

        return added;
    }

    private IEnumerable<FunctionBuilder> StaticConstructorsInOrder() =>
        _touchedTypes
            .OrderBy(t => MetadataTokens.GetRowNumber(t.Value.Handle))
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => _staticConstructors.GetValueOrDefault(t.Key))
            .OfType<FunctionBuilder>();

    // ---- classes ------------------------------------------------------------------------

    public ClassInfo GetClass(TypeDefinitionHandle handle, string where) => GetClass(ClassTypeOf(handle, where), where);

    public ClassInfo GetClass(ClassClrType type, string where)
    {
        if (_classes.TryGetValue(type.Display, out ClassInfo? existing))
        {
            return existing;
        }

        TypeDefinition definition = Reader.GetTypeDefinition(type.Handle);
        int genericParameters = definition.GetGenericParameters().Count;
        if (genericParameters != (type.TypeArgs.IsDefault ? 0 : type.TypeArgs.Length))
        {
            throw new TranslationException($"{where}: generic type '{type.FullName}' used without type arguments.");
        }

        GenericContext context = type.Context;
        ClassInfo? baseClass = null;
        if (!type.IsInterface && !type.IsValueType)
        {
            ClrType? baseType = definition.BaseType.IsNil ? null : ResolveTypeToken(definition.BaseType, context);
            baseClass = baseType switch
            {
                ClassClrType c => GetClass(c, where),
                ObjectClrType => null,
                _ => throw new TranslationException(
                    $"{where}: class '{type.Display}' derives from '{baseType}'; cartridge classes may only derive from object or other cartridge classes."),
            };
        }

        var fields = new List<VmType>(baseClass?.Builder.Fields ?? []);
        var slots = new Dictionary<FieldDefinitionHandle, int>();
        var structSlots = new Dictionary<int, ClassClrType>();
        foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
        {
            FieldDefinition field = Reader.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) != 0)
            {
                continue;
            }

            ClrType fieldType = field.DecodeSignature(TypeProvider, context);
            if (fieldType is ByRefClrType)
            {
                throw new TranslationException($"{where}: ref fields are not supported.");
            }

            slots[fieldHandle] = fields.Count;
            if (ClrTypeMapping.IsStruct(fieldType))
            {
                structSlots[fields.Count] = (ClassClrType)fieldType;
            }

            fields.Add(ClrTypeMapping.MapValue(fieldType, $"field '{type.Display}.{Reader.GetString(field.Name)}'").Type!.Value);
        }

        var info = new ClassInfo
        {
            Name = type.Display,
            Type = type,
            Base = baseClass,
            Builder = _module.DefineClass(type.Display, baseClass?.Builder.Index ?? VmClass.NoBase, type.IsInterface, fields),
        };
        info.Builder.IsValueType = type.IsValueType;
        foreach ((FieldDefinitionHandle fieldHandle, int slot) in slots)
        {
            info.Slots[fieldHandle] = slot;
        }

        _classes[type.Display] = info;
        _classList.Add(info);
        Touch(type.Handle, context);

        if (type.IsValueType)
        {
            // Nested struct values are deep-copied along with the struct that contains them.
            foreach ((int slot, ClassClrType nested) in structSlots)
            {
                info.Builder.ValueFields[slot] = GetClass(nested, where).Builder.Index;
            }
        }

        foreach (InterfaceImplementationHandle implHandle in definition.GetInterfaceImplementations())
        {
            ClrType iface = ResolveTypeToken(Reader.GetInterfaceImplementation(implHandle).Interface, context);
            if (iface is UnsupportedClrType { Description: "System.Collections.IEnumerable" })
            {
                // Marker that C# requires for collection initializers; its method is never called.
                continue;
            }

            info.DirectInterfaces.Add(iface switch
            {
                ClassClrType c => GetClass(c, where),
                SdkInterfaceClrType sdk => GetSdkInterface(sdk.FullName),
                _ => throw new TranslationException(
                    $"{where}: class '{type.Display}' implements '{iface}', which is not supported (only cartridge and ZTX16 SDK interfaces)."),
            });
        }

        return info;
    }

    private ClassClrType ClassTypeOf(TypeDefinitionHandle handle, string where)
    {
        ClrType t = TypeProvider.GetTypeFromDefinition(Reader, handle, 0);
        return t as ClassClrType
            ?? throw new TranslationException($"{where}: type '{t}' cannot be used as a class{(t is UnsupportedClrType u ? $" ({u.Reason})" : string.Empty)}.");
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
    public ClassInfo? ClassForTypeTest(EntityHandle token, GenericContext context, string where) => ResolveTypeToken(token, context) switch
    {
        ClassClrType c => GetClass(c, where),
        SdkInterfaceClrType sdk => GetSdkInterface(sdk.FullName),
        ObjectClrType => null,
        var other => throw new TranslationException($"{where}: casts and type tests to '{other}' are not supported yet."),
    };

    public ClrType ResolveTypeToken(EntityHandle token, GenericContext context) => token.Kind switch
    {
        HandleKind.TypeDefinition => TypeProvider.GetTypeFromDefinition(Reader, (TypeDefinitionHandle)token, 0),
        HandleKind.TypeReference => TypeProvider.GetTypeFromReference(Reader, (TypeReferenceHandle)token, 0),
        HandleKind.TypeSpecification => TypeProvider.GetTypeFromSpecification(Reader, context, (TypeSpecificationHandle)token, 0),
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

            if (cls.IsInterface || cls.Type is null)
            {
                continue;
            }

            foreach (SelectorInfo selector in _selectors.Values.ToArray())
            {
                if (cls.Builder.Methods.ContainsKey(selector.Index))
                {
                    continue;
                }

                MethodTarget? impl = selector.Interface is null
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

    private MethodTarget? FindVirtualImplementation(ClassInfo cls, string name, string signatureKey)
    {
        for (ClassInfo? c = cls; c?.Handle is { } handle; c = c.Base)
        {
            foreach (MethodDefinitionHandle m in FindMethods(Reader.GetTypeDefinition(handle), name))
            {
                MethodDefinition method = Reader.GetMethodDefinition(m);
                if ((method.Attributes & MethodAttributes.Virtual) != 0 && SignatureKey(method, c.Context) == signatureKey)
                {
                    return (method.Attributes & MethodAttributes.Abstract) != 0 ? null : new MethodTarget(m, c.Context);
                }
            }
        }

        return null;
    }

    private MethodTarget? FindInterfaceImplementation(ClassInfo cls, ClassInfo iface, string name, string signatureKey)
    {
        // 1. Explicit implementations (MethodImpl rows), most derived first.
        for (ClassInfo? c = cls; c?.Handle is { } handle; c = c.Base)
        {
            foreach (MethodImplementationHandle implHandle in Reader.GetTypeDefinition(handle).GetMethodImplementations())
            {
                MethodImplementation impl = Reader.GetMethodImplementation(implHandle);
                if (DescribeMethod(impl.MethodDeclaration, c.Context) is var (declaringType, declName, declKey)
                    && declaringType == iface.Name && declName == name && declKey == signatureKey
                    && impl.MethodBody.Kind == HandleKind.MethodDefinition)
                {
                    return new MethodTarget((MethodDefinitionHandle)impl.MethodBody, c.Context);
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
                    && (method.Attributes & MethodAttributes.Abstract) == 0 && SignatureKey(method, c.Context) == signatureKey)
                {
                    return new MethodTarget(m, c.Context);
                }
            }
        }

        // 3. Default interface methods declared in cartridge interfaces.
        foreach (ClassInfo candidate in InterfaceClosure(cls).Where(i => i.Handle is not null))
        {
            foreach (MethodDefinitionHandle m in FindMethods(Reader.GetTypeDefinition(candidate.Handle!.Value), name))
            {
                MethodDefinition method = Reader.GetMethodDefinition(m);
                if (method.RelativeVirtualAddress != 0 && !method.IsStatic() && SignatureKey(method, candidate.Context) == signatureKey)
                {
                    return new MethodTarget(m, candidate.Context);
                }
            }
        }

        return null;
    }

    private ClassInfo FindCartridgeClass()
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

        if (candidates.Count > 1)
        {
            throw new TranslationException(
                $"More than one class implements {CartridgeInterface}: {string.Join(", ", candidates.Select(h => ClrTypeProvider.DefinitionName(Reader, h)))}.");
        }

        if (candidates.Count == 0)
        {
            throw new TranslationException($"No class implements {CartridgeInterface}. Declare 'public sealed class Game : ICartridge'.");
        }

        if (Reader.GetTypeDefinition(candidates[0]).GetGenericParameters().Count > 0)
        {
            throw new TranslationException("The cartridge class cannot be generic.");
        }

        return GetClass(candidates[0], "cartridge class");
    }

    private bool ImplementsCartridge(TypeDefinitionHandle handle)
    {
        TypeDefinition type = Reader.GetTypeDefinition(handle);
        foreach (InterfaceImplementationHandle implHandle in type.GetInterfaceImplementations())
        {
            if (ResolveTypeToken(Reader.GetInterfaceImplementation(implHandle).Interface, GenericContext.Empty) is SdkInterfaceClrType { FullName: CartridgeInterface })
            {
                return true;
            }
        }

        return !type.BaseType.IsNil && type.BaseType.Kind == HandleKind.TypeDefinition && ImplementsCartridge((TypeDefinitionHandle)type.BaseType);
    }

    private FunctionBuilder? LifecycleImplementation(ClassInfo cartridge, string name, bool required)
    {
        ClassInfo iface = GetSdkInterface(CartridgeInterface);
        if (FindInterfaceImplementation(cartridge, iface, name, $"{name}():Void") is { } impl)
        {
            return GetFunction(impl, $"{cartridge.Name}.{name}");
        }

        return required
            ? throw new TranslationException($"Cartridge class '{cartridge.Name}' must implement ICartridge.{name}().")
            : null;
    }

    // ---- members ------------------------------------------------------------------------

    /// <summary>Resolves a call target to its full VM signature (receiver first for instance calls) and the emitter.</summary>
    public (VmSignature Signature, Action<FunctionBuilder> Emit) ResolveCall(EntityHandle target, bool virtualCall, GenericContext context, string where)
    {
        if (target.Kind == HandleKind.MemberReference && ResolveExternalMember((MemberReferenceHandle)target, virtualCall, context, where) is { } external)
        {
            return external;
        }

        (MethodTarget method, ClassInfo? owner) = ResolveMethod(target, context, where);
        MethodDefinition definition = Reader.GetMethodDefinition(method.Handle);

        if (ClrTypeProvider.DefinitionName(Reader, definition.GetDeclaringType()) == IntrinsicsType)
        {
            return Intrinsic(Reader.GetString(definition.Name), method.Context, where);
        }

        if (!definition.IsStatic())
        {
            owner ??= GetClass(definition.GetDeclaringType(), where);
            bool isVirtual = (definition.Attributes & MethodAttributes.Virtual) != 0 && (definition.Attributes & MethodAttributes.Final) == 0;
            if (virtualCall && (isVirtual || owner.IsInterface))
            {
                if (!method.Context.MethodArgs.IsDefaultOrEmpty)
                {
                    throw new TranslationException($"{where}: generic virtual methods are not supported yet.");
                }

                MethodSignature<ClrType> signature = definition.DecodeSignature(TypeProvider, method.Context);
                SelectorInfo selector = GetSelector(Reader.GetString(definition.Name), signature, owner.IsInterface ? owner : null, where);
                return (ClrTypeMapping.ToVmSignature(signature, where, hasThis: true), f => f.Emit(OpCode.CallVirtual, selector.Index));
            }
        }

        FunctionBuilder callee = GetFunction(method, where);
        return (callee.Signature, f => f.Call(callee));
    }

    /// <summary>
    /// Resolves a method token (definition, member reference into a generic cartridge type, or
    /// generic method instantiation) to the method body and the generic context it runs in.
    /// </summary>
    private (MethodTarget Method, ClassInfo? Owner) ResolveMethod(EntityHandle target, GenericContext context, string where)
    {
        switch (target.Kind)
        {
            case HandleKind.MethodDefinition:
                {
                    var handle = (MethodDefinitionHandle)target;
                    TypeDefinitionHandle declaring = Reader.GetMethodDefinition(handle).GetDeclaringType();
                    // A definition token is only used for non-generic declaring types (or from inside the type itself).
                    GenericContext typeContext = Reader.GetTypeDefinition(declaring).GetGenericParameters().Count == 0
                        ? GenericContext.Empty
                        : context with { MethodArgs = [] };
                    return (new MethodTarget(handle, typeContext), null);
                }

            case HandleKind.MemberReference:
                {
                    MemberReference reference = Reader.GetMemberReference((MemberReferenceHandle)target);
                    if (ResolveTypeToken(reference.Parent, context) is not ClassClrType parent)
                    {
                        throw new TranslationException($"{where}: call to '{ParentTypeName(reference.Parent, context)}.{Reader.GetString(reference.Name)}' is not supported.");
                    }

                    string name = Reader.GetString(reference.Name);
                    string key = ClrTypeMapping.SignatureKey(name, reference.DecodeMethodSignature(TypeProvider, null));
                    foreach (MethodDefinitionHandle m in FindMethods(Reader.GetTypeDefinition(parent.Handle), name))
                    {
                        if (SignatureKey(Reader.GetMethodDefinition(m), null) == key)
                        {
                            ClassInfo owner = GetClass(parent, where);
                            return (new MethodTarget(m, parent.Context), owner);
                        }
                    }

                    throw new TranslationException($"{where}: method '{parent}.{name}' not found.");
                }

            case HandleKind.MethodSpecification:
                {
                    MethodSpecification spec = Reader.GetMethodSpecification((MethodSpecificationHandle)target);
                    ImmutableArray<ClrType> methodArgs = spec.DecodeSignature(TypeProvider, context);
                    if (methodArgs.FirstOrDefault(a => a is UnsupportedClrType) is UnsupportedClrType bad)
                    {
                        throw new TranslationException($"{where}: generic argument '{bad.Description}' is not supported ({bad.Reason}).");
                    }

                    (MethodTarget method, ClassInfo? owner) = ResolveMethod(spec.Method, context, where);
                    return (method with { Context = method.Context.WithMethodArgs(methodArgs) }, owner);
                }

            default:
                throw new TranslationException($"{where}: unsupported call target ({target.Kind}).");
        }
    }

    /// <summary>SDK syscalls, SDK interface methods and the supported BCL members. Null for cartridge members.</summary>
    private (VmSignature, Action<FunctionBuilder>)? ResolveExternalMember(MemberReferenceHandle handle, bool virtualCall, GenericContext context, string where)
    {
        MemberReference reference = Reader.GetMemberReference(handle);
        if (reference.Parent.Kind == HandleKind.TypeSpecification && ResolveTypeToken(reference.Parent, context) is ClassClrType)
        {
            return null; // member of a generic cartridge type
        }

        if (reference.Parent.Kind is HandleKind.TypeDefinition)
        {
            return null;
        }

        string typeName = ParentTypeName(reference.Parent, context);
        string methodName = Reader.GetString(reference.Name);
        MethodSignature<ClrType> signature = reference.DecodeMethodSignature(TypeProvider, context);
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
            case ("System.String", "Concat", 1) when signature.ParameterTypes[0] is ArrayClrType { Element: StringClrType }:
                return (VmSignature.Of(R, R), f => f.Emit(OpCode.StringConcatArray));
            case ("System.String", "IsNullOrEmpty", 1):
                return (VmSignature.Of(I, R), EmitIsNullOrEmpty);
        }

        if (SdkAbi.SdkAssembly.GetType(typeName) is { IsInterface: true })
        {
            if (!virtualCall)
            {
                throw new TranslationException($"{where}: non-virtual call to interface method '{display}'.");
            }

            SelectorInfo selector = GetSelector(methodName, signature, GetSdkInterface(typeName), where);
            return (ClrTypeMapping.ToVmSignature(signature, display, hasThis: true), f => f.Emit(OpCode.CallVirtual, selector.Index));
        }

        string hint = typeName.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
            ? " Use the ZTX16.Collections equivalent."
            : string.Empty;
        throw new TranslationException(
            $"{where}: call to '{display}' is not supported.{hint} Cartridges may call their own methods, ZTX16 SDK APIs and a small set of string members.");
    }

    /// <summary>Runtime support used by ZTX16.Collections, specialized per type argument.</summary>
    private (VmSignature, Action<FunctionBuilder>) Intrinsic(string name, GenericContext context, string where)
    {
        VmType R = VmType.Ref, I = VmType.I32;
        switch (name)
        {
            case "Fail":
                return (VmSignature.Of(null, R), f => f.Emit(OpCode.Trap));
            case "Hash" or "Equal":
                {
                    VmType t = ClrTypeMapping.MapValue(context.MethodArgs[0], $"{where} intrinsic {name}").Type!.Value;
                    if (name == "Hash")
                    {
                        return (VmSignature.Of(I, t), f => f.Emit(OpCode.Hash, (int)t));
                    }

                    OpCode eq = t switch
                    {
                        VmType.I32 => OpCode.EqI32,
                        VmType.I64 => OpCode.EqI64,
                        VmType.F32 => OpCode.EqF32,
                        VmType.F64 => OpCode.EqF64,
                        _ => OpCode.ValueEquals,
                    };
                    return (VmSignature.Of(I, t, t), f => f.Emit(eq));
                }

            default:
                throw new TranslationException($"{where}: unknown intrinsic '{name}'.");
        }
    }

    // string.IsNullOrEmpty(s): s == null ? true : s.Length == 0
    private static void EmitIsNullOrEmpty(FunctionBuilder f)
    {
        Label done = f.DefineLabel();
        Label notNull = f.DefineLabel();
        f.Emit(OpCode.Dup).Emit(OpCode.ConstNull).Emit(OpCode.EqRef);
        f.Emit(OpCode.BranchIfFalse, notNull);
        f.Emit(OpCode.Pop).ConstI32(1).Emit(OpCode.Branch, done);
        f.Mark(notNull).Emit(OpCode.StringLength).ConstI32(0).Emit(OpCode.EqI32);
        f.Mark(done);
    }

    /// <summary>Resolves a constructor used by <c>newobj</c>.</summary>
    public (VmSignature Constructor, Action<FunctionBuilder> Emit) ResolveNew(EntityHandle target, GenericContext context, string where)
    {
        if (target.Kind == HandleKind.MemberReference
            && ResolveTypeToken(Reader.GetMemberReference((MemberReferenceHandle)target).Parent, context) is not ClassClrType)
        {
            string name = ParentTypeName(Reader.GetMemberReference((MemberReferenceHandle)target).Parent, context);
            string hint = name.StartsWith("System.Collections.Generic.", StringComparison.Ordinal) ? " Use the ZTX16.Collections equivalent." : string.Empty;
            throw new TranslationException($"{where}: creating instances of '{name}' is not supported; only cartridge classes can be instantiated.{hint}");
        }

        (MethodTarget method, ClassInfo? owner) = ResolveMethod(target, context, where);
        TypeDefinitionHandle declaring = Reader.GetMethodDefinition(method.Handle).GetDeclaringType();
        owner ??= GetClass(declaring, where);
        if ((Reader.GetTypeDefinition(declaring).Attributes & TypeAttributes.Abstract) != 0)
        {
            throw new TranslationException($"{where}: cannot instantiate abstract class '{owner.Name}'.");
        }

        FunctionBuilder ctor = GetFunction(method, where);
        return (ctor.Signature, f => f.New(owner.Builder.Index, ctor.Index));
    }

    public FieldRef GetInstanceField(EntityHandle handle, GenericContext context, string where)
    {
        (FieldDefinitionHandle fieldHandle, ClassClrType ownerType) = ResolveField(handle, context, where);
        string key = $"{ownerType.Display}::{MetadataTokens.GetRowNumber(fieldHandle)}";
        if (_instanceFields.TryGetValue(key, out FieldRef? existing))
        {
            return existing;
        }

        FieldDefinition field = Reader.GetFieldDefinition(fieldHandle);
        ClassInfo owner = GetClass(ownerType, where);
        if (!owner.Slots.TryGetValue(fieldHandle, out int slot))
        {
            throw new TranslationException($"{where}: '{owner.Name}.{Reader.GetString(field.Name)}' is not an instance field.");
        }

        ClrType clr = field.DecodeSignature(TypeProvider, ownerType.Context);
        VmTypeInfo type = ClrTypeMapping.MapValue(clr, $"field '{owner.Name}.{Reader.GetString(field.Name)}'");
        var result = new FieldRef(_module.AddField(owner.Builder.Index, slot), type, clr);
        _instanceFields[key] = result;
        return result;
    }

    public FieldRef GetGlobal(EntityHandle handle, GenericContext context, string where)
    {
        (FieldDefinitionHandle fieldHandle, ClassClrType ownerType) = ResolveField(handle, context, where);
        string key = $"{ownerType.Display}::{MetadataTokens.GetRowNumber(fieldHandle)}";
        if (_globals.TryGetValue(key, out FieldRef? existing))
        {
            return existing;
        }

        FieldDefinition field = Reader.GetFieldDefinition(fieldHandle);
        string name = $"{ownerType.Display}.{Reader.GetString(field.Name)}";
        if ((field.Attributes & FieldAttributes.Static) == 0)
        {
            throw new TranslationException($"{where}: '{name}' is an instance field.");
        }

        ClrType clr = field.DecodeSignature(TypeProvider, ownerType.Context);
        VmTypeInfo type = ClrTypeMapping.MapValue(clr, $"field '{name}'");
        int index = _module.AddGlobal(type.Type!.Value);
        Touch(ownerType.Handle, ownerType.Context);
        var result = new FieldRef(index, type, clr);
        _globals[key] = result;
        return result;
    }

    /// <summary>The VM class of a struct type (registering it if needed).</summary>
    public int StructClass(ClrType type, string where) => GetClass((ClassClrType)type, where).Builder.Index;

    private (FieldDefinitionHandle Field, ClassClrType Owner) ResolveField(EntityHandle handle, GenericContext context, string where)
    {
        switch (handle.Kind)
        {
            case HandleKind.FieldDefinition:
                {
                    var fieldHandle = (FieldDefinitionHandle)handle;
                    TypeDefinitionHandle declaring = Reader.GetFieldDefinition(fieldHandle).GetDeclaringType();
                    ClassClrType owner = TypeProvider.GetTypeFromDefinition(Reader, declaring, 0) switch
                    {
                        ClassClrType c when Reader.GetTypeDefinition(declaring).GetGenericParameters().Count == 0 => c,
                        ClassClrType c => c.Instantiate(context.TypeArgs),
                        var other => throw new TranslationException($"{where}: fields of '{other}' are not supported."),
                    };
                    return (fieldHandle, owner);
                }

            case HandleKind.MemberReference:
                {
                    MemberReference reference = Reader.GetMemberReference((MemberReferenceHandle)handle);
                    if (ResolveTypeToken(reference.Parent, context) is not ClassClrType owner)
                    {
                        throw new TranslationException($"{where}: only fields declared in the cartridge can be used.");
                    }

                    string name = Reader.GetString(reference.Name);
                    foreach (FieldDefinitionHandle f in Reader.GetTypeDefinition(owner.Handle).GetFields())
                    {
                        if (Reader.GetString(Reader.GetFieldDefinition(f).Name) == name)
                        {
                            return (f, owner);
                        }
                    }

                    throw new TranslationException($"{where}: field '{owner}.{name}' not found.");
                }

            default:
                throw new TranslationException($"{where}: only fields declared in the cartridge can be used.");
        }
    }

    public int AddString(string value) => _module.AddString(value);

    /// <summary>True for <c>System.Runtime.CompilerServices.RuntimeHelpers.InitializeArray</c>.</summary>
    public bool IsInitializeArray(EntityHandle target) =>
        target.Kind == HandleKind.MemberReference
        && Reader.GetMemberReference((MemberReferenceHandle)target) is var reference
        && Reader.GetString(reference.Name) == "InitializeArray"
        && reference.Parent.Kind == HandleKind.TypeReference
        && TypeReferenceName((TypeReferenceHandle)reference.Parent) == "System.Runtime.CompilerServices.RuntimeHelpers";

    /// <summary>The constant data the compiler embedded for an array initializer (a field with an RVA).</summary>
    public byte[] FieldInitialData(EntityHandle token, string where)
    {
        if (token.Kind != HandleKind.FieldDefinition)
        {
            throw new TranslationException($"{where}: unexpected array initializer token.");
        }

        FieldDefinition field = Reader.GetFieldDefinition((FieldDefinitionHandle)token);
        int rva = field.GetRelativeVirtualAddress();
        int size = field.DecodeSignature(TypeProvider, null) switch
        {
            ClassClrType holder => Reader.GetTypeDefinition(holder.Handle).GetLayout().Size,
            PrimitiveClrType { Code: PrimitiveTypeCode.Byte or PrimitiveTypeCode.SByte } => 1,
            PrimitiveClrType { Code: PrimitiveTypeCode.Int16 or PrimitiveTypeCode.UInt16 } => 2,
            PrimitiveClrType { Code: PrimitiveTypeCode.Int32 or PrimitiveTypeCode.UInt32 } => 4,
            PrimitiveClrType { Code: PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 } => 8,
            var other => throw new TranslationException($"{where}: unexpected array initializer data type '{other}'."),
        };

        if (rva == 0 || size <= 0)
        {
            throw new TranslationException($"{where}: array initializer has no data.");
        }

        return _pe.GetSectionData(rva).GetContent(0, size).ToArray();
    }

    /// <summary>If <paramref name="target"/> is <c>ToString()</c> on a supported primitive, its format kind.</summary>
    public VmFormatKind? PrimitiveToString(EntityHandle target)
    {
        if (target.Kind != HandleKind.MemberReference)
        {
            return null;
        }

        MemberReference reference = Reader.GetMemberReference((MemberReferenceHandle)target);
        if (Reader.GetString(reference.Name) != "ToString" || reference.Parent.Kind != HandleKind.TypeReference
            || reference.DecodeMethodSignature(TypeProvider, null).ParameterTypes.Length != 0)
        {
            return null;
        }

        return TypeReferenceName((TypeReferenceHandle)reference.Parent) switch
        {
            "System.Int32" or "System.Int16" or "System.SByte" => VmFormatKind.Int32,
            "System.UInt32" or "System.UInt16" or "System.Byte" => VmFormatKind.UInt32,
            "System.Int64" => VmFormatKind.Int64,
            "System.UInt64" => VmFormatKind.UInt64,
            "System.Single" => VmFormatKind.Single,
            "System.Double" => VmFormatKind.Double,
            "System.Char" => VmFormatKind.Char,
            "System.Boolean" => VmFormatKind.Boolean,
            _ => null,
        };
    }

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

    private FunctionBuilder GetFunction(MethodTarget target, string where)
    {
        MethodDefinition method = Reader.GetMethodDefinition(target.Handle);
        TypeDefinitionHandle declaring = method.GetDeclaringType();
        string key = $"{MetadataTokens.GetRowNumber(target.Handle)}{target.Context.Key}";
        if (_functions.TryGetValue(key, out FunctionBuilder? existing))
        {
            return existing;
        }

        string typeName = TypeKey(declaring, target.Context);
        string methodArgs = target.Context.MethodArgs.IsDefaultOrEmpty ? "" : $"<{string.Join(",", target.Context.MethodArgs)}>";
        string name = $"{typeName}.{Reader.GetString(method.Name)}{methodArgs}";

        int methodParameters = method.GetGenericParameters().Count;
        if (methodParameters != (target.Context.MethodArgs.IsDefault ? 0 : target.Context.MethodArgs.Length))
        {
            throw new TranslationException($"{where}: generic method '{name}' used without type arguments.");
        }

        if ((method.Attributes & MethodAttributes.Abstract) != 0)
        {
            throw new TranslationException($"{where}: abstract method '{name}' has no body.");
        }

        bool hasThis = !method.IsStatic();
        if (hasThis)
        {
            GetClass(ClassTypeOf(declaring, where).Instantiate(target.Context.TypeArgs), where);
        }

        MethodSignature<ClrType> signature = method.DecodeSignature(TypeProvider, target.Context);
        FunctionBuilder builder = _module.DefineFunction(UniqueName(name), ClrTypeMapping.ToVmSignature(signature, name, hasThis));
        _functions[key] = builder;
        Touch(declaring, target.Context);
        _pending.Enqueue((target.Handle, target.Context, builder));
        return builder;
    }

    // ---- metadata helpers ---------------------------------------------------------------

    private string SignatureKey(MethodDefinition method, GenericContext? context) =>
        ClrTypeMapping.SignatureKey(Reader.GetString(method.Name), method.DecodeSignature(TypeProvider, context));

    private (string DeclaringType, string Name, string Key)? DescribeMethod(EntityHandle handle, GenericContext context)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                {
                    MethodDefinition method = Reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    string name = Reader.GetString(method.Name);
                    return (ClrTypeProvider.DefinitionName(Reader, method.GetDeclaringType()), name, SignatureKey(method, GenericContext.Empty));
                }

            case HandleKind.MemberReference:
                {
                    MemberReference reference = Reader.GetMemberReference((MemberReferenceHandle)handle);
                    string name = Reader.GetString(reference.Name);
                    ClrType parent = ResolveTypeToken(reference.Parent, context);
                    GenericContext parentContext = parent is ClassClrType c ? c.Context : GenericContext.Empty;
                    return (parent.Display, name, ClrTypeMapping.SignatureKey(name, reference.DecodeMethodSignature(TypeProvider, parentContext)));
                }

            default:
                return null;
        }
    }

    private IEnumerable<MethodDefinitionHandle> FindMethods(TypeDefinition type, string name) =>
        type.GetMethods().Where(h => Reader.GetString(Reader.GetMethodDefinition(h).Name) == name);

    private string ParentTypeName(EntityHandle parent, GenericContext context) => parent.Kind switch
    {
        HandleKind.TypeReference => TypeReferenceName((TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => ClrTypeProvider.DefinitionName(Reader, (TypeDefinitionHandle)parent),
        HandleKind.TypeSpecification => ResolveTypeToken(parent, context) switch
        {
            UnsupportedClrType u => u.Description,
            var t => t.Display,
        },
        _ => "<unsupported parent>",
    };

    private string TypeReferenceName(TypeReferenceHandle handle)
    {
        TypeReference reference = Reader.GetTypeReference(handle);
        return ClrTypeProvider.FullName(Reader, reference.Namespace, reference.Name);
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
