namespace ZTX16.Platform.Audio;

/// <summary>
/// The ZTX16 Audio Engine: the low-level audio hardware (eight fixed channels, two LFOs, two filters, two FX
/// units, finalizing mixer, master volume, safety limiter) and the PCM stream the host sends to its device.
/// </summary>
/// <remarks>
/// <para>
/// Threading: control calls run on the runtime thread. They are validated at once (an invalid argument
/// throws, which traps the cartridge) and queued; <see cref="Update"/> — the Audio Update phase of the tick —
/// applies the whole batch to the hardware under a lock, so a tick's changes take effect together.
/// <see cref="Render"/> runs on the host audio thread under the same lock. Reads return the state after the
/// last rendered audio.
/// </para>
/// <para>
/// Numeric values outside their documented range are clamped; non-finite values, bad channel/unit indices and
/// operations a channel kind does not support are errors.
/// </para>
/// </remarks>
public sealed class AudioEngine
{
    /// <summary>Output channel count (interleaved stereo).</summary>
    public const int OutputChannels = 2;

    private readonly DiagnosticTone _diagnosticTone = new();
    private readonly AudioHardware _hardware;
    private readonly object _lock = new();
    private readonly List<Action<AudioHardware>> _pending = new();

    internal AudioEngine(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        SampleRate = sampleRate;
        _hardware = new AudioHardware(sampleRate);
    }

    /// <summary>Output sample rate in Hz, as negotiated with the host device.</summary>
    public int SampleRate { get; }

    /// <summary>Host diagnostics: a sine test tone mixed into the output.</summary>
    internal DiagnosticTone DiagnosticTone => _diagnosticTone;

    // ---- Channels ----

    /// <summary>Waveform (0..5) of a Synth channel.</summary>
    public void SetWaveform(int channel, int waveform)
    {
        SynthChannel(channel);
        Range(waveform, 0, 5, "waveform");
        Queue(h => h.Channels[channel].Waveform = waveform);
    }

    /// <summary>Base frequency of a Synth or Wavetable channel.</summary>
    public void SetFrequency(int channel, float hz)
    {
        Channel(channel);
        double value = Math.Clamp(Finite(hz), AudioHardware.MinFrequency, AudioHardware.MaxFrequency);
        Queue(h => h.Channels[channel].Frequency = value);
    }

    /// <summary>Base volume 0..1.</summary>
    public void SetVolume(int channel, float volume)
    {
        Channel(channel);
        float value = Math.Clamp(Finite(volume), 0f, 1f);
        Queue(h => h.Channels[channel].Volume = value);
    }

    /// <summary>Pan −1..1.</summary>
    public void SetPan(int channel, float pan)
    {
        Channel(channel);
        float value = Math.Clamp(Finite(pan), -1f, 1f);
        Queue(h => h.Channels[channel].Pan = value);
    }

    /// <summary>ADSR times (0..10 s) and sustain level (0..1).</summary>
    public void SetAdsr(int channel, float attack, float decay, float sustain, float release)
    {
        Channel(channel);
        float a = Math.Clamp(Finite(attack), 0f, 10f), d = Math.Clamp(Finite(decay), 0f, 10f);
        float s = Math.Clamp(Finite(sustain), 0f, 1f), r = Math.Clamp(Finite(release), 0f, 10f);
        Queue(h =>
        {
            Channel c = h.Channels[channel];
            (c.AttackTime, c.DecayTime, c.SustainLevel, c.ReleaseTime) = (a, d, s, r);
        });
    }

    /// <summary>Starts or retriggers the attack.</summary>
    public void NoteOn(int channel)
    {
        Channel(channel);
        Queue(h => h.Channels[channel].NoteOn());
    }

    /// <summary>Begins the release.</summary>
    public void NoteOff(int channel)
    {
        Channel(channel);
        Queue(h => h.Channels[channel].NoteOff());
    }

    /// <summary>Silences at once.</summary>
    public void Stop(int channel)
    {
        Channel(channel);
        Queue(h => h.Channels[channel].StopNow());
    }

    /// <summary>Mutes or unmutes.</summary>
    public void SetMuted(int channel, bool muted)
    {
        Channel(channel);
        Queue(h => h.Channels[channel].Muted = muted);
    }

    /// <summary>Makes a Sample/Wavetable channel play a sample (one-shot, whole sample).</summary>
    public void UseSample(int channel, sbyte[] pcm)
    {
        SampleChannel(channel);
        Queue(h => h.Channels[channel].UseSample(pcm));
    }

    /// <summary>Makes a Sample/Wavetable channel play a wavetable.</summary>
    public void UseWavetable(int channel, sbyte[] table)
    {
        SampleChannel(channel);
        Queue(h => h.Channels[channel].UseWavetable(table));
    }

    /// <summary>Sample looping; end −1 = sample end. Start and end are clamped to the sample when it plays.</summary>
    public void SetSampleLoop(int channel, bool loop, int start, int end)
    {
        SampleChannel(channel);
        if (start < 0 || end < -1 || (end >= 0 && end <= start))
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Loop start must be ≥ 0 and loop end > start (or -1 for the sample end).");
        }

        Queue(h =>
        {
            Channel c = h.Channels[channel];
            (c.Loop, c.LoopStart, c.LoopEnd) = (loop, start, end);
        });
    }

    /// <summary>Sample playback rate multiplier (0..64).</summary>
    public void SetPlaybackRate(int channel, float rate)
    {
        SampleChannel(channel);
        float value = Math.Clamp(Finite(rate), 0f, 64f);
        Queue(h => h.Channels[channel].PlaybackRate = value);
    }

    /// <summary>Finalizing mixer volume 0..1.</summary>
    public void SetMixerVolume(int channel, float volume)
    {
        Channel(channel);
        float value = Math.Clamp(Finite(volume), 0f, 1f);
        Queue(h => h.Channels[channel].MixerVolume = value);
    }

    /// <summary>Master volume 0..1.</summary>
    public void SetMasterVolume(float volume)
    {
        float value = Math.Clamp(Finite(volume), 0f, 1f);
        Queue(h => h.MasterVolume = value);
    }

    // ---- Modulation ----

    /// <summary>LFO waveform (0..3) and frequency (0.01..60 Hz).</summary>
    public void SetLfo(int lfo, int waveform, float hz)
    {
        Range(lfo, 0, AudioHardware.LfoCount - 1, "LFO");
        Range(waveform, 0, 3, "LFO waveform");
        double value = Math.Clamp(Finite(hz), 0.01, 60.0);
        Queue(h => (h.Lfos[lfo].Waveform, h.Lfos[lfo].Frequency) = (waveform, value));
    }

    /// <summary>LFO → channel pitch (semitones, ±96) or volume (factor, ±1).</summary>
    public void RouteLfo(int lfo, int channel, int destination, float amount)
    {
        Range(lfo, 0, AudioHardware.LfoCount - 1, "LFO");
        Channel(channel);
        Range(destination, 0, 1, "modulation destination");
        float value = ClampAmount(destination, amount);
        Queue(h => h.Channels[channel].LfoAmounts[lfo, destination] = value);
    }

    /// <summary>LFO → processor parameter (filter: cutoff semitones ±96, resonance; FX: parameter units).</summary>
    public void RouteLfoToProcessor(int lfo, int unit, int parameter, float amount)
    {
        Range(lfo, 0, AudioHardware.LfoCount - 1, "LFO");
        Range(unit, 0, AudioHardware.UnitCount - 1, "processor unit");
        Range(parameter, 0, unit < 2 ? 1 : 2, "processor parameter");
        float value = Math.Clamp(Finite(amount), -1000f, 1000f);
        Queue(h => h.ProcessorRoutes[lfo, unit, parameter] = value);
    }

    /// <summary>The channel ADSR's one destination: pitch (semitones, ±96) or volume (factor, ±1).</summary>
    public void RouteAdsr(int channel, int destination, float amount)
    {
        Channel(channel);
        Range(destination, 0, 1, "modulation destination");
        float value = ClampAmount(destination, amount);
        Queue(h => (h.Channels[channel].AdsrDestination, h.Channels[channel].AdsrAmount) = (destination, value));
    }

    // ---- Processing ----

    /// <summary>Filter mode (0..2), cutoff (20..20000 Hz), resonance (0..1).</summary>
    public void SetFilter(int filter, int mode, float cutoff, float resonance)
    {
        Range(filter, 0, 1, "filter");
        Range(mode, 0, 2, "filter mode");
        double c = Math.Clamp(Finite(cutoff), 20f, 20000f);
        float r = Math.Clamp(Finite(resonance), 0f, 1f);
        Queue(h =>
        {
            Filter f = h.Filters[filter];
            if (f.Mode != mode)
            {
                f.Reset();
            }

            (f.Mode, f.Cutoff, f.Resonance) = (mode, c, r);
        });
    }

    /// <summary>FX type (0..2) and parameters; changing the type clears the unit's buffers.</summary>
    public void SetEffect(int fx, int type, float parameter1, float parameter2, float mix)
    {
        Range(fx, 0, 1, "FX unit");
        Range(type, 0, 2, "effect type");
        (float lo1, float hi1, float lo2, float hi2) = type switch
        {
            0 => (1f, 40f, 0f, 20f),
            1 => (1f, 1000f, 0f, 0.95f),
            _ => (0f, 1f, 0f, 1f),
        };
        float p1 = Math.Clamp(Finite(parameter1), lo1, hi1), p2 = Math.Clamp(Finite(parameter2), lo2, hi2);
        float m = Math.Clamp(Finite(mix), 0f, 1f);
        Queue(h =>
        {
            Effect e = h.Effects[fx];
            if (e.Type != type)
            {
                e.Reset();
            }

            (e.Type, e.Parameter1, e.Parameter2, e.Mix) = (type, p1, p2, m);
        });
    }

    /// <summary>Assigns a unit to −1 (none), a channel 0..7 (insert) or 8 (global); reassigning replaces.</summary>
    public void AssignProcessor(int unit, int target)
    {
        Range(unit, 0, AudioHardware.UnitCount - 1, "processor unit");
        Range(target, AudioHardware.Unassigned, AudioHardware.Global, "processor target");
        Queue(h =>
        {
            if (h.Assignments[unit] != target)
            {
                h.ResetUnit(unit);
                h.Assignments[unit] = target;
            }
        });
    }

    // ---- Readback ----

    /// <summary>Voice state (0 Idle, 1 Attack, 2 Decay, 3 Sustain, 4 Release).</summary>
    public int GetChannelState(int channel)
    {
        Channel(channel);
        lock (_lock)
        {
            return _hardware.Channels[channel].State;
        }
    }

    /// <summary>Effective frequency (Hz) or playback rate.</summary>
    public float GetEffectivePitch(int channel)
    {
        Channel(channel);
        lock (_lock)
        {
            return _hardware.Channels[channel].EffectivePitch;
        }
    }

    /// <summary>Effective volume after modulation.</summary>
    public float GetEffectiveVolume(int channel)
    {
        Channel(channel);
        lock (_lock)
        {
            return _hardware.Channels[channel].EffectiveVolume;
        }
    }

    /// <summary>Current LFO output.</summary>
    public float GetLfoValue(int lfo)
    {
        Range(lfo, 0, AudioHardware.LfoCount - 1, "LFO");
        lock (_lock)
        {
            return _hardware.Lfos[lfo].Value;
        }
    }

    /// <summary>Current ADSR output.</summary>
    public float GetAdsrValue(int channel)
    {
        Channel(channel);
        lock (_lock)
        {
            return _hardware.Channels[channel].Envelope;
        }
    }

    /// <summary>Audio Update phase of the tick: applies the tick's queued changes together.</summary>
    internal void Update()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (Action<AudioHardware> change in _pending)
            {
                change(_hardware);
            }
        }

        _pending.Clear();
    }

    /// <summary>
    /// Renders interleaved stereo float samples in -1..1. Called from the audio thread.
    /// </summary>
    internal void Render(Span<float> interleaved)
    {
        lock (_lock)
        {
            _hardware.Render(interleaved);
        }

        _diagnosticTone.MixInto(interleaved, SampleRate);
    }

    private void Queue(Action<AudioHardware> change) => _pending.Add(change);

    private static void Channel(int channel) => Range(channel, 0, AudioHardware.ChannelCount - 1, "channel");

    private static void SynthChannel(int channel)
    {
        Channel(channel);
        if (channel >= AudioHardware.FirstSampleChannel)
        {
            throw new ArgumentException($"Channel {channel} is a Sample/Wavetable channel; waveforms are for Synth channels 0..5.");
        }
    }

    private static void SampleChannel(int channel)
    {
        Channel(channel);
        if (channel < AudioHardware.FirstSampleChannel)
        {
            throw new ArgumentException($"Channel {channel} is a Synth channel; samples and wavetables play on channels 6 and 7.");
        }
    }

    private static void Range(int value, int min, int max, string what)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(what, value, $"The {what} must be {min}..{max}.");
        }
    }

    private static float Finite(float value) =>
        float.IsFinite(value) ? value : throw new ArgumentException("Audio values must be finite numbers.");

    private static float ClampAmount(int destination, float amount) =>
        destination == 0 ? Math.Clamp(Finite(amount), -96f, 96f) : Math.Clamp(Finite(amount), -1f, 1f);
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
