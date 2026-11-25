namespace ZTX16.Platform.Audio;

/// <summary>
/// The physical audio hardware state and DSP: eight channels, two LFOs, two filters, two FX units, the
/// finalizing mixer, master volume and the safety limiter. Not thread-safe: <see cref="AudioEngine"/> guards it.
/// </summary>
internal sealed class AudioHardware
{
    public const int ChannelCount = 8;
    public const int FirstSampleChannel = 6;
    public const int LfoCount = 2;
    public const int UnitCount = 4;          // Filter1, Filter2, FX1, FX2
    public const int Unassigned = -1;
    public const int Global = 8;
    public const double MinFrequency = 0.01;
    public const double MaxFrequency = 20000;

    private readonly float[] _scratch = new float[2];

    public AudioHardware(int sampleRate)
    {
        SampleRate = sampleRate;
        for (int i = 0; i < ChannelCount; i++)
        {
            Channels[i] = new Channel(i, i >= FirstSampleChannel, (uint)(0x1234567 + (i * 7919)));
        }

        for (int i = 0; i < LfoCount; i++)
        {
            Lfos[i] = new Lfo();
        }

        Filters[0] = new Filter();
        Filters[1] = new Filter();
        Effects[0] = new Effect(sampleRate);
        Effects[1] = new Effect(sampleRate);
        Array.Fill(Assignments, Unassigned);
    }

    public int SampleRate { get; }

    public Channel[] Channels { get; } = new Channel[ChannelCount];

    public Lfo[] Lfos { get; } = new Lfo[LfoCount];

    public Filter[] Filters { get; } = new Filter[2];

    public Effect[] Effects { get; } = new Effect[2];

    /// <summary>Target of each processing unit: <see cref="Unassigned"/>, a channel or <see cref="Global"/>.</summary>
    public int[] Assignments { get; } = new int[UnitCount];

    /// <summary>LFO → processor parameter amounts: [lfo, unit, parameter].</summary>
    public float[,,] ProcessorRoutes { get; } = new float[LfoCount, UnitCount, 3];

    public float MasterVolume { get; set; } = 1f;

    /// <summary>Current gain of the safety limiter (1 = not limiting).</summary>
    public float LimiterGain { get; private set; } = 1f;

    /// <summary>Renders interleaved stereo frames, adding nothing else.</summary>
    public void Render(Span<float> interleaved)
    {
        double rate = SampleRate;
        for (int i = 0; i + 1 < interleaved.Length; i += 2)
        {
            float lfo0 = Lfos[0].Advance(rate), lfo1 = Lfos[1].Advance(rate);
            double mixL = 0, mixR = 0;
            for (int c = 0; c < ChannelCount; c++)
            {
                Channel channel = Channels[c];
                float mono = channel.Next(rate, lfo0, lfo1);
                float left = mono * channel.PanLeft, right = mono * channel.PanRight;
                if (channel.Muted)
                {
                    left = right = 0f;
                }

                ProcessChain(c, ref left, ref right, lfo0, lfo1);
                mixL += left * channel.MixerVolume;
                mixR += right * channel.MixerVolume;
            }

            float l = (float)mixL, r = (float)mixR;
            ProcessChain(Global, ref l, ref r, lfo0, lfo1);
            l *= MasterVolume;
            r *= MasterVolume;

            // Safety limiter: instant attack, ~0.1 s release, then a hard clamp.
            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r)) * LimiterGain;
            if (peak > 1f)
            {
                LimiterGain /= peak;
            }
            else
            {
                LimiterGain = MathF.Min(1f, LimiterGain + (float)(10.0 / rate));
            }

            interleaved[i] = Math.Clamp(l * LimiterGain, -1f, 1f);
            interleaved[i + 1] = Math.Clamp(r * LimiterGain, -1f, 1f);
        }
    }

    private void ProcessChain(int target, ref float left, ref float right, float lfo0, float lfo1)
    {
        for (int u = 0; u < UnitCount; u++)
        {
            if (Assignments[u] != target)
            {
                continue;
            }

            float m0 = Modulation(u, 0, lfo0, lfo1), m1 = Modulation(u, 1, lfo0, lfo1), m2 = Modulation(u, 2, lfo0, lfo1);
            if (u < 2)
            {
                Filters[u].Process(ref left, ref right, SampleRate, m0, m1);
            }
            else
            {
                Effects[u - 2].Process(ref left, ref right, m0, m1, m2);
            }
        }
    }

    private float Modulation(int unit, int parameter, float lfo0, float lfo1) =>
        (ProcessorRoutes[0, unit, parameter] * lfo0) + (ProcessorRoutes[1, unit, parameter] * lfo1);

    /// <summary>Clears the processing state of a unit (its buffers) when it is reconfigured or reassigned.</summary>
    public void ResetUnit(int unit)
    {
        if (unit < 2)
        {
            Filters[unit].Reset();
        }
        else
        {
            Effects[unit - 2].Reset();
        }
    }
}

internal enum SourceKind
{
    Synth,
    None,
    Sample,
    Wavetable,
}

/// <summary>One physical channel: source, ADSR, base values, modulation routes and runtime state.</summary>
internal sealed class Channel
{
    private const int Idle = 0, Attack = 1, Decay = 2, Sustain = 3, Release = 4;

    private uint _lfsr;
    private float _noise;
    private double _phase;
    private double _position;
    private double _releaseStep;
    private float _pan;

    public Channel(int index, bool sampleChannel, uint seed)
    {
        Index = index;
        IsSampleChannel = sampleChannel;
        Source = sampleChannel ? SourceKind.None : SourceKind.Synth;
        _lfsr = seed;
        Pan = 0f;
    }

    public int Index { get; }

    public bool IsSampleChannel { get; }

    public SourceKind Source { get; private set; }

    public int Waveform { get; set; }

    public double Frequency { get; set; } = 440;

    public float Volume { get; set; } = 1f;

    public float Pan
    {
        get => _pan;
        set
        {
            _pan = value;
            double angle = (value + 1) * Math.PI / 4;
            PanLeft = (float)Math.Cos(angle);
            PanRight = (float)Math.Sin(angle);
        }
    }

    public float PanLeft { get; private set; }

    public float PanRight { get; private set; }

    public float MixerVolume { get; set; } = 1f;

    public bool Muted { get; set; }

    public float AttackTime { get; set; } = 0.005f;

    public float DecayTime { get; set; }

    public float SustainLevel { get; set; } = 1f;

    public float ReleaseTime { get; set; } = 0.05f;

    /// <summary>ADSR destination (0 = pitch, 1 = volume) and amount.</summary>
    public int AdsrDestination { get; set; } = 1;

    public float AdsrAmount { get; set; } = 1f;

    /// <summary>LFO amounts: [lfo, destination].</summary>
    public float[,] LfoAmounts { get; } = new float[2, 2];

    public sbyte[]? Data { get; private set; }

    public bool Loop { get; set; }

    public int LoopStart { get; set; }

    public int LoopEnd { get; set; } = -1;

    public float PlaybackRate { get; set; } = 1f;

    public int State { get; private set; }

    public float Envelope { get; private set; }

    public float EffectivePitch { get; private set; }

    public float EffectiveVolume { get; private set; }

    public void UseSample(sbyte[] data)
    {
        StopNow();
        Source = SourceKind.Sample;
        Data = data;
        Loop = false;
        LoopStart = 0;
        LoopEnd = -1;
    }

    public void UseWavetable(sbyte[] table)
    {
        StopNow();
        Source = SourceKind.Wavetable;
        Data = table;
    }

    public void NoteOn()
    {
        if (Source == SourceKind.None)
        {
            return;
        }

        if (State == Idle)
        {
            _phase = 0;
            _noise = NextNoise();
        }

        _position = 0;
        State = Attack;
    }

    public void NoteOff()
    {
        if (State is Attack or Decay or Sustain)
        {
            State = Release;
            _releaseStep = -1;
        }
    }

    public void StopNow()
    {
        State = Idle;
        Envelope = 0f;
    }

    /// <summary>Next mono sample after ADSR and volume, before pan.</summary>
    public float Next(double rate, float lfo0, float lfo1)
    {
        float env = AdvanceEnvelope(rate);

        // Base → LFO1 → LFO2 → ADSR, for pitch (semitones) and volume (factors).
        double semitones = (LfoAmounts[0, 0] * lfo0) + (LfoAmounts[1, 0] * lfo1);
        float volume = Volume;
        volume = MathF.Max(0f, volume * (1f + (LfoAmounts[0, 1] * lfo0)));
        volume = MathF.Max(0f, volume * (1f + (LfoAmounts[1, 1] * lfo1)));
        if (AdsrDestination == 0)
        {
            semitones += AdsrAmount * env;
        }
        else
        {
            volume *= AdsrAmount >= 0 ? 1f - AdsrAmount + (AdsrAmount * env) : 1f + (AdsrAmount * env);
        }

        volume = Math.Clamp(volume, 0f, 1f);
        double factor = semitones == 0 ? 1.0 : Math.Pow(2.0, semitones / 12.0);
        double pitch = Source == SourceKind.Sample
            ? Math.Clamp(PlaybackRate * factor, 0.0, 64.0)
            : Math.Clamp(Frequency * factor, AudioHardware.MinFrequency, AudioHardware.MaxFrequency);
        EffectivePitch = (float)pitch;
        EffectiveVolume = volume;

        if (State == Idle)
        {
            return 0f;
        }

        float value = Source switch
        {
            SourceKind.Synth => Oscillator(pitch / rate),
            SourceKind.Wavetable => WavetableValue(pitch / rate),
            SourceKind.Sample => SampleValue(pitch * Cartridge.SampleFormat.SampleRate / rate),
            _ => 0f,
        };

        return value * volume;
    }

    private float AdvanceEnvelope(double rate)
    {
        switch (State)
        {
            case Attack:
                Envelope = AttackTime <= 0 ? 1f : Envelope + (float)(1.0 / (AttackTime * rate));
                if (Envelope >= 1f)
                {
                    Envelope = 1f;
                    State = Decay;
                }

                break;
            case Decay:
                Envelope = DecayTime <= 0 ? SustainLevel : Envelope - (float)((1.0 - SustainLevel) / (DecayTime * rate));
                if (Envelope <= SustainLevel)
                {
                    Envelope = SustainLevel;
                    State = Sustain;
                }

                break;
            case Sustain:
                Envelope = SustainLevel;
                break;
            case Release:
                if (_releaseStep < 0)
                {
                    _releaseStep = ReleaseTime <= 0 ? double.PositiveInfinity : Envelope / (ReleaseTime * rate);
                }

                Envelope = (float)(Envelope - _releaseStep);
                if (Envelope <= 0f)
                {
                    StopNow();
                }

                break;
        }

        return Envelope;
    }

    private float Oscillator(double step)
    {
        double p = _phase;
        _phase += step;
        if (_phase >= 1.0)
        {
            _phase -= Math.Floor(_phase);
            if (Waveform == 5)
            {
                _noise = NextNoise();
            }
        }

        return Waveform switch
        {
            0 => (float)Math.Sin(2 * Math.PI * p),
            1 => (float)(p < 0.5 ? (4 * p) - 1 : 3 - (4 * p)),
            2 => (float)((2 * p) - 1),
            3 => (float)(1 - (2 * p)),
            4 => p < 0.5 ? 1f : -1f,
            _ => _noise,
        };
    }

    private float WavetableValue(double step)
    {
        sbyte[] table = Data!;
        double position = _phase * table.Length;
        int a = (int)position;
        int b = (a + 1) % table.Length;
        float value = (float)((table[a] + ((table[b] - table[a]) * (position - a))) / 128.0);
        _phase += step;
        _phase -= Math.Floor(_phase);
        return value;
    }

    private float SampleValue(double step)
    {
        sbyte[] data = Data!;
        int end = LoopEnd < 0 || LoopEnd > data.Length ? data.Length : LoopEnd;
        int start = Math.Clamp(LoopStart, 0, Math.Max(0, end - 1));
        if (_position >= data.Length)
        {
            StopNow();
            return 0f;
        }

        int a = (int)_position;
        int b = a + 1;
        if (Loop && b >= end)
        {
            b = start;
        }
        else if (b >= data.Length)
        {
            b = a;
        }

        float value = (float)((data[a] + ((data[b] - data[a]) * (_position - a))) / 128.0);
        _position += step;
        if (Loop && _position >= end)
        {
            double length = end - start;
            _position = start + ((_position - start) % length);
        }

        return value;
    }

    private float NextNoise()
    {
        // xorshift32: deterministic per channel.
        _lfsr ^= _lfsr << 13;
        _lfsr ^= _lfsr >> 17;
        _lfsr ^= _lfsr << 5;
        return ((_lfsr >> 8) / (float)(1 << 23)) - 1f;
    }
}

/// <summary>A global LFO; it runs continuously.</summary>
internal sealed class Lfo
{
    private double _phase;

    public int Waveform { get; set; }

    public double Frequency { get; set; } = 1;

    public float Value { get; private set; }

    public float Advance(double rate)
    {
        double p = _phase;
        Value = Waveform switch
        {
            0 => (float)(p < 0.5 ? (4 * p) - 1 : 3 - (4 * p)),
            1 => (float)((2 * p) - 1),
            2 => (float)(1 - (2 * p)),
            _ => p < 0.5 ? 1f : -1f,
        };
        _phase += Frequency / rate;
        _phase -= Math.Floor(_phase);
        return Value;
    }
}

/// <summary>Stereo state-variable filter (topology-preserving transform).</summary>
internal sealed class Filter
{
    private double _l1, _l2, _r1, _r2;

    public int Mode { get; set; }

    public double Cutoff { get; set; } = 20000;

    public float Resonance { get; set; }

    public void Reset() => _l1 = _l2 = _r1 = _r2 = 0;

    // cutoffSemitones: LFO modulation of the cutoff in semitones; resonanceOffset: LFO modulation of the resonance.
    public void Process(ref float left, ref float right, int sampleRate, float cutoffSemitones, float resonanceOffset)
    {
        double cutoff = cutoffSemitones == 0 ? Cutoff : Cutoff * Math.Pow(2.0, cutoffSemitones / 12.0);
        cutoff = Math.Clamp(cutoff, 20.0, Math.Min(20000.0, sampleRate * 0.45));
        double resonance = Math.Clamp(Resonance + resonanceOffset, 0.0, 1.0);
        double g = Math.Tan(Math.PI * cutoff / sampleRate);
        double k = 2.0 - (1.96 * resonance);
        double a1 = 1.0 / (1.0 + (g * (g + k)));
        double a2 = g * a1;
        double a3 = g * a2;
        left = Step(left, ref _l1, ref _l2, a1, a2, a3, k);
        right = Step(right, ref _r1, ref _r2, a1, a2, a3, k);
    }

    private float Step(float x, ref double ic1, ref double ic2, double a1, double a2, double a3, double k)
    {
        double v3 = x - ic2;
        double v1 = (a1 * ic1) + (a2 * v3);
        double v2 = ic2 + (a2 * ic1) + (a3 * v3);
        ic1 = (2 * v1) - ic1;
        ic2 = (2 * v2) - ic2;
        return Mode switch
        {
            0 => (float)v2,
            1 => (float)(x - (k * v1) - v2),
            _ => (float)v1,
        };
    }
}

/// <summary>A stereo FX unit: Chorus, Delay or Reverb.</summary>
internal sealed class Effect
{
    private static readonly int[] CombLengths = [1116, 1188, 1277, 1356];
    private static readonly int[] AllpassLengths = [556, 441];
    private const int StereoSpread = 23;

    private readonly int _sampleRate;
    private readonly float[] _lineL, _lineR;
    private int _write;
    private readonly float[][] _combs;
    private readonly int[] _combPos;
    private readonly float[] _combFilter;
    private readonly float[][] _allpasses;
    private readonly int[] _allpassPos;

    public Effect(int sampleRate)
    {
        _sampleRate = sampleRate;
        int lineLength = (int)(sampleRate * 1.1) + 4; // up to 1 s delay plus modulation
        _lineL = new float[lineLength];
        _lineR = new float[lineLength];
        double scale = sampleRate / 44100.0;
        _combs = new float[8][];
        _combPos = new int[8];
        _combFilter = new float[8];
        for (int i = 0; i < 8; i++)
        {
            _combs[i] = new float[(int)((CombLengths[i % 4] + (i >= 4 ? StereoSpread : 0)) * scale) + 1];
        }

        _allpasses = new float[4][];
        _allpassPos = new int[4];
        for (int i = 0; i < 4; i++)
        {
            _allpasses[i] = new float[(int)((AllpassLengths[i % 2] + (i >= 2 ? StereoSpread : 0)) * scale) + 1];
        }
    }

    public int Type { get; set; }

    public float Parameter1 { get; set; } = 15f;

    public float Parameter2 { get; set; } = 3f;

    public float Mix { get; set; } = 0.5f;

    public void Reset()
    {
        Array.Clear(_lineL);
        Array.Clear(_lineR);
        foreach (float[] comb in _combs)
        {
            Array.Clear(comb);
        }

        foreach (float[] allpass in _allpasses)
        {
            Array.Clear(allpass);
        }

        Array.Clear(_combFilter);
    }

    public void Process(ref float left, ref float right, float m1, float m2, float m3)
    {
        float mix = Math.Clamp(Mix + m3, 0f, 1f);
        float wetL, wetR;
        switch (Type)
        {
            case 0:
            {
                // Chorus: an LFO routed to Depth swings the delay by ±Depth ms around Delay.
                float delayMs = Math.Clamp(Parameter1 + m1, 1f, 40f);
                float depthMs = Math.Clamp(Parameter2, 0f, 20f);
                float swing = Math.Clamp(m2, -1f, 1f);
                float time = Math.Clamp(delayMs + (depthMs * swing), 0.5f, 60f) * _sampleRate / 1000f;
                Write(left, right);
                wetL = Read(_lineL, time);
                wetR = Read(_lineR, time);
                break;
            }

            case 1:
            {
                float time = Math.Clamp(Parameter1 + m1, 1f, 1000f) * _sampleRate / 1000f;
                float feedback = Math.Clamp(Parameter2 + m2, 0f, 0.95f);
                wetL = Read(_lineL, time + 1);
                wetR = Read(_lineR, time + 1);
                Write(left + (wetL * feedback), right + (wetR * feedback));
                break;
            }

            default:
            {
                float size = Math.Clamp(Parameter1 + m1, 0f, 1f);
                float decay = Math.Clamp(Parameter2 + m2, 0f, 1f);
                float feedback = 0.7f + (0.28f * decay);
                float input = (left + right) * 0.03f;
                wetL = 0f;
                wetR = 0f;
                for (int i = 0; i < 8; i++)
                {
                    float[] comb = _combs[i];
                    int length = Math.Max(1, (int)(comb.Length * (0.5f + (0.5f * size))));
                    int pos = _combPos[i] % length;
                    float output = comb[pos];
                    _combFilter[i] = (output * 0.8f) + (_combFilter[i] * 0.2f);
                    comb[pos] = input + (_combFilter[i] * feedback);
                    _combPos[i] = (pos + 1) % length;
                    if (i < 4)
                    {
                        wetL += output;
                    }
                    else
                    {
                        wetR += output;
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    float[] allpass = _allpasses[i];
                    int pos = _allpassPos[i];
                    float buffered = allpass[pos];
                    ref float signal = ref (i < 2 ? ref wetL : ref wetR);
                    float output = buffered - signal;
                    allpass[pos] = signal + (buffered * 0.5f);
                    signal = output;
                    _allpassPos[i] = (pos + 1) % allpass.Length;
                }

                break;
            }
        }

        left = (left * (1f - mix)) + (wetL * mix);
        right = (right * (1f - mix)) + (wetR * mix);
    }

    private void Write(float left, float right)
    {
        _lineL[_write] = left;
        _lineR[_write] = right;
        _write = (_write + 1) % _lineL.Length;
    }

    // Reads the line 'delay' samples behind the most recent write, with linear interpolation.
    private float Read(float[] line, float delay)
    {
        double position = _write - 1 - delay;
        while (position < 0)
        {
            position += line.Length;
        }

        int a = (int)position;
        int b = (a + 1) % line.Length;
        float t = (float)(position - a);
        return line[a] + ((line[b] - line[a]) * t);
    }
}
