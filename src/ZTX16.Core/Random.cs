using ZTX16.Collections;
using ZTX16.Core;

namespace ZTX16;

/// <summary>
/// The platform deterministic pseudo-random generator — the only randomness available to cartridge code.
/// The same seed and the same sequence of calls give the same results on every ZTX16 machine, in every
/// version: the algorithm and the way each operation uses it are part of the platform contract.
/// </summary>
/// <remarks>
/// <para>
/// Algorithm: xoshiro128** (state of four 32-bit words), seeded by expanding the 64-bit seed with
/// SplitMix64. Integer ranges use Lemire's multiply-and-reject method, so every value in a range is equally
/// likely. Numeric ranges include the lower bound and exclude the upper one. <see cref="Choice{T}(T[])"/>
/// advances the same state as every other operation.
/// </para>
/// <para>
/// It is not suitable for cryptography.
/// </para>
/// </remarks>
public sealed class DeterministicRandom
{
    private uint _s0, _s1, _s2, _s3;

    /// <summary>Creates a generator from <paramref name="seed"/>.</summary>
    public DeterministicRandom(long seed)
    {
        ulong x = (ulong)seed;
        ulong a = SplitMix(ref x);
        ulong b = SplitMix(ref x);
        _s0 = (uint)a;
        _s1 = (uint)(a >> 32);
        _s2 = (uint)b;
        _s3 = (uint)(b >> 32);
        if ((_s0 | _s1 | _s2 | _s3) == 0)
        {
            _s0 = 1;
        }
    }

    /// <summary>An integer in [<paramref name="min"/>, <paramref name="max"/>). Requires min &lt; max.</summary>
    public int Next(int min, int max)
    {
        if (max <= min)
        {
            Intrinsics.Fail("DeterministicRandom.Next: max must be greater than min");
        }

        uint range = (uint)((long)max - min);
        uint x = NextBits();
        ulong m = (ulong)x * range;
        uint low = (uint)m;
        if (low < range)
        {
            uint threshold = (0u - range) % range;
            while (low < threshold)
            {
                x = NextBits();
                m = (ulong)x * range;
                low = (uint)m;
            }
        }

        return (int)(min + (long)(m >> 32));
    }

    /// <summary>An integer in [0, <paramref name="max"/>). Requires max &gt; 0.</summary>
    public int Next(int max) => Next(0, max);

    /// <summary>32 random bits as an integer (any int value, each equally likely).</summary>
    public int NextInt() => (int)NextBits();

    /// <summary>A float in [0, 1), in steps of 2⁻²⁴.</summary>
    public float NextFloat() => (NextBits() >> 8) * (1f / 16777216f);

    /// <summary>A float in [<paramref name="min"/>, <paramref name="max"/>). Requires finite min &lt; max.</summary>
    /// <remarks>If rounding would produce <paramref name="max"/>, the result is <paramref name="min"/>.</remarks>
    public float NextFloat(float min, float max)
    {
        if (!(max > min) || max - min > float.MaxValue)
        {
            Intrinsics.Fail("DeterministicRandom.NextFloat: needs finite min < max");
        }

        float value = min + ((max - min) * NextFloat());
        return value < max ? value : min;
    }

    /// <summary>True or false with equal probability.</summary>
    public bool NextBool() => (NextBits() >> 31) != 0;

    /// <summary>A random element of a non-empty array.</summary>
    public T Choice<T>(T[] items)
    {
        if (items.Length == 0)
        {
            Intrinsics.Fail("DeterministicRandom.Choice: the array is empty");
        }

        return items[Next(items.Length)];
    }

    /// <summary>A random element of a non-empty list.</summary>
    public T Choice<T>(List<T> items)
    {
        if (items.Count == 0)
        {
            Intrinsics.Fail("DeterministicRandom.Choice: the list is empty");
        }

        return items[Next(items.Count)];
    }

    /// <summary>The generator's state (four integers), for example to keep it in a Storage Entry.</summary>
    public int[] GetState() => new int[] { (int)_s0, (int)_s1, (int)_s2, (int)_s3 };

    /// <summary>Restores a state from <see cref="GetState"/>; the generator continues exactly from there.</summary>
    public void SetState(int[] state)
    {
        if (state.Length != 4 || (state[0] | state[1] | state[2] | state[3]) == 0)
        {
            Intrinsics.Fail("DeterministicRandom.SetState: needs the four values from GetState (not all zero)");
        }

        _s0 = (uint)state[0];
        _s1 = (uint)state[1];
        _s2 = (uint)state[2];
        _s3 = (uint)state[3];
    }

    // xoshiro128**: the next 32 random bits.
    private uint NextBits()
    {
        uint result = RotateLeft(_s1 * 5, 7) * 9;
        uint t = _s1 << 9;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 11);
        return result;
    }

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        ulong z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
