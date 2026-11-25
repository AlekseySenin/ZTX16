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
/// A class or interface. Instances of a class hold <see cref="Fields"/> slots, laid out with the
/// base class's fields first.
/// </summary>
/// <param name="Name">Class name, for diagnostics.</param>
/// <param name="BaseClass">Index of the base class, or <see cref="VmClass.NoBase"/>.</param>
/// <param name="IsInterface">Interfaces have no instances and no fields; they exist for type tests and dispatch.</param>
/// <param name="Fields">Types of all instance field slots, base class fields included as a prefix.</param>
/// <param name="Interfaces">Every interface the class implements, directly or through its bases.</param>
/// <param name="Methods">Virtual dispatch table: selector index → implementing function, inherited entries included.</param>
public sealed record VmClass(
    string Name,
    int BaseClass,
    bool IsInterface,
    IReadOnlyList<VmType> Fields,
    IReadOnlyList<int> Interfaces,
    IReadOnlyDictionary<int, int> Methods)
{
    /// <summary>Marker for a class without a base class.</summary>
    public const int NoBase = ushort.MaxValue;
}

/// <summary>An instance field: a slot of a class's instances.</summary>
public sealed record VmField(int Class, int Slot);

/// <summary>
/// A virtual or interface method slot. The signature includes the receiver as its first
/// parameter, which must be <see cref="VmType.Ref"/>.
/// </summary>
public sealed record VmSelector(string Name, VmSignature Signature);

/// <summary>
/// An immutable ZTX16 executable module: globals, syscall imports, functions, exports,
/// classes, fields, selectors and strings.
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
        IReadOnlyDictionary<string, int> exports,
        IReadOnlyList<VmClass>? classes = null,
        IReadOnlyList<VmField>? fields = null,
        IReadOnlyList<VmSelector>? selectors = null,
        IReadOnlyList<string>? strings = null)
    {
        Globals = globals.ToArray();
        Imports = imports.ToArray();
        Functions = functions.ToArray();
        Exports = new Dictionary<string, int>(exports, StringComparer.Ordinal);
        Classes = classes?.ToArray() ?? [];
        Fields = fields?.ToArray() ?? [];
        Selectors = selectors?.ToArray() ?? [];
        Strings = strings?.ToArray() ?? [];
    }

    /// <summary>Types of the module's globals, zero-initialized at instantiation.</summary>
    public IReadOnlyList<VmType> Globals { get; }

    /// <summary>Syscall imports, addressed by index from <see cref="OpCode.Syscall"/>.</summary>
    public IReadOnlyList<VmImport> Imports { get; }

    /// <summary>Functions, addressed by index from <see cref="OpCode.Call"/>.</summary>
    public IReadOnlyList<VmFunction> Functions { get; }

    /// <summary>Exported function names mapped to function indices.</summary>
    public IReadOnlyDictionary<string, int> Exports { get; }

    /// <summary>Classes and interfaces.</summary>
    public IReadOnlyList<VmClass> Classes { get; }

    /// <summary>Instance fields, addressed by index from field instructions.</summary>
    public IReadOnlyList<VmField> Fields { get; }

    /// <summary>Virtual/interface method selectors.</summary>
    public IReadOnlyList<VmSelector> Selectors { get; }

    /// <summary>String literals, addressed by index from <see cref="OpCode.LoadString"/>.</summary>
    public IReadOnlyList<string> Strings { get; }

    /// <summary>Returns the function index exported as <paramref name="name"/>, or null.</summary>
    public int? FindExport(string name) => Exports.TryGetValue(name, out int index) ? index : null;

    /// <summary>Type of the value stored in field <paramref name="field"/>.</summary>
    public VmType FieldType(int field)
    {
        VmField f = Fields[field];
        return Classes[f.Class].Fields[f.Slot];
    }
}
