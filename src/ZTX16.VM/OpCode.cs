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
    /// <summary>Swap the two top values.</summary>
    Swap = 0x1A,

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

    // References and objects
    /// <summary>Push a null reference.</summary>
    ConstNull = 0x05,
    /// <summary>Pop two references; push i32 1 if they are the same object (or both null).</summary>
    EqRef = 0x6D,
    /// <summary>
    /// Allocate an instance of a class and run its constructor. Operands: u16 class, u16 constructor
    /// function. Pops the constructor's arguments (excluding <c>this</c>), pushes the new reference.
    /// </summary>
    New = 0xA0,
    /// <summary>Pop an object reference; push the field. Operand: u16 field.</summary>
    LoadField = 0xA1,
    /// <summary>Pop a value and an object reference; store the field. Operand: u16 field.</summary>
    StoreField = 0xA2,
    /// <summary>Virtual/interface call through a selector. Operand: u16 selector. The receiver is the first argument.</summary>
    CallVirtual = 0xA3,
    /// <summary>Pop a reference; push it if it is an instance of the class (or null otherwise). Operand: u16 class.</summary>
    IsInstance = 0xA4,
    /// <summary>Pop a reference; push it, trapping if it is non-null and not an instance of the class. Operand: u16 class.</summary>
    CastClass = 0xA5,

    // Arrays
    /// <summary>Pop an i32 length; push a new zeroed array. Operand: u16 <see cref="VmArrayKind"/>.</summary>
    NewArray = 0xA8,
    /// <summary>Pop an i32 index and an array; push the element. Operand: u16 <see cref="VmElementAccess"/>.</summary>
    LoadElement = 0xA9,
    /// <summary>Pop a value, an i32 index and an array; store the element. Operand: u16 <see cref="VmElementAccess"/>.</summary>
    StoreElement = 0xAA,
    /// <summary>Pop an array; push its length as i32.</summary>
    ArrayLength = 0xAB,

    // Strings
    /// <summary>Push a string from the module string table. Operand: u16 string index.</summary>
    LoadString = 0xAC,
    /// <summary>Pop a string; push its length.</summary>
    StringLength = 0xB0,
    /// <summary>Pop an i32 index and a string; push the UTF-16 code unit as i32.</summary>
    StringCharAt = 0xB1,
    /// <summary>Pop two strings; push their concatenation (null is treated as empty).</summary>
    StringConcat = 0xB2,
    /// <summary>Pop two strings; push i32 1 if they are equal by value (null-safe).</summary>
    StringEquals = 0xB3,
    /// <summary>Pop an array of strings; push their concatenation (null elements are empty).</summary>
    StringConcatArray = 0xB4,

    // Runtime support for generic collections
    /// <summary>
    /// Pop a value of the operand's <see cref="VmType"/>; push an i32 hash. Strings hash by content,
    /// other references by identity. Operand: u16 type.
    /// </summary>
    Hash = 0xB8,
    /// <summary>Pop two references; push i32 1 if they are the same object or two strings with equal content.</summary>
    ValueEquals = 0xB9,
    /// <summary>Pop a string and trap with it as the message.</summary>
    Trap = 0xBA,
    /// <summary>
    /// Pop a value and push its text (invariant culture, like .NET's <c>ToString()</c>).
    /// Operand: u16 <see cref="VmFormatKind"/>.
    /// </summary>
    ToText = 0xBB,

    // Value types (structs): heap objects with copy semantics; null is the default value.
    /// <summary>Pop a reference; push a deep copy of the value-type instance (null stays null).</summary>
    Clone = 0xC0,
    /// <summary>Pop a source value and a destination instance; copy the source fields into the destination (null source = zero).</summary>
    CopyInto = 0xC1,
    /// <summary>Push the value-type instance stored in a local, creating a default one first if the local is null. Operands: u16 local, u16 class.</summary>
    MaterializeLocal = 0xC2,
    /// <summary>Pop an object; push the value-type instance stored in its field, creating it if null. Operands: u16 field, u16 class.</summary>
    MaterializeField = 0xC3,
    /// <summary>Push the value-type instance stored in a global, creating it if null. Operands: u16 global, u16 class.</summary>
    MaterializeGlobal = 0xC4,
    /// <summary>Pop an i32 index and an array; push the value-type element, creating it if null. Operand: u16 class.</summary>
    MaterializeElement = 0xC5,

    // Managed pointers to non-struct locations (ref/out parameters, ref locals)
    /// <summary>Push a new cell (a heap-allocated variable) holding zero. Operand: u16 element access describing its type.</summary>
    NewCell = 0xC8,
    /// <summary>Pop an object; push a pointer to one of its fields. Operands: u16 field, u16 element access of the field type.</summary>
    FieldPointer = 0xC9,
    /// <summary>Pop an i32 index and an array; push a pointer to the element. Operand: u16 element access.</summary>
    ElementPointer = 0xCA,
    /// <summary>Push a pointer to a global. Operands: u16 global, u16 element access of the global type.</summary>
    GlobalPointer = 0xCB,
    /// <summary>Pop a pointer; push the value it points to. Operand: u16 element access.</summary>
    LoadIndirect = 0xCC,
    /// <summary>Pop a value and a pointer; store the value through the pointer. Operand: u16 element access.</summary>
    StoreIndirect = 0xCD,
}

/// <summary>How <see cref="OpCode.ToText"/> interprets its operand.</summary>
public enum VmFormatKind : byte
{
    /// <summary>i32 as signed int.</summary>
    Int32 = 1,

    /// <summary>i32 as unsigned int.</summary>
    UInt32 = 2,

    /// <summary>i64 as signed long.</summary>
    Int64 = 3,

    /// <summary>i64 as unsigned long.</summary>
    UInt64 = 4,

    /// <summary>f32.</summary>
    Single = 5,

    /// <summary>f64.</summary>
    Double = 6,

    /// <summary>i32 as a UTF-16 char.</summary>
    Char = 7,

    /// <summary>i32 as bool ("True"/"False").</summary>
    Boolean = 8,
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
    U16Pair,
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
        OperandKind.I32 or OperandKind.F32 or OperandKind.BranchTarget or OperandKind.U16Pair => 4,
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
                    or OpCode.Call or OpCode.Syscall
                    or OpCode.LoadField or OpCode.StoreField or OpCode.CallVirtual
                    or OpCode.IsInstance or OpCode.CastClass
                    or OpCode.NewArray or OpCode.LoadElement or OpCode.StoreElement
                    or OpCode.LoadString or OpCode.Hash or OpCode.ToText => OperandKind.U16,
                OpCode.New or OpCode.MaterializeLocal or OpCode.MaterializeField or OpCode.MaterializeGlobal
                    or OpCode.FieldPointer or OpCode.GlobalPointer => OperandKind.U16Pair,
                OpCode.MaterializeElement or OpCode.NewCell or OpCode.ElementPointer
                    or OpCode.LoadIndirect or OpCode.StoreIndirect => OperandKind.U16,
                OpCode.Branch or OpCode.BranchIfTrue or OpCode.BranchIfFalse => OperandKind.BranchTarget,
                _ => OperandKind.None,
            };
        }

        return table;
    }
}
