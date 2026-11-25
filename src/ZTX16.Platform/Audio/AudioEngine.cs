namespace ZTX16.Audio;

/// <summary>
/// The ZTX16 Audio Engine: owns synthesis and mixing and produces the PCM stream the
/// host sends to its audio device.
/// </summary>
/// <remarks>
/// Threading: <see cref="Update"/> runs on the runtime thread once per tick;
/// <see cref="Render"/> runs on the host audio thread. State shared between them must
/// be handed over safely; currently only the diagnostic tone crosses that boundary.
///
/// MVP status: the eight-channel hardware model is not implemented yet. The engine
/// renders silence plus an optional host-side diagnostic tone used to verify the
/// audio device path.
/// </remarks>
public sealed class AudioEngine
{
    /// <summary>Output channel count (interleaved stereo).</summary>
    public const int OutputChannels = 2;

    private readonly DiagnosticTone _diagnosticTone = new();

    internal AudioEngine(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        SampleRate = sampleRate;
    }

    /// <summary>Output sample rate in Hz, as negotiated with the host device.</summary>
    public int SampleRate { get; }

    /// <summary>Host diagnostics: a sine test tone mixed into the output.</summary>
    internal DiagnosticTone DiagnosticTone => _diagnosticTone;

    /// <summary>Audio Update phase of the tick: applies scheduled audio state changes.</summary>
    internal void Update()
    {
        // Channel/sequence scheduling will live here once the hardware model exists.
    }

    /// <summary>
    /// Renders interleaved stereo float samples in -1..1. Called from the audio thread.
    /// </summary>
    internal void Render(Span<float> interleaved)
    {
        interleaved.Clear();
        _diagnosticTone.MixInto(interleaved, SampleRate);
    }
}

/// <summary>Sine test tone for verifying the host audio path. Not part of the platform API.</summary>
internal sealed class DiagnosticTone
{
    private volatile bool _enabled;
    private volatile float _frequency = 440f;
    private volatile float _volume = 0.15f;
    private double _phase;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public float Frequency
    {
        get => _frequency;
        set => _frequency = Math.Clamp(value, 20f, 20000f);
    }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public void MixInto(Span<float> interleaved, int sampleRate)
    {
        if (!_enabled)
        {
            return;
        }

        double step = 2.0 * Math.PI * _frequency / sampleRate;
        float volume = _volume;
        for (int i = 0; i + 1 < interleaved.Length; i += AudioEngine.OutputChannels)
        {
            float sample = (float)Math.Sin(_phase) * volume;
            interleaved[i] += sample;
            interleaved[i + 1] += sample;
            _phase += step;
            if (_phase >= 2.0 * Math.PI)
            {
                _phase -= 2.0 * Math.PI;
            }
        }
    }
}
