namespace ZTX16.VM;

/// <summary>A function defined in a module.</summary>
/// <param name="Name">Function name, unique within the module. Used for exports and diagnostics.</param>
/// <param name="Signature">Parameter and result types. Parameters occupy the first locals.</param>
/// <param name="Locals">Types of additional locals that follow the parameters; zero-initialized on entry.</param>
/// <param name="Code">Encoded instructions.</param>
public sealed record VmFunction(string Name, VmSignature Signature, IReadOnlyList<VmType> Locals, byte[] Code)
{
    /// <summary>Total number of local slots, parameters included.</summary>
    public int LocalCount => Signature.Parameters.Count + Locals.Count;

    /// <summary>Type of local slot <paramref name="index"/>, parameters included.</summary>
    public VmType LocalType(int index) =>
        index < Signature.Parameters.Count ? Signature.Parameters[index] : Locals[index - Signature.Parameters.Count];
}

/// <summary>A syscall imported by a module: a platform operation requested by name and signature.</summary>
/// <remarks>Binding by name keeps executables valid across runtime versions that renumber operations internally.</remarks>
public sealed record VmImport(string Name, VmSignature Signature);

/// <summary>
/// An immutable ZTX16 executable module: globals, syscall imports, functions and exports.
/// </summary>
public sealed class VmModule
{
    /// <summary>Lifecycle export names required by the cartridge contract.</summary>
    public static class LifecycleExports
    {
        /// <summary>Called once before the first tick.</summary>
        public const string Initialize = "Initialize";

        /// <summary>Game Logic phase.</summary>
        public const string Tick = "Tick";

        /// <summary>Render phase.</summary>
        public const string Render = "Render";

        /// <summary>Optional controlled shutdown.</summary>
        public const string Deinitialize = "Deinitialize";
    }

    /// <summary>Creates a module. Structural validity is checked by <see cref="Verifier"/>, not here.</summary>
    public VmModule(
        IReadOnlyList<VmType> globals,
        IReadOnlyList<VmImport> imports,
        IReadOnlyList<VmFunction> functions,
        IReadOnlyDictionary<string, int> exports)
    {
        Globals = globals.ToArray();
        Imports = imports.ToArray();
        Functions = functions.ToArray();
        Exports = new Dictionary<string, int>(exports, StringComparer.Ordinal);
    }

    /// <summary>Types of the module's globals, zero-initialized at instantiation.</summary>
    public IReadOnlyList<VmType> Globals { get; }

    /// <summary>Syscall imports, addressed by index from <see cref="OpCode.Syscall"/>.</summary>
    public IReadOnlyList<VmImport> Imports { get; }

    /// <summary>Functions, addressed by index from <see cref="OpCode.Call"/>.</summary>
    public IReadOnlyList<VmFunction> Functions { get; }

    /// <summary>Exported function names mapped to function indices.</summary>
    public IReadOnlyDictionary<string, int> Exports { get; }

    /// <summary>Returns the function index exported as <paramref name="name"/>, or null.</summary>
    public int? FindExport(string name) => Exports.TryGetValue(name, out int index) ? index : null;
}
