using ZTX16.Cartridge;
using ZTX16.Platform.Execution;
using ZTX16.Platform.Runtime;
using ZTX16.Toolchain;
using ZTX16.VM;

namespace ZTX16.Tests;

public class MusicFormatTests
{
    [Theory]
    [InlineData("\"C4\"", 60)]
    [InlineData("\"A4\"", 69)]
    [InlineData("\"F#3\"", 54)]
    [InlineData("\"Bb2\"", 46)]
    [InlineData("\"C-1\"", 0)]
    [InlineData("100", 100)]
    public void NoteNamesParse(string json, int note)
    {
        Assert.Equal(note, NoteNames.Parse(System.Text.Json.Nodes.JsonNode.Parse(json), "x"));
    }

    [Fact]
    public void InstrumentRoundTrips()
    {
        InstrumentDefinition i = InstrumentFormat.ParseJson("""
            { "sample": "kick", "volume": 0.8, "adsr": [0, 0.1, 0.5, 0.2], "adsrTarget": "pitch", "adsrAmount": -12,
              "lfo1": { "pitch": 0.5 }, "loop": [100, 2000], "rate": 1.5, "baseNote": 48 }
            """, "kick.instrument");
        Assert.Equal(InstrumentSourceKind.Sample, i.Source);
        Assert.Equal((0, -12f, 0.5f, true, 2000, 48), (i.AdsrTarget, i.AdsrAmount, i.Lfo1Pitch, i.Loop, i.LoopEnd, i.BaseNote));
        Assert.Equal(i, InstrumentFormat.ParseJson(InstrumentFormat.FormatJson(i), "again.instrument"));
        Assert.Equal(i, InstrumentFormat.Decode(InstrumentFormat.Encode(i), "kick"));
    }

    [Fact]
    public void SequenceRoundTrips()
    {
        SequenceDefinition s = SequenceFormat.ParseJson("""
            { "instrument": "lead", "ticksPerBeat": 8, "bpm": 140, "loop": true,
              "events": [ ["C4", 2], ["E4", 4, 1], ["rest", 2], { "volume": 0.5 }, { "note": 67, "length": 3, "gate": 2 } ] }
            """, "tune.sequence");
        Assert.Equal(5, s.Events.Length);
        Assert.Equal(new SequenceEvent(0, 64, 4, 1, 0f), s.Events[1]);
        Assert.Equal(new SequenceEvent(2, 0, 0, 0, 0.5f), s.Events[3]);
        string json = SequenceFormat.FormatJson(s);
        Assert.Equal(json, SequenceFormat.FormatJson(SequenceFormat.ParseJson(json, "again.sequence")));
        Assert.Equal(json, SequenceFormat.FormatJson(SequenceFormat.Decode(SequenceFormat.Encode(s), "tune")));
    }

    [Fact]
    public void SongRoundTripsAndSortsEvents()
    {
        SongDefinition s = SongFormat.ParseJson("""
            { "ticksPerBeat": 4, "bpm": 100, "length": 64, "loop": true, "events": [
                { "at": 16, "channel": 0, "sequence": null },
                { "at": 0, "channel": 0, "sequence": "bass" },
                { "at": 0, "lfo": 0, "waveform": "square", "hz": 4 },
                { "at": 0, "lfo": 0, "channel": 2, "target": "pitch", "amount": 0.3 },
                { "at": 0, "lfo": 1, "unit": "filter2", "parameter": 0, "amount": 12 },
                { "at": 0, "filter": 1, "mode": "bandpass", "cutoff": 900, "resonance": 0.4, "assign": 3 },
                { "at": 8, "effect": 0, "type": "delay", "parameters": [200, 0.4], "mix": 0.25, "assign": "global" } ] }
            """, "tune.song");
        Assert.Equal([0, 0, 0, 0, 0, 8, 16], s.Events.Select(e => e.At));
        Assert.Equal("bass", s.Events[0].Name);
        Assert.Null(s.Events[6].Name);
        Assert.Equal(8, s.Events[5].Assign);
        string json = SongFormat.FormatJson(s);
        Assert.Equal(json, SongFormat.FormatJson(SongFormat.ParseJson(json, "again.song")));
        Assert.Equal(json, SongFormat.FormatJson(SongFormat.Decode(SongFormat.Encode(s), "tune")));
    }

    [Theory]
    [InlineData("instrument", """{ "waveform": "square", "sample": "x" }""", "exactly one")]
    [InlineData("instrument", """{ "waveform": "pulse" }""", "must be one of")]
    [InlineData("instrument", """{ "waveform": "saw", "loop": [0, 10] }""", "only for sample")]
    [InlineData("instrument", """{ "waveform": "saw", "volume": 2 }""", "out of range")]
    [InlineData("sequence", """{ "instrument": "x", "events": [] }""", "non-empty")]
    [InlineData("sequence", """{ "instrument": "x", "events": [ ["C4", 2, 3] ] }""", "gate 1..length")]
    [InlineData("sequence", """{ "instrument": "x", "events": [ ["H4", 2] ] }""", "note name")]
    [InlineData("song", """{ "length": 8, "events": [ { "at": 8, "channel": 0, "sequence": "a" } ] }""", "0..length-1")]
    [InlineData("song", """{ "length": 8, "events": [ { "at": 0, "filter": 0, "assign": 12 } ] }""", "invalid")]
    [InlineData("song", """{ "length": 8, "events": [ { "at": 0, "bogus": 1 } ] }""", "expected a sequence")]
    public void InvalidResourcesAreRejected(string kind, string json, string expected)
    {
        var e = Assert.Throws<ProjectException>(() => kind switch
        {
            "instrument" => (object)InstrumentFormat.ParseJson(json, "bad"),
            "sequence" => SequenceFormat.ParseJson(json, "bad"),
            _ => SongFormat.ParseJson(json, "bad"),
        });
        Assert.Contains(expected, e.Message);
    }
}

public class MusicRuntimeTests
{
    private static TempDirectory Project(string initialize, string tick, string fields = "")
    {
        var dir = new TempDirectory();
        dir.Write(ProjectConfig.FileName, """
            { "name": "Music", "gameId": "dev.test.music", "directories": { "code": "code", "samples": "samples", "wavetables": "wavetables",
              "instruments": "instruments", "sequences": "sequences", "songs": "songs" } }
            """);
        File.WriteAllBytes(Path.Combine(dir.Path, "samples", "hit.wav").EnsureDirectory(),
            SampleFormat.ToWav(Enumerable.Range(0, 3000).Select(i => (sbyte)(i % 20 < 10 ? 80 : -80)).ToArray()));
        dir.Write("wavetables/wt.wavetable", WavetableFormat.FormatJson(Enumerable.Range(0, 64).Select(i => (sbyte)(i < 32 ? 50 : -50)).ToArray()));
        dir.Write("instruments/lead.instrument", """{ "waveform": "square", "volume": 0.5, "adsr": [0, 0, 1, 0.02] }""");
        dir.Write("instruments/soft.instrument", """{ "waveform": "sine", "volume": 0.2, "adsr": [0, 0, 1, 0.02] }""");
        dir.Write("instruments/pad.instrument", """{ "wavetable": "wt", "volume": 0.4 }""");
        dir.Write("instruments/drum.instrument", """{ "sample": "hit", "baseNote": 60 }""");
        dir.Write("sequences/melody.sequence", """{ "instrument": "lead", "ticksPerBeat": 4, "bpm": 900, "events": [ ["C4", 2, 1], ["rest", 1], ["E4", 2] ] }""");
        dir.Write("sequences/bass.sequence", """{ "instrument": "lead", "ticksPerBeat": 4, "bpm": 900, "loop": true, "events": [ ["A2", 2, 1] ] }""");
        dir.Write("songs/tune.song", """
            { "ticksPerBeat": 4, "bpm": 900, "length": 16, "events": [
                { "at": 0, "channel": 0, "sequence": "bass" },
                { "at": 0, "lfo": 0, "waveform": "square", "hz": 2 },
                { "at": 0, "filter": 0, "mode": "lowpass", "cutoff": 2000, "assign": 0 },
                { "at": 0, "effect": 1, "type": "delay", "parameters": [50, 0.3], "mix": 0.2, "assign": "global" },
                { "at": 12, "channel": 0, "sequence": null } ] }
            """);
        dir.Write("code/Game.cs", $$"""
            using ZTX16;
            public sealed class Game : ICartridge
            {
                {{fields}}

                public void Initialize()
                {
                    {{initialize}}
                }

                public void Tick()
                {
                    long t = Machine.TickCount;
                    {{tick}}
                }

                public void Render() { }
            }
            """);
        return dir;
    }

    private static RuntimeSession Start(string initialize, string tick = "", string fields = "")
    {
        using TempDirectory dir = Project(initialize, tick, fields);
        CartridgeContent cartridge = ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory);
        var session = new RuntimeSession(VmProgram.Load(cartridge.Executable), 48000, new ResourceStore(cartridge.Resources));
        session.Initialize();
        return session;
    }

    // One runtime tick plus the audio rendered during it (1/60 s at 48 kHz).
    private static void Step(RuntimeSession session)
    {
        session.RunTick(TimeSpan.Zero);
        session.Audio.Render(new float[1600]);
    }

    private static int[] Row(RuntimeSession session, int layer, int y, int count) =>
        Enumerable.Range(0, count).Select(x => session.Graphics.Layers[layer].GetPixel(x, y)).ToArray();

    [Fact]
    public void AllocatorTakesFreeChannelsThenStealsRespectingTheLock()
    {
        RuntimeSession session = Start("""
            Instrument lead = Instrument.Load("lead");
            Voice[] voices = new Voice[6];
            for (int i = 0; i < 6; i++)
            {
                voices[i] = new Voice(lead);
                voices[i].Play(60 + i);
                Graphics.SetPixel(0, i, 0, voices[i].Channel + 1);
            }

            voices[0].StealLock = true;
            Voice extra = new Voice(lead);
            Graphics.SetPixel(0, 6, 0, extra.Play(72) ? 1 : 0);
            Graphics.SetPixel(0, 7, 0, extra.Channel + 1);            // ByOrder skips the locked channel 0
            Graphics.SetPixel(0, 8, 0, voices[1].Channel + 1);        // stolen: no channel any more
            Graphics.SetPixel(0, 9, 0, voices[1].IsPlaying ? 1 : 0);

            for (int i = 0; i < 6; i++) { voices[i].StealLock = true; }
            extra.StealLock = true;
            Voice late = new Voice(lead);
            Graphics.SetPixel(0, 10, 0, late.Play(50) ? 1 : 0);       // every candidate locked: refused
            Graphics.SetPixel(0, 11, 0, late.Channel + 1);

            Voice pad = new Voice(Instrument.Load("pad"));
            Graphics.SetPixel(0, 12, 0, pad.Play(57) ? 1 : 0);        // its own channel kind is still free
            Graphics.SetPixel(0, 13, 0, pad.Channel + 1);
            Voice drum = new Voice(Instrument.Load("drum"));
            drum.Play(72);
            Graphics.SetPixel(0, 14, 0, drum.Channel + 1);
            """);

        Assert.Equal([1, 2, 3, 4, 5, 6, 1, 2, 0, 0, 0, 0, 1, 7, 8], Row(session, 0, 0, 15));
        session.RunTick(TimeSpan.Zero);
        Assert.Equal(1, session.Audio.GetChannelState(7));            // drum sample started (attack)
        session.Audio.Render(new float[64]);
        Assert.Equal(2f, session.Audio.GetEffectivePitch(7), 3);       // an octave above its base note
    }

    [Fact]
    public void QuietestAndOldestPolicies()
    {
        RuntimeSession session = Start("", """
            if (t == 0)
            {
                Instrument lead = Instrument.Load("lead");
                float[] volumes = new float[] { 0.9f, 0.8f, 0.1f, 0.7f, 0.6f, 0.5f };
                for (int i = 0; i < 6; i++)
                {
                    voices[i] = new Voice(lead);
                    voices[i].SetVolume(volumes[i]);
                    voices[i].Play(60);
                }
            }
            else if (t == 1)
            {
                Voice quiet = new Voice(Instrument.Load("lead"), ChannelGroup.Synth(StealPolicy.Quietest));
                quiet.Play(70);
                Graphics.SetPixel(0, 0, 0, quiet.Channel + 1);           // channel 2 had volume 0.1
            }
            else if (t == 2)
            {
                voices[0].Play(61);                                      // channel 0 restarts: no longer oldest
                Voice old = new Voice(Instrument.Load("lead"), ChannelGroup.Synth(StealPolicy.Oldest));
                old.Play(70);
                Graphics.SetPixel(0, 1, 0, old.Channel + 1);             // channel 1 started first
            }
            """, "private readonly Voice[] voices = new Voice[6];");

        for (int i = 0; i < 3; i++)
        {
            Step(session);
        }

        Assert.Equal([3, 2], Row(session, 0, 0, 2));
    }

    [Fact]
    public void SequencePlaybackFollowsSequenceTicks()
    {
        RuntimeSession session = Start("""
            playback = new SequencePlayback(Sequence.Load("melody"));
            playback.Play();
            """, """
            playback.Update();
            if (playback.Finished) { Graphics.SetPixel(0, (int)t, 1, 1); }
            Graphics.SetPixel(0, (int)t, 0, playback.Voice.Channel + 1);
            """, "private SequencePlayback playback = null!;");

        var states = new List<int>();
        var pitches = new List<float>();
        for (int i = 0; i < 8; i++)
        {
            Step(session);
            states.Add(session.Audio.GetChannelState(0));
            pitches.Add(session.Audio.GetEffectivePitch(0));
        }

        // TimeScale = 900 bpm × 4 / 3600 = 1 Sequence Tick per runtime tick.
        // C4 at 0 (gate 1) → released at 1; rest 2..3; E4 at 3, released at 5; then finished.
        Assert.Equal(3, states[0]);
        Assert.Equal(261.6f, pitches[0], 0);
        Assert.Equal(4, states[1]);
        Assert.Equal(0, states[2]);
        Assert.Equal(3, states[3]);
        Assert.Equal(329.6f, pitches[3], 0);
        Assert.Equal(3, states[4]);
        Assert.Equal(4, states[5]);
        Assert.Equal([0, 0, 0, 0, 0, 1, 0, 0], Row(session, 0, 1, 8));
        Assert.Equal(1, session.Graphics.Layers[0].GetPixel(0, 0));
    }

    [Fact]
    public void SongIsHardBoundAndTakesStolenChannelsBack()
    {
        RuntimeSession session = Start("""
            song = new SongPlayback(Song.Load("tune"));
            song.Play();
            sfx = new Voice(Instrument.Load("soft"), new ChannelGroup(new int[] { 0 }));
            """, """
            if (t == 4) { sfx.Play(96); }
            if (t == 5) { sfx.ReleaseNote(); }
            song.Update();
            Graphics.SetPixel(0, (int)t, 0, song.Controls(0) ? 1 : 0);
            if (song.Finished) { Graphics.SetPixel(0, (int)t, 1, 1); }
            """, "private SongPlayback song = null!; private Voice sfx = null!;");

        var pitches = new List<float>();
        var states = new List<int>();
        for (int i = 0; i < 16; i++)
        {
            Step(session);
            pitches.Add(session.Audio.GetEffectivePitch(0));
            states.Add(session.Audio.GetChannelState(0));
        }

        // Owned from tick 0; stolen at 4; the SFX releases at 5 and is silent after tick 6's audio;
        // the song takes channel 0 back at 7 and lets it go when its sequence is cleared at 12.
        Assert.Equal([1, 1, 1, 1, 0, 0, 0, 1, 1, 1, 1, 1, 0, 0, 0, 0], Row(session, 0, 0, 16));
        Assert.Equal(110f, pitches[0], 0);              // A2 from the bass sequence
        Assert.Equal(2093f, pitches[4], 0);             // C7 from the sound effect
        Assert.Equal(110f, pitches[8], 0);              // the song plays again with its own instrument
        Assert.Equal(3, states[8]);
        Assert.Equal(1, session.Graphics.Layers[0].GetPixel(15, 1));    // finished at the end (length 16)
        Assert.Equal(1f, Math.Abs(session.Audio.GetLfoValue(0)));       // the song set LFO 1 to a square
    }

    [Fact]
    public void BuildChecksMusicReferences()
    {
        using (TempDirectory dir = Project("", ""))
        {
            dir.Write("sequences/broken.sequence", """{ "instrument": "ghost", "events": [ ["C4", 1] ] }""");
            var e = Assert.Throws<ProjectException>(() => ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory));
            Assert.Contains("'ghost'", e.Message);
        }

        using (TempDirectory dir = Project("", ""))
        {
            dir.Write("songs/wrong.song", """{ "length": 4, "events": [ { "at": 0, "channel": 6, "sequence": "bass" } ] }""");
            var e = Assert.Throws<ProjectException>(() => ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory));
            Assert.Contains("cannot play on channel 6", e.Message);
        }

        using (TempDirectory dir = Project("", ""))
        {
            dir.Write("instruments/nosample.instrument", """{ "sample": "missing" }""");
            var e = Assert.Throws<ProjectException>(() => ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory));
            Assert.Contains("'missing'", e.Message);
        }
    }
}
