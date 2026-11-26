using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZTX16.Cartridge;

/// <summary>
/// A flat runtime stream of 32-bit values (integers or float bits) and a string table, served to the Core
/// library through the <c>data.*</c> syscalls. Instrument, Sequence and Song resources use it.
/// </summary>
public sealed record ValueStream(int[] Data, string[] Strings);

/// <summary>Builds and reads <see cref="ValueStream"/>s and their cartridge encoding.</summary>
public sealed class ValueStreamWriter
{
    private readonly List<int> _data = new();
    private readonly List<string> _strings = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    /// <summary>Appends an integer.</summary>
    public ValueStreamWriter Int(int value)
    {
        _data.Add(value);
        return this;
    }

    /// <summary>Appends a float (as bits).</summary>
    public ValueStreamWriter Float(float value) => Int(BitConverter.SingleToInt32Bits(value));

    /// <summary>Appends a string index (−1 for null).</summary>
    public ValueStreamWriter String(string? value)
    {
        if (value is null)
        {
            return Int(-1);
        }

        if (!_index.TryGetValue(value, out int i))
        {
            i = _strings.Count;
            _strings.Add(value);
            _index[value] = i;
        }

        return Int(i);
    }

    /// <summary>The finished stream.</summary>
    public ValueStream ToStream() => new(_data.ToArray(), _strings.ToArray());

    /// <summary>Cartridge encoding: u16 string count and strings, i32 value count and values.</summary>
    public static byte[] Encode(ValueStream stream)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write((ushort)stream.Strings.Length);
        foreach (string s in stream.Strings)
        {
            FormatHelpers.WriteName(writer, s);
        }

        writer.Write(stream.Data.Length);
        foreach (int value in stream.Data)
        {
            writer.Write(value);
        }

        writer.Flush();
        return memory.ToArray();
    }

    /// <summary>Decodes the cartridge encoding.</summary>
    public static ValueStream Decode(ReadOnlySpan<byte> data, string where)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data.ToArray()));
            var strings = new string[reader.ReadUInt16()];
            for (int i = 0; i < strings.Length; i++)
            {
                strings[i] = FormatHelpers.ReadName(reader);
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 4)
            {
                throw new EndOfStreamException();
            }

            int[] values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = reader.ReadInt32();
            }

            return new ValueStream(values, strings);
        }
        catch (EndOfStreamException e)
        {
            throw new ProjectException($"{where}: data is truncated.", e);
        }
    }
}

/// <summary>Sequential reader of a <see cref="ValueStream"/>, used when validating decoded resources.</summary>
internal sealed class ValueStreamReader(ValueStream stream, string where)
{
    private int _position;

    public int Int() => _position < stream.Data.Length ? stream.Data[_position++] : throw new ProjectException($"{where}: data is truncated.");

    public float Float() => BitConverter.Int32BitsToSingle(Int());

    public string? String()
    {
        int i = Int();
        return i == -1 ? null
            : (uint)i < (uint)stream.Strings.Length ? stream.Strings[i]
            : throw new ProjectException($"{where}: bad string index.");
    }

    public void End()
    {
        if (_position != stream.Data.Length)
        {
            throw new ProjectException($"{where}: data has trailing values.");
        }
    }
}

/// <summary>Note names: <c>C4</c> = 60, <c>A4</c> = 69 (440 Hz), with <c>#</c> or <c>b</c>; or MIDI numbers 0..127.</summary>
public static partial class NoteNames
{
    /// <summary>Parses a note name or number.</summary>
    /// <exception cref="ProjectException">Not a note.</exception>
    public static int Parse(JsonNode? node, string where)
    {
        if (node is JsonValue v && v.TryGetValue(out int number))
        {
            return number is >= 0 and <= 127 ? number : throw new ProjectException($"{where}: note numbers are 0..127.");
        }

        if (node is JsonValue s && s.TryGetValue(out string? text) && Pattern().Match(text) is { Success: true } m)
        {
            int semitone = "C D EF G A B".IndexOf(char.ToUpperInvariant(m.Groups[1].Value[0]));
            semitone += m.Groups[2].Value switch { "#" => 1, "b" => -1, _ => 0 };
            int note = ((int.Parse(m.Groups[3].Value) + 1) * 12) + semitone;
            return note is >= 0 and <= 127 ? note : throw new ProjectException($"{where}: note '{text}' is outside 0..127.");
        }

        throw new ProjectException($"{where}: expected a note name like \"C4\", \"F#3\", \"Bb2\" or a number 0..127.");
    }

    /// <summary>Formats a note number as a name (sharps).</summary>
    public static string Format(int note) =>
        new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" }[note % 12] + ((note / 12) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex("^([A-Ga-g])([#b]?)(-?[0-9])$")]
    private static partial Regex Pattern();
}

/// <summary>Where an instrument's sound comes from.</summary>
public enum InstrumentSourceKind
{
    /// <summary>A Synth oscillator (channels 0–5).</summary>
    Synth = 0,

    /// <summary>A Sample resource (channels 6–7).</summary>
    Sample = 1,

    /// <summary>A Wavetable resource (channels 6–7).</summary>
    Wavetable = 2,
}

/// <summary>An immutable instrument: the base channel configuration a Voice or Song applies.</summary>
public sealed record InstrumentDefinition(
    InstrumentSourceKind Source,
    int Waveform,
    string? Resource,
    float Volume,
    float Pan,
    float Attack,
    float Decay,
    float Sustain,
    float Release,
    int AdsrTarget,
    float AdsrAmount,
    float Lfo1Pitch,
    float Lfo1Volume,
    float Lfo2Pitch,
    float Lfo2Volume,
    bool Loop,
    int LoopStart,
    int LoopEnd,
    float PlaybackRate,
    int BaseNote);

/// <summary>
/// Instrument resources. Project side (<c>.instrument</c>): JSON with exactly one source —
/// <c>"waveform": "square"</c> (sine, triangle, saw, reverseSaw, square, noise), <c>"sample": "name"</c> or
/// <c>"wavetable": "name"</c> — and optional <c>volume</c> (0.5), <c>pan</c> (0), <c>adsr</c>
/// <c>[attack, decay, sustain, release]</c> ([0.005, 0, 1, 0.05]), <c>adsrTarget</c> ("volume" or "pitch"),
/// <c>adsrAmount</c> (1), <c>lfo1</c>/<c>lfo2</c> <c>{ "pitch": semitones, "volume": factor }</c>, and for
/// samples <c>loop</c> <c>[start, end]</c> (end −1 = sample end), <c>rate</c> (1) and <c>baseNote</c> (60, the note
/// that plays at rate 1).
/// </summary>
public static class InstrumentFormat
{
    private const int Version = 1;

    private static readonly string[] Waveforms = ["sine", "triangle", "saw", "reverseSaw", "square", "noise"];

    /// <summary>Parses the project JSON.</summary>
    public static InstrumentDefinition ParseJson(string json, string file)
    {
        JsonObject o = SpriteFormat.ParseObject(json, file);
        int sources = (o["waveform"] is null ? 0 : 1) + (o["sample"] is null ? 0 : 1) + (o["wavetable"] is null ? 0 : 1);
        if (sources != 1)
        {
            throw new ProjectException($"{file}: an instrument needs exactly one of 'waveform', 'sample' and 'wavetable'.");
        }

        var source = InstrumentSourceKind.Synth;
        int waveform = 0;
        string? resource = null;
        if (o["waveform"] is not null)
        {
            string name = SpriteFormat.RequiredString(o, "waveform", file);
            waveform = Array.IndexOf(Waveforms, name);
            if (waveform < 0)
            {
                throw new ProjectException($"{file}: 'waveform' must be one of {string.Join(", ", Waveforms)}.");
            }
        }
        else if (o["sample"] is not null)
        {
            source = InstrumentSourceKind.Sample;
            resource = SpriteFormat.RequiredString(o, "sample", file);
        }
        else
        {
            source = InstrumentSourceKind.Wavetable;
            resource = SpriteFormat.RequiredString(o, "wavetable", file);
        }

        float[] adsr = o["adsr"] is null ? [0.005f, 0f, 1f, 0.05f] : MusicJson.Floats(o, "adsr", 4, file);
        string adsrTarget = o["adsrTarget"] is null ? "volume" : SpriteFormat.RequiredString(o, "adsrTarget", file);
        if (adsrTarget is not ("volume" or "pitch"))
        {
            throw new ProjectException($"{file}: 'adsrTarget' must be \"volume\" or \"pitch\".");
        }

        (float lfo1Pitch, float lfo1Volume) = Lfo(o, "lfo1", file);
        (float lfo2Pitch, float lfo2Volume) = Lfo(o, "lfo2", file);
        int[] loop = o["loop"] is null ? [-2, -2] : MusicJson.Ints(o, "loop", 2, file);
        if (o["loop"] is not null && source != InstrumentSourceKind.Sample)
        {
            throw new ProjectException($"{file}: 'loop' is only for sample instruments.");
        }

        var instrument = new InstrumentDefinition(source, waveform, resource,
            MusicJson.Float(o, "volume", 0.5f, file), MusicJson.Float(o, "pan", 0f, file),
            adsr[0], adsr[1], adsr[2], adsr[3], adsrTarget == "pitch" ? 0 : 1, MusicJson.Float(o, "adsrAmount", adsrTarget == "pitch" ? 12f : 1f, file),
            lfo1Pitch, lfo1Volume, lfo2Pitch, lfo2Volume,
            o["loop"] is not null, loop[0] < 0 ? 0 : loop[0], o["loop"] is null ? -1 : loop[1],
            MusicJson.Float(o, "rate", 1f, file), SpriteFormat.OptionalInt(o, "baseNote", 60, file));
        Validate(instrument, file);
        return instrument;

        static (float Pitch, float Volume) Lfo(JsonObject o, string key, string file)
        {
            if (o[key] is null)
            {
                return (0f, 0f);
            }

            JsonObject lfo = o[key] as JsonObject ?? throw new ProjectException($"{file}: '{key}' must be an object.");
            return (MusicJson.Float(lfo, "pitch", 0f, file), MusicJson.Float(lfo, "volume", 0f, file));
        }
    }

    /// <summary>Formats project JSON.</summary>
    public static string FormatJson(InstrumentDefinition i)
    {
        var o = new JsonObject();
        switch (i.Source)
        {
            case InstrumentSourceKind.Synth:
                o["waveform"] = Waveforms[i.Waveform];
                break;
            case InstrumentSourceKind.Sample:
                o["sample"] = i.Resource;
                break;
            default:
                o["wavetable"] = i.Resource;
                break;
        }

        o["volume"] = i.Volume;
        o["pan"] = i.Pan;
        o["adsr"] = new JsonArray(i.Attack, i.Decay, i.Sustain, i.Release);
        o["adsrTarget"] = i.AdsrTarget == 0 ? "pitch" : "volume";
        o["adsrAmount"] = i.AdsrAmount;
        o["lfo1"] = new JsonObject { ["pitch"] = i.Lfo1Pitch, ["volume"] = i.Lfo1Volume };
        o["lfo2"] = new JsonObject { ["pitch"] = i.Lfo2Pitch, ["volume"] = i.Lfo2Volume };
        if (i.Source == InstrumentSourceKind.Sample)
        {
            if (i.Loop)
            {
                o["loop"] = new JsonArray(i.LoopStart, i.LoopEnd);
            }

            o["rate"] = i.PlaybackRate;
            o["baseNote"] = i.BaseNote;
        }

        return o.ToJsonString(MusicJson.Indented) + "\n";
    }

    /// <summary>The runtime stream.</summary>
    public static ValueStream ToStream(InstrumentDefinition i) => new ValueStreamWriter()
        .Int(Version).Int((int)i.Source).Int(i.Waveform).String(i.Resource)
        .Float(i.Volume).Float(i.Pan).Float(i.Attack).Float(i.Decay).Float(i.Sustain).Float(i.Release)
        .Int(i.AdsrTarget).Float(i.AdsrAmount).Float(i.Lfo1Pitch).Float(i.Lfo1Volume).Float(i.Lfo2Pitch).Float(i.Lfo2Volume)
        .Int(i.Loop ? 1 : 0).Int(i.LoopStart).Int(i.LoopEnd).Float(i.PlaybackRate).Int(i.BaseNote)
        .ToStream();

    /// <summary>Rebuilds an instrument from its stream.</summary>
    public static InstrumentDefinition FromStream(ValueStream stream, string where)
    {
        var r = new ValueStreamReader(stream, where);
        if (r.Int() != Version)
        {
            throw new ProjectException($"{where}: unsupported instrument data version.");
        }

        var i = new InstrumentDefinition((InstrumentSourceKind)r.Int(), r.Int(), r.String(), r.Float(), r.Float(), r.Float(), r.Float(),
            r.Float(), r.Float(), r.Int(), r.Float(), r.Float(), r.Float(), r.Float(), r.Float(), r.Int() != 0, r.Int(), r.Int(), r.Float(), r.Int());
        r.End();
        Validate(i, where);
        return i;
    }

    /// <summary>Encodes cartridge data.</summary>
    public static byte[] Encode(InstrumentDefinition i) => ValueStreamWriter.Encode(ToStream(i));

    /// <summary>Decodes cartridge data.</summary>
    public static InstrumentDefinition Decode(ReadOnlySpan<byte> data, string name) =>
        FromStream(ValueStreamWriter.Decode(data, $"Instrument '{name}'"), $"Instrument '{name}'");

    private static void Validate(InstrumentDefinition i, string where)
    {
        bool ok = Enum.IsDefined(i.Source) && i.Waveform is >= 0 and <= 5
            && (i.Source == InstrumentSourceKind.Synth) == (i.Resource is null)
            && i.Volume is >= 0 and <= 1 && i.Pan is >= -1 and <= 1
            && i.Attack is >= 0 and <= 10 && i.Decay is >= 0 and <= 10 && i.Sustain is >= 0 and <= 1 && i.Release is >= 0 and <= 10
            && i.AdsrTarget is 0 or 1
            && (i.AdsrTarget == 0 ? i.AdsrAmount is >= -96 and <= 96 : i.AdsrAmount is >= -1 and <= 1)
            && i.Lfo1Pitch is >= -96 and <= 96 && i.Lfo2Pitch is >= -96 and <= 96
            && i.Lfo1Volume is >= -1 and <= 1 && i.Lfo2Volume is >= -1 and <= 1
            && i.LoopStart >= 0 && (i.LoopEnd == -1 || i.LoopEnd > i.LoopStart)
            && i.PlaybackRate is > 0 and <= 64 && i.BaseNote is >= 0 and <= 127;
        if (!ok)
        {
            throw new ProjectException($"{where}: instrument values out of range (volume 0..1, pan -1..1, ADSR times 0..10 s, sustain 0..1, "
                + "pitch amounts ±96 semitones, volume amounts ±1, loop end > start, rate 0..64, baseNote 0..127).");
        }
    }
}

/// <summary>One sequence event.</summary>
/// <param name="Kind">0 = note, 1 = rest, 2 = volume change.</param>
/// <param name="Note">MIDI note for a note event.</param>
/// <param name="Length">Sequence ticks until the next event (note, rest); 0 for a volume change.</param>
/// <param name="Gate">Sequence ticks the note is held before NoteOff (1..Length).</param>
/// <param name="Volume">Volume factor for a volume change (0..1, multiplies the instrument volume).</param>
public sealed record SequenceEvent(int Kind, int Note, int Length, int Gate, float Volume);

/// <summary>An immutable monophonic sequence with its instrument.</summary>
public sealed record SequenceDefinition(string Instrument, int TicksPerBeat, float Bpm, bool Loop, SequenceEvent[] Events);

/// <summary>
/// Sequence resources. Project side (<c>.sequence</c>): JSON
/// <c>{ "instrument": "lead", "ticksPerBeat": 4, "bpm": 120, "loop": false, "events": [ ["C4", 2], ["E4", 2, 1],
/// ["rest", 4], { "volume": 0.5 }, { "note": "G4", "length": 4, "gate": 3 } ] }</c>. A note is
/// <c>[note, length]</c> or <c>[note, length, gate]</c> (gate defaults to the length), a rest
/// <c>["rest", length]</c>; lengths are in Sequence Ticks.
/// </summary>
public static class SequenceFormat
{
    private const int Version = 1;

    /// <summary>Parses the project JSON.</summary>
    public static SequenceDefinition ParseJson(string json, string file)
    {
        JsonObject o = SpriteFormat.ParseObject(json, file);
        string instrument = SpriteFormat.RequiredString(o, "instrument", file);
        int tpb = SpriteFormat.OptionalInt(o, "ticksPerBeat", 4, file);
        float bpm = MusicJson.Float(o, "bpm", 120f, file);
        bool loop = o["loop"] is JsonValue lv && lv.TryGetValue(out bool b) ? b
            : o["loop"] is null ? false : throw new ProjectException($"{file}: 'loop' must be true or false.");
        if (o["events"] is not JsonArray list || list.Count == 0)
        {
            throw new ProjectException($"{file}: 'events' must be a non-empty array.");
        }

        var events = new List<SequenceEvent>();
        for (int i = 0; i < list.Count; i++)
        {
            string where = $"{file}: events[{i}]";
            events.Add(list[i] switch
            {
                JsonArray a when a.Count >= 2 && a[0] is JsonValue first && first.TryGetValue(out string? word) && word == "rest" =>
                    new SequenceEvent(1, 0, Int(a[1], where), 0, 0f),
                JsonArray a when a.Count is 2 or 3 =>
                    Note(NoteNames.Parse(a[0], where), Int(a[1], where), a.Count == 3 ? Int(a[2], where) : -1),
                JsonObject e when e["rest"] is not null => new SequenceEvent(1, 0, SpriteFormat.RequiredInt(e, "rest", where), 0, 0f),
                JsonObject e when e["volume"] is not null => new SequenceEvent(2, 0, 0, 0, MusicJson.Float(e, "volume", 1f, where)),
                JsonObject e when e["note"] is not null =>
                    Note(NoteNames.Parse(e["note"], where), SpriteFormat.RequiredInt(e, "length", where), SpriteFormat.OptionalInt(e, "gate", -1, where)),
                _ => throw new ProjectException($"{where}: expected [note, length(, gate)], [\"rest\", length] or an object with note, rest or volume."),
            });
        }

        var sequence = new SequenceDefinition(instrument, tpb, bpm, loop, events.ToArray());
        Validate(sequence, file);
        return sequence;

        static SequenceEvent Note(int note, int length, int gate) => new(0, note, length, gate < 0 ? length : gate, 0f);

        static int Int(JsonNode? node, string where) =>
            node is JsonValue v && v.TryGetValue(out int n) ? n : throw new ProjectException($"{where}: lengths must be integers.");
    }

    /// <summary>Formats project JSON.</summary>
    public static string FormatJson(SequenceDefinition s)
    {
        var events = new JsonArray();
        foreach (SequenceEvent e in s.Events)
        {
            events.Add(e.Kind switch
            {
                0 when e.Gate == e.Length => new JsonArray(NoteNames.Format(e.Note), e.Length),
                0 => new JsonArray(NoteNames.Format(e.Note), e.Length, e.Gate),
                1 => new JsonArray("rest", e.Length),
                _ => (JsonNode)new JsonObject { ["volume"] = e.Volume },
            });
        }

        var o = new JsonObject { ["instrument"] = s.Instrument, ["ticksPerBeat"] = s.TicksPerBeat, ["bpm"] = s.Bpm, ["loop"] = s.Loop, ["events"] = events };
        return o.ToJsonString(MusicJson.Indented) + "\n";
    }

    /// <summary>
    /// The runtime stream: version, instrument, ticksPerBeat, bpm, loop, event count, then per event kind,
    /// note, length, gate, volume.
    /// </summary>
    public static ValueStream ToStream(SequenceDefinition s)
    {
        var w = new ValueStreamWriter().Int(Version).String(s.Instrument).Int(s.TicksPerBeat).Float(s.Bpm).Int(s.Loop ? 1 : 0).Int(s.Events.Length);
        foreach (SequenceEvent e in s.Events)
        {
            w.Int(e.Kind).Int(e.Note).Int(e.Length).Int(e.Gate).Float(e.Volume);
        }

        return w.ToStream();
    }

    /// <summary>Rebuilds a sequence from its stream.</summary>
    public static SequenceDefinition FromStream(ValueStream stream, string where)
    {
        var r = new ValueStreamReader(stream, where);
        if (r.Int() != Version)
        {
            throw new ProjectException($"{where}: unsupported sequence data version.");
        }

        string instrument = r.String() ?? throw new ProjectException($"{where}: missing instrument.");
        int tpb = r.Int();
        float bpm = r.Float();
        bool loop = r.Int() != 0;
        int count = r.Int();
        if (count is < 0 or > 65535)
        {
            throw new ProjectException($"{where}: bad event count.");
        }

        var events = new SequenceEvent[count];
        for (int i = 0; i < count; i++)
        {
            events[i] = new SequenceEvent(r.Int(), r.Int(), r.Int(), r.Int(), r.Float());
        }

        r.End();
        var s = new SequenceDefinition(instrument, tpb, bpm, loop, events);
        Validate(s, where);
        return s;
    }

    /// <summary>Encodes cartridge data.</summary>
    public static byte[] Encode(SequenceDefinition s) => ValueStreamWriter.Encode(ToStream(s));

    /// <summary>Decodes cartridge data.</summary>
    public static SequenceDefinition Decode(ReadOnlySpan<byte> data, string name) =>
        FromStream(ValueStreamWriter.Decode(data, $"Sequence '{name}'"), $"Sequence '{name}'");

    private static void Validate(SequenceDefinition s, string where)
    {
        if (string.IsNullOrEmpty(s.Instrument) || s.TicksPerBeat is < 1 or > 960 || !(s.Bpm is >= 1 and <= 1000) || s.Events.Length == 0)
        {
            throw new ProjectException($"{where}: a sequence needs an instrument, ticksPerBeat 1..960, bpm 1..1000 and at least one event.");
        }

        for (int i = 0; i < s.Events.Length; i++)
        {
            SequenceEvent e = s.Events[i];
            bool ok = e.Kind switch
            {
                0 => e.Note is >= 0 and <= 127 && e.Length >= 1 && e.Gate >= 1 && e.Gate <= e.Length,
                1 => e.Length >= 1,
                2 => e.Volume is >= 0 and <= 1,
                _ => false,
            };
            if (!ok)
            {
                throw new ProjectException($"{where}: event {i}: lengths must be ≥ 1, gate 1..length, notes 0..127, volume 0..1.");
            }
        }

        if (s.Loop && s.Events.All(e => e.Length == 0))
        {
            throw new ProjectException($"{where}: a looping sequence needs a note or rest.");
        }
    }
}

/// <summary>One scheduled Song event; the meaning of P0..P5 depends on <see cref="Kind"/> (see <see cref="SongFormat"/>).</summary>
public sealed record SongEvent(int At, int Kind, int P0, int P1, string? Name, float F0, float F1, float F2, int Assign);

/// <summary>An immutable song: timed sequence, LFO, filter and effect events over the physical hardware.</summary>
public sealed record SongDefinition(int TicksPerBeat, float Bpm, int Length, bool Loop, SongEvent[] Events);

/// <summary>
/// Song resources. Project side (<c>.song</c>): JSON with <c>ticksPerBeat</c> (4), <c>bpm</c> (120),
/// <c>length</c> (song ticks), <c>loop</c> and <c>events</c>, each with <c>at</c> (song tick) and one of:
/// <list type="bullet">
/// <item><c>{ "channel": 0, "sequence": "bass" }</c> — assign a sequence to a physical channel (null stops it);</item>
/// <item><c>{ "lfo": 0, "waveform": "triangle", "hz": 5 }</c> — LFO settings;</item>
/// <item><c>{ "lfo": 0, "channel": 3, "target": "pitch", "amount": 0.2 }</c> — LFO → channel route;</item>
/// <item><c>{ "lfo": 1, "unit": "filter1", "parameter": 0, "amount": 12 }</c> — LFO → processor route;</item>
/// <item><c>{ "filter": 0, "mode": "lowpass", "cutoff": 800, "resonance": 0.2, "assign": 2 }</c>;</item>
/// <item><c>{ "effect": 0, "type": "reverb", "parameters": [0.6, 0.5], "mix": 0.3, "assign": "global" }</c>.</item>
/// </list>
/// <c>assign</c> is a channel 0..7, "global" or "none"; omitted, the assignment is unchanged.
/// Kinds: 0 sequence (P0 channel, Name), 1 LFO settings (P0 lfo, P1 waveform, F0 hz), 2 LFO route (P0 lfo,
/// P1 channel, Assign = target, F0 amount), 3 LFO → processor (P0 lfo, P1 unit, Assign = parameter, F0 amount),
/// 4 filter (P0 filter, P1 mode, F0 cutoff, F1 resonance, Assign), 5 effect (P0 fx, P1 type, F0, F1, F2 mix,
/// Assign). Assign −2 = keep.
/// </summary>
public static class SongFormat
{
    private const int Version = 1;

    /// <summary>"Keep the current assignment".</summary>
    public const int Keep = -2;

    private static readonly string[] LfoWaveforms = ["triangle", "saw", "reverseSaw", "square"];
    private static readonly string[] FilterModes = ["lowpass", "highpass", "bandpass"];
    private static readonly string[] EffectTypes = ["chorus", "delay", "reverb"];
    private static readonly string[] Units = ["filter1", "filter2", "fx1", "fx2"];

    /// <summary>Parses the project JSON.</summary>
    public static SongDefinition ParseJson(string json, string file)
    {
        JsonObject o = SpriteFormat.ParseObject(json, file);
        int tpb = SpriteFormat.OptionalInt(o, "ticksPerBeat", 4, file);
        float bpm = MusicJson.Float(o, "bpm", 120f, file);
        int length = SpriteFormat.RequiredInt(o, "length", file);
        bool loop = o["loop"] is JsonValue lv && lv.TryGetValue(out bool b) ? b
            : o["loop"] is null ? false : throw new ProjectException($"{file}: 'loop' must be true or false.");
        var events = new List<SongEvent>();
        if (o["events"] is JsonArray list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                string where = $"{file}: events[{i}]";
                JsonObject e = list[i] as JsonObject ?? throw new ProjectException($"{where} must be an object.");
                events.Add(ParseEvent(e, where));
            }
        }

        var song = new SongDefinition(tpb, bpm, length, loop, events.OrderBy(e => e.At).ToArray());
        Validate(song, file);
        return song;
    }

    private static SongEvent ParseEvent(JsonObject e, string where)
    {
        int at = SpriteFormat.RequiredInt(e, "at", where);
        if (e["sequence"] is not null || (e.ContainsKey("sequence") && e["lfo"] is null))
        {
            string? sequence = e["sequence"] is null ? null : SpriteFormat.RequiredString(e, "sequence", where);
            return new SongEvent(at, 0, SpriteFormat.RequiredInt(e, "channel", where), 0, sequence, 0, 0, 0, Keep);
        }

        if (e["lfo"] is not null)
        {
            int lfo = SpriteFormat.RequiredInt(e, "lfo", where);
            if (e["waveform"] is not null || e["hz"] is not null)
            {
                return new SongEvent(at, 1, lfo, Choice(e, "waveform", LfoWaveforms, 0, where), null, MusicJson.Float(e, "hz", 1f, where), 0, 0, Keep);
            }

            if (e["unit"] is not null)
            {
                return new SongEvent(at, 3, lfo, Choice(e, "unit", Units, -1, where), null, MusicJson.Float(e, "amount", 0f, where), 0, 0,
                    SpriteFormat.RequiredInt(e, "parameter", where));
            }

            string target = SpriteFormat.RequiredString(e, "target", where);
            if (target is not ("pitch" or "volume"))
            {
                throw new ProjectException($"{where}: 'target' must be \"pitch\" or \"volume\".");
            }

            return new SongEvent(at, 2, lfo, SpriteFormat.RequiredInt(e, "channel", where), null, MusicJson.Float(e, "amount", 0f, where), 0, 0,
                target == "pitch" ? 0 : 1);
        }

        if (e["filter"] is not null)
        {
            return new SongEvent(at, 4, SpriteFormat.RequiredInt(e, "filter", where), Choice(e, "mode", FilterModes, 0, where), null,
                MusicJson.Float(e, "cutoff", 20000f, where), MusicJson.Float(e, "resonance", 0f, where), 0, Assign(e, where));
        }

        if (e["effect"] is not null)
        {
            float[] p = e["parameters"] is null ? [0.5f, 0.5f] : MusicJson.Floats(e, "parameters", 2, where);
            return new SongEvent(at, 5, SpriteFormat.RequiredInt(e, "effect", where), Choice(e, "type", EffectTypes, -1, where), null,
                p[0], p[1], MusicJson.Float(e, "mix", 0.3f, where), Assign(e, where));
        }

        throw new ProjectException($"{where}: expected a sequence, lfo, filter or effect event.");
    }

    private static int Choice(JsonObject e, string key, string[] options, int fallback, string where)
    {
        if (e[key] is null && fallback >= 0)
        {
            return fallback;
        }

        int i = Array.IndexOf(options, SpriteFormat.RequiredString(e, key, where));
        return i >= 0 ? i : throw new ProjectException($"{where}: '{key}' must be one of {string.Join(", ", options)}.");
    }

    private static int Assign(JsonObject e, string where) => e["assign"] switch
    {
        null => Keep,
        JsonValue v when v.TryGetValue(out int channel) => channel,
        JsonValue v when v.TryGetValue(out string? s) && s == "global" => 8,
        JsonValue v when v.TryGetValue(out string? s) && s == "none" => -1,
        _ => throw new ProjectException($"{where}: 'assign' must be a channel 0..7, \"global\" or \"none\"."),
    };

    /// <summary>Formats project JSON.</summary>
    public static string FormatJson(SongDefinition s)
    {
        var events = new JsonArray();
        foreach (SongEvent e in s.Events)
        {
            var o = new JsonObject { ["at"] = e.At };
            switch (e.Kind)
            {
                case 0:
                    o["channel"] = e.P0;
                    o["sequence"] = e.Name;
                    break;
                case 1:
                    o["lfo"] = e.P0;
                    o["waveform"] = LfoWaveforms[e.P1];
                    o["hz"] = e.F0;
                    break;
                case 2:
                    o["lfo"] = e.P0;
                    o["channel"] = e.P1;
                    o["target"] = e.Assign == 0 ? "pitch" : "volume";
                    o["amount"] = e.F0;
                    break;
                case 3:
                    o["lfo"] = e.P0;
                    o["unit"] = Units[e.P1];
                    o["parameter"] = e.Assign;
                    o["amount"] = e.F0;
                    break;
                case 4:
                    o["filter"] = e.P0;
                    o["mode"] = FilterModes[e.P1];
                    o["cutoff"] = e.F0;
                    o["resonance"] = e.F1;
                    AssignJson(o, e.Assign);
                    break;
                default:
                    o["effect"] = e.P0;
                    o["type"] = EffectTypes[e.P1];
                    o["parameters"] = new JsonArray(e.F0, e.F1);
                    o["mix"] = e.F2;
                    AssignJson(o, e.Assign);
                    break;
            }

            events.Add(o);
        }

        var root = new JsonObject { ["ticksPerBeat"] = s.TicksPerBeat, ["bpm"] = s.Bpm, ["length"] = s.Length, ["loop"] = s.Loop, ["events"] = events };
        return root.ToJsonString(MusicJson.Indented) + "\n";

        static void AssignJson(JsonObject o, int assign)
        {
            if (assign != Keep)
            {
                o["assign"] = assign switch { 8 => "global", -1 => "none", _ => assign };
            }
        }
    }

    /// <summary>
    /// The runtime stream: version, ticksPerBeat, bpm, length, loop, event count, then per event at, kind, P0,
    /// P1, name, F0, F1, F2, assign.
    /// </summary>
    public static ValueStream ToStream(SongDefinition s)
    {
        var w = new ValueStreamWriter().Int(Version).Int(s.TicksPerBeat).Float(s.Bpm).Int(s.Length).Int(s.Loop ? 1 : 0).Int(s.Events.Length);
        foreach (SongEvent e in s.Events)
        {
            w.Int(e.At).Int(e.Kind).Int(e.P0).Int(e.P1).String(e.Name).Float(e.F0).Float(e.F1).Float(e.F2).Int(e.Assign);
        }

        return w.ToStream();
    }

    /// <summary>Rebuilds a song from its stream.</summary>
    public static SongDefinition FromStream(ValueStream stream, string where)
    {
        var r = new ValueStreamReader(stream, where);
        if (r.Int() != Version)
        {
            throw new ProjectException($"{where}: unsupported song data version.");
        }

        int tpb = r.Int();
        float bpm = r.Float();
        int length = r.Int();
        bool loop = r.Int() != 0;
        int count = r.Int();
        if (count is < 0 or > 65535)
        {
            throw new ProjectException($"{where}: bad event count.");
        }

        var events = new SongEvent[count];
        for (int i = 0; i < count; i++)
        {
            events[i] = new SongEvent(r.Int(), r.Int(), r.Int(), r.Int(), r.String(), r.Float(), r.Float(), r.Float(), r.Int());
        }

        r.End();
        var s = new SongDefinition(tpb, bpm, length, loop, events);
        Validate(s, where);
        return s;
    }

    /// <summary>Encodes cartridge data.</summary>
    public static byte[] Encode(SongDefinition s) => ValueStreamWriter.Encode(ToStream(s));

    /// <summary>Decodes cartridge data.</summary>
    public static SongDefinition Decode(ReadOnlySpan<byte> data, string name) =>
        FromStream(ValueStreamWriter.Decode(data, $"Song '{name}'"), $"Song '{name}'");

    private static void Validate(SongDefinition s, string where)
    {
        if (s.TicksPerBeat is < 1 or > 960 || !(s.Bpm is >= 1 and <= 1000) || s.Length < 1)
        {
            throw new ProjectException($"{where}: a song needs ticksPerBeat 1..960, bpm 1..1000 and a length ≥ 1.");
        }

        for (int i = 0; i < s.Events.Length; i++)
        {
            SongEvent e = s.Events[i];
            bool assignOk = e.Assign is SongFormat.Keep or (>= -1 and <= 8);
            bool ok = e.At >= 0 && e.At < s.Length && (i == 0 || e.At >= s.Events[i - 1].At) && e.Kind switch
            {
                0 => e.P0 is >= 0 and <= 7 && (e.Name is null || e.Name.Length > 0),
                1 => e.P0 is 0 or 1 && e.P1 is >= 0 and <= 3 && e.F0 is >= 0.01f and <= 60f,
                2 => e.P0 is 0 or 1 && e.P1 is >= 0 and <= 7 && e.Assign is 0 or 1 && float.IsFinite(e.F0),
                3 => e.P0 is 0 or 1 && e.P1 is >= 0 and <= 3 && e.Assign >= 0 && e.Assign <= (e.P1 < 2 ? 1 : 2) && float.IsFinite(e.F0),
                4 => e.P0 is 0 or 1 && e.P1 is >= 0 and <= 2 && float.IsFinite(e.F0) && float.IsFinite(e.F1) && assignOk,
                5 => e.P0 is 0 or 1 && e.P1 is >= 0 and <= 2 && float.IsFinite(e.F0) && float.IsFinite(e.F1) && float.IsFinite(e.F2) && assignOk,
                _ => false,
            };
            if (!ok)
            {
                throw new ProjectException($"{where}: event {i} is invalid (at must be 0..length-1; check channel, lfo, unit, parameter and assign).");
            }
        }
    }
}

internal static class MusicJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static float Float(JsonObject o, string key, float fallback, string where) =>
        o[key] is null ? fallback
        : o[key] is JsonValue v && v.TryGetValue(out float f) && float.IsFinite(f) ? f
        : throw new ProjectException($"{where}: '{key}' must be a number.");

    public static float[] Floats(JsonObject o, string key, int count, string where)
    {
        if (o[key] is not JsonArray a || a.Count != count)
        {
            throw new ProjectException($"{where}: '{key}' must be an array of {count} numbers.");
        }

        return a.Select(n => n is JsonValue v && v.TryGetValue(out float f) && float.IsFinite(f) ? f
            : throw new ProjectException($"{where}: '{key}' must be an array of {count} numbers.")).ToArray();
    }

    public static int[] Ints(JsonObject o, string key, int count, string where)
    {
        if (o[key] is not JsonArray a || a.Count != count)
        {
            throw new ProjectException($"{where}: '{key}' must be an array of {count} integers.");
        }

        return a.Select(n => n is JsonValue v && v.TryGetValue(out int i) ? i
            : throw new ProjectException($"{where}: '{key}' must be an array of {count} integers.")).ToArray();
    }
}
