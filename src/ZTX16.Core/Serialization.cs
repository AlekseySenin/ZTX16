using ZTX16.Core;

namespace ZTX16;

/// <summary>
/// Builds binary data (for example a Storage Entry) from numbers, booleans, strings and byte arrays.
/// The format is little-endian and fixed, so <see cref="ByteReader"/> reads it back the same on every machine.
/// </summary>
/// <remarks>
/// Layout: byte = 1 byte, bool = 1 byte (0/1), int = 4, long = 8, float = 4 (IEEE 754 bits), string = int
/// length (−1 for null) then UTF-16 code units (2 bytes each), bytes = int length then the bytes.
/// </remarks>
public sealed class ByteWriter
{
    private byte[] _buffer = new byte[64];

    /// <summary>Number of bytes written.</summary>
    public int Length { get; private set; }

    /// <summary>Writes the low 8 bits of <paramref name="value"/>.</summary>
    public void WriteByte(int value)
    {
        Ensure(1);
        _buffer[Length++] = (byte)value;
    }

    /// <summary>Writes a boolean as one byte.</summary>
    public void WriteBool(bool value) => WriteByte(value ? 1 : 0);

    /// <summary>Writes a 32-bit integer.</summary>
    public void WriteInt(int value)
    {
        Ensure(4);
        _buffer[Length] = (byte)value;
        _buffer[Length + 1] = (byte)(value >> 8);
        _buffer[Length + 2] = (byte)(value >> 16);
        _buffer[Length + 3] = (byte)(value >> 24);
        Length += 4;
    }

    /// <summary>Writes a 64-bit integer.</summary>
    public void WriteLong(long value)
    {
        WriteInt((int)value);
        WriteInt((int)(value >> 32));
    }

    /// <summary>Writes a float exactly (its IEEE 754 bits; every NaN becomes the canonical NaN).</summary>
    public void WriteFloat(float value) => WriteInt(FloatBits.ToBits(value));

    /// <summary>Writes a string (or null).</summary>
    public void WriteString(string? value)
    {
        if (value is null)
        {
            WriteInt(-1);
            return;
        }

        WriteInt(value.Length);
        Ensure(value.Length * 2);
        for (int i = 0; i < value.Length; i++)
        {
            int c = value[i];
            _buffer[Length++] = (byte)c;
            _buffer[Length++] = (byte)(c >> 8);
        }
    }

    /// <summary>Writes a byte array with its length.</summary>
    public void WriteBytes(byte[] value)
    {
        WriteInt(value.Length);
        Ensure(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            _buffer[Length++] = value[i];
        }
    }

    /// <summary>A copy of the bytes written so far.</summary>
    public byte[] ToArray()
    {
        byte[] result = new byte[Length];
        for (int i = 0; i < Length; i++)
        {
            result[i] = _buffer[i];
        }

        return result;
    }

    private void Ensure(int count)
    {
        if (Length + count <= _buffer.Length)
        {
            return;
        }

        int size = _buffer.Length * 2;
        while (size < Length + count)
        {
            size *= 2;
        }

        byte[] bigger = new byte[size];
        for (int i = 0; i < Length; i++)
        {
            bigger[i] = _buffer[i];
        }

        _buffer = bigger;
    }
}

/// <summary>Reads data written by <see cref="ByteWriter"/>. Reading past the end stops the cartridge with an error.</summary>
public sealed class ByteReader
{
    private readonly byte[] _data;

    /// <summary>Reads <paramref name="data"/> from the start.</summary>
    public ByteReader(byte[] data)
    {
        _data = data;
    }

    /// <summary>Current read position.</summary>
    public int Position { get; private set; }

    /// <summary>Bytes left.</summary>
    public int Remaining => _data.Length - Position;

    /// <summary>True when everything has been read.</summary>
    public bool IsAtEnd => Position >= _data.Length;

    /// <summary>Reads one byte (0..255).</summary>
    public int ReadByte()
    {
        Need(1);
        return _data[Position++];
    }

    /// <summary>Reads a boolean.</summary>
    public bool ReadBool() => ReadByte() != 0;

    /// <summary>Reads a 32-bit integer.</summary>
    public int ReadInt()
    {
        Need(4);
        int value = _data[Position] | (_data[Position + 1] << 8) | (_data[Position + 2] << 16) | (_data[Position + 3] << 24);
        Position += 4;
        return value;
    }

    /// <summary>Reads a 64-bit integer.</summary>
    public long ReadLong()
    {
        long low = (uint)ReadInt();
        long high = ReadInt();
        return low | (high << 32);
    }

    /// <summary>Reads a float.</summary>
    public float ReadFloat() => FloatBits.FromBits(ReadInt());

    /// <summary>Reads a string (or null).</summary>
    public string? ReadString()
    {
        int length = ReadInt();
        if (length == -1)
        {
            return null;
        }

        if (length < 0)
        {
            Intrinsics.Fail("ByteReader: bad string length");
        }

        Need(length * 2);
        string[] parts = new string[length];
        for (int i = 0; i < length; i++)
        {
            char c = (char)(_data[Position] | (_data[Position + 1] << 8));
            parts[i] = c.ToString();
            Position += 2;
        }

        return string.Concat(parts);
    }

    /// <summary>Reads a byte array written with <see cref="ByteWriter.WriteBytes"/>.</summary>
    public byte[] ReadBytes()
    {
        int length = ReadInt();
        if (length < 0)
        {
            Intrinsics.Fail("ByteReader: bad byte array length");
        }

        Need(length);
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = _data[Position++];
        }

        return result;
    }

    private void Need(int count)
    {
        if (count > Remaining)
        {
            Intrinsics.Fail("ByteReader: read past the end of the data");
        }
    }
}

/// <summary>
/// Exact conversion between a float and its IEEE 754 bits, by arithmetic only (multiplying by powers of two
/// is exact), so it behaves identically in the VM and on .NET.
/// </summary>
public static class FloatBits
{
    /// <summary>The bits of <paramref name="value"/>; every NaN gives 0x7FC00000.</summary>
    public static int ToBits(float value)
    {
        float same = value;
        if (same != value)
        {
            return 0x7FC00000;     // NaN is the only value not equal to itself
        }

        int sign = 0;
        if (value < 0f || (value == 0f && 1f / value < 0f))
        {
            sign = int.MinValue;
            value = -value;
        }

        if (value == 0f)
        {
            return sign;
        }

        if (value > float.MaxValue)
        {
            return sign | 0x7F800000;
        }

        double d = value;
        int exponent = 0;
        while (d >= 2.0)
        {
            d *= 0.5;
            exponent++;
        }

        while (d < 1.0)
        {
            d *= 2.0;
            exponent--;
        }

        if (exponent < -126)
        {
            // Subnormal: the mantissa is value × 2^149.
            double m = value;
            for (int i = 0; i < 149; i++)
            {
                m *= 2.0;
            }

            return sign | (int)m;
        }

        int mantissa = (int)((d - 1.0) * 8388608.0);
        return sign | ((exponent + 127) << 23) | mantissa;
    }

    /// <summary>The float with bits <paramref name="bits"/>.</summary>
    public static float FromBits(int bits)
    {
        int exponent = (bits >> 23) & 0xFF;
        int mantissa = bits & 0x7FFFFF;
        float result;
        if (exponent == 255)
        {
            result = mantissa == 0 ? float.PositiveInfinity : float.NaN;
        }
        else if (exponent == 0)
        {
            double m = mantissa;
            for (int i = 0; i < 149; i++)
            {
                m *= 0.5;
            }

            result = (float)m;
        }
        else
        {
            double m = 1.0 + (mantissa / 8388608.0);
            for (int e = exponent - 127; e > 0; e--)
            {
                m *= 2.0;
            }

            for (int e = exponent - 127; e < 0; e++)
            {
                m *= 0.5;
            }

            result = (float)m;
        }

        return bits < 0 ? -result : result;
    }
}
