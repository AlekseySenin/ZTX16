namespace ZTX16;

/// <summary>
/// Marks an SDK method or property accessor as a platform syscall. The toolchain turns
/// every call to it into a VM syscall with this name; the method body never runs.
/// </summary>
/// <remarks>
/// These declarations are the single description of the platform ABI: the toolchain
/// takes syscall names from them, and the runtime takes syscall signatures from them.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SyscallAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    public SyscallAttribute(string name)
    {
        Name = name;
    }

    /// <summary>Stable syscall name, e.g. <c>graphics.fill_rect</c>.</summary>
    public string Name { get; }
}

/// <summary>
/// The cartridge lifecycle contract. A cartridge has exactly one non-abstract class that
/// implements this interface and has a public parameterless constructor; the runtime creates
/// it once and drives it from the fixed 60 Hz loop.
/// </summary>
public interface ICartridge
{
    /// <summary>Called once before the first tick.</summary>
    void Initialize();

    /// <summary>Game Logic phase: called once per tick, after Input.</summary>
    void Tick();

    /// <summary>Render phase: called once per tick, after Audio Update.</summary>
    void Render();

    /// <summary>Controlled shutdown. Optional; not called after an unhandled failure.</summary>
    void Deinitialize()
    {
    }
}

/// <summary>Thrown if SDK code is executed on .NET instead of the ZTX16 VM.</summary>
public sealed class NotInVmException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public NotInVmException()
        : base("ZTX16 SDK methods only run inside the ZTX16 VM. Build the cartridge with the ZTX16 toolchain.")
    {
    }
}
