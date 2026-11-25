using System.Reflection;
using System.Runtime.Loader;
using ZTX16.Platform.Runtime;
using ZTX16.Toolchain;
using ZTX16.VM;

namespace ZTX16.Tests;

/// <summary>
/// Differential tests for the managed heap: classes, inheritance, interfaces, arrays,
/// strings and null, run on .NET and in the VM (Debug and Release compilation).
/// </summary>
public class HeapSemanticsTests
{
    private const string Source = """
        using ZTX16;

        public abstract class Shape
        {
            protected int id;
            protected Shape(int id) { this.id = id; }
            public abstract int Area();
            public virtual int Describe() => Area() * 10 + id;
            public int Id => id;
        }

        public class Rect : Shape
        {
            public int W, H;
            public Rect(int id, int w, int h) : base(id) { W = w; H = h; }
            public override int Area() => W * H;
        }

        public sealed class Square : Rect
        {
            public Square(int id, int s) : base(id, s, s) { }
            public override int Describe() => base.Describe() + 1000;
        }

        public interface ICounter { int Next(); int Peek() => -1; }
        public interface INamed { string Name(); }

        public sealed class Counter : ICounter, INamed
        {
            private int value;
            public int Next() => ++value;
            int ICounter.Peek() => value;          // explicit implementation
            public string Name() => "counter";
        }

        public sealed class Plain : ICounter { public int Next() => 7; }   // uses the default Peek

        public sealed class Node
        {
            public int Value;
            public Node? Next;
            public Node(int value, Node? next) { Value = value; Next = next; }
            public int Add(int x) => Value + x;
        }

        public static class Fns
        {
            public static int Virtuals(int n)
            {
                Shape[] shapes = { new Rect(1, 2, 3), new Square(2, n), new Rect(3, n, 1) };
                int sum = 0;
                foreach (Shape s in shapes) sum += s.Describe() + s.Id;
                return sum;
            }

            public static int Interfaces(int n)
            {
                ICounter a = new Counter();
                ICounter b = new Plain();
                int r = 0;
                for (int i = 0; i < n; i++) r += a.Next();
                return r * 100 + a.Peek() * 10 + b.Next() + b.Peek();
            }

            public static int TypeTests(int k)
            {
                Shape s = k > 0 ? new Square(1, k) : new Rect(2, 1, 1);
                int r = 0;
                if (s is Square) r += 1;
                if (s is Rect) r += 2;
                Square? sq = s as Square;
                if (sq != null) r += 4;
                object o = s;
                if (o is Shape) r += 8;
                INamed? named = (object)new Counter() as INamed;
                if (named != null && named.Name() == "counter") r += 16;
                return r;
            }

            public static int Arrays(int n)
            {
                int[] ints = new int[n];
                byte[] bytes = new byte[n];
                sbyte[] sbytes = new sbyte[n];
                char[] chars = new char[n];
                bool[] flags = new bool[n];
                float[] floats = new float[n];
                long[] longs = new long[n];
                for (int i = 0; i < n; i++)
                {
                    ints[i] = i * i;
                    bytes[i] = (byte)(i * 37);
                    sbytes[i] = (sbyte)(i * 37);
                    chars[i] = (char)('a' + i % 26);
                    flags[i] = i % 3 == 0;
                    floats[i] = i * 0.5f;
                    longs[i] = (long)i << 40;
                }

                int[][] jagged = new int[3][];
                for (int i = 0; i < jagged.Length; i++) { jagged[i] = new int[i + 1]; jagged[i][i] = i + 5; }

                long sum = 0;
                for (int i = 0; i < n; i++)
                {
                    sum += ints[i] + bytes[i] + sbytes[i] + chars[i] + (flags[i] ? 1 : 0) + (int)(floats[i] * 2) + (longs[i] >> 40);
                }

                return (int)sum + jagged[2][2] + jagged[1].Length + ints.Length;
            }

            public static int Strings(int n)
            {
                string s = "ZTX";
                string t = s + "16";
                string u = string.Concat(t, "-", "ok");
                int r = t.Length * 100 + u.Length;
                if (t == "ZTX16") r += 1000;
                if (u != t) r += 2000;
                if (string.IsNullOrEmpty("")) r += 4000;
                string? missing = n > 100 ? "x" : null;
                if (string.IsNullOrEmpty(missing)) r += 8000;
                for (int i = 0; i < t.Length; i++) r += t[i];
                return r;
            }

            public static int LinkedList(int n)
            {
                Node? head = null;
                for (int i = 1; i <= n; i++) head = new Node(i, head);
                int sum = 0, count = 0;
                for (Node? p = head; p != null; p = p.Next) { sum += p.Value; count++; }
                return sum * 1000 + count;
            }

            // Garbage collection while a live object is reachable only from a caller's local.
            public static int RootInLocal(int n)
            {
                Node keep = new Node(42, new Node(8, null));
                Churn(n);
                return keep.Value + keep.Next!.Value;
            }

            // Garbage collection while a live object sits on the caller's operand stack.
            public static int RootOnOperandStack(int n) => new Node(5, null).Add(ChurnAndCount(n));

            // Garbage collection while live objects sit in an array and a static field.
            public static int RootInArrayAndStatic(int n)
            {
                Holder.Saved = new Node[] { new Node(1, null), new Node(2, null) };
                Churn(n);
                return Holder.Saved[0].Value + Holder.Saved[1].Value * 10;
            }

            static void Churn(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    int[] garbage = new int[256];
                    garbage[0] = i;
                    string text = "g" + "c";
                }
            }

            static int ChurnAndCount(int n) { Churn(n); return n; }
        }

        public static class Holder { public static Node[]? Saved; }

        public sealed class Game : ICartridge
        {
            static int sink;

            public void Initialize()
            {
                sink = Fns.Virtuals(1) + Fns.Interfaces(1) + Fns.TypeTests(1) + Fns.Arrays(1) + Fns.Strings(1)
                    + Fns.LinkedList(1) + Fns.RootInLocal(1) + Fns.RootOnOperandStack(1) + Fns.RootInArrayAndStatic(1);
            }

            public void Tick() { }
            public void Render() { }
        }
        """;

    public static TheoryData<string, int> Cases => new()
    {
        { "Virtuals", 4 }, { "Virtuals", 0 },
        { "Interfaces", 5 },
        { "TypeTests", 3 }, { "TypeTests", -1 },
        { "Arrays", 40 },
        { "Strings", 0 },
        { "LinkedList", 500 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void VmMatchesDotNet(string function, int arg)
    {
        foreach (bool optimize in new[] { false, true })
        {
            (int expected, int actual, _) = RunBoth(function, arg, optimize);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData("RootInLocal")]
    [InlineData("RootOnOperandStack")]
    [InlineData("RootInArrayAndStatic")]
    public void LiveObjectsSurviveCollection(string function)
    {
        // 20000 × ~1 KB of garbage forces several collections during the call.
        foreach (bool optimize in new[] { false, true })
        {
            (int expected, int actual, VmInstance instance) = RunBoth(function, 20000, optimize);
            Assert.Equal(expected, actual);
            Assert.True(instance.GarbageCollections > 0, "expected at least one collection");
            Assert.True(instance.HeapBytes < VmHeap.LimitBytes, "garbage must be reclaimed");
        }
    }

    private static (int Expected, int Actual, VmInstance Instance) RunBoth(string function, int arg, bool optimize)
    {
        byte[] assembly = CartridgeCompiler.Compile(Source, optimize);

        var context = new AssemblyLoadContext(null, isCollectible: true);
        MethodInfo method = context.LoadFromStream(new MemoryStream(assembly)).GetType("Fns")!.GetMethod(function)!;
        int expected = (int)method.Invoke(null, [arg])!;

        VmModule module = IlTranslator.Translate(assembly);
        int index = module.Functions.ToList().FindIndex(f => f.Name == $"Fns.{function}");
        var instance = new VmInstance(Verifier.Verify(module), new SyscallTable());
        int actual = (int)instance.Invoke(index, [arg]);
        return (expected, actual, instance);
    }
}

public class HeapTrapTests
{
    private static RuntimeSession Start(string tick) => CartridgeCompiler.Start($$"""
        using ZTX16;
        public sealed class Thing { public int V; }
        public class Animal { } public sealed class Cat : Animal { } public sealed class Dog : Animal { }
        public sealed class Game : ICartridge
        {
            Thing? thing;
            int[] data = new int[4];
            public void Initialize() { }
            public void Render() { }
            public void Tick() { {{tick}} }
        }
        """, optimize: true);

    [Theory]
    [InlineData("Graphics.Clear(thing!.V);", "Null reference")]
    [InlineData("Graphics.Clear(data[4]);", "out of range")]
    [InlineData("Animal a = new Cat(); Dog d = (Dog)a; Graphics.Clear(d == null ? 0 : 1);", "Invalid cast")]
    [InlineData("int n = -1; var big = new int[n];", "Negative array length")]
    public void RuntimeFaultsTrap(string tick, string expected)
    {
        RuntimeSession session = Start(tick);
        var e = Assert.Throws<VmTrapException>(() => session.RunTick(TimeSpan.Zero));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void ExhaustingTheHeapTraps()
    {
        RuntimeSession session = Start("Thing[][] keep = new Thing[100000][]; for (int i = 0; i < keep.Length; i++) keep[i] = new Thing[1000];");
        var e = Assert.Throws<VmTrapException>(() => session.RunTick(TimeSpan.Zero));
        Assert.Contains("Out of memory", e.Message);
    }

    [Fact]
    public void GarbageFromEveryTickIsReclaimed()
    {
        RuntimeSession session = Start("for (int i = 0; i < 200; i++) { var tmp = new Thing[64]; tmp[0] = new Thing(); }");
        for (int tick = 0; tick < 200; tick++)
        {
            session.RunTick(TimeSpan.Zero); // 200 ticks × 200 × ~600 B ≈ 24 MB allocated in total
        }
    }
}
