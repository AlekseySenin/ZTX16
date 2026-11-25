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

    /// <summary>Reference to a heap object (instance, array or string), or null.</summary>
    Ref = 5,
}

/// <summary>Storage of array elements.</summary>
public enum VmArrayKind : byte
{
    /// <summary>8-bit integers (sbyte, byte, bool).</summary>
    Bits8 = 1,

    /// <summary>16-bit integers (short, ushort, char).</summary>
    Bits16 = 2,

    /// <summary>32-bit integers (int, uint, 32-bit enums).</summary>
    Bits32 = 3,

    /// <summary>64-bit integers.</summary>
    Bits64 = 4,

    /// <summary>32-bit floats.</summary>
    F32 = 5,

    /// <summary>64-bit floats.</summary>
    F64 = 6,

    /// <summary>References.</summary>
    Ref = 7,
}

/// <summary>How an array element is read or written; determines width and sign extension.</summary>
public enum VmElementAccess : byte
{
    /// <summary>Signed 8-bit, as i32.</summary>
    I8 = 1,

    /// <summary>Unsigned 8-bit, as i32.</summary>
    U8 = 2,

    /// <summary>Signed 16-bit, as i32.</summary>
    I16 = 3,

    /// <summary>Unsigned 16-bit, as i32.</summary>
    U16 = 4,

    /// <summary>32-bit integer.</summary>
    I32 = 5,

    /// <summary>64-bit integer.</summary>
    I64 = 6,

    /// <summary>32-bit float.</summary>
    F32 = 7,

    /// <summary>64-bit float.</summary>
    F64 = 8,

    /// <summary>Reference.</summary>
    Ref = 9,
}

/// <summary>Helpers for <see cref="VmType"/>.</summary>
public static class VmTypes
{
    /// <summary>True if <paramref name="type"/> is a defined VM type.</summary>
    public static bool IsDefined(VmType type) => type is >= VmType.I32 and <= VmType.Ref;

    /// <summary>Stack type produced or consumed by an element access.</summary>
    public static VmType OfAccess(VmElementAccess access) => access switch
    {
        VmElementAccess.I8 or VmElementAccess.U8 or VmElementAccess.I16 or VmElementAccess.U16 or VmElementAccess.I32 => VmType.I32,
        VmElementAccess.I64 => VmType.I64,
        VmElementAccess.F32 => VmType.F32,
        VmElementAccess.F64 => VmType.F64,
        _ => VmType.Ref,
    };

    /// <summary>The array storage an access requires.</summary>
    public static VmArrayKind StorageOf(VmElementAccess access) => access switch
    {
        VmElementAccess.I8 or VmElementAccess.U8 => VmArrayKind.Bits8,
        VmElementAccess.I16 or VmElementAccess.U16 => VmArrayKind.Bits16,
        VmElementAccess.I32 => VmArrayKind.Bits32,
        VmElementAccess.I64 => VmArrayKind.Bits64,
        VmElementAccess.F32 => VmArrayKind.F32,
        VmElementAccess.F64 => VmArrayKind.F64,
        _ => VmArrayKind.Ref,
    };

    /// <summary>Lower-case mnemonic used in disassembly and error messages.</summary>
    public static string Name(VmType type) => type switch
    {
        VmType.I32 => "i32",
        VmType.I64 => "i64",
        VmType.F32 => "f32",
        VmType.F64 => "f64",
        VmType.Ref => "ref",
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
