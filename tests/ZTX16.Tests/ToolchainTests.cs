using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ZTX16.Platform.Execution;
using ZTX16.Platform.Runtime;
using ZTX16.Toolchain;
using ZTX16.VM;

namespace ZTX16.Tests;

/// <summary>Compiles C# with Roslyn and runs the result through the ZTX16 toolchain.</summary>
internal static class CartridgeCompiler
{
    // Same language version as the cartridge build targets.
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);
    private static readonly MetadataReference[] References = BuildReferences();
    private static readonly SyntaxTree[] CoreLibrary = LoadCoreLibrary();

    /// <summary>Repository root (the directory containing ZTX16.sln).</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static byte[] Compile(string source, bool optimize)
    {
        // Like the cartridge build targets: the core library source is compiled into the cartridge.
        CSharpCompilation compilation = CSharpCompilation.Create(
            "TestCartridge",
            [CSharpSyntaxTree.ParseText(source, ParseOptions), .. CoreLibrary],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: optimize ? OptimizationLevel.Release : OptimizationLevel.Debug));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            string errors = string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException($"C# compilation failed:{Environment.NewLine}{errors}");
        }

        return stream.ToArray();
    }

    public static VmModule Translate(string source, bool optimize) => IlTranslator.Translate(Compile(source, optimize));

    public static RuntimeSession Start(string source, bool optimize)
    {
        var session = new RuntimeSession(VmProgram.FromModule(Translate(source, optimize)), 48000);
        session.Initialize();
        return session;
    }

    private static string FindRepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "ZTX16.sln")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }

        return root;
    }

    private static SyntaxTree[] LoadCoreLibrary()
    {
        string core = Path.Combine(FindRepositoryRoot(), "src", "ZTX16.Core");
        return Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), ParseOptions, path: p))
            .ToArray();
    }

    private static MetadataReference[] BuildReferences()
    {
        string[] trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return trusted
            .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "ZTX16.Sdk.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }
}

public class ToolchainCartridgeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovesSquareWithKeyboardAndDraws(bool optimize)
    {
        const string source = """
            using ZTX16;

            public sealed class Game : ICartridge
            {
                static int x = 100;
                static int y = 50;
                const int Speed = 2;

                public void Initialize() => Graphics.Fill(0, 18);

                public void Tick()
                {
                    if (Input.WasKeyPressed(Key.Escape)) Machine.Exit();
                    if (Input.IsKeyDown(Key.Right)) x += Speed;
                    if (Input.IsKeyDown(Key.Up)) y += Speed;
                    x = Clamp(x, 0, 240);
                    y = Clamp(y, 0, 240);
                }

                public void Render()
                {
                    Graphics.Clear(2);
                    Graphics.FillRect(2, x, y, 16, 16, 11);
                }

                static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
            }
            """;

        RuntimeSession session = CartridgeCompiler.Start(source, optimize);
        Assert.Equal(18, session.Graphics.Layers[0].GetPixel(0, 0));

        session.RunTick(TimeSpan.Zero);
        Assert.Equal(11, session.Graphics.Layers[2].GetPixel(100, 50));

        session.Input.Enqueue(Platform.Input.InputEvent.KeyChange(Key.Right, true));
        for (int i = 0; i < 100; i++)
        {
            session.RunTick(TimeSpan.Zero);
        }

        // 100 + 2 * 100 would be 300; Clamp stops it at 240.
        Assert.Equal(11, session.Graphics.Layers[2].GetPixel(240, 50));
        Assert.Equal(0, session.Graphics.Layers[2].GetPixel(239, 50));

        session.Input.Enqueue(Platform.Input.InputEvent.KeyChange(Key.Escape, true));
        session.RunTick(TimeSpan.Zero);
        Assert.True(session.ShouldExit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticConstructorsRunBeforeInitialize(bool optimize)
    {
        const string source = """
            using ZTX16;

            static class Settings
            {
                public static readonly int Color = Compute();
                static int Compute() => 3 * 3;
            }

            public sealed class Game : ICartridge
            {
                static byte counter = 250;

                public void Initialize() => Graphics.Fill(1, Settings.Color);
                public void Tick() { counter += 10; }   // wraps as a byte
                public void Render() => Graphics.SetPixel(1, 0, 0, counter);
            }
            """;

        RuntimeSession session = CartridgeCompiler.Start(source, optimize);
        Assert.Equal(9, session.Graphics.Layers[1].GetPixel(5, 5));

        session.RunTick(TimeSpan.Zero);
        Assert.Equal(4, session.Graphics.Layers[1].GetPixel(0, 0)); // (250 + 10) mod 256
    }
}

/// <summary>
/// Differential tests: the same C# methods run natively on .NET and translated in the VM,
/// and must produce identical results.
/// </summary>
public class ToolchainSemanticsTests
{
    private const string Source = """
        using ZTX16;

        public enum Dir { None, Left = 3, Right = 7 }

        public static class Fns
        {
            public static int Arith(int a, int b) => a * 31 + b - (a ^ b) + (a & 0xFF) - (b | 1);
            public static int Div(int a, int b) => b == 0 ? 0 : a / b + a % b;
            public static uint UDiv(uint a, uint b) => b == 0 ? 0u : a / b + a % b;
            public static int Shifts(int a, int s) => (a << s) + (a >> s) + (int)((uint)a >> s);
            public static long Wide(long a, int b) => a * b + (a >> 3) - (a / (b | 1)) + ((long)b << 33);
            public static ulong UWide(ulong a, ulong b) => b == 0 ? 0ul : a / b + a % b;
            public static float Floats(float a, float b) => a * b - a / (b == 0 ? 1 : b) + a % 7.5f;
            public static double Doubles(double a, double b) => a * b + a / 3.0 - b;
            public static int Convert(float f) => (int)f + (byte)(int)f + (sbyte)(int)f + (short)(f * 1000) + (char)(int)f;
            public static uint ToUInt(double d) => (uint)d;
            public static double FromUInt(uint u) => u;
            public static long FloatToLong(double d) => (long)d;
            public static ulong ToULong(double d) => (ulong)d;
            public static double FromULong(ulong u) => u;
            public static uint FloatToUInt(float f) => (uint)f;
            public static int Compare(float a, float b)
            {
                int r = 0;
                if (a < b) r |= 1;
                if (a <= b) r |= 2;
                if (a > b) r |= 4;
                if (a >= b) r |= 8;
                if (a == b) r |= 16;
                if (a != b) r |= 32;
                if (!(a < b)) r |= 64;
                if (!(a >= b)) r |= 128;
                return r;
            }
            public static int UCompare(uint a, uint b) => (a < b ? 1 : 0) + (a >= b ? 2 : 0) + (a > b ? 4 : 0) + (a <= b ? 8 : 0);
            public static bool Logic(int a, int b) => (a > 0 && b > 0) || (a < -5 && !(b == 3));
            public static int Loop(int n)
            {
                int sum = 0;
                for (int i = 0; i < n; i++)
                {
                    if (i % 3 == 0) continue;
                    sum += i * i;
                    if (sum > 100000) break;
                }
                return sum;
            }
            public static int While(int n) { int steps = 0; while (n != 1 && steps < 1000) { n = (n & 1) == 0 ? n / 2 : 3 * n + 1; steps++; } return steps; }
            public static int Switch(int v)
            {
                switch (v)
                {
                    case 0: return 10;
                    case 1: return 11;
                    case 2: return 12;
                    case 3: return 13;
                    case 5: return 15;
                    case 100: return 99;
                    default: return -1;
                }
            }
            public static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
            public static int EnumMath(Dir d) => d == Dir.Right ? 70 : (int)d * 2;
            public static int Chars(char c) => c >= 'a' && c <= 'z' ? c - 32 : c;
        }

        public sealed class Game : ICartridge
        {
            static double sink;

            public void Initialize()
            {
                // Make every function reachable so the toolchain translates it.
                sink = Fns.Arith(1, 2) + Fns.Div(1, 1) + Fns.UDiv(1, 1) + Fns.Shifts(1, 1) + Fns.Wide(1, 1)
                    + (double)Fns.UWide(1, 1) + Fns.Floats(1, 1) + Fns.Doubles(1, 1) + Fns.Convert(1) + Fns.ToUInt(1)
                    + Fns.FromUInt(1) + Fns.FloatToLong(1) + (double)Fns.ToULong(1) + Fns.FromULong(1) + Fns.FloatToUInt(1) + Fns.Compare(1, 1) + Fns.UCompare(1, 1) + (Fns.Logic(1, 1) ? 1 : 0)
                    + Fns.Loop(1) + Fns.While(1) + Fns.Switch(1) + Fns.Fib(1) + Fns.EnumMath(Dir.Left) + Fns.Chars('a');
            }

            public void Tick() { }
            public void Render() { }
        }
        """;

    public static TheoryData<string, object[]> Cases => new()
    {
        { "Arith", [123456, -789] }, { "Arith", [int.MaxValue, int.MinValue] },
        { "Div", [-17, 5] }, { "Div", [int.MinValue, 7] }, { "Div", [5, 0] },
        { "UDiv", [0xFFFFFFF0u, 7u] }, { "UDiv", [3000000000u, 3u] },
        { "Shifts", [-123456, 5] }, { "Shifts", [1, 37] },
        { "Wide", [-9_000_000_000L, 77] }, { "Wide", [long.MaxValue, 3] },
        { "UWide", [ulong.MaxValue, 10ul] },
        { "Floats", [3.25f, -1.5f] }, { "Floats", [1e30f, 1e10f] }, { "Floats", [float.NaN, 2f] },
        { "Doubles", [1.0 / 3, 2.5] },
        { "Convert", [300.75f] }, { "Convert", [-129.5f] }, { "Convert", [1e12f] }, { "Convert", [float.NaN] },
        { "ToUInt", [3_500_000_000.9] }, { "ToUInt", [-5.5] }, { "ToUInt", [5e9] }, { "FloatToUInt", [-1.5f] }, { "FloatToUInt", [4e9f] },
        { "ToULong", [1.5e19] }, { "ToULong", [-3.0] }, { "FromULong", [ulong.MaxValue - 5] }, { "FromUInt", [4_000_000_000u] }, { "FloatToLong", [-1e18] },
        { "Compare", [1f, 2f] }, { "Compare", [2f, 2f] }, { "Compare", [3f, 2f] }, { "Compare", [float.NaN, 2f] },
        { "UCompare", [1u, 0xFFFFFFFFu] }, { "UCompare", [5u, 5u] },
        { "Logic", [1, 1] }, { "Logic", [-6, 3] }, { "Logic", [-6, 4] }, { "Logic", [0, 9] },
        { "Loop", [50] }, { "Loop", [100000] },
        { "While", [27] },
        { "Switch", [0] }, { "Switch", [3] }, { "Switch", [4] }, { "Switch", [5] }, { "Switch", [100] }, { "Switch", [-1] },
        { "Fib", [20] },
        { "Chars", ['q'] }, { "Chars", ['Q'] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void VmMatchesDotNet(string function, object[] args)
    {
        foreach (bool optimize in new[] { false, true })
        {
            byte[] assembly = CartridgeCompiler.Compile(Source, optimize);
            MethodInfo method = LoadNative(assembly).GetType("Fns")!.GetMethod(function)!;
            object? expected = method.Invoke(null, args);

            VmModule module = IlTranslator.Translate(assembly);
            int index = module.Functions.ToList().FindIndex(f => f.Name == $"Fns.{function}");
            var instance = new VmInstance(Verifier.Verify(module), new SyscallTable());
            long raw = instance.Invoke(index, args.Select(ToSlot).ToArray());

            Assert.Equal(expected, FromSlot(raw, method.ReturnType));
        }
    }

    [Fact]
    public void EnumParametersUseUnderlyingValues()
    {
        byte[] assembly = CartridgeCompiler.Compile(Source, optimize: true);
        VmModule module = IlTranslator.Translate(assembly);
        int index = module.Functions.ToList().FindIndex(f => f.Name == "Fns.EnumMath");
        var instance = new VmInstance(Verifier.Verify(module), new SyscallTable());

        Assert.Equal(70, instance.Invoke(index, [7]));
        Assert.Equal(6, instance.Invoke(index, [3]));
    }

    private static Assembly LoadNative(byte[] assembly)
    {
        var context = new AssemblyLoadContext(null, isCollectible: true);
        return context.LoadFromStream(new MemoryStream(assembly));
    }

    private static long ToSlot(object value) => value switch
    {
        int i => i,
        uint u => (int)u,
        long l => l,
        ulong ul => (long)ul,
        float f => VmValue.FromF32(f),
        double d => VmValue.FromF64(d),
        bool b => b ? 1 : 0,
        char c => c,
        _ => throw new ArgumentException($"Unsupported test argument {value.GetType()}"),
    };

    private static object FromSlot(long raw, Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.Int32 => (int)raw,
        TypeCode.UInt32 => (uint)(int)raw,
        TypeCode.Int64 => raw,
        TypeCode.UInt64 => (ulong)raw,
        TypeCode.Single => VmValue.ToF32(raw),
        TypeCode.Double => VmValue.ToF64(raw),
        TypeCode.Boolean => (int)raw != 0,
        _ => throw new ArgumentException($"Unsupported return type {type}"),
    };
}

public class ToolchainRejectionTests
{
    private static string Cartridge(string members, string extraTypes = "") => $$"""
        using ZTX16;
        {{extraTypes}}
        public sealed class Game : ICartridge
        {
            public void Initialize() { }
            public void Render() { }
            {{members}}
        }
        """;

    [Theory]
    [InlineData("public void Tick() { Graphics.Clear(System.Math.Abs(-1)); }", "", "System.Math.Abs")]
    [InlineData("public void Tick() { try { Graphics.Clear(0); } finally { Graphics.Clear(1); } }", "", "try/catch/finally")]
    [InlineData("public void Tick() { checked { int x = int.MaxValue; x++; Graphics.Clear(x); } }", "", "checked")]
    [InlineData("public void Tick() { int? n = 5; Graphics.Clear(n.Value); }", "", "System.Nullable")]
    [InlineData("public void Tick() { var l = new System.Collections.Generic.List<int>(); }", "", "List")]
    [InlineData("public void Tick() { System.Action a = () => Graphics.Clear(0); }", "", "delegates")]
    [InlineData("public void Tick() { object o = 5; Graphics.Clear(o == null ? 0 : 1); }", "", "boxing")]
    [InlineData("public void Tick() { var t = (1, 2); Graphics.Clear(t.Item1); }", "", "ValueTuple")]
    [InlineData("public void Tick() { var e = new E(); }", "class E : System.Exception { }", "System.Exception")]
    public void UnsupportedFeaturesAreReportedClearly(string tick, string extraTypes, string expectedFragment)
    {
        var e = Assert.Throws<TranslationException>(() => CartridgeCompiler.Translate(Cartridge(tick, extraTypes), optimize: false));
        Assert.Contains(expectedFragment, e.Message);
        Assert.Contains("Game.Tick", e.Message);
    }

    [Fact]
    public void MissingCartridgeClassIsReported()
    {
        const string source = "public static class Game { public static void Initialize() { } }";
        var e = Assert.Throws<TranslationException>(() => CartridgeCompiler.Translate(source, optimize: false));
        Assert.Contains("ICartridge", e.Message);
    }

    [Fact]
    public void MoreThanOneCartridgeClassIsReported()
    {
        string source = Cartridge("public void Tick() { }",
            "public sealed class Other : ICartridge { public void Initialize() { } public void Tick() { } public void Render() { } }");
        var e = Assert.Throws<TranslationException>(() => CartridgeCompiler.Translate(source, optimize: false));
        Assert.Contains("More than one", e.Message);
    }
}

public class SdkAbiTests
{
    [Fact]
    public void EverySdkSyscallIsImplementedByThePlatform()
    {
        // PlatformSyscalls.Create throws if any SDK syscall lacks an implementation.
        var session = new RuntimeSession(new NullProgram(), 48000);
        SyscallTable table = PlatformSyscalls.Create(session);
        Assert.Equal(Abi.SdkAbi.Syscalls.Count, table.Definitions.Count());
    }

    private sealed class NullProgram : IRuntimeProgram
    {
        public void Initialize(RuntimeSession session) { }
        public void Tick() { }
        public void Render() { }
        public void Deinitialize() { }
    }
}

public class SampleCartridgeTests
{
    [Fact]
    public void HelloSquareSampleRunsHeadless()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "ZTX16.sln")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }

        string source = File.ReadAllText(Path.Combine(root, "samples", "HelloSquare", "code", "Game.cs"));
        RuntimeSession session = CartridgeCompiler.Start(source, optimize: true);

        session.Input.Enqueue(Platform.Input.InputEvent.KeyChange(Key.Left, true));
        for (int i = 0; i < 10; i++)
        {
            session.RunTick(TimeSpan.Zero);
        }

        // Starts at x = 120 and moves 2 px per tick to the left.
        Assert.Equal(11, session.Graphics.Layers[2].GetPixel(100, 120));
        Assert.Equal(512, session.Graphics.Layers[0].Width);
        Assert.Equal(7, session.Graphics.Layers[1].GetPixel(0, 0));
        Assert.Equal(5, session.Graphics.DisplayLayers[0].OriginX);
    }

    [Fact]
    public void HelloSquareProjectWallsStopTheShipAndGemsAreCollected()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "ZTX16.sln")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }

        Cartridge.CartridgeContent cartridge = ProjectBuilder.Build(Path.Combine(root, "samples", "HelloSquare"), ProjectFixtures.CoreDirectory);
        var session = new RuntimeSession(VmProgram.Load(cartridge.Executable), 48000, new ResourceStore(cartridge.Resources));
        session.Initialize();

        // The "west" wall (x 40..55) is rasterized into the Markers layer; its bottom brick row is mortar.
        Assert.Equal(25, session.Graphics.Layers[1].GetPixel(40, 56));

        // Flying left from x = 120 stops touching the wall at x = 56: the ship's center pixel is at (64, 128).
        session.Input.Enqueue(Platform.Input.InputEvent.KeyChange(Key.Left, true));
        RunTicks(session, 50);
        Assert.Equal(11, session.Graphics.Layers[2].GetPixel(64, 128));
        Assert.Equal(0, session.Graphics.Layers[2].GetPixel(54, 128));
        Assert.NotEqual(0, session.Audio.GetChannelState(7));     // engine hum while moving
        Assert.NotEqual(0, session.Audio.GetChannelState(0));     // wall thud: first free effects channel (nothing rendered, so still in attack)
        Assert.NotEqual(0, session.Audio.GetChannelState(2));     // background song: bass

        // Back to x = 120, then down over gem5 at (120, 20): it disappears from the Sprites layer.
        session.Input.Enqueue(Platform.Input.InputEvent.KeyChange(Key.Left, false));
        session.Input.Enqueue(Platform.Input.InputEvent.KeyChange(Key.Right, true));
        RunTicks(session, 32);
        session.Input.Enqueue(Platform.Input.InputEvent.KeyChange(Key.Right, false));
        RunTicks(session, 1);
        Assert.Equal(28, session.Graphics.Layers[2].GetPixel(121, 24));
        session.Input.Enqueue(Platform.Input.InputEvent.KeyChange(Key.Down, true));
        RunTicks(session, 70);
        Assert.Equal(0, session.Graphics.Layers[2].GetPixel(121, 24));
        Assert.NotEqual(0, session.Audio.GetChannelState(1));     // gem chime: channel 0 still holds the thud
        var pcm = new float[4800];
        session.Audio.Render(pcm);
        Assert.Contains(pcm, s => s != 0);
    }

    private static void RunTicks(RuntimeSession session, int count)
    {
        for (int i = 0; i < count; i++)
        {
            session.RunTick(TimeSpan.Zero);
        }
    }
}
