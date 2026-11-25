using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ZTX16.Abi;
using ZTX16.VM;

namespace ZTX16.Toolchain;

/// <summary>
/// Translates a compiled cartridge assembly (IL) into a ZTX16 executable module.
/// </summary>
/// <remarks>
/// Supported subset (this iteration): static methods and static fields of numeric types,
/// <c>bool</c>, <c>char</c> and enums; arithmetic, comparisons, conversions, branches,
/// <c>switch</c>; calls to cartridge methods and to SDK syscalls. Everything else is
/// rejected with a <see cref="TranslationException"/>.
///
/// The cartridge entry class carries <see cref="CartridgeAttribute"/> and declares static
/// lifecycle methods. Static constructors of every type the cartridge uses run once,
/// in type-definition order, before <c>Initialize</c>.
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

        var context = new TranslationContext(pe);
        return context.Run();
    }

    /// <summary>Translates and serializes to executable bytes.</summary>
    public static byte[] TranslateToExecutable(ReadOnlySpan<byte> assembly) => ExecutableFormat.Write(Translate(assembly));
}

/// <summary>Module-wide translation state: functions, globals, imports and static constructors.</summary>
internal sealed class TranslationContext
{
    private readonly PEReader _pe;
    private readonly ModuleBuilder _module = new();
    private readonly Dictionary<MethodDefinitionHandle, FunctionBuilder> _functions = [];
    private readonly Queue<(MethodDefinitionHandle Handle, FunctionBuilder Builder, MethodSignature<ClrType> Signature)> _pending = new();
    private readonly Dictionary<FieldDefinitionHandle, (int Index, VmTypeInfo Type)> _globals = [];
    private readonly HashSet<string> _functionNames = new(StringComparer.Ordinal);
    private readonly HashSet<TypeDefinitionHandle> _touchedTypes = [];

    public TranslationContext(PEReader pe)
    {
        _pe = pe;
        Reader = pe.GetMetadataReader();
        TypeProvider = new ClrTypeProvider(Reader);
    }

    public MetadataReader Reader { get; }

    public ClrTypeProvider TypeProvider { get; }

    public MethodBodyBlock GetBody(MethodDefinition method) => _pe.GetMethodBody(method.RelativeVirtualAddress);

    public VmModule Run()
    {
        TypeDefinition cartridge = FindCartridgeType();
        string cartridgeName = ClrTypeProvider.FullName(Reader, cartridge.Namespace, cartridge.Name);

        FunctionBuilder initialize = LifecycleFunction(cartridge, cartridgeName, VmModule.LifecycleExports.Initialize, required: true)!;
        FunctionBuilder tick = LifecycleFunction(cartridge, cartridgeName, VmModule.LifecycleExports.Tick, required: true)!;
        FunctionBuilder render = LifecycleFunction(cartridge, cartridgeName, VmModule.LifecycleExports.Render, required: true)!;
        FunctionBuilder? deinitialize = LifecycleFunction(cartridge, cartridgeName, VmModule.LifecycleExports.Deinitialize, required: false);
        ProcessPending();

        List<FunctionBuilder> staticConstructors = TranslateStaticConstructors();
        if (staticConstructors.Count > 0)
        {
            FunctionBuilder entry = _module.DefineFunction("<cartridge>.Initialize", VmSignature.Void);
            foreach (FunctionBuilder constructor in staticConstructors)
            {
                entry.Call(constructor);
            }

            entry.Call(initialize).Emit(OpCode.Return);
            initialize = entry;
        }

        _module.Export(VmModule.LifecycleExports.Initialize, initialize);
        _module.Export(VmModule.LifecycleExports.Tick, tick);
        _module.Export(VmModule.LifecycleExports.Render, render);
        if (deinitialize is not null)
        {
            _module.Export(VmModule.LifecycleExports.Deinitialize, deinitialize);
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

    /// <summary>Resolves a call target to its VM signature and the emitter for the call instruction.</summary>
    public (VmSignature Signature, Action<FunctionBuilder> Emit) ResolveCallFor(EntityHandle target, string where)
    {
        switch (target.Kind)
        {
            case HandleKind.MethodDefinition:
                {
                    FunctionBuilder callee = GetFunction((MethodDefinitionHandle)target, where);
                    return (callee.Signature, f => f.Call(callee));
                }

            case HandleKind.MemberReference:
                {
                    MemberReference reference = Reader.GetMemberReference((MemberReferenceHandle)target);
                    string typeName = ParentTypeName(reference.Parent);
                    string methodName = Reader.GetString(reference.Name);

                    SyscallDescriptor syscall = SdkAbi.FindByMethod(typeName, methodName)
                        ?? throw new TranslationException(
                            $"{where}: call to '{typeName}.{methodName}' is not supported. Cartridges may call their own static methods and ZTX16 SDK syscalls only.");

                    VmSignature referenced = ClrTypeMapping.ToVmSignature(reference.DecodeMethodSignature(TypeProvider, null), $"{typeName}.{methodName}");
                    if (!referenced.Matches(syscall.Signature))
                    {
                        throw new TranslationException($"{where}: '{typeName}.{methodName}' was compiled against a different SDK version ({referenced} vs {syscall.Signature}).");
                    }

                    int import = _module.Import(syscall.Name, syscall.Signature);
                    return (syscall.Signature, f => f.Emit(OpCode.Syscall, import));
                }

            case HandleKind.MethodSpecification:
                throw new TranslationException($"{where}: generic methods are not supported yet.");

            default:
                throw new TranslationException($"{where}: unsupported call target ({target.Kind}).");
        }
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
            throw new TranslationException($"{where}: instance field '{name}' is not supported yet (instance members arrive with the managed heap).");
        }

        VmTypeInfo type = ClrTypeMapping.MapValue(field.DecodeSignature(TypeProvider, null), $"field '{name}'");
        int index = _module.AddGlobal(type.Type!.Value);
        _touchedTypes.Add(field.GetDeclaringType());
        _globals[fieldHandle] = (index, type);
        return (index, type);
    }

    private FunctionBuilder GetFunction(MethodDefinitionHandle handle, string where)
    {
        if (_functions.TryGetValue(handle, out FunctionBuilder? existing))
        {
            return existing;
        }

        MethodDefinition method = Reader.GetMethodDefinition(handle);
        string name = $"{TypeName(method.GetDeclaringType())}.{Reader.GetString(method.Name)}";
        if (!method.IsStatic())
        {
            throw new TranslationException($"{where}: instance method '{name}' is not supported yet (instance members arrive with the managed heap).");
        }

        if (method.GetGenericParameters().Count > 0)
        {
            throw new TranslationException($"{where}: generic method '{name}' is not supported yet.");
        }

        MethodSignature<ClrType> signature = method.DecodeSignature(TypeProvider, null);
        FunctionBuilder builder = _module.DefineFunction(UniqueName(name), ClrTypeMapping.ToVmSignature(signature, name));
        _functions[handle] = builder;
        _touchedTypes.Add(method.GetDeclaringType());
        _pending.Enqueue((handle, builder, signature));
        return builder;
    }

    private void ProcessPending()
    {
        while (_pending.TryDequeue(out var item))
        {
            new MethodTranslator(this, Reader.GetMethodDefinition(item.Handle), item.Builder, item.Signature).Translate();
        }
    }

    private List<FunctionBuilder> TranslateStaticConstructors()
    {
        var scheduled = new HashSet<TypeDefinitionHandle>();
        bool added;
        do
        {
            added = false;
            foreach (TypeDefinitionHandle type in _touchedTypes.ToArray())
            {
                if (scheduled.Add(type) && FindMethod(Reader.GetTypeDefinition(type), ".cctor") is { } cctor)
                {
                    GetFunction(cctor, $"{TypeName(type)} static constructor");
                    added = true;
                }
            }

            ProcessPending();
        }
        while (added);

        // Run in type-definition order for determinism.
        return Reader.TypeDefinitions
            .Where(scheduled.Contains)
            .Select(t => FindMethod(Reader.GetTypeDefinition(t), ".cctor"))
            .Where(h => h is not null)
            .Select(h => _functions[h!.Value])
            .ToList();
    }

    private FunctionBuilder? LifecycleFunction(TypeDefinition type, string typeName, string methodName, bool required)
    {
        if (FindMethod(type, methodName) is not { } handle)
        {
            return required
                ? throw new TranslationException($"Cartridge class '{typeName}' must declare 'public static void {methodName}()'.")
                : null;
        }

        MethodDefinition method = Reader.GetMethodDefinition(handle);
        MethodSignature<ClrType> signature = method.DecodeSignature(TypeProvider, null);
        bool isVoid = signature.ReturnType is PrimitiveClrType { Code: PrimitiveTypeCode.Void };
        if (!method.IsStatic() || signature.ParameterTypes.Length != 0 || !isVoid)
        {
            throw new TranslationException($"Cartridge lifecycle method '{typeName}.{methodName}' must be 'static void {methodName}()'.");
        }

        return GetFunction(handle, $"{typeName}.{methodName}");
    }

    private MethodDefinitionHandle? FindMethod(TypeDefinition type, string name)
    {
        foreach (MethodDefinitionHandle handle in type.GetMethods())
        {
            if (Reader.GetString(Reader.GetMethodDefinition(handle).Name) == name)
            {
                return handle;
            }
        }

        return null;
    }

    private TypeDefinition FindCartridgeType()
    {
        TypeDefinition? found = null;
        foreach (TypeDefinitionHandle handle in Reader.TypeDefinitions)
        {
            TypeDefinition type = Reader.GetTypeDefinition(handle);
            foreach (CustomAttributeHandle attributeHandle in type.GetCustomAttributes())
            {
                if (AttributeTypeName(Reader.GetCustomAttribute(attributeHandle)) == typeof(CartridgeAttribute).FullName)
                {
                    if (found is not null)
                    {
                        throw new TranslationException("More than one class is marked [Cartridge].");
                    }

                    found = type;
                }
            }
        }

        return found ?? throw new TranslationException("No class is marked [Cartridge]. Mark the cartridge entry class with [ZTX16.Cartridge].");
    }

    private string AttributeTypeName(CustomAttribute attribute) => attribute.Constructor.Kind switch
    {
        HandleKind.MemberReference => ParentTypeName(Reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
        HandleKind.MethodDefinition => TypeName(Reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
        _ => string.Empty,
    };

    private string ParentTypeName(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => TypeReferenceName((TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => TypeName((TypeDefinitionHandle)parent),
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
}
