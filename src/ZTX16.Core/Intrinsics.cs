using System.ComponentModel;

namespace ZTX16.Core;

/// <summary>
/// Runtime support for the core library. The ZTX16 toolchain replaces every call with a VM
/// instruction specialized for the concrete type; the bodies below only run when the same
/// code executes on .NET (for example in tests).
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class Intrinsics
{
    /// <summary>Hash of a value: numbers by value, strings by content, other objects by identity.</summary>
    public static int Hash<T>(T value) => value is null ? 0 : System.Collections.Generic.EqualityComparer<T>.Default.GetHashCode(value);

    /// <summary>Equality: numbers by value, strings by content, other objects by identity.</summary>
    public static bool Equal<T>(T a, T b) => System.Collections.Generic.EqualityComparer<T>.Default.Equals(a, b);

    /// <summary>Stops the cartridge with <paramref name="message"/> (a VM trap).</summary>
    public static void Fail(string message) => throw new System.InvalidOperationException(message);
}
