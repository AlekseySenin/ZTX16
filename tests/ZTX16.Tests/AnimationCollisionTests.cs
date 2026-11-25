using System.Reflection;
using System.Runtime.Loader;
using ZTX16.Cartridge;
using ZTX16.Platform.Execution;
using ZTX16.Platform.Runtime;
using ZTX16.Toolchain;
using ZTX16.VM;

namespace ZTX16.Tests;

public class AnimationFormatTests
{
    [Fact]
    public void JsonRoundTrips()
    {
        AnimationDefinition animation = AnimationFormat.ParseJson("""
            { "frames": [ { "duration": 6, "tiles": [[0, 64], [1, 65]] }, { "duration": 3, "tiles": [[0, 66]] }, { "duration": 1 } ] }
            """, "walk.anim");
        Assert.Equal(3, animation.Frames.Length);
        Assert.Equal([0, 1], animation.Frames[0].SpriteTiles);
        Assert.Equal([64, 65], animation.Frames[0].TileIds);
        Assert.Empty(animation.Frames[2].SpriteTiles);

        AnimationDefinition again = AnimationFormat.ParseJson(AnimationFormat.FormatJson(animation), "again.anim");
        Assert.Equal(animation.Frames.Select(f => f.Duration), again.Frames.Select(f => f.Duration));
        Assert.Equal(animation.Frames[1].TileIds, AnimationFormat.Decode(AnimationFormat.Encode(animation), "walk").Frames[1].TileIds);
    }

    [Theory]
    [InlineData("""{ "frames": [] }""", "non-empty")]
    [InlineData("""{ "frames": [ { "duration": 0 } ] }""", "at least 1 tick")]
    [InlineData("""{ "frames": [ { "duration": 1, "tiles": [[0]] } ] }""", "pairs")]
    [InlineData("""{ "frames": [ { "duration": 1, "tiles": [[0, 5000]] } ] }""", "out of range")]
    public void InvalidAnimationsAreRejected(string json, string expected)
    {
        var e = Assert.Throws<ProjectException>(() => AnimationFormat.ParseJson(json, "bad.anim"));
        Assert.Contains(expected, e.Message);
    }
}

public class AnimationPlaybackTests
{
    // Each tick the cartridge writes: x=0 → frame + 1, x=1 → tile of sprite index 0, x=2 → flags (1 started, 2 looped, 4 finished),
    // x=3 → 1 while playing.
    private static RuntimeSession Start(string setup)
    {
        using var dir = new TempDirectory();
        dir.Write(ProjectConfig.FileName, """{ "name": "Anim", "gameId": "dev.test.anim", "directories": { "code": "code", "tilemaps": "tilemaps", "sprites": "sprites", "animations": "animations" } }""");
        File.WriteAllBytes(Path.Combine(dir.Path, "tilemaps", "art.png").EnsureDirectory(), TileArt.Png());
        dir.Write("sprites/hero.sprite", """{ "tilemap": "art", "width": 2, "tiles": [0, 0] }""");
        dir.Write("animations/blink.anim", """{ "frames": [ { "duration": 2, "tiles": [[0, 5]] }, { "duration": 3, "tiles": [[0, 6]] } ] }""");
        dir.Write("code/Game.cs", $$"""
            using ZTX16;
            public sealed class Game : ICartridge
            {
                private AnimationPlayback playback = null!;

                public void Initialize()
                {
                    playback = new AnimationPlayback(Animation.Load("blink"), Sprite.Load("hero"));
                    {{setup}}
                }

                public void Tick() { playback.Update(); }

                public void Render()
                {
                    Graphics.SetPixel(0, 0, 0, playback.Frame + 1);
                    Graphics.SetPixel(0, 1, 0, playback.Sprite.GetTile(0, 0));
                    Graphics.SetPixel(0, 2, 0, (playback.Started ? 1 : 0) | (playback.Looped ? 2 : 0) | (playback.Finished ? 4 : 0));
                    Graphics.SetPixel(0, 3, 0, playback.IsPlaying ? 1 : 0);
                }
            }
            """);

        CartridgeContent cartridge = ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory);
        var session = new RuntimeSession(VmProgram.Load(cartridge.Executable), 48000, new ResourceStore(cartridge.Resources));
        session.Initialize();
        return session;
    }

    private static (int Frame, int Tile, int Flags, int Playing) Step(RuntimeSession session)
    {
        session.RunTick(TimeSpan.Zero);
        var layer = session.Graphics.Layers[0];
        return (layer.GetPixel(0, 0) - 1, layer.GetPixel(1, 0), layer.GetPixel(2, 0), layer.GetPixel(3, 0));
    }

    [Fact]
    public void LoopAdvancesByFrameDurationAndWraps()
    {
        RuntimeSession session = Start("playback.Play();");
        var ticks = Enumerable.Range(0, 6).Select(_ => Step(session)).ToArray();

        // Durations 2 and 3: frame 0 for ticks 1, frame 1 from tick 2 to 4, back to frame 0 at tick 5.
        Assert.Equal([0, 1, 1, 1, 0, 0], ticks.Select(t => t.Frame));
        Assert.Equal([5, 6, 6, 6, 5, 5], ticks.Select(t => t.Tile));
        Assert.Equal(1, ticks[0].Flags);          // Started
        Assert.Equal(2, ticks[4].Flags);          // Looped
        Assert.All(ticks, t => Assert.Equal(1, t.Playing));
    }

    [Fact]
    public void OneShotStopsOnTheLastFrame()
    {
        RuntimeSession session = Start("playback.Mode = PlaybackMode.OneShot; playback.Play();");
        var ticks = Enumerable.Range(0, 7).Select(_ => Step(session)).ToArray();

        Assert.Equal([0, 1, 1, 1, 1, 1, 1], ticks.Select(t => t.Frame));
        Assert.Equal(4, ticks[4].Flags);          // Finished exactly once
        Assert.Equal(0, ticks[5].Flags);
        Assert.Equal(0, ticks[6].Playing);
        Assert.Equal(6, ticks[6].Tile);           // keeps the final sprite state
    }

    [Fact]
    public void PlaySpeedScalesTime()
    {
        RuntimeSession session = Start("playback.PlaySpeed = 0.5f; playback.Play();");
        var ticks = Enumerable.Range(0, 5).Select(_ => Step(session)).ToArray();
        Assert.Equal([0, 0, 0, 1, 1], ticks.Select(t => t.Frame)); // the 2-tick frame lasts 4 ticks
    }

    [Fact]
    public void NotPlayingLeavesTheSpriteAlone()
    {
        RuntimeSession session = Start("");
        var tick = Step(session);
        Assert.Equal((0, 0, 0, 0), tick);
    }
}

/// <summary>Differential tests: collision queries run natively and in the VM and must agree.</summary>
public class CollisionSemanticsTests
{
    private const string Source = """
        using ZTX16;
        using ZTX16.Collections;

        public static class Fns
        {
            static ColliderSet World(out Collider mover)
            {
                var world = new ColliderSet();
                world.Add(new Collider(100, 0, 10, 200));    // wall to the right
                world.Add(new Collider(0, 100, 90, 10));     // ceiling segment
                world.Add(new Collider(-20, 0, 10, 50));     // wall to the left
                var off = new Collider(60, 60, 5, 5);
                off.Enabled = false;                         // disabled: never hit
                world.Add(off);
                mover = new Collider(40, 40, 16, 16);
                world.Add(mover);                            // in the set itself: never hits itself
                return world;
            }

            static int Encode(CollisionHit hit, ColliderSet world)
            {
                int index = -1;
                for (int i = 0; i < world.Count; i++) if (world[i] == hit.Collider) index = i;
                int h = hit.Hit ? 1 : 0;
                h = h * 31 + index;
                h = h * 31 + hit.NormalX;
                h = h * 31 + hit.NormalY;
                h = h * 31 + hit.SafeX;
                h = h * 31 + hit.SafeY;
                h = h * 31 + hit.PointX;
                h = h * 31 + hit.PointY;
                return h;
            }

            public static int Move(int targetX, int targetY)
            {
                ColliderSet world = World(out Collider mover);
                return Encode(Collision.Move(mover, targetX, targetY, world), world);
            }

            public static int MoveFrom(int x, int y, int targetX, int targetY)
            {
                ColliderSet world = World(out Collider mover);
                mover.SetPosition(x, y);
                return Encode(Collision.Move(mover, targetX, targetY, world), world);
            }

            public static int Queries(int n)
            {
                ColliderSet world = World(out Collider mover);
                var a = new Collider(0, 0, 10, 10);
                var b = new Collider(10, 0, 10, 10);        // touching a: no overlap
                var c = new Collider(9, 9, 5, 5);           // overlaps a
                var results = new List<Collider>();
                int count = Collision.Overlapping(95, 95, 20, 20, world, results);
                int r = (Collision.Overlaps(a, b) ? 1 : 0) + (Collision.Overlaps(a, c) ? 2 : 0)
                    + (Collision.Contains(a, 9, 9) ? 4 : 0) + (Collision.Contains(a, 10, 9) ? 8 : 0)
                    + count * 16 + results.Count * 256 + Collision.Overlapping(mover, world) * 4096;
                c.Enabled = false;
                return r + (Collision.Overlaps(a, c) ? 100000 : 0) + n;
            }
        }

        public sealed class Game : ICartridge
        {
            static int sink;
            public void Initialize() { sink = Fns.Move(0, 0) + Fns.MoveFrom(0, 0, 0, 0) + Fns.Queries(0); }
            public void Tick() { }
            public void Render() { }
        }
        """;

    public static TheoryData<string, int[]> Cases => new()
    {
        { "Move", [150, 40] },      // right into the wall: stops at x = 84
        { "Move", [150, 70] },      // diagonal right-up hits the wall first
        { "Move", [40, 150] },      // up into the ceiling
        { "Move", [-100, 40] },     // left into the left wall
        { "Move", [60, 60] },       // short move: no hit (disabled collider ignored)
        { "Move", [84, 40] },       // ends exactly touching the wall: no hit
        { "Move", [120, 120] },     // corner region: earliest of wall and ceiling
        { "Move", [40, 40] },       // no motion
        { "MoveFrom", [95, 40, 200, 40] },   // starts overlapping the wall
        { "MoveFrom", [0, 120, 200, 120] },  // above the ceiling, into the wall
        { "MoveFrom", [200, 40, 0, 40] },    // from beyond the wall, moving left
        { "Queries", [0] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void VmMatchesDotNet(string function, int[] args)
    {
        foreach (bool optimize in new[] { false, true })
        {
            byte[] assembly = CartridgeCompiler.Compile(Source, optimize);
            var context = new AssemblyLoadContext(null, isCollectible: true);
            MethodInfo method = context.LoadFromStream(new MemoryStream(assembly)).GetType("Fns")!.GetMethod(function)!;
            int expected = (int)method.Invoke(null, args.Cast<object>().ToArray())!;

            VmModule module = IlTranslator.Translate(assembly);
            int index = module.Functions.ToList().FindIndex(f => f.Name == $"Fns.{function}");
            var instance = new VmInstance(Verifier.Verify(module), new SyscallTable());
            Assert.Equal(expected, (int)instance.Invoke(index, args.Select(a => (long)a).ToArray()));
        }
    }

    [Fact]
    public void MovingIntoAWallStopsTouchingIt()
    {
        // Checked natively against hand-computed values (the differential test then covers the VM).
        byte[] assembly = CartridgeCompiler.Compile(Source.Replace("public static int Move(", "public static CollisionHit Raw(int targetX, int targetY) { ColliderSet world = World(out Collider mover); return Collision.Move(mover, targetX, targetY, world); }\n            public static int Move("), optimize: true);
        var context = new AssemblyLoadContext(null, isCollectible: true);
        MethodInfo raw = context.LoadFromStream(new MemoryStream(assembly)).GetType("Fns")!.GetMethod("Raw")!;
        object hit = raw.Invoke(null, [150, 40])!;
        Type t = hit.GetType();
        Assert.True((bool)t.GetField("Hit")!.GetValue(hit)!);
        Assert.Equal(84, t.GetField("SafeX")!.GetValue(hit));       // 100 - 16
        Assert.Equal(40, t.GetField("SafeY")!.GetValue(hit));
        Assert.Equal(-1, t.GetField("NormalX")!.GetValue(hit));
        Assert.Equal(100, t.GetField("PointX")!.GetValue(hit));
    }
}

public class CollisionDebugTests
{
    [Fact]
    public void DrawsOutlinesOfEnabledCollidersOnly()
    {
        RuntimeSession session = CartridgeCompiler.Start("""
            using ZTX16;
            public sealed class Game : ICartridge
            {
                public void Initialize()
                {
                    var set = new ColliderSet();
                    set.Add(new Collider(10, 10, 5, 4));
                    var hidden = new Collider(50, 50, 5, 5);
                    hidden.Enabled = false;
                    set.Add(hidden);
                    CollisionDebug.Draw(1, set, 17);
                }
                public void Tick() { }
                public void Render() { }
            }
            """, optimize: true);

        var layer = session.Graphics.Layers[1];
        Assert.Equal(17, layer.GetPixel(10, 10));
        Assert.Equal(17, layer.GetPixel(14, 13));
        Assert.Equal(0, layer.GetPixel(12, 11));    // inside stays empty
        Assert.Equal(0, layer.GetPixel(50, 50));    // disabled collider not drawn
    }
}
