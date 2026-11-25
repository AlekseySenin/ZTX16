namespace ZTX16.Platform.Runtime;

/// <summary>
/// Host-side bridge to the code a session executes, following the cartridge lifecycle
/// contract: Initialize, Tick, Render, optional Deinitialize.
/// </summary>
/// <remarks>
/// This is not the cartridge format. Cartridges are ZTX16 executables run by the VM;
/// the VM will implement this interface to drive them. Until then it is also
/// implemented by trusted built-in host programs such as diagnostics.
/// </remarks>
public interface IRuntimeProgram
{
    /// <summary>Called once before the first tick.</summary>
    void Initialize(RuntimeSession session);

    /// <summary>Game Logic phase: called once per simulation tick, after Input.</summary>
    void Tick();

    /// <summary>Render phase: called once per tick, after Audio Update.</summary>
    void Render();

    /// <summary>Controlled shutdown. Not called after an unhandled failure.</summary>
    void Deinitialize();
}
