using System.Reflection;
using System.Runtime.Loader;
using ZTX16.Cartridge;
using ZTX16.Platform.Execution;
using ZTX16.Platform.Runtime;
using ZTX16.Platform.Storage;
using ZTX16.Toolchain;
using ZTX16.VM;

namespace ZTX16.Tests;

/// <summary>Compiles cartridge source once and runs its static functions natively and in the VM.</summary>
internal sealed class DualRunner
{
    private readonly MethodInfo[] _native;
    private readonly VerifiedModule _module;

    public DualRunner(string source, bool optimize)
    {
        byte[] assembly = CartridgeCompiler.Compile(source, optimize);
        var context = new AssemblyLoadContext(null, isCollectible: true);
        _native = context.LoadFromStream(new MemoryStream(assembly)).GetType("Fns")!.GetMethods(BindingFlags.Public | BindingFlags.Static);
        _module = Verifier.Verify(IlTranslator.Translate(assembly));
    }

    public long Native(string function, params long[] args)
    {
        MethodInfo method = _native.Single(m => m.Name == function);
        // Float arguments are passed as their bits, as VM slots hold them.
        object?[] converted = method.GetParameters().Select((p, i) => p.ParameterType == typeof(float)
            ? BitConverter.Int32BitsToSingle((int)args[i])
            : Convert.ChangeType(args[i], p.ParameterType)).ToArray();
        return Convert.ToInt64(method.Invoke(null, converted));
    }

    public long Vm(string function, params long[] args)
    {
        int index = _module.Module.Functions.ToList().FindIndex(f => f.Name == $"Fns.{function}");
        return new VmInstance(_module, new SyscallTable()).Invoke(index, args);
    }
}

public class DeterministicRandomTests
{
    private const string Source = """
        using ZTX16;
        using ZTX16.Collections;

        public static class Fns
        {
            // The raw 32-bit outputs, three at a time from a state.
            public static int Raw(int a, int b, int c, int d, int index)
            {
                var random = new DeterministicRandom(0);
                random.SetState(new int[] { a, b, c, d });
                int value = 0;
                for (int i = 0; i <= index; i++) value = random.NextInt();
                return value;
            }

            public static int State(long seed, int word) => new DeterministicRandom(seed).GetState()[word];

            public static int NthBelow(long seed, int max, int index)
            {
                var random = new DeterministicRandom(seed);
                int value = 0;
                for (int i = 0; i <= index; i++) value = random.Next(max);
                return value;
            }

            // Every operation interleaved; returns a hash of all results.
            public static int Mixed(long seed, int rounds)
            {
                var random = new DeterministicRandom(seed);
                string[] words = new string[] { "alpha", "beta", "gamma", "delta", "epsilon" };
                var numbers = new List<int>();
                for (int i = 0; i < 7; i++) numbers.Add(i * i);
                int h = 17;
                for (int i = 0; i < rounds; i++)
                {
                    h = (h * 31) + random.Next(10);
                    h = (h * 31) + random.Next(-1000, 1000);
                    h = (h * 31) + random.Next(int.MinValue, int.MaxValue);
                    h = (h * 31) + random.NextInt();
                    h = (h * 31) + (int)(random.NextFloat() * 16777216f);
                    h = (h * 31) + (int)(random.NextFloat(-5f, 5f) * 1000f);
                    h = (h * 31) + (random.NextBool() ? 1 : 0);
                    h = (h * 31) + random.Choice(words).Length;
                    h = (h * 31) + random.Choice(numbers);
                }

                return h;
            }

            // Saving the state mid-way and continuing in a new generator gives the same tail.
            public static int Resume(long seed, int before, int after)
            {
                var straight = new DeterministicRandom(seed);
                for (int i = 0; i < before; i++) straight.Next(1000);
                var saved = straight.GetState();
                var resumed = new DeterministicRandom(seed + 1);
                resumed.SetState(saved);
                int same = 0;
                for (int i = 0; i < after; i++) if (straight.Next(1000) == resumed.Next(1000)) same++;
                return same;
            }

            // Range and distribution checks: returns 0 when everything holds, else a failure code.
            public static int Ranges(long seed)
            {
                var random = new DeterministicRandom(seed);
                int[] counts = new int[10];
                for (int i = 0; i < 100000; i++)
                {
                    int v = random.Next(10);
                    if (v < 0 || v >= 10) return 1;
                    counts[v]++;
                    int w = random.Next(-3, 4);
                    if (w < -3 || w > 3) return 2;
                    float f = random.NextFloat();
                    if (f < 0f || f >= 1f) return 3;
                    float g = random.NextFloat(2.5f, 2.75f);
                    if (g < 2.5f || g >= 2.75f) return 4;
                    int x = random.Next(int.MaxValue - 1, int.MaxValue);
                    if (x != int.MaxValue - 1) return 5;
                }

                for (int i = 0; i < 10; i++)
                {
                    if (counts[i] < 9500 || counts[i] > 10500) return 10 + i;   // ±5% of 10000
                }

                int trues = 0;
                for (int i = 0; i < 100000; i++) if (random.NextBool()) trues++;
                if (trues < 49000 || trues > 51000) return 30;

                // Extreme range: values on both sides of zero appear.
                int negative = 0;
                for (int i = 0; i < 1000; i++) if (random.Next(int.MinValue, int.MaxValue) < 0) negative++;
                if (negative < 400 || negative > 600) return 31;
                return 0;
            }

            public static int BadRange() => new DeterministicRandom(1).Next(5, 5);
        }

        public sealed class Game : ICartridge
        {
            static int sink;
            public void Initialize() { sink = Fns.Raw(1, 2, 3, 4, 0) + Fns.State(0, 0) + Fns.NthBelow(0, 10, 0) + Fns.Mixed(0, 0) + Fns.Resume(0, 0, 0) + Fns.Ranges(0) + Fns.BadRange(); }
            public void Tick() { }
            public void Render() { }
        }
        """;

    private static readonly Lazy<DualRunner> Debug = new(() => new DualRunner(Source, optimize: false));
    private static readonly Lazy<DualRunner> Release = new(() => new DualRunner(Source, optimize: true));

    private static IEnumerable<DualRunner> Both() => [Debug.Value, Release.Value];

    [Fact]
    public void MatchesThePublishedXoshiro128StarStarVector()
    {
        // Reference outputs of xoshiro128** from the state {1, 2, 3, 4}.
        foreach (DualRunner run in Both())
        {
            Assert.Equal(11520u, (uint)run.Vm("Raw", 1, 2, 3, 4, 0));
            Assert.Equal(0u, (uint)run.Vm("Raw", 1, 2, 3, 4, 1));
            Assert.Equal(5927040u, (uint)run.Vm("Raw", 1, 2, 3, 4, 2));
        }
    }

    [Theory]
    [InlineData(1L, 2L, 3L, 4L)]
    [InlineData(0x12345678L, -1L, 42L, 7L)]
    public void RawOutputsMatchAnIndependentReference(long a, long b, long c, long d)
    {
        uint[] expected = Reference.Xoshiro((uint)a, (uint)b, (uint)c, (uint)d, 50);
        for (int i = 0; i < expected.Length; i += 7)
        {
            Assert.Equal(expected[i], (uint)Release.Value.Vm("Raw", a, b, c, d, i));
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(12345L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void SeedingIsSplitMix64(long seed)
    {
        uint[] expected = Reference.SeedState(seed);
        for (int word = 0; word < 4; word++)
        {
            Assert.Equal(expected[word], (uint)Release.Value.Vm("State", seed, word));
        }
    }

    [Fact]
    public void GoldenValuesNeverChange()
    {
        // Platform contract: these exact values for seed 2026 must stay the same in every ZTX16 version.
        int[] golden = [GoldenSeed2026[0], GoldenSeed2026[1], GoldenSeed2026[2], GoldenSeed2026[3], GoldenSeed2026[4], GoldenSeed2026[5],
            GoldenSeed2026[6], GoldenSeed2026[7], GoldenSeed2026[8], GoldenSeed2026[9]];
        foreach (DualRunner run in Both())
        {
            Assert.Equal(golden, Enumerable.Range(0, 10).Select(i => (int)run.Vm("NthBelow", 2026, 100, i)));
        }
    }

    // Next(100) ten times from seed 2026 (recorded after checking the algorithm against the reference above).
    internal static readonly int[] GoldenSeed2026 = [24, 91, 2, 2, 82, 47, 35, 31, 41, 66];

    [Theory]
    [InlineData(0L)]
    [InlineData(7L)]
    [InlineData(2026L)]
    [InlineData(-123456789L)]
    [InlineData(long.MaxValue)]
    public void VmMatchesDotNetForEveryOperation(long seed)
    {
        foreach (DualRunner run in Both())
        {
            Assert.Equal(run.Native("Mixed", seed, 200), run.Vm("Mixed", seed, 200));
        }
    }

    [Fact]
    public void SameSeedSameSequenceDifferentSeedDifferentSequence()
    {
        DualRunner run = Release.Value;
        long first = run.Vm("Mixed", 99, 100);
        Assert.Equal(first, run.Vm("Mixed", 99, 100));
        Assert.Equal(first, Debug.Value.Vm("Mixed", 99, 100));
        Assert.NotEqual(first, run.Vm("Mixed", 100, 100));
        Assert.NotEqual(run.Vm("Mixed", 0, 100), run.Vm("Mixed", 1, 100));
    }

    [Fact]
    public void SavedStateContinuesExactly()
    {
        Assert.Equal(500, Release.Value.Vm("Resume", 5, 37, 500));
        Assert.Equal(500, Debug.Value.Vm("Resume", 5, 37, 500));
    }

    [Fact]
    public void RangesAndDistribution()
    {
        Assert.Equal(0, Release.Value.Vm("Ranges", 31337));
        Assert.Equal(0, Release.Value.Native("Ranges", 31337));
    }

    [Fact]
    public void EmptyRangeIsAnError()
    {
        var e = Assert.Throws<VmTrapException>(() => Release.Value.Vm("BadRange"));
        Assert.Contains("max must be greater than min", e.Message);
    }

    /// <summary>Independent reference implementations of SplitMix64 seeding and xoshiro128**.</summary>
    private static class Reference
    {
        public static uint[] SeedState(long seed)
        {
            ulong x = (ulong)seed;
            ulong a = Next(ref x), b = Next(ref x);
            return [(uint)a, (uint)(a >> 32), (uint)b, (uint)(b >> 32)];

            static ulong Next(ref ulong x)
            {
                ulong z = x += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        public static uint[] Xoshiro(uint s0, uint s1, uint s2, uint s3, int count)
        {
            var result = new uint[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = uint.RotateLeft(s1 * 5, 7) * 9;
                uint t = s1 << 9;
                s2 ^= s0;
                s3 ^= s1;
                s1 ^= s2;
                s0 ^= s3;
                s2 ^= t;
                s3 = uint.RotateLeft(s3, 11);
            }

            return result;
        }
    }
}

public class SerializationTests
{
    private const string Source = """
        using ZTX16;

        public static class Fns
        {
            public static int Bits(float value) => FloatBits.ToBits(value);

            public static int RoundTrip(int bits) => FloatBits.ToBits(FloatBits.FromBits(bits));

            public static int Writer(int n)
            {
                var w = new ByteWriter();
                w.WriteByte(200);
                w.WriteBool(true);
                w.WriteInt(-123456789);
                w.WriteLong(-9876543210123L);
                w.WriteFloat(-0.15625f);
                w.WriteString("Привіт, ZTX16!");
                w.WriteString(null);
                w.WriteBytes(new byte[] { 1, 2, 3 });
                for (int i = 0; i < n; i++) w.WriteInt(i);

                var r = new ByteReader(w.ToArray());
                int ok = 0;
                if (r.ReadByte() == 200) ok |= 1;
                if (r.ReadBool()) ok |= 2;
                if (r.ReadInt() == -123456789) ok |= 4;
                if (r.ReadLong() == -9876543210123L) ok |= 8;
                if (r.ReadFloat() == -0.15625f) ok |= 16;
                if (r.ReadString() == "Привіт, ZTX16!") ok |= 32;
                if (r.ReadString() == null) ok |= 64;
                byte[] bytes = r.ReadBytes();
                if (bytes.Length == 3 && bytes[2] == 3) ok |= 128;
                int sum = 0;
                for (int i = 0; i < n; i++) sum += r.ReadInt();
                if (sum == n * (n - 1) / 2 && r.IsAtEnd) ok |= 256;
                return ok;
            }

            public static int Overrun() => new ByteReader(new byte[] { 1, 2 }).ReadInt();
        }

        public sealed class Game : ICartridge
        {
            static int sink;
            public void Initialize() { sink = Fns.Bits(0f) + Fns.RoundTrip(0) + Fns.Writer(0) + Fns.Overrun(); }
            public void Tick() { }
            public void Render() { }
        }
        """;

    private static readonly Lazy<DualRunner> Run = new(() => new DualRunner(Source, optimize: true));

    public static TheoryData<float> Floats => new()
    {
        0f, -0f, 1f, -1f, 0.1f, -0.15625f, 3.4028235e38f, float.Epsilon, -float.Epsilon, 1.17549435e-38f, 1.1754942e-38f,
        float.PositiveInfinity, float.NegativeInfinity, 123456.789f, 1e-40f, 16777216f, 0.33333334f,
    };

    [Theory]
    [MemberData(nameof(Floats))]
    public void FloatBitsAreExact(float value)
    {
        int expected = BitConverter.SingleToInt32Bits(value);
        Assert.Equal(expected, (int)Run.Value.Vm("Bits", BitConverter.SingleToInt32Bits(value)));
        Assert.Equal(expected, (int)Run.Value.Native("Bits", BitConverter.SingleToInt32Bits(value)));
        Assert.Equal(expected, (int)Run.Value.Vm("RoundTrip", expected));
    }

    [Fact]
    public void NaNIsCanonical()
    {
        Assert.Equal(0x7FC00000, (int)Run.Value.Vm("Bits", BitConverter.SingleToInt32Bits(float.NaN)));
    }

    [Fact]
    public void WriterAndReaderRoundTrip()
    {
        Assert.Equal(511, Run.Value.Vm("Writer", 100));
        Assert.Equal(511, Run.Value.Native("Writer", 100));
    }

    [Fact]
    public void ReadingPastTheEndIsAnError()
    {
        var e = Assert.Throws<VmTrapException>(() => Run.Value.Vm("Overrun"));
        Assert.Contains("past the end", e.Message);
    }
}

public class StorageTests
{
    [Fact]
    public void EscapingIsReversibleAndCaseDistinct()
    {
        foreach (string name in new[] { "save", "Save", "slot 1", "../../etc", "Привіт", "a.b", "~0041" })
        {
            string file = DirectoryStorage.Escape(name);
            Assert.Matches("^[a-z0-9_~A-F-]+$", file);
            Assert.Equal(name, DirectoryStorage.Unescape(file));
        }

        Assert.NotEqual(DirectoryStorage.Escape("Save").ToLowerInvariant(), DirectoryStorage.Escape("save").ToLowerInvariant());
    }

    [Fact]
    public void DirectoryStoragePersistsAndEnumeratesSorted()
    {
        using var dir = new TempDirectory();
        var storage = new StorageSystem(new DirectoryStorage(dir.Path));
        storage.Write("save", "slot 2", [2]);
        storage.Write("save", "Slot 1", [1, 1]);
        storage.Write("settings", "audio", [9]);
        storage.Write("save", "slot 2", [2, 2, 2]);       // replaces

        var again = new StorageSystem(new DirectoryStorage(dir.Path));   // "relaunch"
        Assert.True(again.IsPersistent);
        Assert.Equal(["save", "settings"], again.Types());
        Assert.Equal(["Slot 1", "slot 2"], again.Names("save"));
        Assert.Equal([2, 2, 2], again.Read("save", "slot 2"));
        Assert.Null(again.Read("save", "slot 3"));
        Assert.True(again.Delete("settings", "audio"));
        Assert.False(again.Delete("settings", "audio"));
        Assert.Equal(["save"], again.Types());
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void NamesAndLimitsAreEnforced()
    {
        var storage = new StorageSystem();
        Assert.False(storage.IsPersistent);
        Assert.Throws<ArgumentException>(() => storage.Write("", "x", []));
        Assert.Throws<ArgumentException>(() => storage.Write("save", new string('n', 65), []));
        Assert.Throws<ArgumentException>(() => storage.Write("save", "bad\nname", []));
        Assert.Throws<ArgumentException>(() => storage.Write("save", "big", new byte[StorageSystem.MaxEntrySize + 1]));

        for (int i = 0; i < 16; i++)
        {
            storage.Write("blob", i.ToString(System.Globalization.CultureInfo.InvariantCulture), new byte[StorageSystem.MaxEntrySize]);
        }

        Assert.Throws<InvalidOperationException>(() => storage.Write("blob", "one more", [1]));
        storage.Write("blob", "0", [1]);                // replacing frees space
        storage.Write("blob", "one more", [1]);
    }

    [Fact]
    public void CartridgeDataSurvivesARelaunch()
    {
        using var data = new TempDirectory();
        string source = """
            using ZTX16;
            public sealed class Game : ICartridge
            {
                public void Initialize()
                {
                    int runs = 0;
                    byte[]? saved = Storage.Read("save", "progress");
                    if (saved != null)
                    {
                        var reader = new ByteReader(saved);
                        runs = reader.ReadInt();
                        Graphics.SetPixel(0, 1, 0, reader.ReadString() == "hello" ? 1 : 0);
                    }

                    runs++;
                    var writer = new ByteWriter();
                    writer.WriteInt(runs);
                    writer.WriteString("hello");
                    Storage.Write("save", "progress", writer.ToArray());
                    Graphics.SetPixel(0, 0, 0, runs);
                    Graphics.SetPixel(0, 2, 0, Storage.Count("save"));
                    Graphics.SetPixel(0, 3, 0, Storage.TypeName(0) == "save" ? 1 : 0);
                    Graphics.SetPixel(0, 4, 0, Storage.Exists("save", "nothing") ? 1 : 0);
                    Graphics.SetPixel(0, 5, 0, Storage.IsPersistent() ? 1 : 0);
                }
                public void Tick() { }
                public void Render() { }
            }
            """;

        int[] Launch()
        {
            var session = new RuntimeSession(VmProgram.FromModule(CartridgeCompiler.Translate(source, optimize: true)), 48000,
                storage: new StorageSystem(new DirectoryStorage(data.Path)));
            session.Initialize();
            return Enumerable.Range(0, 6).Select(x => session.Graphics.Layers[0].GetPixel(x, 0)).ToArray();
        }

        Assert.Equal([1, 0, 1, 1, 0, 1], Launch());
        Assert.Equal([2, 1, 1, 1, 0, 1], Launch());
        Assert.Equal([3, 1, 1, 1, 0, 1], Launch());
    }

    [Fact]
    public void InvalidStorageCallTrapsTheCartridge()
    {
        var e = Assert.Throws<VmTrapException>(() => CartridgeCompiler.Start("""
            using ZTX16;
            public sealed class Game : ICartridge
            {
                public void Initialize() { Storage.Write("save", "", new byte[0]); }
                public void Tick() { }
                public void Render() { }
            }
            """, optimize: true));
        Assert.Contains("1..64 characters", e.Message);
    }
}
