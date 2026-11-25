namespace ZTX16.VM;

/// <summary>Value types of the ZTX16 VM. Every stack slot, local and global holds one of these.</summary>
/// <remarks>Reference types are added together with the managed heap.</remarks>
public enum VmType : byte
{
    /// <summary>32-bit signed integer. Also used for Booleans (0 = false, non-zero = true).</summary>
    I32 = 1,

    /// <summary>64-bit signed integer.</summary>
    I64 = 2,

    /// <summary>32-bit IEEE float.</summary>
    F32 = 3,

    /// <summary>64-bit IEEE float.</summary>
    F64 = 4,
}

/// <summary>Helpers for <see cref="VmType"/>.</summary>
public static class VmTypes
{
    /// <summary>True if <paramref name="type"/> is a defined VM type.</summary>
    public static bool IsDefined(VmType type) => type is >= VmType.I32 and <= VmType.F64;

    /// <summary>Lower-case mnemonic used in disassembly and error messages.</summary>
    public static string Name(VmType type) => type switch
    {
        VmType.I32 => "i32",
        VmType.I64 => "i64",
        VmType.F32 => "f32",
        VmType.F64 => "f64",
        _ => $"?{(byte)type}",
    };
}

/// <summary>A function or syscall signature.</summary>
/// <param name="Parameters">Parameter types, in order.</param>
/// <param name="Result">Result type, or null for no result.</param>
public sealed record VmSignature(IReadOnlyList<VmType> Parameters, VmType? Result)
{
    /// <summary>Signature with no parameters and no result.</summary>
    public static VmSignature Void { get; } = new([], null);

    /// <summary>Creates a signature.</summary>
    public static VmSignature Of(VmType? result, params VmType[] parameters) => new(parameters, result);

    /// <summary>True if both signatures have identical parameter and result types.</summary>
    public bool Matches(VmSignature other) =>
        Result == other.Result && Parameters.SequenceEqual(other.Parameters);

    /// <inheritdoc />
    public override string ToString() =>
        $"({string.Join(", ", Parameters.Select(VmTypes.Name))}) -> {(Result is { } r ? VmTypes.Name(r) : "void")}";
}
