using System.Collections.Immutable;
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

/// <summary>Any type the VM cannot represent yet (classes, structs, arrays, strings, pointers…).</summary>
internal sealed record UnsupportedClrType(string Description) : ClrType(Description);

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
        PrimitiveTypeCode code = type switch
        {
            PrimitiveClrType p => p.Code,
            EnumClrType e => e.Underlying,
            _ => throw new TranslationException($"{context}: type '{type}' is not supported yet; {UnsupportedReason(type)}."),
        };

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

    private static string UnsupportedReason(ClrType type) => type.Display switch
    {
        "string" => "strings arrive with the managed heap",
        var d when d.EndsWith("[]", StringComparison.Ordinal) || d.EndsWith("[,]", StringComparison.Ordinal) => "arrays arrive with the managed heap",
        _ => "this iteration supports numeric types, bool, char and enums only; classes and structs arrive with the managed heap",
    };

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

    /// <summary>Converts a decoded method signature into a VM signature.</summary>
    public static VmSignature ToVmSignature(MethodSignature<ClrType> signature, string context) =>
        new(signature.ParameterTypes.Select((p, i) => MapValue(p, $"{context} parameter {i}").Type!.Value).ToArray(),
            Map(signature.ReturnType, $"{context} return type").Type);
}

/// <summary>
/// Decodes metadata signatures into <see cref="ClrType"/>. Enums defined in the cartridge
/// are resolved from its own metadata; enums referenced from the SDK are resolved by
/// reflection over the SDK assembly.
/// </summary>
internal sealed class ClrTypeProvider : ISignatureTypeProvider<ClrType, object?>
{
    private const string SdkAssemblyName = "ZTX16.Sdk";

    private readonly MetadataReader _reader;
    private readonly Dictionary<TypeDefinitionHandle, ClrType> _definitionCache = [];

    public ClrTypeProvider(MetadataReader reader)
    {
        _reader = reader;
    }

    public ClrType GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.String => new UnsupportedClrType("string"),
        PrimitiveTypeCode.Object => new UnsupportedClrType("object"),
        PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => new UnsupportedClrType("native int"),
        PrimitiveTypeCode.TypedReference => new UnsupportedClrType("TypedReference"),
        _ => new PrimitiveClrType(typeCode),
    };

    public ClrType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        if (_definitionCache.TryGetValue(handle, out ClrType? cached))
        {
            return cached;
        }

        TypeDefinition definition = reader.GetTypeDefinition(handle);
        string fullName = FullName(reader, definition.Namespace, definition.Name);
        ClrType result = new UnsupportedClrType(fullName);

        if (IsEnum(reader, definition))
        {
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
            if (reader.GetString(assembly.Name) == SdkAssemblyName
                && SdkAbi.SdkAssembly.GetType(fullName) is { IsEnum: true } sdkEnum)
            {
                return new EnumClrType(fullName, Type.GetTypeCode(Enum.GetUnderlyingType(sdkEnum)) switch
                {
                    TypeCode.SByte => PrimitiveTypeCode.SByte,
                    TypeCode.Byte => PrimitiveTypeCode.Byte,
                    TypeCode.Int16 => PrimitiveTypeCode.Int16,
                    TypeCode.UInt16 => PrimitiveTypeCode.UInt16,
                    TypeCode.Int32 => PrimitiveTypeCode.Int32,
                    TypeCode.UInt32 => PrimitiveTypeCode.UInt32,
                    TypeCode.Int64 => PrimitiveTypeCode.Int64,
                    _ => PrimitiveTypeCode.UInt64,
                });
            }
        }

        return new UnsupportedClrType(fullName);
    }

    public ClrType GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        new UnsupportedClrType("generic type");

    public ClrType GetSZArrayType(ClrType elementType) => new UnsupportedClrType($"{elementType}[]");

    public ClrType GetArrayType(ClrType elementType, ArrayShape shape) => new UnsupportedClrType($"{elementType}[,]");

    public ClrType GetByReferenceType(ClrType elementType) => new UnsupportedClrType($"ref {elementType}");

    public ClrType GetPointerType(ClrType elementType) => new UnsupportedClrType($"{elementType}*");

    public ClrType GetPinnedType(ClrType elementType) => elementType;

    public ClrType GetModifiedType(ClrType modifier, ClrType unmodifiedType, bool isRequired) => unmodifiedType;

    public ClrType GetFunctionPointerType(MethodSignature<ClrType> signature) => new UnsupportedClrType("function pointer");

    public ClrType GetGenericInstantiation(ClrType genericType, ImmutableArray<ClrType> typeArguments) =>
        new UnsupportedClrType($"{genericType}<…>");

    public ClrType GetGenericMethodParameter(object? genericContext, int index) => new UnsupportedClrType($"!!{index}");

    public ClrType GetGenericTypeParameter(object? genericContext, int index) => new UnsupportedClrType($"!{index}");

    public static string FullName(MetadataReader reader, StringHandle ns, StringHandle name)
    {
        string n = reader.GetString(ns);
        return n.Length == 0 ? reader.GetString(name) : $"{n}.{reader.GetString(name)}";
    }

    private static bool IsEnum(MetadataReader reader, TypeDefinition definition)
    {
        EntityHandle baseType = definition.BaseType;
        if (baseType.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)baseType);
        return reader.GetString(reference.Namespace) == "System" && reader.GetString(reference.Name) == "Enum";
    }
}
