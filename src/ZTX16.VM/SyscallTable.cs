namespace ZTX16.VM;

/// <summary>
/// Implementation of one syscall. Arguments arrive as raw 64-bit slots in declaration
/// order; use <see cref="VmValue"/> to decode numbers and <paramref name="context"/> for
/// strings. The return value is ignored for syscalls without a result.
/// </summary>
public delegate long SyscallHandler(ISyscallContext context, ReadOnlySpan<long> args);

/// <summary>A syscall implementation that only uses numeric arguments.</summary>
public delegate long SimpleSyscallHandler(ReadOnlySpan<long> args);

/// <summary>Access to the calling VM instance from inside a syscall.</summary>
public interface ISyscallContext
{
    /// <summary>Reads a string argument (null for a null reference).</summary>
    /// <exception cref="ArgumentException">The reference is not a string.</exception>
    string? ReadString(long handle);

    /// <summary>Allocates a string on the VM heap and returns its reference, for use as a result.</summary>
    long NewString(string value);
}

/// <summary>Thrown when a module imports a syscall the runtime does not provide.</summary>
public sealed class LinkException : Exception
{
    /// <summary>Creates the exception.</summary>
    public LinkException(string message)
        : base(message)
    {
    }
}

/// <summary>A syscall the runtime provides: name, signature and implementation.</summary>
public sealed record SyscallDefinition(string Name, VmSignature Signature, SyscallHandler Handler);

/// <summary>
/// The set of platform operations available to cartridge code. This table is the
/// sandbox boundary: an operation not registered here does not exist for the cartridge.
/// </summary>
public sealed class SyscallTable
{
    private readonly Dictionary<string, SyscallDefinition> _definitions = new(StringComparer.Ordinal);

    /// <summary>All registered syscalls.</summary>
    public IEnumerable<SyscallDefinition> Definitions => _definitions.Values;

    /// <summary>Registers a syscall.</summary>
    /// <exception cref="InvalidOperationException">The name is already registered.</exception>
    public SyscallTable Add(string name, VmSignature signature, SyscallHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_definitions.TryAdd(name, new SyscallDefinition(name, signature, handler)))
        {
            throw new InvalidOperationException($"Syscall '{name}' is already registered.");
        }

        return this;
    }

    /// <summary>Registers a syscall that only uses numeric arguments.</summary>
    public SyscallTable Add(string name, VmSignature signature, SimpleSyscallHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(name, signature, (_, args) => handler(args));
    }

    /// <summary>Resolves an import against this table.</summary>
    /// <exception cref="LinkException">The syscall is unknown or its signature differs.</exception>
    public SyscallHandler Resolve(VmImport import)
    {
        if (!_definitions.TryGetValue(import.Name, out SyscallDefinition? definition))
        {
            throw new LinkException($"Unknown syscall '{import.Name}'.");
        }

        if (!definition.Signature.Matches(import.Signature))
        {
            throw new LinkException(
                $"Syscall '{import.Name}' imported as {import.Signature} but provided as {definition.Signature}.");
        }

        return definition.Handler;
    }
}

/// <summary>Encoding of VM values in raw 64-bit slots.</summary>
public static class VmValue
{
    /// <summary>Encodes an i32.</summary>
    public static long FromI32(int value) => value;

    /// <summary>Encodes a Boolean as i32 0/1.</summary>
    public static long FromBool(bool value) => value ? 1 : 0;

    /// <summary>Encodes an f32.</summary>
    public static long FromF32(float value) => BitConverter.SingleToInt32Bits(value);

    /// <summary>Encodes an f64.</summary>
    public static long FromF64(double value) => BitConverter.DoubleToInt64Bits(value);

    /// <summary>Decodes an i32.</summary>
    public static int ToI32(long slot) => (int)slot;

    /// <summary>Decodes an i32 Boolean (non-zero = true).</summary>
    public static bool ToBool(long slot) => (int)slot != 0;

    /// <summary>Decodes an f32.</summary>
    public static float ToF32(long slot) => BitConverter.Int32BitsToSingle((int)slot);

    /// <summary>Decodes an f64.</summary>
    public static double ToF64(long slot) => BitConverter.Int64BitsToDouble(slot);
}
