using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using ZTX16.Abi;
using ZTX16.VM;

namespace ZTX16.Toolchain;

/// <summary>A CLR type as seen in cartridge metadata, reduced to what the translator needs.</summary>
internal abstract record ClrType(string Display)
{
    public sealed override string ToString() => Display;
}

/// <summary>A built-in primitive type.</summary>
internal sealed record PrimitiveClrType(PrimitiveTypeCode Code) : ClrType(Code.ToString());

/// <summary>An enum, represented by its underlying primitive type.</summary>
internal sealed record EnumClrType(string FullName, PrimitiveTypeCode Underlying) : ClrType(FullName);

/// <summary>
/// A class or interface defined in the cartridge, possibly a generic instantiation. Generic
/// definitions without arguments are "open"; the translator only instantiates closed ones.
/// </summary>
internal sealed record ClassClrType(TypeDefinitionHandle Handle, string FullName, bool IsInterface, ImmutableArray<ClrType> TypeArgs, bool IsValueType = false)
    : ClrType(TypeArgs.IsDefaultOrEmpty ? FullName : $"{FullName}<{string.Join(",", TypeArgs)}>")
{
    /// <summary>Generic context for members of this type.</summary>
    public GenericContext Context => new(TypeArgs.IsDefault ? [] : TypeArgs, []);

    /// <summary>
    /// The same definition with <paramref name="typeArgs"/>. Always use this instead of a
    /// <c>with</c> expression: <see cref="ClrType.Display"/> is computed at construction.
    /// </summary>
    public ClassClrType Instantiate(ImmutableArray<ClrType> typeArgs) => new(Handle, FullName, IsInterface, typeArgs, IsValueType);
}

/// <summary>An unresolved generic parameter (only seen when decoding without a context).</summary>
internal sealed record GenericParameterClrType(int Index, bool IsMethodParameter) : ClrType(IsMethodParameter ? $"!!{Index}" : $"!{Index}");

/// <summary>Type arguments in effect while decoding signatures and tokens.</summary>
internal sealed record GenericContext(ImmutableArray<ClrType> TypeArgs, ImmutableArray<ClrType> MethodArgs)
{
    /// <summary>No generic arguments.</summary>
    public static GenericContext Empty { get; } = new([], []);

    /// <summary>Canonical text form, used in keys and names.</summary>
    public string Key => $"{(TypeArgs.IsDefaultOrEmpty ? "" : $"<{string.Join(",", TypeArgs)}>")}{(MethodArgs.IsDefaultOrEmpty ? "" : $"[{string.Join(",", MethodArgs)}]")}";

    /// <summary>Same type arguments, with method arguments.</summary>
    public GenericContext WithMethodArgs(ImmutableArray<ClrType> methodArgs) => this with { MethodArgs = methodArgs };
}

/// <summary>An interface defined by the SDK (e.g. <c>ZTX16.ICartridge</c>).</summary>
internal sealed record SdkInterfaceClrType(string FullName) : ClrType(FullName);

/// <summary><c>System.String</c>.</summary>
internal sealed record StringClrType() : ClrType("string");

/// <summary>
/// A managed pointer (<c>ref</c>/<c>out</c>/<c>in</c> parameters, ref locals). In the VM it is a reference:
/// the struct instance itself for struct targets, a pointer object otherwise.
/// </summary>
internal sealed record ByRefClrType(ClrType Element) : ClrType($"ref {Element}");

/// <summary>
/// <c>System.ReadOnlySpan&lt;char&gt;</c>, which the C# compiler uses to concatenate strings with characters.
/// The toolchain represents it as a string.
/// </summary>
internal sealed record CharSpanClrType() : ClrType("ReadOnlySpan<char>");

/// <summary><c>System.Object</c>.</summary>
internal sealed record ObjectClrType() : ClrType("object");

/// <summary>A one-dimensional, zero-based array.</summary>
internal sealed record ArrayClrType(ClrType Element) : ClrType($"{Element}[]");

/// <summary>Any type the VM cannot represent yet (structs, generics, pointers, multi-dimensional arrays…).</summary>
internal sealed record UnsupportedClrType(string Description, string Reason) : ClrType(Description);

/// <summary>How a CLR type is stored in the VM.</summary>
/// <param name="Type">VM slot type, or null for void.</param>
/// <param name="Narrow">Truncation to apply when storing into a location of this type (sub-32-bit integers).</param>
internal readonly record struct VmTypeInfo(VmType? Type, OpCode? Narrow);

/// <summary>Maps metadata types to VM types.</summary>
internal static class ClrTypeMapping
{
    /// <summary>Maps <paramref name="type"/> or throws a translation error naming <paramref name="context"/>.</summary>
    public static VmTypeInfo Map(ClrType type, string context)
    {
        PrimitiveTypeCode code;
        switch (type)
        {
            case PrimitiveClrType p:
                code = p.Code;
                break;
            case EnumClrType e:
                code = e.Underlying;
                break;
            case ClassClrType or SdkInterfaceClrType or StringClrType or ObjectClrType or ByRefClrType or CharSpanClrType:
                return new(VmType.Ref, null);
            case ArrayClrType array:
                ElementAccess(array.Element, context); // validates the element type
                return new(VmType.Ref, null);
            case UnsupportedClrType u:
                throw new TranslationException($"{context}: type '{u.Description}' is not supported yet ({u.Reason}).");
            default:
                throw new TranslationException($"{context}: type '{type}' is not supported yet.");
        }

        return code switch
        {
            PrimitiveTypeCode.Void => new(null, null),
            PrimitiveTypeCode.Boolean => new(VmType.I32, null),
            PrimitiveTypeCode.SByte => new(VmType.I32, OpCode.I32ToI8),
            PrimitiveTypeCode.Byte => new(VmType.I32, OpCode.I32ToU8),
            PrimitiveTypeCode.Int16 => new(VmType.I32, OpCode.I32ToI16),
            PrimitiveTypeCode.UInt16 or PrimitiveTypeCode.Char => new(VmType.I32, OpCode.I32ToU16),
            PrimitiveTypeCode.Int32 or PrimitiveTypeCode.UInt32 => new(VmType.I32, null),
            PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => new(VmType.I64, null),
            PrimitiveTypeCode.Single => new(VmType.F32, null),
            PrimitiveTypeCode.Double => new(VmType.F64, null),
            _ => throw new TranslationException($"{context}: type '{type}' is not supported yet."),
        };
    }

    /// <summary>Maps a value type (never void).</summary>
    public static VmTypeInfo MapValue(ClrType type, string context)
    {
        VmTypeInfo info = Map(type, context);
        if (info.Type is null)
        {
            throw new TranslationException($"{context}: 'void' is not a value type.");
        }

        return info;
    }

    /// <summary>How elements of an array with element type <paramref name="element"/> are accessed.</summary>
    public static VmElementAccess ElementAccess(ClrType element, string context)
    {
        PrimitiveTypeCode? code = element switch
        {
            PrimitiveClrType p => p.Code,
            EnumClrType e => e.Underlying,
            _ => null,
        };

        if (code is null)
        {
            VmTypeInfo info = MapValue(element, $"{context} array element");
            return info.Type == VmType.Ref
                ? VmElementAccess.Ref
                : throw new TranslationException($"{context}: arrays of '{element}' are not supported.");
        }

        return code switch
        {
            PrimitiveTypeCode.SByte => VmElementAccess.I8,
            PrimitiveTypeCode.Byte or PrimitiveTypeCode.Boolean => VmElementAccess.U8,
            PrimitiveTypeCode.Int16 => VmElementAccess.I16,
            PrimitiveTypeCode.UInt16 or PrimitiveTypeCode.Char => VmElementAccess.U16,
            PrimitiveTypeCode.Int32 or PrimitiveTypeCode.UInt32 => VmElementAccess.I32,
            PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => VmElementAccess.I64,
            PrimitiveTypeCode.Single => VmElementAccess.F32,
            PrimitiveTypeCode.Double => VmElementAccess.F64,
            _ => throw new TranslationException($"{context}: arrays of '{element}' are not supported."),
        };
    }

    /// <summary>True for value types defined in the cartridge (structs).</summary>
    public static bool IsStruct(ClrType type) => type is ClassClrType { IsValueType: true };

    /// <summary>Converts a decoded method signature into a VM signature; instance methods get a leading receiver.</summary>
    public static VmSignature ToVmSignature(MethodSignature<ClrType> signature, string context, bool hasThis = false)
    {
        IEnumerable<VmType> parameters = signature.ParameterTypes.Select((p, i) => MapValue(p, $"{context} parameter {i}").Type!.Value);
        if (hasThis)
        {
            parameters = parameters.Prepend(VmType.Ref);
        }

        return new(parameters.ToArray(), Map(signature.ReturnType, $"{context} return type").Type);
    }

    /// <summary>A stable text key for matching overrides and interface implementations.</summary>
    public static string SignatureKey(string name, MethodSignature<ClrType> signature) =>
        $"{name}({string.Join(",", signature.ParameterTypes)}):{signature.ReturnType}";
}

/// <summary>
/// Decodes metadata signatures into <see cref="ClrType"/>. Cartridge types are resolved from
/// its own metadata; SDK enums and interfaces by reflection over the SDK assembly; the
/// handful of supported <c>System</c> types by name.
/// </summary>
internal sealed class ClrTypeProvider : ISignatureTypeProvider<ClrType, GenericContext?>
{
    private const string SdkAssemblyName = "ZTX16.Sdk";

    private readonly Dictionary<TypeDefinitionHandle, ClrType> _definitionCache = [];

    public ClrType GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.String => new StringClrType(),
        PrimitiveTypeCode.Object => new ObjectClrType(),
        PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => new UnsupportedClrType("native int", "native integers are not part of the ZTX16 model"),
        PrimitiveTypeCode.TypedReference => new UnsupportedClrType("TypedReference", "not supported"),
        _ => new PrimitiveClrType(typeCode),
    };

    public ClrType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        if (_definitionCache.TryGetValue(handle, out ClrType? cached))
        {
            return cached;
        }

        TypeDefinition definition = reader.GetTypeDefinition(handle);
        string fullName = DefinitionName(reader, handle);
        string? baseName = BaseTypeName(reader, definition);
        ClrType result;

        if (baseName == "System.Enum")
        {
            result = new UnsupportedClrType(fullName, "enum without a value field");
            foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
            {
                FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
                if (reader.GetString(field.Name) == "value__"
                    && field.DecodeSignature(this, null) is PrimitiveClrType underlying)
                {
                    result = new EnumClrType(fullName, underlying.Code);
                    break;
                }
            }
        }
        else if (baseName == "System.ValueType")
        {
            result = new ClassClrType(handle, fullName, IsInterface: false, [], IsValueType: true);
        }
        else
        {
            result = new ClassClrType(handle, fullName, (definition.Attributes & TypeAttributes.Interface) != 0, []);
        }

        _definitionCache[handle] = result;
        return result;
    }

    public ClrType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        TypeReference reference = reader.GetTypeReference(handle);
        string fullName = FullName(reader, reference.Namespace, reference.Name);

        if (reference.ResolutionScope.Kind == HandleKind.AssemblyReference)
        {
            AssemblyReference assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
            if (reader.GetString(assembly.Name) == SdkAssemblyName && SdkAbi.SdkAssembly.GetType(fullName) is { } sdkType)
            {
                if (sdkType.IsEnum)
                {
                    return new EnumClrType(fullName, ToPrimitive(Enum.GetUnderlyingType(sdkType)));
                }

                if (sdkType.IsInterface)
                {
                    return new SdkInterfaceClrType(fullName);
                }
            }
        }

        return fullName switch
        {
            "System.Boolean" => new PrimitiveClrType(PrimitiveTypeCode.Boolean),
            "System.Char" => new PrimitiveClrType(PrimitiveTypeCode.Char),
            "System.SByte" => new PrimitiveClrType(PrimitiveTypeCode.SByte),
            "System.Byte" => new PrimitiveClrType(PrimitiveTypeCode.Byte),
            "System.Int16" => new PrimitiveClrType(PrimitiveTypeCode.Int16),
            "System.UInt16" => new PrimitiveClrType(PrimitiveTypeCode.UInt16),
            "System.Int32" => new PrimitiveClrType(PrimitiveTypeCode.Int32),
            "System.UInt32" => new PrimitiveClrType(PrimitiveTypeCode.UInt32),
            "System.Int64" => new PrimitiveClrType(PrimitiveTypeCode.Int64),
            "System.UInt64" => new PrimitiveClrType(PrimitiveTypeCode.UInt64),
            "System.Single" => new PrimitiveClrType(PrimitiveTypeCode.Single),
            "System.Double" => new PrimitiveClrType(PrimitiveTypeCode.Double),
            "System.String" => new StringClrType(),
            "System.Object" => new ObjectClrType(),
            _ when fullName.StartsWith("System.Action", StringComparison.Ordinal)
                || fullName.StartsWith("System.Func", StringComparison.Ordinal)
                || fullName.StartsWith("System.Predicate", StringComparison.Ordinal)
                || fullName is "System.Delegate" or "System.MulticastDelegate" or "System.EventHandler"
                => new UnsupportedClrType(fullName, "delegates and lambdas are not supported yet"),
            _ => new UnsupportedClrType(fullName, "only cartridge types, ZTX16 SDK types and core primitives can be used"),
        };
    }

    public ClrType GetTypeFromSpecification(MetadataReader reader, GenericContext? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public ClrType GetSZArrayType(ClrType elementType) => new ArrayClrType(elementType);

    public ClrType GetArrayType(ClrType elementType, ArrayShape shape) =>
        new UnsupportedClrType($"{elementType}[,]", "multi-dimensional arrays are not supported; use jagged arrays");

    public ClrType GetByReferenceType(ClrType elementType) =>
        elementType is UnsupportedClrType u ? u : new ByRefClrType(elementType);

    public ClrType GetPointerType(ClrType elementType) => new UnsupportedClrType($"{elementType}*", "pointers are not part of the ZTX16 model");

    public ClrType GetPinnedType(ClrType elementType) => elementType;

    public ClrType GetModifiedType(ClrType modifier, ClrType unmodifiedType, bool isRequired) => unmodifiedType;

    public ClrType GetFunctionPointerType(MethodSignature<ClrType> signature) =>
        new UnsupportedClrType("function pointer", "pointers are not part of the ZTX16 model");

    public ClrType GetGenericInstantiation(ClrType genericType, ImmutableArray<ClrType> typeArguments)
    {
        if (typeArguments.FirstOrDefault(a => a is UnsupportedClrType) is UnsupportedClrType badArgument)
        {
            return new UnsupportedClrType($"{genericType}<{string.Join(",", typeArguments)}>", badArgument.Reason);
        }

        return genericType switch
        {
            ClassClrType c => c.Instantiate(typeArguments),
            UnsupportedClrType { Description: "System.ReadOnlySpan`1" } when typeArguments is [PrimitiveClrType { Code: PrimitiveTypeCode.Char }] => new CharSpanClrType(),
            UnsupportedClrType u when u.Description.StartsWith("System.Collections.Generic.", StringComparison.Ordinal) =>
                new UnsupportedClrType(u.Description, $"use the ZTX16.Collections equivalent instead of {u.Description}"),
            UnsupportedClrType u => new UnsupportedClrType($"{u.Description}<{string.Join(",", typeArguments)}>", u.Reason),
            _ => new UnsupportedClrType($"{genericType}<{string.Join(",", typeArguments)}>", "only cartridge generic types can be instantiated"),
        };
    }

    public ClrType GetGenericMethodParameter(GenericContext? genericContext, int index) =>
        genericContext is { MethodArgs.IsDefaultOrEmpty: false } ctx && index < ctx.MethodArgs.Length
            ? ctx.MethodArgs[index]
            : new GenericParameterClrType(index, IsMethodParameter: true);

    public ClrType GetGenericTypeParameter(GenericContext? genericContext, int index) =>
        genericContext is { TypeArgs.IsDefaultOrEmpty: false } ctx && index < ctx.TypeArgs.Length
            ? ctx.TypeArgs[index]
            : new GenericParameterClrType(index, IsMethodParameter: false);

    /// <summary>Full name of a type definition, with nesting written as <c>Outer+Inner</c>.</summary>
    public static string DefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = FullName(reader, type.Namespace, type.Name);
        TypeDefinitionHandle declaring = type.GetDeclaringType();
        return declaring.IsNil ? name : $"{DefinitionName(reader, declaring)}+{name}";
    }

    public static string FullName(MetadataReader reader, StringHandle ns, StringHandle name)
    {
        string n = reader.GetString(ns);
        return n.Length == 0 ? reader.GetString(name) : $"{n}.{reader.GetString(name)}";
    }

    public static string? BaseTypeName(MetadataReader reader, TypeDefinition definition)
    {
        EntityHandle baseType = definition.BaseType;
        if (baseType.IsNil)
        {
            return null; // interfaces and <Module>
        }

        return baseType.Kind switch
        {
            HandleKind.TypeReference => FullName(reader,
                reader.GetTypeReference((TypeReferenceHandle)baseType).Namespace,
                reader.GetTypeReference((TypeReferenceHandle)baseType).Name),
            HandleKind.TypeDefinition => FullName(reader,
                reader.GetTypeDefinition((TypeDefinitionHandle)baseType).Namespace,
                reader.GetTypeDefinition((TypeDefinitionHandle)baseType).Name),
            _ => null,
        };
    }

    private static PrimitiveTypeCode ToPrimitive(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.SByte => PrimitiveTypeCode.SByte,
        TypeCode.Byte => PrimitiveTypeCode.Byte,
        TypeCode.Int16 => PrimitiveTypeCode.Int16,
        TypeCode.UInt16 => PrimitiveTypeCode.UInt16,
        TypeCode.Int32 => PrimitiveTypeCode.Int32,
        TypeCode.UInt32 => PrimitiveTypeCode.UInt32,
        TypeCode.Int64 => PrimitiveTypeCode.Int64,
        _ => PrimitiveTypeCode.UInt64,
    };
}
