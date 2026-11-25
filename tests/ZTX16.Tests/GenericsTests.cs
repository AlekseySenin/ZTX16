using System.Reflection;
using System.Runtime.Loader;
using ZTX16.Platform.Runtime;
using ZTX16.Toolchain;
using ZTX16.VM;

namespace ZTX16.Tests;

/// <summary>
/// Differential tests for generics and ZTX16.Collections, on .NET and in the VM
/// (Debug and Release compilation).
/// </summary>
public class GenericsSemanticsTests
{
    private const string Source = """
        using ZTX16;
        using ZTX16.Collections;

        public sealed class Box<T>
        {
            public T Value;
            public Box(T value) { Value = value; }
            public Box<T> With(T value) => new Box<T>(value);
        }

        public sealed class Pair<A, B>
        {
            public A First; public B Second;
            public Pair(A first, B second) { First = first; Second = second; }
            public Pair<B, A> Swap() => new Pair<B, A>(Second, First);
        }

        public interface IScorer<T> { int Score(T item); }
        public sealed class LengthScorer : IScorer<string> { public int Score(string item) => item.Length; }
        public sealed class DoubleScorer : IScorer<int> { public int Score(int item) => item * 2; }

        public class Animal { public virtual int Legs() => 4; }
        public sealed class Bird : Animal { public override int Legs() => 2; }

        public static class Util
        {
            public static T Pick<T>(bool first, T a, T b) => first ? a : b;
            public static int Sum<T>(List<T> items, IScorer<T> scorer)
            {
                int total = 0;
                foreach (T item in items) total += scorer.Score(item);
                return total;
            }
            public static T Last<T>(T[] items) => items[items.Length - 1];
        }

        public static class Static<T> { public static int Counter; }

        public static class Fns
        {
            public static int Boxes(int n)
            {
                var a = new Box<int>(n);
                var b = new Box<string>("ztx");
                var c = new Box<Box<long>>(new Box<long>(7_000_000_000L));
                var d = a.With(n * 2);
                return a.Value + d.Value + b.Value.Length + (int)(c.Value.Value / 1_000_000_000L);
            }

            public static int Pairs(int n)
            {
                var p = new Pair<int, string>(n, "abc").Swap();
                return p.First.Length * 100 + p.Second;
            }

            public static int GenericMethods(int n)
            {
                int x = Util.Pick(n > 0, 10, 20);
                string s = Util.Pick(n > 0, "yes", "no");
                double d = Util.Last(new double[] { 1.5, 2.5, n });
                return x + s.Length + (int)(d * 10);
            }

            public static int GenericInterfaces(int n)
            {
                var words = new List<string> { "a", "bb", "ccc" };
                var numbers = new List<int>();
                for (int i = 0; i < n; i++) numbers.Add(i);
                return Util.Sum(words, new LengthScorer()) * 1000 + Util.Sum(numbers, new DoubleScorer());
            }

            public static int StaticPerInstantiation(int n)
            {
                Static<int>.Counter += n;
                Static<string>.Counter += 1;
                Static<int>.Counter += n;
                return Static<int>.Counter * 10 + Static<string>.Counter;
            }

            public static int Lists(int n)
            {
                var list = new List<int>();
                for (int i = 0; i < n; i++) list.Add(i * 3);
                list.Insert(0, -1);
                list.RemoveAt(2);
                list.Remove(9);
                list[1] = 100;
                list.Reverse();
                int sum = 0;
                foreach (int v in list) sum += v;
                int[] array = list.ToArray();
                return sum * 1000 + list.Count * 10 + (list.Contains(100) ? 1 : 0) + list.IndexOf(-1) + array.Length;
            }

            public static int ListOfObjects(int n)
            {
                var animals = new List<Animal>();
                for (int i = 0; i < n; i++) animals.Add(i % 2 == 0 ? new Animal() : new Bird());
                int legs = 0;
                for (int i = 0; i < animals.Count; i++) legs += animals[i].Legs();
                var bird = animals[1];
                animals.Remove(bird);
                return legs * 100 + animals.Count + (animals.Contains(bird) ? 1000 : 0);
            }

            public static int Dictionaries(int n)
            {
                var ages = new Dictionary<string, int>();
                ages["ann"] = 30;
                ages.Add("bob", 25);
                ages["ann"] = 31;
                var squares = new Dictionary<int, long>();
                for (int i = 0; i < n; i++) squares[i] = (long)i * i;
                squares.Remove(3);
                squares.Remove(4);
                squares[1000] = 7;
                long total = 0;
                foreach (int key in squares.Keys) total += squares[key];
                int keyOrder = 0;
                foreach (int key in squares.Keys) keyOrder = keyOrder * 31 + key;
                return (int)(total % 100000) + ages["ann"] + ages.GetValueOrDefault("zed", -5) + squares.Count * 7
                    + (ages.ContainsKey("bob") ? 1 : 0) + keyOrder % 1000;
            }

            public static int DictionaryOfObjects(int n)
            {
                var a = new Animal();
                var b = new Animal();
                var map = new Dictionary<Animal, string>();
                map[a] = "first";
                map[b] = "second";
                map[a] = "again";
                return map[a].Length * 10 + map[b].Length + map.Count * 100 + n;
            }

            public static int Sets(int n)
            {
                var set = new HashSet<int>();
                int added = 0;
                for (int i = 0; i < n; i++) if (set.Add(i % 7)) added++;
                set.Remove(3);
                return added * 100 + set.Count * 10 + (set.Contains(3) ? 1 : 0) + (set.Contains(6) ? 2 : 0);
            }

            public static int Texts(int n)
            {
                string s = n.ToString() + "|" + (n * -1_000_000_000L).ToString() + "|" + ('a' + n % 26).ToString() + "|" + ((char)('a' + n % 26)).ToString() + (n > 0).ToString() + ((uint)n * 3u).ToString();
                int h = s.Length;
                foreach (char c in s) h = h * 31 + c;
                return h;
            }

            public static int StacksAndQueues(int n)
            {
                var stack = new Stack<int>();
                var queue = new Queue<string>();
                for (int i = 0; i < n; i++) { stack.Push(i); queue.Enqueue("q" + i); }
                int s = 0;
                while (stack.Count > 2) s = s * 3 + stack.Pop();
                string first = queue.Dequeue();
                queue.Enqueue("tail");
                int len = 0;
                while (queue.Count > 0) len += queue.Dequeue().Length;
                return (s % 100000) + first.Length * 7 + len + stack.Peek();
            }
        }

        public sealed class Game : ICartridge
        {
            static int sink;

            public void Initialize()
            {
                sink = Fns.Boxes(1) + Fns.Pairs(1) + Fns.GenericMethods(1) + Fns.GenericInterfaces(1)
                    + Fns.StaticPerInstantiation(1) + Fns.Lists(1) + Fns.ListOfObjects(2) + Fns.Dictionaries(5)
                    + Fns.DictionaryOfObjects(1) + Fns.Sets(1) + Fns.StacksAndQueues(3) + Fns.Texts(1);
            }

            public void Tick() { }
            public void Render() { }
        }
        """;

    public static TheoryData<string, int> Cases => new()
    {
        { "Boxes", 5 }, { "Pairs", 9 }, { "GenericMethods", 1 }, { "GenericMethods", -1 },
        { "GenericInterfaces", 10 }, { "StaticPerInstantiation", 4 },
        { "Lists", 10 }, { "Lists", 100 }, { "ListOfObjects", 9 },
        { "Dictionaries", 50 }, { "Dictionaries", 500 }, { "DictionaryOfObjects", 1 },
        { "Sets", 30 }, { "StacksAndQueues", 12 }, { "Texts", 7 }, { "Texts", -123 },
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
            // Static<T>.Counter lives in globals initialized by static constructors; run them first.
            Assert.Equal(expected, (int)instance.Invoke(index, [arg]));
        }
    }

    [Fact]
    public void EachInstantiationIsTranslatedOnce()
    {
        VmModule module = IlTranslator.Translate(CartridgeCompiler.Compile(Source, optimize: true));
        string[] names = module.Functions.Select(f => f.Name).ToArray();

        Assert.Contains("ZTX16.Collections.List`1<Int32>.Add", names);
        Assert.Contains("ZTX16.Collections.List`1<string>.Add", names);
        Assert.Single(names, n => n == "ZTX16.Collections.List`1<Int32>.Add");
        Assert.DoesNotContain(names, n => n.Contains("!0", StringComparison.Ordinal));
    }
}

public class CollectionTrapTests
{
    private static RuntimeSession Start(string tick) => CartridgeCompiler.Start($$"""
        using ZTX16;
        using ZTX16.Collections;
        public sealed class Game : ICartridge
        {
            public void Initialize() { }
            public void Render() { }
            public void Tick() { {{tick}} }
        }
        """, optimize: true);

    [Theory]
    [InlineData("var l = new List<int>(); l.Add(1); Graphics.Clear(l[1]);", "List index out of range")]
    [InlineData("var d = new Dictionary<string, int>(); Graphics.Clear(d[\"missing\"]);", "key not found")]
    [InlineData("var d = new Dictionary<int, int>(); d.Add(1, 1); d.Add(1, 2);", "same key")]
    [InlineData("var s = new Stack<int>(); s.Pop();", "Stack is empty")]
    [InlineData("var q = new Queue<int>(); q.Dequeue();", "Queue is empty")]
    public void MisuseStopsTheCartridgeWithAClearMessage(string tick, string expected)
    {
        RuntimeSession session = Start(tick);
        var e = Assert.Throws<VmTrapException>(() => session.RunTick(TimeSpan.Zero));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void BclCollectionsPointToTheZtx16Equivalents()
    {
        var e = Assert.Throws<TranslationException>(() => CartridgeCompiler.Translate("""
            using ZTX16;
            public sealed class Game : ICartridge
            {
                public void Initialize() { var l = new System.Collections.Generic.List<int>(); l.Add(1); }
                public void Tick() { }
                public void Render() { }
            }
            """, optimize: true));
        Assert.Contains("ZTX16.Collections", e.Message);
    }
}
