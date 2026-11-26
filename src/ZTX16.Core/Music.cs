using ZTX16.Collections;
using ZTX16.Core;

namespace ZTX16;

/// <summary>Where an <see cref="Instrument"/>'s sound comes from, which decides the channels it can use.</summary>
public enum InstrumentSource
{
    /// <summary>A Synth oscillator: channels 0–5.</summary>
    Synth = 0,

    /// <summary>A Sample resource: channels 6–7.</summary>
    Sample = 1,

    /// <summary>A Wavetable resource: channels 6–7.</summary>
    Wavetable = 2,
}

/// <summary>
/// An Instrument resource: the immutable base configuration of a channel (source, volume, pan, ADSR and its
/// destination, desired LFO amounts). It owns no LFOs, Filters or FX and never sets their waveform or rate.
/// </summary>
public sealed class Instrument
{
    private static readonly float[] SemitoneRatios =
    [
        1f, 1.0594631f, 1.122462f, 1.1892071f, 1.2599211f, 1.3348398f, 1.4142135f, 1.4983071f, 1.587401f, 1.6817929f,
        1.7817974f, 1.8877486f,
    ];

    private Instrument(string name)
    {
        Name = name;
    }

    /// <summary>Resource name.</summary>
    public string Name { get; }

    /// <summary>Synth, Sample or Wavetable.</summary>
    public InstrumentSource Source { get; private set; }

    /// <summary>Oscillator waveform of a Synth instrument.</summary>
    public Waveform Waveform { get; private set; }

    /// <summary>Sample or Wavetable resource name, or null for Synth instruments.</summary>
    public string? Resource { get; private set; }

    /// <summary>Base volume.</summary>
    public float Volume { get; private set; }

    /// <summary>Base pan.</summary>
    public float Pan { get; private set; }

    /// <summary>ADSR attack in seconds.</summary>
    public float Attack { get; private set; }

    /// <summary>ADSR decay in seconds.</summary>
    public float Decay { get; private set; }

    /// <summary>ADSR sustain level.</summary>
    public float Sustain { get; private set; }

    /// <summary>ADSR release in seconds.</summary>
    public float Release { get; private set; }

    /// <summary>The ADSR's destination.</summary>
    public ModulationDestination AdsrTarget { get; private set; }

    /// <summary>The ADSR's amount.</summary>
    public float AdsrAmount { get; private set; }

    /// <summary>Desired LFO 1 → pitch amount (semitones).</summary>
    public float Lfo1Pitch { get; private set; }

    /// <summary>Desired LFO 1 → volume amount.</summary>
    public float Lfo1Volume { get; private set; }

    /// <summary>Desired LFO 2 → pitch amount (semitones).</summary>
    public float Lfo2Pitch { get; private set; }

    /// <summary>Desired LFO 2 → volume amount.</summary>
    public float Lfo2Volume { get; private set; }

    /// <summary>Whether a sample instrument loops.</summary>
    public bool Loop { get; private set; }

    /// <summary>Loop start in frames.</summary>
    public int LoopStart { get; private set; }

    /// <summary>Loop end in frames (−1 = sample end).</summary>
    public int LoopEnd { get; private set; }

    /// <summary>Playback rate of a sample instrument at <see cref="BaseNote"/>.</summary>
    public float PlaybackRate { get; private set; }

    /// <summary>The note a sample instrument plays at <see cref="PlaybackRate"/>.</summary>
    public int BaseNote { get; private set; }

    /// <summary>True if the instrument plays on Synth channels (0–5); otherwise on channels 6–7.</summary>
    public bool UsesSynthChannels => Source == InstrumentSource.Synth;

    /// <summary>Loads the Instrument resource <paramref name="name"/>.</summary>
    public static Instrument Load(string name)
    {
        var r = new ResourceReader(ResourceType.Instrument, name);
        if (r.Int() != 1)
        {
            Intrinsics.Fail("Instrument: unsupported data version");
        }

        var i = new Instrument(name);
        i.Source = (InstrumentSource)r.Int();
        i.Waveform = (Waveform)r.Int();
        i.Resource = r.StringOrNull();
        i.Volume = r.Float();
        i.Pan = r.Float();
        i.Attack = r.Float();
        i.Decay = r.Float();
        i.Sustain = r.Float();
        i.Release = r.Float();
        i.AdsrTarget = (ModulationDestination)r.Int();
        i.AdsrAmount = r.Float();
        i.Lfo1Pitch = r.Float();
        i.Lfo1Volume = r.Float();
        i.Lfo2Pitch = r.Float();
        i.Lfo2Volume = r.Float();
        i.Loop = r.Int() != 0;
        i.LoopStart = r.Int();
        i.LoopEnd = r.Int();
        i.PlaybackRate = r.Float();
        i.BaseNote = r.Int();
        return i;
    }

    /// <summary>Frequency of a MIDI note in Hz (A4 = 69 = 440 Hz, equal temperament).</summary>
    public static float NoteFrequency(int note) => 440f * Ratio(note - 69);

    /// <summary>Frequency ratio of a whole number of semitones.</summary>
    public static float Ratio(int semitones)
    {
        int octave = semitones >= 0 ? semitones / 12 : -((11 - semitones) / 12);
        int semitone = semitones - (octave * 12);
        float ratio = SemitoneRatios[semitone];
        for (int i = 0; i < octave; i++)
        {
            ratio *= 2f;
        }

        for (int i = 0; i > octave; i--)
        {
            ratio *= 0.5f;
        }

        return ratio;
    }

    /// <summary>Fully configures a physical channel with this instrument (source, levels, ADSR, LFO amounts).</summary>
    internal void Configure(int channel)
    {
        if (Source == InstrumentSource.Synth)
        {
            Audio.SetWaveform(channel, Waveform);
        }
        else if (Source == InstrumentSource.Sample)
        {
            Audio.UseSample(channel, Resource!);
            Audio.SetSampleLoop(channel, Loop, LoopStart, LoopEnd);
        }
        else
        {
            Audio.UseWavetable(channel, Resource!);
        }

        Audio.SetVolume(channel, Volume);
        Audio.SetPan(channel, Pan);
        Audio.SetAdsr(channel, Attack, Decay, Sustain, Release);
        Audio.RouteAdsr(channel, AdsrTarget, AdsrAmount);
        Audio.RouteLfo(0, channel, ModulationDestination.Pitch, Lfo1Pitch);
        Audio.RouteLfo(0, channel, ModulationDestination.Volume, Lfo1Volume);
        Audio.RouteLfo(1, channel, ModulationDestination.Pitch, Lfo2Pitch);
        Audio.RouteLfo(1, channel, ModulationDestination.Volume, Lfo2Volume);
    }

    /// <summary>Sets the channel's frequency (or sample playback rate) for a note.</summary>
    internal void SetNote(int channel, int note)
    {
        if (Source == InstrumentSource.Sample)
        {
            Audio.SetPlaybackRate(channel, PlaybackRate * Ratio(note - BaseNote));
        }
        else
        {
            Audio.SetFrequency(channel, NoteFrequency(note));
        }
    }

    /// <summary>Sets the channel's frequency (or sample playback rate) directly.</summary>
    internal void SetPitch(int channel, float pitch)
    {
        if (Source == InstrumentSource.Sample)
        {
            Audio.SetPlaybackRate(channel, pitch);
        }
        else
        {
            Audio.SetFrequency(channel, pitch);
        }
    }
}

/// <summary>Which occupied channel the allocator takes when every eligible channel is busy.</summary>
public enum StealPolicy
{
    /// <summary>The first eligible channel in group order (default).</summary>
    ByOrder = 0,

    /// <summary>The channel whose current sound started longest ago.</summary>
    Oldest = 1,

    /// <summary>The channel with the lowest effective volume.</summary>
    Quietest = 2,
}

/// <summary>The physical channels a <see cref="Voice"/> may use, in preference order, and the steal policy.</summary>
public sealed class ChannelGroup
{
    private readonly int[] _channels;

    /// <summary>Creates a group. All channels must be of one kind (Synth 0–5, or Sample/Wavetable 6–7).</summary>
    public ChannelGroup(int[] channels, StealPolicy policy = StealPolicy.ByOrder)
    {
        if (channels.Length == 0)
        {
            Intrinsics.Fail("ChannelGroup: needs at least one channel");
        }

        _channels = new int[channels.Length];
        for (int i = 0; i < channels.Length; i++)
        {
            int c = channels[i];
            if (c < 0 || c >= Audio.ChannelCount)
            {
                Intrinsics.Fail("ChannelGroup: channels are 0..7");
            }

            if ((c < Audio.FirstSampleChannel) != (channels[0] < Audio.FirstSampleChannel))
            {
                Intrinsics.Fail("ChannelGroup: Synth (0..5) and Sample/Wavetable (6..7) channels cannot be mixed");
            }

            for (int j = 0; j < i; j++)
            {
                if (_channels[j] == c)
                {
                    Intrinsics.Fail("ChannelGroup: a channel is listed twice");
                }
            }

            _channels[i] = c;
        }

        Policy = policy;
    }

    /// <summary>The steal policy.</summary>
    public StealPolicy Policy { get; }

    /// <summary>Number of channels.</summary>
    public int Count => _channels.Length;

    /// <summary>True for a group of Synth channels.</summary>
    public bool IsSynth => _channels[0] < Audio.FirstSampleChannel;

    /// <summary>The channel at <paramref name="index"/>.</summary>
    public int this[int index] => _channels[index];

    /// <summary>All Synth channels 0–5.</summary>
    public static ChannelGroup Synth(StealPolicy policy = StealPolicy.ByOrder) => new ChannelGroup(new int[] { 0, 1, 2, 3, 4, 5 }, policy);

    /// <summary>Both Sample/Wavetable channels 6–7.</summary>
    public static ChannelGroup Samplers(StealPolicy policy = StealPolicy.ByOrder) => new ChannelGroup(new int[] { 6, 7 }, policy);
}

/// <summary>
/// One sounding runtime instance of an <see cref="Instrument"/>. Playing asks the allocator for a channel of
/// its <see cref="ChannelGroup"/>: a free one first, otherwise one chosen by the group's steal policy. A voice
/// keeps its channel until another owner takes it, so repeated notes retrigger the same channel.
/// </summary>
/// <remarks>
/// <see cref="StealLock"/> keeps a sounding voice from being stolen. If every eligible channel is occupied by
/// a steal-locked voice, <see cref="Play"/> returns false and nothing sounds — the lock is never broken.
/// </remarks>
public sealed class Voice
{
    private int _channel = -1;
    private bool _hasVolume;
    private float _volume;
    private bool _hasPan;
    private float _pan;

    /// <summary>Creates a voice; without a group it uses all channels its instrument can play on.</summary>
    public Voice(Instrument instrument, ChannelGroup? group = null)
    {
        Instrument = instrument;
        Group = group ?? (instrument.UsesSynthChannels ? ChannelGroup.Synth() : ChannelGroup.Samplers());
        if (Group.IsSynth != instrument.UsesSynthChannels)
        {
            Intrinsics.Fail("Voice: the channel group does not match the instrument (Synth 0..5, Sample/Wavetable 6..7)");
        }
    }

    /// <summary>The instrument.</summary>
    public Instrument Instrument { get; }

    /// <summary>The eligible channels.</summary>
    public ChannelGroup Group { get; }

    /// <summary>While true, a sounding voice is never chosen for stealing.</summary>
    public bool StealLock { get; set; }

    /// <summary>The note of the last <see cref="Play"/> (−1 after <see cref="PlayPitch"/>).</summary>
    public int Note { get; private set; } = -1;

    /// <summary>The physical channel the voice owns, or −1.</summary>
    public int Channel => OwnsChannel ? _channel : -1;

    /// <summary>True while the voice owns a channel that is sounding.</summary>
    public bool IsPlaying => OwnsChannel && AudioChannels.IsSounding(_channel);

    private bool OwnsChannel => _channel >= 0 && AudioChannels.Owner(_channel) == this;

    /// <summary>Plays a MIDI note (60 = C4). Returns false if no channel could be allocated.</summary>
    public bool Play(int note)
    {
        if (!Acquire())
        {
            return false;
        }

        Note = note;
        Instrument.SetNote(_channel, note);
        Trigger();
        return true;
    }

    /// <summary>Plays at a frequency in Hz (Synth, Wavetable) or a playback rate (Sample).</summary>
    public bool PlayPitch(float pitch)
    {
        if (!Acquire())
        {
            return false;
        }

        Note = -1;
        Instrument.SetPitch(_channel, pitch);
        Trigger();
        return true;
    }

    /// <summary>Begins the release of the current note.</summary>
    public void ReleaseNote()
    {
        if (OwnsChannel)
        {
            Audio.NoteOff(_channel);
        }
    }

    /// <summary>Silences at once and gives the channel back.</summary>
    public void Stop()
    {
        if (OwnsChannel)
        {
            Audio.Stop(_channel);
            AudioChannels.Release(_channel);
        }

        _channel = -1;
    }

    /// <summary>Overrides the instrument's volume for this voice.</summary>
    public void SetVolume(float volume)
    {
        _hasVolume = true;
        _volume = volume;
        if (OwnsChannel)
        {
            Audio.SetVolume(_channel, volume);
        }
    }

    /// <summary>Overrides the instrument's pan for this voice.</summary>
    public void SetPan(float pan)
    {
        _hasPan = true;
        _pan = pan;
        if (OwnsChannel)
        {
            Audio.SetPan(_channel, pan);
        }
    }

    /// <summary>Returns to the instrument's own volume and pan.</summary>
    public void ClearOverrides()
    {
        _hasVolume = false;
        _hasPan = false;
        if (OwnsChannel)
        {
            Audio.SetVolume(_channel, Instrument.Volume);
            Audio.SetPan(_channel, Instrument.Pan);
        }
    }

    private bool Acquire()
    {
        if (OwnsChannel)
        {
            return true;
        }

        int channel = AudioChannels.Allocate(this);
        if (channel < 0)
        {
            _channel = -1;
            return false;
        }

        // A reused channel is fully reconfigured; the old owner's sound does not carry over.
        _channel = channel;
        Instrument.Configure(channel);
        if (_hasVolume)
        {
            Audio.SetVolume(channel, _volume);
        }

        if (_hasPan)
        {
            Audio.SetPan(channel, _pan);
        }

        return true;
    }

    private void Trigger()
    {
        Audio.NoteOn(_channel);
        AudioChannels.Started(_channel);
    }
}

/// <summary>
/// Ownership of the eight physical channels: who drives each (a <see cref="Voice"/>, a
/// <see cref="SongPlayback"/> or nobody), when its sound started and which song it belongs to.
/// </summary>
internal static class AudioChannels
{
    private static object?[]? _owners;
    private static long[]? _started;
    private static SongPlayback?[]? _homes;

    public static object? Owner(int channel)
    {
        Ensure();
        return _owners![channel];
    }

    public static SongPlayback? Home(int channel)
    {
        Ensure();
        return _homes![channel];
    }

    public static void SetHome(int channel, SongPlayback? song)
    {
        Ensure();
        _homes![channel] = song;
    }

    /// <summary>
    /// True while the channel's current sound is running. A note started this tick counts even though the
    /// hardware applies it only in this tick's Audio Update.
    /// </summary>
    public static bool IsSounding(int channel)
    {
        Ensure();
        return _started![channel] == Machine.TickCount || Audio.GetChannelState(channel) != ChannelState.Idle;
    }

    /// <summary>True if a voice may take the channel without stealing: nobody owns it, or a voice's sound
    /// ended on a channel no song is waiting for.</summary>
    public static bool IsFree(int channel)
    {
        Ensure();
        object? owner = _owners![channel];
        if (_homes![channel] is not null && owner != _homes[channel])
        {
            // A song waits for this channel: it is the song's again once the stealing voice ends.
            return false;
        }

        return owner is null || (owner is Voice && !IsSounding(channel));
    }

    public static void Assign(int channel, object owner)
    {
        Ensure();
        _owners![channel] = owner;
        _started![channel] = Machine.TickCount;
    }

    public static void Started(int channel)
    {
        Ensure();
        _started![channel] = Machine.TickCount;
    }

    public static void Release(int channel)
    {
        Ensure();
        _owners![channel] = null;
    }

    /// <summary>Finds a channel for a voice: free first, then the group's steal policy. −1 if every candidate is steal-locked.</summary>
    public static int Allocate(Voice voice)
    {
        Ensure();
        ChannelGroup group = voice.Group;
        for (int i = 0; i < group.Count; i++)
        {
            int c = group[i];
            if (IsFree(c))
            {
                Assign(c, voice);
                return c;
            }
        }

        int best = -1;
        float bestVolume = 0f;
        for (int i = 0; i < group.Count; i++)
        {
            int c = group[i];
            if (_owners![c] is Voice owner && owner.StealLock && IsSounding(c))
            {
                continue;
            }

            if (best < 0)
            {
                best = c;
                bestVolume = Audio.GetEffectiveVolume(c);
                if (group.Policy == StealPolicy.ByOrder)
                {
                    break;
                }

                continue;
            }

            if (group.Policy == StealPolicy.Oldest && _started![c] < _started[best])
            {
                best = c;
            }
            else if (group.Policy == StealPolicy.Quietest)
            {
                float volume = Audio.GetEffectiveVolume(c);
                if (volume < bestVolume)
                {
                    best = c;
                    bestVolume = volume;
                }
            }
        }

        if (best >= 0)
        {
            Assign(best, voice);
        }

        return best;
    }

    private static void Ensure()
    {
        if (_owners is null)
        {
            _owners = new object?[Audio.ChannelCount];
            _started = new long[Audio.ChannelCount];
            _homes = new SongPlayback?[Audio.ChannelCount];
            for (int i = 0; i < Audio.ChannelCount; i++)
            {
                _started[i] = -1;
            }
        }
    }
}

/// <summary>A strictly monophonic Sequence resource: its instrument and a timeline of notes, rests and volume changes.</summary>
public sealed class Sequence
{
    private Sequence(string name)
    {
        Name = name;
    }

    /// <summary>Resource name.</summary>
    public string Name { get; }

    /// <summary>The instrument the sequence plays.</summary>
    public Instrument Instrument { get; private set; } = null!;

    /// <summary>Sequence Ticks per beat.</summary>
    public int TicksPerBeat { get; private set; }

    /// <summary>Default tempo.</summary>
    public float Bpm { get; private set; }

    /// <summary>Whether playback restarts at the end.</summary>
    public bool Loop { get; private set; }

    /// <summary>Number of events.</summary>
    public int EventCount => Kinds.Length;

    internal int[] Kinds { get; private set; } = null!;

    internal int[] Notes { get; private set; } = null!;

    internal int[] Lengths { get; private set; } = null!;

    internal int[] Gates { get; private set; } = null!;

    internal float[] Volumes { get; private set; } = null!;

    /// <summary>Loads the Sequence resource <paramref name="name"/> (and its instrument).</summary>
    public static Sequence Load(string name)
    {
        var r = new ResourceReader(ResourceType.Sequence, name);
        if (r.Int() != 1)
        {
            Intrinsics.Fail("Sequence: unsupported data version");
        }

        var s = new Sequence(name);
        s.Instrument = Instrument.Load(r.String());
        s.TicksPerBeat = r.Int();
        s.Bpm = r.Float();
        s.Loop = r.Int() != 0;
        int count = r.Int();
        s.Kinds = new int[count];
        s.Notes = new int[count];
        s.Lengths = new int[count];
        s.Gates = new int[count];
        s.Volumes = new float[count];
        for (int i = 0; i < count; i++)
        {
            s.Kinds[i] = r.Int();
            s.Notes[i] = r.Int();
            s.Lengths[i] = r.Int();
            s.Gates[i] = r.Int();
            s.Volumes[i] = r.Float();
        }

        return s;
    }
}

/// <summary>Receives what a sequence does: a Voice for standalone playback, a fixed channel inside a Song.</summary>
internal interface ISequenceTarget
{
    void StartNote(int note);

    void ReleaseNote();

    void SetVolumeFactor(float factor);
}

/// <summary>Position and timing in a sequence, in Sequence Ticks.</summary>
internal sealed class SequenceCursor
{
    private int _index;
    private double _time;
    private double _nextAt;
    private double _offAt;
    private bool _noteOn;

    public SequenceCursor(Sequence sequence)
    {
        Sequence = sequence;
    }

    public Sequence Sequence { get; }

    public bool Finished { get; private set; }

    public double Time => _time;

    public void Reset()
    {
        _index = 0;
        _time = 0;
        _nextAt = 0;
        _offAt = 0;
        _noteOn = false;
        Finished = false;
    }

    /// <summary>
    /// Runs every event due at the current time, then advances by <paramref name="ticks"/> Sequence Ticks.
    /// An event at time t therefore fires in the update during which the position reaches t.
    /// </summary>
    public void Advance(double ticks, ISequenceTarget target)
    {
        if (Finished)
        {
            return;
        }

        Run(target);
        _time += ticks;
    }

    private void Run(ISequenceTarget target)
    {
        Sequence s = Sequence;
        while (true)
        {
            bool eventsLeft = _index < s.EventCount;
            if (_noteOn && _offAt <= _time && (!eventsLeft || _offAt <= _nextAt))
            {
                target.ReleaseNote();
                _noteOn = false;
                continue;
            }

            if (eventsLeft)
            {
                if (_nextAt > _time)
                {
                    return;
                }

                int kind = s.Kinds[_index];
                if (kind == 0)
                {
                    target.StartNote(s.Notes[_index]);
                    _noteOn = true;
                    _offAt = _nextAt + s.Gates[_index];
                    _nextAt += s.Lengths[_index];
                }
                else if (kind == 1)
                {
                    _nextAt += s.Lengths[_index];
                }
                else
                {
                    target.SetVolumeFactor(s.Volumes[_index]);
                }

                _index++;
                continue;
            }

            if (s.Loop)
            {
                _index = 0;
                continue;
            }

            if (!_noteOn)
            {
                Finished = true;
            }

            return;
        }
    }

    /// <summary>Ends a held note now (used when playback stops).</summary>
    public void Silence(ISequenceTarget target)
    {
        if (_noteOn)
        {
            target.ReleaseNote();
            _noteOn = false;
        }
    }
}

/// <summary>
/// Runtime state that executes a <see cref="Sequence"/> through one <see cref="Voice"/>. Time is counted in
/// Sequence Ticks; <see cref="TimeScale"/> is how many pass per runtime tick, and <see cref="SetBpm"/> sets it
/// from a tempo. Call <see cref="Update"/> once per tick. Polyphony = several playbacks.
/// </summary>
public sealed class SequencePlayback
{
    private readonly SequenceCursor _cursor;
    private readonly VoiceTarget _target;

    /// <summary>Creates a stopped playback; without a voice it makes one for the sequence's instrument.</summary>
    public SequencePlayback(Sequence sequence, Voice? voice = null)
    {
        Sequence = sequence;
        Voice = voice ?? new Voice(sequence.Instrument);
        _cursor = new SequenceCursor(sequence);
        _target = new VoiceTarget(Voice);
        SetBpm(sequence.Bpm);
    }

    /// <summary>The sequence.</summary>
    public Sequence Sequence { get; }

    /// <summary>The voice the notes play through.</summary>
    public Voice Voice { get; }

    /// <summary>Sequence Ticks per runtime tick.</summary>
    public float TimeScale { get; set; }

    /// <summary>True while playing.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>True during the update in which a non-looping sequence ended.</summary>
    public bool Finished { get; private set; }

    /// <summary>Current position in Sequence Ticks.</summary>
    public float Position => (float)_cursor.Time;

    /// <summary>Sets <see cref="TimeScale"/> from beats per minute (60 runtime ticks per second).</summary>
    public void SetBpm(float bpm) => TimeScale = bpm * Sequence.TicksPerBeat / 3600f;

    /// <summary>Starts from the beginning.</summary>
    public void Play()
    {
        _cursor.Reset();
        Voice.ClearOverrides();
        IsPlaying = true;
        Finished = false;
    }

    /// <summary>Stops and releases the current note.</summary>
    public void Stop()
    {
        _cursor.Silence(_target);
        IsPlaying = false;
    }

    /// <summary>Stops advancing (a held note keeps sounding).</summary>
    public void Pause() => IsPlaying = false;

    /// <summary>Continues after <see cref="Pause"/>.</summary>
    public void Resume() => IsPlaying = !_cursor.Finished;

    /// <summary>Advances by one runtime tick.</summary>
    public void Update()
    {
        Finished = false;
        if (!IsPlaying)
        {
            return;
        }

        _cursor.Advance(TimeScale, _target);
        if (_cursor.Finished)
        {
            IsPlaying = false;
            Finished = true;
        }
    }

    private sealed class VoiceTarget : ISequenceTarget
    {
        private readonly Voice _voice;

        public VoiceTarget(Voice voice)
        {
            _voice = voice;
        }

        public void StartNote(int note) => _voice.Play(note);

        public void ReleaseNote() => _voice.ReleaseNote();

        public void SetVolumeFactor(float factor) => _voice.SetVolume(_voice.Instrument.Volume * factor);
    }
}

/// <summary>
/// A Song resource: the complete physical audio configuration over time — which sequence plays on which
/// physical channel, and LFO, filter and effect settings and routing.
/// </summary>
public sealed class Song
{
    private readonly Dictionary<string, Sequence> _sequences = new();

    private Song(string name)
    {
        Name = name;
    }

    /// <summary>Resource name.</summary>
    public string Name { get; }

    /// <summary>Song ticks per beat.</summary>
    public int TicksPerBeat { get; private set; }

    /// <summary>Default tempo.</summary>
    public float Bpm { get; private set; }

    /// <summary>Length in song ticks.</summary>
    public int Length { get; private set; }

    /// <summary>Whether the song restarts at the end.</summary>
    public bool Loop { get; private set; }

    internal int EventCount => At.Length;

    internal int[] At { get; private set; } = null!;

    internal int[] Kind { get; private set; } = null!;

    internal int[] P0 { get; private set; } = null!;

    internal int[] P1 { get; private set; } = null!;

    internal Sequence?[] Sequences { get; private set; } = null!;

    internal float[] F0 { get; private set; } = null!;

    internal float[] F1 { get; private set; } = null!;

    internal float[] F2 { get; private set; } = null!;

    internal int[] Assign { get; private set; } = null!;

    /// <summary>Loads the Song resource <paramref name="name"/> and the sequences it uses.</summary>
    public static Song Load(string name)
    {
        var r = new ResourceReader(ResourceType.Song, name);
        if (r.Int() != 1)
        {
            Intrinsics.Fail("Song: unsupported data version");
        }

        var song = new Song(name);
        song.TicksPerBeat = r.Int();
        song.Bpm = r.Float();
        song.Length = r.Int();
        song.Loop = r.Int() != 0;
        int count = r.Int();
        song.At = new int[count];
        song.Kind = new int[count];
        song.P0 = new int[count];
        song.P1 = new int[count];
        song.Sequences = new Sequence?[count];
        song.F0 = new float[count];
        song.F1 = new float[count];
        song.F2 = new float[count];
        song.Assign = new int[count];
        for (int i = 0; i < count; i++)
        {
            song.At[i] = r.Int();
            song.Kind[i] = r.Int();
            song.P0[i] = r.Int();
            song.P1[i] = r.Int();
            string? sequence = r.StringOrNull();
            if (sequence is not null)
            {
                if (!song._sequences.TryGetValue(sequence, out Sequence loaded))
                {
                    loaded = Sequence.Load(sequence);
                    song._sequences[sequence] = loaded;
                }

                song.Sequences[i] = loaded;
            }

            song.F0[i] = r.Float();
            song.F1[i] = r.Float();
            song.F2[i] = r.Float();
            song.Assign[i] = r.Int();
        }

        return song;
    }
}

/// <summary>
/// Runtime state that executes a <see cref="Song"/>. It drives the physical channels the song names directly
/// (no allocator) and sets LFOs, filters and effects. Call <see cref="Update"/> once per tick.
/// </summary>
/// <remarks>
/// A voice (sound effect or standalone sequence) may steal a song channel: the song then has no control over
/// it, its timeline continues and the events it misses are not replayed. When the stealing voice ends, the
/// song takes the channel back and restores the instrument of the sequence assigned to it; shared LFO, filter
/// and effect settings are left as they are. The song never steals a channel back from a sounding voice.
/// </remarks>
public sealed class SongPlayback
{
    private const int Keep = -2;

    private readonly Track?[] _tracks = new Track?[Audio.ChannelCount];
    private double _time;
    private int _index;

    /// <summary>Creates a stopped playback.</summary>
    public SongPlayback(Song song)
    {
        Song = song;
        SetBpm(song.Bpm);
    }

    /// <summary>The song.</summary>
    public Song Song { get; }

    /// <summary>Song ticks per runtime tick.</summary>
    public float TimeScale { get; set; }

    /// <summary>True while playing.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>True during the update in which a non-looping song ended.</summary>
    public bool Finished { get; private set; }

    /// <summary>Current position in song ticks.</summary>
    public float Position => (float)_time;

    /// <summary>Sets <see cref="TimeScale"/> from beats per minute.</summary>
    public void SetBpm(float bpm) => TimeScale = bpm * Song.TicksPerBeat / 3600f;

    /// <summary>True if the song currently controls <paramref name="channel"/> (it is not stolen).</summary>
    public bool Controls(int channel) => AudioChannels.Owner(channel) == this;

    /// <summary>Starts from the beginning.</summary>
    public void Play()
    {
        Stop();
        _time = 0;
        _index = 0;
        IsPlaying = true;
        Finished = false;
    }

    /// <summary>Stops: releases the song's notes and gives its channels up.</summary>
    public void Stop()
    {
        for (int c = 0; c < Audio.ChannelCount; c++)
        {
            ClearTrack(c);
        }

        IsPlaying = false;
    }

    /// <summary>Advances by one runtime tick.</summary>
    public void Update()
    {
        Finished = false;
        if (!IsPlaying)
        {
            return;
        }

        // Events due at the current position run first, then everything advances by one runtime tick.
        Reclaim();
        RunEvents();
        AdvanceTracks(TimeScale);
        _time += TimeScale;
        if (_time >= Song.Length)
        {
            if (Song.Loop)
            {
                _time -= Song.Length;
                _index = 0;
            }
            else
            {
                Stop();
                Finished = true;
            }
        }
    }

    private void RunEvents()
    {
        Song s = Song;
        while (_index < s.EventCount && s.At[_index] <= _time)
        {
            int i = _index++;
            int kind = s.Kind[i];
            if (kind == 0)
            {
                AssignSequence(s.P0[i], s.Sequences[i], _time - s.At[i]);
            }
            else if (kind == 1)
            {
                Audio.SetLfo(s.P0[i], (LfoWaveform)s.P1[i], s.F0[i]);
            }
            else if (kind == 2)
            {
                Audio.RouteLfo(s.P0[i], s.P1[i], (ModulationDestination)s.Assign[i], s.F0[i]);
            }
            else if (kind == 3)
            {
                Audio.RouteLfoToProcessor(s.P0[i], (ProcessorUnit)s.P1[i], (ProcessorParameter)s.Assign[i], s.F0[i]);
            }
            else if (kind == 4)
            {
                Audio.SetFilter(s.P0[i], (FilterMode)s.P1[i], s.F0[i], s.F1[i]);
                if (s.Assign[i] != Keep)
                {
                    Audio.AssignProcessor((ProcessorUnit)s.P0[i], s.Assign[i]);
                }
            }
            else
            {
                Audio.SetEffect(s.P0[i], (EffectType)s.P1[i], s.F0[i], s.F1[i], s.F2[i]);
                if (s.Assign[i] != Keep)
                {
                    Audio.AssignProcessor((ProcessorUnit)(s.P0[i] + 2), s.Assign[i]);
                }
            }
        }
    }

    private void AssignSequence(int channel, Sequence? sequence, double late)
    {
        ClearTrack(channel);
        if (sequence is null)
        {
            return;
        }

        var track = new Track(this, channel, sequence);
        _tracks[channel] = track;
        AudioChannels.SetHome(channel, this);
        object? owner = AudioChannels.Owner(channel);
        if (owner is null || owner == this || owner is SongPlayback || !AudioChannels.IsSounding(channel))
        {
            AudioChannels.Assign(channel, this);
            track.Configure();
        }

        // Catch up the part of the position that already passed the event's time.
        track.Advance(late * sequence.TicksPerBeat / Song.TicksPerBeat);
    }

    private void ClearTrack(int channel)
    {
        Track? track = _tracks[channel];
        if (track is null)
        {
            return;
        }

        track.Silence();
        _tracks[channel] = null;
        if (AudioChannels.Owner(channel) == this)
        {
            AudioChannels.Release(channel);
        }

        if (AudioChannels.Home(channel) == this)
        {
            AudioChannels.SetHome(channel, null);
        }
    }

    private void AdvanceTracks(double songTicks)
    {
        for (int c = 0; c < Audio.ChannelCount; c++)
        {
            Track? track = _tracks[c];
            if (track is null)
            {
                continue;
            }

            track.Advance(songTicks * track.Sequence.TicksPerBeat / Song.TicksPerBeat);
        }
    }

    // A channel stolen by a voice returns to the song when that voice's sound has ended.
    private void Reclaim()
    {
        for (int c = 0; c < Audio.ChannelCount; c++)
        {
            Track? track = _tracks[c];
            if (track is null || AudioChannels.Owner(c) == this)
            {
                continue;
            }

            object? owner = AudioChannels.Owner(c);
            if (owner is null || (owner is Voice && !AudioChannels.IsSounding(c)))
            {
                AudioChannels.Assign(c, this);
                track.Configure();
            }
        }
    }

    private sealed class Track : ISequenceTarget
    {
        private readonly SongPlayback _song;
        private readonly int _channel;
        private readonly SequenceCursor _cursor;
        private float _volumeFactor = 1f;

        public Track(SongPlayback song, int channel, Sequence sequence)
        {
            _song = song;
            _channel = channel;
            Sequence = sequence;
            _cursor = new SequenceCursor(sequence);
        }

        public Sequence Sequence { get; }

        public void Configure()
        {
            Sequence.Instrument.Configure(_channel);
            Audio.SetVolume(_channel, Sequence.Instrument.Volume * _volumeFactor);
        }

        public void Advance(double ticks) => _cursor.Advance(ticks, this);

        public void Silence() => _cursor.Silence(this);

        public void StartNote(int note)
        {
            if (_song.Controls(_channel))
            {
                Sequence.Instrument.SetNote(_channel, note);
                Audio.NoteOn(_channel);
                AudioChannels.Started(_channel);
            }
        }

        public void ReleaseNote()
        {
            if (_song.Controls(_channel))
            {
                Audio.NoteOff(_channel);
            }
        }

        public void SetVolumeFactor(float factor)
        {
            _volumeFactor = factor;
            if (_song.Controls(_channel))
            {
                Audio.SetVolume(_channel, Sequence.Instrument.Volume * factor);
            }
        }
    }
}

/// <summary>Sequential reader of an Instrument, Sequence or Song value stream.</summary>
internal sealed class ResourceReader
{
    private readonly ResourceType _type;
    private readonly string _name;
    private readonly int _count;
    private int _position;

    public ResourceReader(ResourceType type, string name)
    {
        _type = type;
        _name = name;
        _count = ResourceData.ValueCount(type, name);
    }

    public int Int()
    {
        Check();
        return ResourceData.Value(_type, _name, _position++);
    }

    public float Float()
    {
        Check();
        return ResourceData.FloatValue(_type, _name, _position++);
    }

    public string String()
    {
        string? value = StringOrNull();
        if (value is null)
        {
            Intrinsics.Fail("Resource data: a required name is missing");
        }

        return value!;
    }

    public string? StringOrNull()
    {
        int index = Int();
        return index < 0 ? null : ResourceData.String(_type, _name, index);
    }

    private void Check()
    {
        if (_position >= _count)
        {
            Intrinsics.Fail("Resource data is truncated");
        }
    }
}
