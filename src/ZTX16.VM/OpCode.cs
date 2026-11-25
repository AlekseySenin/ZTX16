namespace ZTX16.VM;

/// <summary>
/// ZTX16 VM instruction set (format version 1). Instructions are typed: the operand
/// types of every opcode are fixed, so the verifier can check a function in one pass
/// and the interpreter needs no runtime type tests.
/// </summary>
/// <remarks>
/// Encoding: one opcode byte followed by its immediate operand, little-endian.
/// Branch targets are absolute byte offsets within the function's code.
/// </remarks>
public enum OpCode : byte
{
    /// <summary>No operation.</summary>
    Nop = 0x00,

    // Constants
    /// <summary>Push an i32 immediate.</summary>
    ConstI32 = 0x01,
    /// <summary>Push an i64 immediate.</summary>
    ConstI64 = 0x02,
    /// <summary>Push an f32 immediate.</summary>
    ConstF32 = 0x03,
    /// <summary>Push an f64 immediate.</summary>
    ConstF64 = 0x04,

    // Variables
    /// <summary>Push local (parameters come first). Operand: u16 index.</summary>
    LoadLocal = 0x10,
    /// <summary>Pop into local. Operand: u16 index.</summary>
    StoreLocal = 0x11,
    /// <summary>Push global. Operand: u16 index.</summary>
    LoadGlobal = 0x12,
    /// <summary>Pop into global. Operand: u16 index.</summary>
    StoreGlobal = 0x13,

    // Stack
    /// <summary>Duplicate the top value.</summary>
    Dup = 0x18,
    /// <summary>Discard the top value.</summary>
    Pop = 0x19,

    // i32 arithmetic and bitwise
    /// <summary>i32 add (wrapping).</summary>
    AddI32 = 0x20,
    /// <summary>i32 subtract (wrapping).</summary>
    SubI32 = 0x21,
    /// <summary>i32 multiply (wrapping).</summary>
    MulI32 = 0x22,
    /// <summary>i32 signed divide; traps on division by zero or overflow.</summary>
    DivI32 = 0x23,
    /// <summary>i32 signed remainder; traps on division by zero.</summary>
    RemI32 = 0x24,
    /// <summary>i32 negate (wrapping).</summary>
    NegI32 = 0x25,
    /// <summary>i32 bitwise and.</summary>
    AndI32 = 0x26,
    /// <summary>i32 bitwise or.</summary>
    OrI32 = 0x27,
    /// <summary>i32 bitwise xor.</summary>
    XorI32 = 0x28,
    /// <summary>i32 bitwise not.</summary>
    NotI32 = 0x29,
    /// <summary>i32 shift left (count masked to 5 bits).</summary>
    ShlI32 = 0x2A,
    /// <summary>i32 arithmetic shift right (count masked to 5 bits).</summary>
    ShrI32 = 0x2B,
    /// <summary>i32 logical shift right (count masked to 5 bits).</summary>
    ShrUnI32 = 0x2C,
    /// <summary>i32 unsigned divide; traps on division by zero.</summary>
    DivUnI32 = 0x2D,
    /// <summary>i32 unsigned remainder; traps on division by zero.</summary>
    RemUnI32 = 0x2E,

    // i64 arithmetic and bitwise
    /// <summary>i64 add (wrapping).</summary>
    AddI64 = 0x30,
    /// <summary>i64 subtract (wrapping).</summary>
    SubI64 = 0x31,
    /// <summary>i64 multiply (wrapping).</summary>
    MulI64 = 0x32,
    /// <summary>i64 signed divide; traps on division by zero or overflow.</summary>
    DivI64 = 0x33,
    /// <summary>i64 signed remainder; traps on division by zero.</summary>
    RemI64 = 0x34,
    /// <summary>i64 negate (wrapping).</summary>
    NegI64 = 0x35,
    /// <summary>i64 bitwise and.</summary>
    AndI64 = 0x36,
    /// <summary>i64 bitwise or.</summary>
    OrI64 = 0x37,
    /// <summary>i64 bitwise xor.</summary>
    XorI64 = 0x38,
    /// <summary>i64 bitwise not.</summary>
    NotI64 = 0x39,
    /// <summary>i64 shift left; the count is an i32 masked to 6 bits.</summary>
    ShlI64 = 0x3A,
    /// <summary>i64 arithmetic shift right; the count is an i32 masked to 6 bits.</summary>
    ShrI64 = 0x3B,
    /// <summary>i64 logical shift right; the count is an i32 masked to 6 bits.</summary>
    ShrUnI64 = 0x3C,
    /// <summary>i64 unsigned divide; traps on division by zero.</summary>
    DivUnI64 = 0x3D,
    /// <summary>i64 unsigned remainder; traps on division by zero.</summary>
    RemUnI64 = 0x3E,

    // f32 arithmetic
    /// <summary>f32 add.</summary>
    AddF32 = 0x40,
    /// <summary>f32 subtract.</summary>
    SubF32 = 0x41,
    /// <summary>f32 multiply.</summary>
    MulF32 = 0x42,
    /// <summary>f32 divide.</summary>
    DivF32 = 0x43,
    /// <summary>f32 remainder (IEEE fmod semantics as in C#).</summary>
    RemF32 = 0x44,
    /// <summary>f32 negate.</summary>
    NegF32 = 0x45,

    // f64 arithmetic
    /// <summary>f64 add.</summary>
    AddF64 = 0x48,
    /// <summary>f64 subtract.</summary>
    SubF64 = 0x49,
    /// <summary>f64 multiply.</summary>
    MulF64 = 0x4A,
    /// <summary>f64 divide.</summary>
    DivF64 = 0x4B,
    /// <summary>f64 remainder.</summary>
    RemF64 = 0x4C,
    /// <summary>f64 negate.</summary>
    NegF64 = 0x4D,

    // Comparisons: pop two operands, push i32 0/1.
    /// <summary>i32 equal.</summary>
    EqI32 = 0x50,
    /// <summary>i32 signed less than.</summary>
    LtI32 = 0x51,
    /// <summary>i32 signed greater than.</summary>
    GtI32 = 0x52,
    /// <summary>i32 unsigned less than.</summary>
    LtUnI32 = 0x53,
    /// <summary>i32 unsigned greater than.</summary>
    GtUnI32 = 0x54,
    /// <summary>i64 equal.</summary>
    EqI64 = 0x58,
    /// <summary>i64 signed less than.</summary>
    LtI64 = 0x59,
    /// <summary>i64 signed greater than.</summary>
    GtI64 = 0x5A,
    /// <summary>i64 unsigned less than.</summary>
    LtUnI64 = 0x5B,
    /// <summary>i64 unsigned greater than.</summary>
    GtUnI64 = 0x5C,
    /// <summary>f32 equal (false if either is NaN).</summary>
    EqF32 = 0x60,
    /// <summary>f32 less than (false if either is NaN).</summary>
    LtF32 = 0x61,
    /// <summary>f32 greater than (false if either is NaN).</summary>
    GtF32 = 0x62,
    /// <summary>f32 less than or unordered.</summary>
    LtUnF32 = 0x63,
    /// <summary>f32 greater than or unordered.</summary>
    GtUnF32 = 0x64,
    /// <summary>f64 equal (false if either is NaN).</summary>
    EqF64 = 0x68,
    /// <summary>f64 less than (false if either is NaN).</summary>
    LtF64 = 0x69,
    /// <summary>f64 greater than (false if either is NaN).</summary>
    GtF64 = 0x6A,
    /// <summary>f64 less than or unordered.</summary>
    LtUnF64 = 0x6B,
    /// <summary>f64 greater than or unordered.</summary>
    GtUnF64 = 0x6C,

    // Conversions (C# unchecked semantics; float→int saturates, NaN → 0).
    /// <summary>i32 → i64 (sign-extend).</summary>
    I32ToI64 = 0x70,
    /// <summary>i32 → i64 (zero-extend).</summary>
    U32ToI64 = 0x71,
    /// <summary>i64 → i32 (truncate).</summary>
    I64ToI32 = 0x72,
    /// <summary>i32 → f32.</summary>
    I32ToF32 = 0x73,
    /// <summary>i32 → f64.</summary>
    I32ToF64 = 0x74,
    /// <summary>i64 → f32.</summary>
    I64ToF32 = 0x75,
    /// <summary>i64 → f64.</summary>
    I64ToF64 = 0x76,
    /// <summary>f32 → i32 (truncate toward zero, saturating).</summary>
    F32ToI32 = 0x77,
    /// <summary>f32 → i64 (truncate toward zero, saturating).</summary>
    F32ToI64 = 0x78,
    /// <summary>f64 → i32 (truncate toward zero, saturating).</summary>
    F64ToI32 = 0x79,
    /// <summary>f64 → i64 (truncate toward zero, saturating).</summary>
    F64ToI64 = 0x7A,
    /// <summary>f32 → f64.</summary>
    F32ToF64 = 0x7B,
    /// <summary>f64 → f32.</summary>
    F64ToF32 = 0x7C,
    /// <summary>i32 → i8 sign-extended back to i32 (C# sbyte cast).</summary>
    I32ToI8 = 0x7D,
    /// <summary>i32 → u8 zero-extended back to i32 (C# byte cast).</summary>
    I32ToU8 = 0x7E,
    /// <summary>i32 → i16 sign-extended back to i32 (C# short cast).</summary>
    I32ToI16 = 0x7F,
    /// <summary>i32 → u16 zero-extended back to i32 (C# ushort/char cast).</summary>
    I32ToU16 = 0x80,
    /// <summary>i64 interpreted as unsigned → f64.</summary>
    U64ToF64 = 0x81,
    /// <summary>f32 → u64 (truncate toward zero, saturating; negatives and NaN → 0).</summary>
    F32ToU64 = 0x82,
    /// <summary>f64 → u64 (truncate toward zero, saturating; negatives and NaN → 0).</summary>
    F64ToU64 = 0x83,
    /// <summary>f32 → u32 (truncate toward zero, saturating; negatives and NaN → 0).</summary>
    F32ToU32 = 0x84,
    /// <summary>f64 → u32 (truncate toward zero, saturating; negatives and NaN → 0).</summary>
    F64ToU32 = 0x85,

    // Control flow
    /// <summary>Unconditional branch. Operand: i32 target offset.</summary>
    Branch = 0x90,
    /// <summary>Pop i32; branch if non-zero. Operand: i32 target offset.</summary>
    BranchIfTrue = 0x91,
    /// <summary>Pop i32; branch if zero. Operand: i32 target offset.</summary>
    BranchIfFalse = 0x92,
    /// <summary>Call a module function. Operand: u16 function index.</summary>
    Call = 0x98,
    /// <summary>Call a platform syscall. Operand: u16 import index.</summary>
    Syscall = 0x99,
    /// <summary>Return from the function; the result, if any, is on the stack.</summary>
    Return = 0x9A,
}

/// <summary>Kind of immediate operand that follows an opcode.</summary>
internal enum OperandKind : byte
{
    None,
    I32,
    I64,
    F32,
    F64,
    U16,
    BranchTarget,
}

/// <summary>Static metadata about opcodes shared by builder, verifier, interpreter and disassembler.</summary>
internal static class OpCodeInfo
{
    private static readonly OperandKind?[] Operands = BuildOperandTable();

    /// <summary>True if <paramref name="op"/> is a defined opcode.</summary>
    public static bool IsDefined(OpCode op) => Operands[(byte)op] is not null;

    /// <summary>Operand kind of <paramref name="op"/>. Must be a defined opcode.</summary>
    public static OperandKind OperandOf(OpCode op) => Operands[(byte)op]!.Value;

    /// <summary>Encoded size of the operand.</summary>
    public static int OperandSize(OperandKind kind) => kind switch
    {
        OperandKind.None => 0,
        OperandKind.U16 => 2,
        OperandKind.I32 or OperandKind.F32 or OperandKind.BranchTarget => 4,
        OperandKind.I64 or OperandKind.F64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static OperandKind?[] BuildOperandTable()
    {
        var table = new OperandKind?[256];
        foreach (OpCode op in Enum.GetValues<OpCode>())
        {
            table[(byte)op] = op switch
            {
                OpCode.ConstI32 => OperandKind.I32,
                OpCode.ConstI64 => OperandKind.I64,
                OpCode.ConstF32 => OperandKind.F32,
                OpCode.ConstF64 => OperandKind.F64,
                OpCode.LoadLocal or OpCode.StoreLocal or OpCode.LoadGlobal or OpCode.StoreGlobal
                    or OpCode.Call or OpCode.Syscall => OperandKind.U16,
                OpCode.Branch or OpCode.BranchIfTrue or OpCode.BranchIfFalse => OperandKind.BranchTarget,
                _ => OperandKind.None,
            };
        }

        return table;
    }
}
