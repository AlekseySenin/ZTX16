using System.Buffers.Binary;
using ZTX16.Cartridge;
using ZTX16.Platform.Audio;
using ZTX16.Platform.Execution;
using ZTX16.Platform.Runtime;
using ZTX16.Toolchain;
using ZTX16.VM;

namespace ZTX16.Tests;

public class AudioEngineTests
{
    private const int Rate = 48000;

    private static AudioEngine Engine() => new(Rate);

    // Renders 'seconds' of audio; returns the interleaved buffer.
    private static float[] Render(AudioEngine engine, double seconds)
    {
        var buffer = new float[(int)(seconds * Rate) * 2];
        for (int offset = 0; offset < buffer.Length; offset += 1024)
        {
            engine.Render(buffer.AsSpan(offset, Math.Min(1024, buffer.Length - offset)));
        }

        return buffer;
    }

    private static float[] Left(float[] interleaved) => interleaved.Where((_, i) => i % 2 == 0).ToArray();

    private static float[] Right(float[] interleaved) => interleaved.Where((_, i) => i % 2 == 1).ToArray();

    private static double Rms(float[] samples) => Math.Sqrt(samples.Average(s => (double)s * s));

    private static int RisingCrossings(float[] samples)
    {
        int count = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] <= 0 && samples[i] > 0)
            {
                count++;
            }
        }

        return count;
    }

    private static void Tone(AudioEngine engine, int channel, int waveform, float hz, float volume = 0.5f)
    {
        engine.SetWaveform(channel, waveform);
        engine.SetFrequency(channel, hz);
        engine.SetVolume(channel, volume);
        engine.NoteOn(channel);
        engine.Update();
    }

    [Fact]
    public void IdleHardwareIsSilent()
    {
        Assert.All(Render(Engine(), 0.05), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void ChangesTakeEffectAtTheAudioUpdate()
    {
        AudioEngine engine = Engine();
        engine.SetFrequency(0, 440);
        engine.NoteOn(0);
        Assert.All(Render(engine, 0.02), s => Assert.Equal(0f, s));
        Assert.Equal(0, engine.GetChannelState(0));

        engine.Update();
        Assert.True(Rms(Render(engine, 0.02)) > 0.1);
        Assert.NotEqual(0, engine.GetChannelState(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OscillatorsRunAtTheirFrequency(int waveform)
    {
        AudioEngine engine = Engine();
        Tone(engine, 0, waveform, 1000);
        float[] output = Render(engine, 0.2);
        Assert.InRange(RisingCrossings(Left(output)), 198, 202);
        Assert.Equal(Left(output), Right(output));     // centered
    }

    [Fact]
    public void NoiseIsDeterministic()
    {
        AudioEngine a = Engine(), b = Engine();
        Tone(a, 3, 5, 8000);
        Tone(b, 3, 5, 8000);
        float[] x = Render(a, 0.05), y = Render(b, 0.05);
        Assert.Equal(x, y);
        Assert.True(Rms(x) > 0.05);
    }

    [Fact]
    public void PanAndMixerAndMute()
    {
        AudioEngine engine = Engine();
        engine.SetPan(0, -1);
        Tone(engine, 0, 4, 440);
        float[] output = Render(engine, 0.05);
        Assert.True(Rms(Left(output)) > 0.3);
        Assert.True(Rms(Right(output)) < 1e-6);

        engine.SetMuted(0, true);
        engine.Update();
        Assert.True(Rms(Render(engine, 0.02)) < 1e-6);
        Assert.Equal(3, engine.GetChannelState(0));    // still sounding (Sustain), only muted

        engine.SetMuted(0, false);
        engine.SetMixerVolume(0, 0);
        engine.Update();
        Assert.True(Rms(Render(engine, 0.02)) < 1e-6);
    }

    [Fact]
    public void AdsrStagesAndRelease()
    {
        AudioEngine engine = Engine();
        engine.SetAdsr(0, 0.01f, 0.01f, 0.5f, 0.02f);
        Tone(engine, 0, 4, 440);
        Render(engine, 0.005);
        Assert.Equal(1, engine.GetChannelState(0));                 // Attack
        Render(engine, 0.01);
        Assert.Equal(2, engine.GetChannelState(0));                 // Decay
        Render(engine, 0.02);
        Assert.Equal(3, engine.GetChannelState(0));                 // Sustain
        Assert.Equal(0.5f, engine.GetAdsrValue(0), 3);
        Assert.Equal(0.25f, engine.GetEffectiveVolume(0), 3);       // 0.5 base × 0.5 envelope

        engine.NoteOff(0);
        engine.Update();
        Render(engine, 0.01);
        Assert.Equal(4, engine.GetChannelState(0));                 // Release
        Render(engine, 0.02);
        Assert.Equal(0, engine.GetChannelState(0));                 // Idle after release
        Assert.True(Rms(Render(engine, 0.01)) < 1e-6);
    }

    [Fact]
    public void StopSilencesWithoutRelease()
    {
        AudioEngine engine = Engine();
        engine.SetAdsr(0, 0, 0, 1, 5);
        Tone(engine, 0, 4, 440);
        Render(engine, 0.01);
        engine.Stop(0);
        engine.Update();
        Assert.Equal(0, engine.GetChannelState(0));
        Assert.True(Rms(Render(engine, 0.01)) < 1e-6);
    }

    [Fact]
    public void AdsrCanModulatePitchInstead()
    {
        AudioEngine engine = Engine();
        engine.SetAdsr(0, 0, 0, 1, 0);
        engine.RouteAdsr(0, 0, 12);
        Tone(engine, 0, 0, 440);
        Render(engine, 0.01);
        Assert.Equal(880f, engine.GetEffectivePitch(0), 1);
        Assert.Equal(0.5f, engine.GetEffectiveVolume(0), 3);       // no longer an envelope on volume
    }

    [Fact]
    public void LfoModulatesPitchAndRunsContinuously()
    {
        AudioEngine engine = Engine();
        engine.SetLfo(0, 3, 10);            // square, 10 Hz
        engine.RouteLfo(0, 0, 0, 12);       // ±1 octave
        Tone(engine, 0, 0, 440);
        var pitches = new HashSet<int>();
        for (int i = 0; i < 20; i++)
        {
            Render(engine, 0.01);
            pitches.Add((int)Math.Round(engine.GetEffectivePitch(0)));
            Assert.InRange(engine.GetLfoValue(0), -1f, 1f);
        }

        Assert.Equal(new HashSet<int> { 220, 880 }, pitches);
    }

    [Fact]
    public void SampleOneShotEndsAndLoopContinues()
    {
        AudioEngine engine = Engine();
        sbyte[] pcm = Enumerable.Range(0, 4410).Select(i => (sbyte)(i % 100 < 50 ? 100 : -100)).ToArray(); // 0.1 s
        engine.UseSample(6, pcm);
        engine.NoteOn(6);
        engine.Update();
        Render(engine, 0.05);
        Assert.NotEqual(0, engine.GetChannelState(6));
        Render(engine, 0.06);
        Assert.Equal(0, engine.GetChannelState(6));                 // reached the physical end

        engine.SetSampleLoop(6, true, 1000, 2000);
        engine.SetPlaybackRate(6, 2);
        engine.NoteOn(6);
        engine.Update();
        Render(engine, 0.5);
        Assert.NotEqual(0, engine.GetChannelState(6));
        Assert.Equal(2f, engine.GetEffectivePitch(6));
    }

    [Fact]
    public void WavetablePlaysCyclically()
    {
        AudioEngine engine = Engine();
        sbyte[] table = Enumerable.Range(0, 64).Select(i => (sbyte)(i < 32 ? 100 : -100)).ToArray();
        engine.UseWavetable(7, table);
        engine.SetFrequency(7, 500);
        engine.NoteOn(7);
        engine.Update();
        Assert.InRange(RisingCrossings(Left(Render(engine, 0.2))), 99, 101);
    }

    [Fact]
    public void FiltersShapeTheSpectrum()
    {
        AudioEngine engine = Engine();
        Tone(engine, 0, 4, 5000);
        double dry = Rms(Render(engine, 0.1));

        engine.SetFilter(0, 0, 200, 0);
        engine.AssignProcessor(0, 0);       // insert on channel 0
        engine.Update();
        Render(engine, 0.02);
        double lowPassed = Rms(Render(engine, 0.1));
        Assert.True(lowPassed < dry * 0.2, $"{lowPassed} vs {dry}");

        engine.SetFilter(0, 1, 200, 0);     // high-pass lets 5 kHz through
        engine.Update();
        Render(engine, 0.02);
        Assert.True(Rms(Render(engine, 0.1)) > dry * 0.8);

        engine.AssignProcessor(0, AudioHardware.Unassigned);
        engine.Update();
        Assert.Equal(dry, Rms(Render(engine, 0.1)), 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EffectsLeaveATailOrColour(int type)
    {
        AudioEngine engine = Engine();
        engine.SetEffect(0, type, type == 1 ? 100 : 0.8f, type == 1 ? 0.5f : 0.8f, 0.5f);
        engine.AssignProcessor(2, AudioHardware.Global);
        engine.SetAdsr(0, 0, 0, 1, 0);
        Tone(engine, 0, 4, 440);
        float[] during = Render(engine, 0.05);
        Assert.True(Rms(during) > 0.01);
        engine.Stop(0);
        engine.Update();
        float[] after = Render(engine, 0.1);
        if (type == 0)
        {
            Assert.True(Rms(after) < 0.05);      // chorus: only a few ms of delay
        }
        else
        {
            Assert.True(Rms(after) > 0.005, $"type {type}: {Rms(after)}");    // delay echo / reverb tail
        }
    }

    [Fact]
    public void LimiterKeepsTheOutputInRange()
    {
        AudioEngine engine = Engine();
        for (int c = 0; c < 6; c++)
        {
            Tone(engine, c, 4, 100, 1f);
        }

        float[] output = Render(engine, 0.1);
        Assert.All(output, s => Assert.InRange(s, -1f, 1f));
        Assert.True(Rms(output) > 0.5);
    }

    [Fact]
    public void InvalidCallsAreRejected()
    {
        AudioEngine engine = Engine();
        Assert.Throws<ArgumentException>(() => engine.SetWaveform(6, 0));
        Assert.Throws<ArgumentException>(() => engine.UseSample(0, [1, 2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetVolume(8, 1));
        Assert.Throws<ArgumentException>(() => engine.SetFrequency(0, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetSampleLoop(6, true, 100, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.RouteLfoToProcessor(0, 0, 2, 1));     // filters have 2 parameters
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.AssignProcessor(0, 9));
        engine.SetFrequency(0, 50000);      // out-of-range values are clamped
        engine.NoteOn(0);
        engine.Update();
        Render(engine, 0.001);
        Assert.Equal(20000f, engine.GetEffectivePitch(0));
    }
}

public class AudioFormatTests
{
    private static byte[] Wav(int channels, int rate, int bits, short[] interleaved)
    {
        int bytes = bits / 8;
        var data = new byte[44 + (interleaved.Length * bytes)];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
        "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), rate * channels * bytes);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32), (short)(channels * bytes));
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34), (short)bits);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), interleaved.Length * bytes);
        for (int i = 0; i < interleaved.Length; i++)
        {
            if (bits == 16)
            {
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(44 + (i * 2)), interleaved[i]);
            }
            else
            {
                data[44 + i] = (byte)(interleaved[i] + 128);
            }
        }

        return data;
    }

    [Fact]
    public void StereoSixteenBitIsDownmixedAndResampled()
    {
        // 22050 Hz stereo: left +16384, right 0 → mono 8192/32768 = 0.25 → 32.
        short[] frames = Enumerable.Range(0, 2205 * 2).Select(i => (short)(i % 2 == 0 ? 16384 : 0)).ToArray();
        sbyte[] pcm = SampleFormat.FromWav(Wav(2, 22050, 16, frames), "x.wav");
        Assert.Equal(4410, pcm.Length);
        Assert.All(pcm, s => Assert.Equal(32, s));
    }

    [Fact]
    public void EightBitRoundTripsThroughWav()
    {
        sbyte[] pcm = Enumerable.Range(0, 1001).Select(i => (sbyte)((i % 255) - 127)).ToArray();
        Assert.Equal(pcm, SampleFormat.FromWav(SampleFormat.ToWav(pcm), "round.wav"));
        Assert.Equal(pcm, SampleFormat.Decode(SampleFormat.Encode(pcm), "s"));
    }

    [Fact]
    public void BadWavIsRejected()
    {
        Assert.Throws<ProjectException>(() => SampleFormat.FromWav("RIFF0000WAVX"u8.ToArray(), "bad.wav"));
        byte[] wav = Wav(1, 44100, 16, [1, 2, 3]);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 2);    // ADPCM
        Assert.Throws<ProjectException>(() => SampleFormat.FromWav(wav, "adpcm.wav"));
    }

    [Fact]
    public void WavetableJson()
    {
        sbyte[] table = Enumerable.Range(0, 64).Select(i => (sbyte)(i * 4 - 128)).ToArray();
        string json = WavetableFormat.FormatJson(table);
        Assert.Equal(table, WavetableFormat.ParseJson(json, "t.wavetable"));
        Assert.Equal(table, WavetableFormat.Decode(WavetableFormat.Encode(table), "t"));
        var e = Assert.Throws<ProjectException>(() => WavetableFormat.ParseJson("""{ "samples": [1, 2] }""", "short.wavetable"));
        Assert.Contains("exactly 64", e.Message);
    }
}

public class AudioCartridgeTests
{
    private static RuntimeSession Start(string initialize, string tick = "")
    {
        using var dir = new TempDirectory();
        dir.Write(ProjectConfig.FileName, """{ "name": "Snd", "gameId": "dev.test.audio", "directories": { "code": "code", "samples": "samples", "wavetables": "wavetables" } }""");
        sbyte[] pcm = Enumerable.Range(0, 2000).Select(i => (sbyte)(i % 40 < 20 ? 90 : -90)).ToArray();
        File.WriteAllBytes(Path.Combine(dir.Path, "samples", "beep.wav").EnsureDirectory(), SampleFormat.ToWav(pcm));
        dir.Write("wavetables/soft.wavetable", WavetableFormat.FormatJson(Enumerable.Range(0, 64).Select(i => (sbyte)(i < 32 ? 60 : -60)).ToArray()));
        dir.Write("code/Game.cs", $$"""
            using ZTX16;
            public sealed class Game : ICartridge
            {
                public void Initialize()
                {
                    {{initialize}}
                }

                public void Tick()
                {
                    {{tick}}
                }

                public void Render() { }
            }
            """);

        CartridgeContent cartridge = ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory);
        var session = new RuntimeSession(VmProgram.Load(cartridge.Executable), 48000, new ResourceStore(cartridge.Resources));
        session.Initialize();
        return session;
    }

    [Fact]
    public void CartridgeDrivesTheHardware()
    {
        RuntimeSession session = Start("""
            Audio.SetWaveform(0, Waveform.Square);
            Audio.SetFrequency(0, 330f);
            Audio.SetAdsr(0, 0f, 0f, 1f, 0.1f);
            Audio.NoteOn(0);
            Audio.UseSample(6, "beep");
            Audio.SetSampleLoop(6, true, 0, Audio.SampleLength("beep"));
            Audio.NoteOn(6);
            Audio.UseWavetable(7, "soft");
            Audio.SetFrequency(7, 220f);
            Audio.NoteOn(7);
            Audio.SetEffect(0, EffectType.Reverb, 0.5f, 0.5f, 0.3f);
            Audio.AssignProcessor(ProcessorUnit.Fx1, Audio.Global);
            Audio.SetLfo(0, LfoWaveform.Triangle, 5f);
            Audio.RouteLfo(0, 7, ModulationDestination.Pitch, 0.5f);
            """, """
            if (Machine.TickCount == 3) { Audio.NoteOff(0); }
            Graphics.SetPixel(0, 0, 0, (int)Audio.GetChannelState(0));
            Graphics.SetPixel(0, 1, 0, Audio.SampleLength("beep") == 2000 ? 1 : 0);
            """);

        AudioEngine audio = session.Audio;
        Assert.Equal(0, audio.GetChannelState(0));      // Initialize's changes wait for the first Audio Update
        session.RunTick(TimeSpan.Zero);
        var buffer = new float[1600];
        audio.Render(buffer);
        Assert.Equal(3, audio.GetChannelState(0));
        Assert.NotEqual(0, audio.GetChannelState(6));
        Assert.NotEqual(0, audio.GetChannelState(7));
        Assert.Contains(buffer, s => s != 0);

        for (int i = 0; i < 4; i++)
        {
            session.RunTick(TimeSpan.Zero);
            audio.Render(buffer);
        }

        Assert.Equal(4, audio.GetChannelState(0));      // released in tick 3
        Assert.Equal(1, session.Graphics.Layers[0].GetPixel(1, 0));
        Assert.Equal(4, session.Graphics.Layers[0].GetPixel(0, 0));   // the cartridge reads its state back
    }

    [Fact]
    public void InvalidAudioCallTrapsTheCartridge()
    {
        var e = Assert.Throws<VmTrapException>(() => Start("Audio.SetWaveform(7, Waveform.Sine);"));
        Assert.Contains("Synth channels", e.Message);
    }
}
