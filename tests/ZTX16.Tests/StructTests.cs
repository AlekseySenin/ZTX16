using System.Reflection;
using System.Runtime.Loader;
using ZTX16.Toolchain;
using ZTX16.VM;

namespace ZTX16.Tests;

/// <summary>
/// Differential tests for structs (value semantics) and ref/out/in parameters, on .NET and in
/// the VM (Debug and Release compilation).
/// </summary>
public class StructSemanticsTests
{
    private const string Source = """
        using ZTX16;
        using ZTX16.Collections;

        public struct Vec
        {
            public int X, Y;
            public Vec(int x, int y) { X = x; Y = y; }
            public int Dot(Vec o) => X * o.X + Y * o.Y;
            public void Scale(int k) { X *= k; Y *= k; }
            public Vec Plus(Vec o) => new Vec(X + o.X, Y + o.Y);
            public static Vec operator +(Vec a, Vec b) => new Vec(a.X + b.X, a.Y + b.Y);
            public static bool operator ==(Vec a, Vec b) => a.X == b.X && a.Y == b.Y;
            public static bool operator !=(Vec a, Vec b) => !(a == b);
        }

        public struct Rect { public Vec Min, Max; public int Area => (Max.X - Min.X) * (Max.Y - Min.Y); }

        public struct Fancy
        {
            public byte Small;
            public long Big;
            public float F;
            public string Name;
            public Vec Inner;
        }

        public sealed class Body { public Vec Position; public Vec Velocity; public Rect Bounds; }

        public static class Fns
        {
            public static int CopySemantics(int n)
            {
                Vec a = new Vec(n, 2);
                Vec b = a;          // copy
                b.X = 100;          // must not change a
                Vec c = default;    // all zero
                c.Y += 7;
                return a.X * 1000 + b.X * 10 + c.X + c.Y * 100000;
            }

            public static int MethodsAndOperators(int n)
            {
                var a = new Vec(n, 3);
                var b = new Vec(2, n);
                a.Scale(2);                       // mutates a through 'this'
                Vec s = a + b;
                Vec p = a.Plus(b);
                return a.Dot(b) + s.X * 100 + (s == p ? 1 : 0) + (s != a ? 10 : 0);
            }

            public static int NestedStructs(int n)
            {
                Rect r = default;
                r.Min.X = 1; r.Min.Y = 2;
                r.Max = new Vec(n, n + 3);
                Rect copy = r;
                copy.Max.X = 0;                    // nested copy is independent
                return r.Area * 100 + copy.Max.X + r.Max.X * 10000;
            }

            public static int StructsInClasses(int n)
            {
                var body = new Body();
                body.Velocity = new Vec(1, n);
                for (int i = 0; i < 5; i++) body.Position += body.Velocity;
                body.Bounds.Max.Y = 9;             // deep field write through addresses
                Vec snapshot = body.Position;
                body.Position.X = -1;              // snapshot must keep its value
                return snapshot.X * 1000 + snapshot.Y + body.Bounds.Max.Y * 100000 + body.Position.X;
            }

            public static int ArraysOfStructs(int n)
            {
                var points = new Vec[n];
                for (int i = 0; i < n; i++) { points[i].X = i; points[i].Y = i * i; }
                Vec first = points[0];
                points[0] = new Vec(50, 60);       // first must not change
                Vec tmp = points[1];
                tmp.X = 999;                        // element must not change
                int sum = 0;
                foreach (Vec v in points) sum += v.X + v.Y;
                return sum * 10 + first.X + points[1].X;
            }

            public static int MixedFields(int n)
            {
                Fancy f = default;
                f.Small = (byte)(n + 250);           // wraps as a byte
                f.Big = (long)n << 40;
                f.F = n * 0.5f;
                f.Name = "fancy";
                f.Inner.X = n;
                Fancy g = f;
                g.Inner.X = 0;
                g.Name = "other";
                return f.Small + (int)(f.Big >> 40) + (int)(f.F * 4) + f.Name.Length * 1000 + f.Inner.X * 100 + g.Inner.X + g.Name.Length;
            }

            public static int GenericsOfStructs(int n)
            {
                var list = new List<Vec>();
                for (int i = 0; i < n; i++) list.Add(new Vec(i, -i));
                Vec v = list[2];
                v.X = 1000;                          // list element must not change
                var map = new Dictionary<Vec, int>();
                map[new Vec(1, 2)] = 5;
                map[new Vec(1, 2)] = 6;              // equal by value → same key
                map[new Vec(2, 1)] = 7;
                return list[2].X * 100 + map.Count * 10 + map[new Vec(1, 2)] + (list.Contains(new Vec(3, -3)) ? 1000 : 0);
            }

            // ----- ref / out / in -----

            static void Swap(ref int a, ref int b) { int t = a; a = b; b = t; }
            static bool TryHalf(int value, out int half) { if ((value & 1) != 0) { half = 0; return false; } half = value / 2; return true; }
            static void Grow(ref Vec v, int by) { v.X += by; v.Y += by; }
            static void Reset(out Vec v) { v = new Vec(-1, -1); }
            static int Read(in Vec v) => v.X + v.Y;
            static void Bump(ref long x) { x += 10; }
            static void SetByte(ref byte b) { b = 255; }
            static void ChangeCopy(Vec v) { v.X = 12345; }              // by value: caller unaffected
            static void Reassign(Vec v, int x) { v = new Vec(x, x); }   // by value: caller unaffected

            public static int RefParameters(int n)
            {
                int a = n, b = 2;
                Swap(ref a, ref b);
                int ok = TryHalf(n, out int half) ? 1 : 0;
                Vec v = new Vec(1, 1);
                Grow(ref v, n);
                Reset(out Vec w);
                ChangeCopy(v);
                Reassign(v, 777);
                return a * 1000000 + b * 10000 + half * 100 + ok * 10 + Read(in v) + w.X;
            }

            public static int RefToFieldsAndElements(int n)
            {
                var body = new Body();
                Grow(ref body.Velocity, n);
                var numbers = new int[3];
                Swap(ref numbers[0], ref numbers[2]);
                numbers[2] = 5;
                Swap(ref numbers[0], ref numbers[2]);
                long big = 1;
                Bump(ref big);
                var bytes = new byte[2];
                SetByte(ref bytes[1]);
                byte local = 0;
                SetByte(ref local);
                var vecs = new Vec[2];
                Grow(ref vecs[1], 3);
                return body.Velocity.X * 1000000 + numbers[0] * 10000 + (int)big * 100 + bytes[1] + local + vecs[1].Y * 10;
            }

            static int counter;
            static Vec origin;

            public static int RefToStatics(int n)
            {
                counter = n;
                Swap(ref counter, ref n);
                Grow(ref origin, 4);
                return counter * 100 + n + origin.X * 10000;
            }

            public static int RefLocals(int n)
            {
                var arr = new int[] { 1, 2, 3 };
                ref int slot = ref arr[1];
                slot = n;
                slot += 1;
                var vecs = new Vec[] { new Vec(1, 1), new Vec(2, 2) };
                ref Vec target = ref vecs[0];
                target.X = 40;
                target = new Vec(7, 8);
                return arr[1] * 1000 + vecs[0].X * 10 + vecs[0].Y;
            }
        }

        public sealed class Game : ICartridge
        {
            static int sink;

            public void Initialize()
            {
                sink = Fns.CopySemantics(1) + Fns.MethodsAndOperators(1) + Fns.NestedStructs(1) + Fns.StructsInClasses(1)
                    + Fns.ArraysOfStructs(3) + Fns.MixedFields(1) + Fns.GenericsOfStructs(4) + Fns.RefParameters(4)
                    + Fns.RefToFieldsAndElements(1) + Fns.RefToStatics(1) + Fns.RefLocals(1);
            }

            public void Tick() { }
            public void Render() { }
        }
        """;

    public static TheoryData<string, int> Cases => new()
    {
        { "CopySemantics", 7 },
        { "MethodsAndOperators", 5 },
        { "NestedStructs", 6 },
        { "StructsInClasses", 3 },
        { "ArraysOfStructs", 10 },
        { "MixedFields", 9 },
        { "GenericsOfStructs", 6 },
        { "RefParameters", 8 }, { "RefParameters", 7 },
        { "RefToFieldsAndElements", 4 },
        { "RefToStatics", 6 },
        { "RefLocals", 9 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void VmMatchesDotNet(string function, int arg)
    {
        foreach (bool optimize in new[] { false, true })
        {
            byte[] assembly = CartridgeCompiler.Compile(Source, optimize);

            var context = new AssemblyLoadContext(null, isCollectible: true);
            MethodInfo method = context.LoadFromStream(new MemoryStream(assembly)).GetType("Fns")!.GetMethod(function)!;
            int expected = (int)method.Invoke(null, [arg])!;

            VmModule module = IlTranslator.Translate(assembly);
            int index = module.Functions.ToList().FindIndex(f => f.Name == $"Fns.{function}");
            var instance = new VmInstance(Verifier.Verify(module), new SyscallTable());
            Assert.Equal(expected, (int)instance.Invoke(index, [arg]));
        }
    }

    [Fact]
    public void StructCopiesSurviveGarbageCollection()
    {
        const string source = """
            using ZTX16;
            public struct Inner { public int A; public string Text; }
            public struct Outer { public Inner In; public int B; }
            public static class Fns
            {
                public static int Churn(int n)
                {
                    var keep = new Outer[4];
                    for (int i = 0; i < n; i++)
                    {
                        Outer o = default;
                        o.In.A = i;
                        o.In.Text = "t" + i;
                        o.B = -i;
                        keep[i % 4] = o;               // copies nested values while garbage accumulates
                    }

                    int sum = 0;
                    foreach (Outer o in keep) sum += o.In.A + o.B + o.In.Text.Length;
                    return sum;
                }
            }
            public sealed class Game : ICartridge
            {
                public void Initialize() { Graphics.Clear(Fns.Churn(1)); }
                public void Tick() { }
                public void Render() { }
            }
            """;

        VmModule module = CartridgeCompiler.Translate(source, optimize: true);
        int index = module.Functions.ToList().FindIndex(f => f.Name == "Fns.Churn");
        var syscalls = new SyscallTable().Add("graphics.clear", VmSignature.Of(null, VmType.I32), _ => 0);
        var instance = new VmInstance(Verifier.Verify(module), syscalls);
        long result = instance.Invoke(index, [50000]);

        Assert.True(instance.GarbageCollections > 0);
        // Last four iterations: i = 49996..49999 → A + B = 0, Text = "t" + 5 digits = 6 chars each.
        Assert.Equal(24, result);
    }
}
