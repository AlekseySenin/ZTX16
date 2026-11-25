using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace ZTX16.Cartridge;

/// <summary>
/// Sample resources: mono 8-bit signed PCM at 44.1 kHz. Project side: a <c>.wav</c> file (PCM 8/16/24/32-bit
/// or 32-bit float, any channel count and sample rate); the build downmixes, resamples linearly to 44.1 kHz
/// and quantizes to 8 bits. Cartridge side: i32 frame count, then one signed byte per frame.
/// </summary>
public static class SampleFormat
{
    /// <summary>The platform sample rate.</summary>
    public const int SampleRate = 44100;

    /// <summary>Longest sample in frames (60 seconds).</summary>
    public const int MaxFrames = SampleRate * 60;

    /// <summary>Converts a WAV file to platform PCM.</summary>
    /// <exception cref="ProjectException">The file is not a supported WAV.</exception>
    public static sbyte[] FromWav(byte[] wav, string file)
    {
        ReadOnlySpan<byte> d = wav;
        if (d.Length < 12 || Encoding.ASCII.GetString(d[..4]) != "RIFF" || Encoding.ASCII.GetString(d.Slice(8, 4)) != "WAVE")
        {
            throw new ProjectException($"{file}: not a RIFF/WAVE file.");
        }

        int format = 0, channels = 0, rate = 0, bits = 0;
        ReadOnlySpan<byte> data = default;
        bool haveData = false;
        int p = 12;
        while (p + 8 <= d.Length)
        {
            string id = Encoding.ASCII.GetString(d.Slice(p, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p + 4, 4));
            if (size < 0 || p + 8 + size > d.Length)
            {
                size = d.Length - p - 8;
            }

            ReadOnlySpan<byte> body = d.Slice(p + 8, size);
            if (id == "fmt " && size >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == 0xFFFE && size >= 26)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]); // WAVE_FORMAT_EXTENSIBLE subformat
                }
            }
            else if (id == "data")
            {
                data = body;
                haveData = true;
            }

            p += 8 + size + (size & 1);
        }

        bool pcm = format == 1 && bits is 8 or 16 or 24 or 32;
        bool ieee = format == 3 && bits == 32;
        if (!haveData || channels < 1 || rate < 1000 || rate > 384000 || !(pcm || ieee))
        {
            throw new ProjectException($"{file}: unsupported WAV (need PCM 8/16/24/32-bit or 32-bit float, with fmt and data chunks).");
        }

        int frameBytes = channels * (bits / 8);
        int frames = data.Length / frameBytes;
        var mono = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                ReadOnlySpan<byte> s = data.Slice((f * frameBytes) + (c * (bits / 8)), bits / 8);
                sum += ieee ? BinaryPrimitives.ReadSingleLittleEndian(s)
                    : bits switch
                    {
                        8 => (s[0] - 128) / 127.0,
                        16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768.0,
                        24 => ((s[0] | (s[1] << 8) | ((sbyte)s[2] << 16))) / 8388608.0,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648.0,
                    };
            }

            mono[f] = sum / channels;
        }

        long outFrames = rate == SampleRate ? frames : (long)Math.Floor((double)frames * SampleRate / rate);
        if (outFrames < 1 || outFrames > MaxFrames)
        {
            throw new ProjectException($"{file}: a sample must be 1..{MaxFrames} frames ({MaxFrames / SampleRate} s) long.");
        }

        var result = new sbyte[outFrames];
        for (long i = 0; i < outFrames; i++)
        {
            double value;
            if (rate == SampleRate)
            {
                value = mono[i];
            }
            else
            {
                double position = (double)i * rate / SampleRate;
                int a = (int)position;
                int b = Math.Min(a + 1, frames - 1);
                value = mono[a] + ((mono[b] - mono[a]) * (position - a));
            }

            result[i] = (sbyte)Math.Clamp((int)Math.Round(value * 127.0), -127, 127);
        }

        return result;
    }

    /// <summary>Writes platform PCM as an 8-bit mono 44.1 kHz WAV file.</summary>
    public static byte[] ToWav(sbyte[] pcm)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length + (pcm.Length & 1));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((ushort)1);
        w.Write((ushort)1);
        w.Write(SampleRate);
        w.Write(SampleRate);
        w.Write((ushort)1);
        w.Write((ushort)8);
        w.Write("data"u8);
        w.Write(pcm.Length);
        foreach (sbyte s in pcm)
        {
            w.Write((byte)(s + 128));
        }

        if ((pcm.Length & 1) != 0)
        {
            w.Write((byte)0);
        }

        w.Flush();
        return stream.ToArray();
    }

    /// <summary>Encodes cartridge data.</summary>
    public static byte[] Encode(sbyte[] pcm)
    {
        var data = new byte[4 + pcm.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data, pcm.Length);
        for (int i = 0; i < pcm.Length; i++)
        {
            data[4 + i] = (byte)pcm[i];
        }

        return data;
    }

    /// <summary>Decodes cartridge data.</summary>
    public static sbyte[] Decode(ReadOnlySpan<byte> data, string name)
    {
        if (data.Length < 4)
        {
            throw new ProjectException($"Sample '{name}' data is truncated.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count < 1 || count > MaxFrames || data.Length != 4 + count)
        {
            throw new ProjectException($"Sample '{name}' data is malformed.");
        }

        var pcm = new sbyte[count];
        for (int i = 0; i < count; i++)
        {
            pcm[i] = (sbyte)data[4 + i];
        }

        return pcm;
    }
}

/// <summary>
/// Wavetable resources: one cycle of exactly 64 signed 8-bit samples. Project side (<c>.wavetable</c>): JSON
/// <c>{ "samples": [0, 12, 25, …] }</c> with 64 integers −128..127. Cartridge side: the 64 bytes.
/// </summary>
public static class WavetableFormat
{
    /// <summary>Samples per cycle.</summary>
    public const int Length = 64;

    /// <summary>Parses the project JSON.</summary>
    /// <exception cref="ProjectException">The JSON is invalid.</exception>
    public static sbyte[] ParseJson(string json, string file)
    {
        JsonObject root = SpriteFormat.ParseObject(json, file);
        if (root["samples"] is not JsonArray samples || samples.Count != Length)
        {
            throw new ProjectException($"{file}: 'samples' must be an array of exactly {Length} integers.");
        }

        var result = new sbyte[Length];
        for (int i = 0; i < Length; i++)
        {
            if (samples[i] is not JsonValue v || !v.TryGetValue(out int s) || s is < -128 or > 127)
            {
                throw new ProjectException($"{file}: samples[{i}] must be an integer -128..127.");
            }

            result[i] = (sbyte)s;
        }

        return result;
    }

    /// <summary>Formats project JSON (eight values per line).</summary>
    public static string FormatJson(sbyte[] table)
    {
        var text = new StringBuilder("{\n  \"samples\": [\n");
        for (int i = 0; i < Length; i += 8)
        {
            text.Append("    ").Append(string.Join(", ", table.Skip(i).Take(8))).Append(i + 8 < Length ? ",\n" : "\n");
        }

        return text.Append("  ]\n}\n").ToString();
    }

    /// <summary>Encodes cartridge data.</summary>
    public static byte[] Encode(sbyte[] table) => table.Select(s => (byte)s).ToArray();

    /// <summary>Decodes cartridge data.</summary>
    public static sbyte[] Decode(ReadOnlySpan<byte> data, string name) =>
        data.Length == Length ? data.ToArray().Select(b => (sbyte)b).ToArray() : throw new ProjectException($"Wavetable '{name}' must have {Length} samples.");
}
