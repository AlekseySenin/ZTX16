using System.Reflection;
using ZTX16.VM;

namespace ZTX16.Abi;

/// <summary>A syscall declared by the SDK.</summary>
/// <param name="Name">Stable syscall name.</param>
/// <param name="Signature">VM signature derived from the SDK method.</param>
/// <param name="DeclaringType">Full name of the SDK type, e.g. <c>ZTX16.Graphics</c>.</param>
/// <param name="MethodName">CLR method name, e.g. <c>FillRect</c> or <c>get_TickCount</c>.</param>
public sealed record SyscallDescriptor(string Name, VmSignature Signature, string DeclaringType, string MethodName);

/// <summary>
/// The platform ABI as declared by the SDK: every <see cref="SyscallAttribute"/> method,
/// with its VM signature. Runtime and toolchain both read it from here, so the ABI is
/// described in exactly one place.
/// </summary>
public static class SdkAbi
{
    private static readonly Lazy<Catalog> Instance = new(Build);

    /// <summary>The SDK assembly.</summary>
    public static Assembly SdkAssembly => typeof(SyscallAttribute).Assembly;

    /// <summary>All declared syscalls by name.</summary>
    public static IReadOnlyDictionary<string, SyscallDescriptor> Syscalls => Instance.Value.ByName;

    /// <summary>Finds the syscall implemented by an SDK method, or null if the method is not a syscall.</summary>
    public static SyscallDescriptor? FindByMethod(string declaringType, string methodName) =>
        Instance.Value.ByMethod.GetValueOrDefault((declaringType, methodName));

    /// <summary>
    /// Maps a CLR type to its VM representation: integral types up to 32 bits, <c>bool</c>,
    /// <c>char</c> and 32-bit enums are i32; 64-bit integers are i64; <c>float</c> and
    /// <c>double</c> are f32/f64; <c>void</c> is null.
    /// </summary>
    /// <exception cref="NotSupportedException">The type has no VM representation yet.</exception>
    public static VmType? MapType(Type type)
    {
        if (type == typeof(void))
        {
            return null;
        }

        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean or TypeCode.Char or TypeCode.SByte or TypeCode.Byte
                or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 => VmType.I32,
            TypeCode.Int64 or TypeCode.UInt64 => VmType.I64,
            TypeCode.Single => VmType.F32,
            TypeCode.Double => VmType.F64,
            TypeCode.String => VmType.Ref,
            _ => throw new NotSupportedException($"Type '{type}' has no ZTX16 VM representation."),
        };
    }

    private static Catalog Build()
    {
        var byName = new Dictionary<string, SyscallDescriptor>(StringComparer.Ordinal);
        var byMethod = new Dictionary<(string, string), SyscallDescriptor>();

        foreach (Type type in SdkAssembly.GetExportedTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<SyscallAttribute>() is not { } attribute)
                {
                    continue;
                }

                VmType?[] parameterTypes = method.GetParameters().Select(p => MapType(p.ParameterType)).ToArray();
                var signature = new VmSignature(parameterTypes.Select(t => t!.Value).ToArray(), MapType(method.ReturnType));
                var descriptor = new SyscallDescriptor(attribute.Name, signature, type.FullName!, method.Name);

                if (!byName.TryAdd(attribute.Name, descriptor))
                {
                    throw new InvalidOperationException($"Syscall '{attribute.Name}' is declared more than once in the SDK.");
                }

                if (!byMethod.TryAdd((descriptor.DeclaringType, descriptor.MethodName), descriptor))
                {
                    throw new InvalidOperationException($"SDK syscall method '{type.FullName}.{method.Name}' is overloaded; syscall methods must have unique names.");
                }
            }
        }

        return new Catalog(byName, byMethod);
    }

    private sealed record Catalog(
        IReadOnlyDictionary<string, SyscallDescriptor> ByName,
        IReadOnlyDictionary<(string, string), SyscallDescriptor> ByMethod);
}
