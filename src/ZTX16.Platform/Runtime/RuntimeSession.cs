using ZTX16.Audio;
using ZTX16.Graphics;
using ZTX16.Input;

namespace ZTX16.Runtime;

/// <summary>
/// One running ZTX16 session: the machine subsystems plus the program being executed.
/// The runtime owns the loop; the program only receives lifecycle calls.
/// </summary>
public sealed class RuntimeSession
{
    private readonly IRuntimeProgram _program;
    private bool _initialized;
    private bool _deinitialized;

    internal RuntimeSession(IRuntimeProgram program, int audioSampleRate)
    {
        ArgumentNullException.ThrowIfNull(program);
        _program = program;
        Audio = new AudioEngine(audioSampleRate);
    }

    /// <summary>Machine API.</summary>
    public Machine Machine { get; } = new();

    /// <summary>Graphics hardware.</summary>
    public GraphicsSystem Graphics { get; } = new();

    /// <summary>Input hardware.</summary>
    public InputSystem Input { get; } = new();

    /// <summary>Audio hardware.</summary>
    public AudioEngine Audio { get; }

    /// <summary>True once the program requested exit.</summary>
    internal bool ShouldExit => Machine.ExitRequested;

    internal void Initialize()
    {
        if (_initialized)
        {
            throw new InvalidOperationException("Session is already initialized.");
        }

        _initialized = true;
        _program.Initialize(this);
    }

    /// <summary>Runs one simulation tick in the fixed order Input → Game Logic → Audio Update → Render.</summary>
    internal void RunTick(TimeSpan elapsed)
    {
        if (!_initialized || _deinitialized)
        {
            throw new InvalidOperationException("Session is not running.");
        }

        Machine.BeginTick(elapsed);
        Input.BeginTick();
        _program.Tick();
        Audio.Update();
        _program.Render();
        Machine.EndTick();
    }

    /// <summary>Controlled shutdown. Safe to call more than once.</summary>
    internal void Deinitialize()
    {
        if (!_initialized || _deinitialized)
        {
            return;
        }

        _deinitialized = true;
        _program.Deinitialize();
    }
}
