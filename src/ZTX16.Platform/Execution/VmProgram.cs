using ZTX16.Runtime;
using ZTX16.VM;

namespace ZTX16.Execution;

/// <summary>
/// Runs a ZTX16 executable in the VM, driving its lifecycle exports from the runtime loop.
/// </summary>
/// <remarks>
/// Loading performs the full load sequence: parse, verify, check the lifecycle contract,
/// then link syscalls when the session initializes. A VM trap propagates as an
/// exception, i.e. an unhandled cartridge failure: the runtime stops and does not call
/// Deinitialize.
/// </remarks>
public sealed class VmProgram : IRuntimeProgram
{
    /// <summary>
    /// Instructions allowed per lifecycle call. Provisional implementation limit until
    /// the machine profile defines execution limits.
    /// </summary>
    public const long InstructionBudgetPerCall = 10_000_000;

    private readonly VerifiedModule _module;
    private readonly int _initialize;
    private readonly int _tick;
    private readonly int _render;
    private readonly int? _deinitialize;
    private VmInstance? _instance;

    private VmProgram(VerifiedModule module)
    {
        _module = module;
        _initialize = RequireLifecycleExport(module.Module, VmModule.LifecycleExports.Initialize);
        _tick = RequireLifecycleExport(module.Module, VmModule.LifecycleExports.Tick);
        _render = RequireLifecycleExport(module.Module, VmModule.LifecycleExports.Render);
        _deinitialize = OptionalLifecycleExport(module.Module, VmModule.LifecycleExports.Deinitialize);
    }

    /// <summary>Loads and verifies an executable.</summary>
    /// <exception cref="ExecutableFormatException">The bytes are not a valid executable.</exception>
    /// <exception cref="VerificationException">The code failed verification or the lifecycle contract.</exception>
    public static VmProgram Load(ReadOnlySpan<byte> executable) => FromModule(ExecutableFormat.Read(executable));

    /// <summary>Verifies an in-memory module.</summary>
    /// <exception cref="VerificationException">The code failed verification or the lifecycle contract.</exception>
    public static VmProgram FromModule(VmModule module) => new(Verifier.Verify(module));

    /// <summary>Instructions executed by the most recent lifecycle call.</summary>
    public long LastInstructionCount => _instance?.LastInstructionCount ?? 0;

    /// <inheritdoc />
    /// <exception cref="LinkException">The executable imports a syscall the platform does not provide.</exception>
    public void Initialize(RuntimeSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _instance = new VmInstance(_module, PlatformSyscalls.Create(session));
        Run(_initialize);
    }

    /// <inheritdoc />
    public void Tick() => Run(_tick);

    /// <inheritdoc />
    public void Render() => Run(_render);

    /// <inheritdoc />
    public void Deinitialize()
    {
        if (_deinitialize is int index)
        {
            Run(index);
        }
    }

    private void Run(int function)
    {
        if (_instance is null)
        {
            throw new InvalidOperationException("Program is not initialized.");
        }

        _instance.Invoke(function, [], InstructionBudgetPerCall);
    }

    private static int RequireLifecycleExport(VmModule module, string name) =>
        OptionalLifecycleExport(module, name)
        ?? throw new VerificationException($"Executable does not export the required lifecycle function '{name}'.");

    private static int? OptionalLifecycleExport(VmModule module, string name)
    {
        if (module.FindExport(name) is not int index)
        {
            return null;
        }

        VmSignature signature = module.Functions[index].Signature;
        if (!signature.Matches(VmSignature.Void))
        {
            throw new VerificationException($"Lifecycle function '{name}' must take no parameters and return nothing; found {signature}.");
        }

        return index;
    }
}
