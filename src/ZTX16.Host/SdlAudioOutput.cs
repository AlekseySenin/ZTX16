using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SDL;
using ZTX16.Platform.Audio;
using static SDL.SDL3;

namespace ZTX16.Host;

/// <summary>
/// Connects the ZTX16 Audio Engine to the default SDL3 playback device. SDL pulls
/// samples through a callback on its audio thread; synthesis stays in the engine.
/// </summary>
internal sealed unsafe class SdlAudioOutput : IDisposable
{
    private readonly AudioEngine _engine;
    private readonly GCHandle _self;
    private SDL_AudioStream* _stream;
    private float[] _buffer = new float[4096];

    public SdlAudioOutput(AudioEngine engine)
    {
        _engine = engine;
        _self = GCHandle.Alloc(this);

        var spec = new SDL_AudioSpec
        {
            format = SDL_AUDIO_F32,
            channels = AudioEngine.OutputChannels,
            freq = engine.SampleRate,
        };

        _stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &OnAudioRequested, GCHandle.ToIntPtr(_self));
        if (_stream == null)
        {
            // Audio is optional for the host to keep running; report and continue silently.
            Console.Error.WriteLine($"ZTX16: audio device unavailable: {SDL_GetError()}");
            return;
        }

        SDL_ResumeAudioStreamDevice(_stream);
    }

    public void Dispose()
    {
        if (_stream != null)
        {
            SDL_DestroyAudioStream(_stream);
            _stream = null;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAudioRequested(IntPtr userdata, SDL_AudioStream* stream, int additionalAmount, int totalAmount)
    {
        if (additionalAmount <= 0 || GCHandle.FromIntPtr(userdata).Target is not SdlAudioOutput output)
        {
            return;
        }

        output.Fill(stream, additionalAmount);
    }

    private void Fill(SDL_AudioStream* stream, int bytes)
    {
        int floats = bytes / sizeof(float);
        if (_buffer.Length < floats)
        {
            _buffer = new float[floats];
        }

        Span<float> samples = _buffer.AsSpan(0, floats);
        _engine.Render(samples);

        fixed (float* data = samples)
        {
            SDL_PutAudioStreamData(stream, (IntPtr)data, floats * sizeof(float));
        }
    }
}
