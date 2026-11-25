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
/// Marks the cartridge entry class. It must be static and declare static
/// <c>Initialize()</c>, <c>Tick()</c> and <c>Render()</c>, and may declare <c>Deinitialize()</c>.
/// </summary>
/// <remarks>
/// Provisional form while the VM has no heap: once classes are supported, the entry point
/// becomes the platform-defined lifecycle interface.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CartridgeAttribute : Attribute
{
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
