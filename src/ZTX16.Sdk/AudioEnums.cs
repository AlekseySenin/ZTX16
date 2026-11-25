namespace ZTX16;

/// <summary>Oscillator waveform of a Synth channel (0–5).</summary>
public enum Waveform
{
    /// <summary>Sine.</summary>
    Sine = 0,

    /// <summary>Triangle.</summary>
    Triangle = 1,

    /// <summary>Rising saw.</summary>
    Saw = 2,

    /// <summary>Falling saw.</summary>
    ReverseSaw = 3,

    /// <summary>Square (50% duty; there is no pulse width).</summary>
    Square = 4,

    /// <summary>Noise: a new random value at the channel frequency.</summary>
    Noise = 5,
}

/// <summary>Waveform of a global LFO.</summary>
public enum LfoWaveform
{
    /// <summary>Triangle.</summary>
    Triangle = 0,

    /// <summary>Rising saw.</summary>
    Saw = 1,

    /// <summary>Falling saw.</summary>
    ReverseSaw = 2,

    /// <summary>Square.</summary>
    Square = 3,
}

/// <summary>A channel parameter that LFOs and the ADSR can modulate.</summary>
public enum ModulationDestination
{
    /// <summary>Frequency (Synth, Wavetable) or Playback Rate (Sample). Amount in semitones.</summary>
    Pitch = 0,

    /// <summary>Volume. Amount is a factor: −1..1.</summary>
    Volume = 1,
}

/// <summary>What a channel plays.</summary>
public enum ChannelState
{
    /// <summary>Silent.</summary>
    Idle = 0,

    /// <summary>ADSR attack.</summary>
    Attack = 1,

    /// <summary>ADSR decay.</summary>
    Decay = 2,

    /// <summary>ADSR sustain.</summary>
    Sustain = 3,

    /// <summary>ADSR release.</summary>
    Release = 4,
}

/// <summary>Filter mode.</summary>
public enum FilterMode
{
    /// <summary>Low-pass.</summary>
    LowPass = 0,

    /// <summary>High-pass.</summary>
    HighPass = 1,

    /// <summary>Band-pass.</summary>
    BandPass = 2,
}

/// <summary>What an FX unit does.</summary>
public enum EffectType
{
    /// <summary>Chorus: Delay (ms), Depth (0..1), Mix (0..1).</summary>
    Chorus = 0,

    /// <summary>Delay: Time (ms), Feedback (0..0.95), Mix (0..1).</summary>
    Delay = 1,

    /// <summary>Reverb: Size (0..1), Decay (0..1), Mix (0..1).</summary>
    Reverb = 2,
}

/// <summary>A shared processing unit.</summary>
public enum ProcessorUnit
{
    /// <summary>Filter 1.</summary>
    Filter1 = 0,

    /// <summary>Filter 2.</summary>
    Filter2 = 1,

    /// <summary>FX unit 1.</summary>
    Fx1 = 2,

    /// <summary>FX unit 2.</summary>
    Fx2 = 3,
}

/// <summary>
/// A parameter of a processing unit, as an LFO destination. Filters use Cutoff (amount in semitones) and
/// Resonance; FX units use their three parameters in order (amounts in the parameter's own units).
/// </summary>
public enum ProcessorParameter
{
    /// <summary>Filter cutoff (amount in semitones).</summary>
    Cutoff = 0,

    /// <summary>Filter resonance.</summary>
    Resonance = 1,

    /// <summary>First FX parameter: Chorus Delay, Delay Time, Reverb Size.</summary>
    Effect1 = 0,

    /// <summary>Second FX parameter: Chorus Depth, Delay Feedback, Reverb Decay.</summary>
    Effect2 = 1,

    /// <summary>Third FX parameter: Mix.</summary>
    Effect3 = 2,
}
