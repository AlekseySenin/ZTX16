using ZTX16.Cartridge;
using ZTX16.Platform.Execution;
using ZTX16.Platform.Runtime;
using ZTX16.Toolchain;

namespace ZTX16.Tests;

public class LevelFormatTests
{
    private const string Sample = """
        { "layers": [
            { "name": "walls", "tilemap": "art", "width": 2, "height": 1, "actors": [
                { "id": "floor", "x": 0, "y": 0, "region": [0, 4, 32, 1], "collider": [0, 0, 256, 8] },
                { "id": "gem", "x": 40, "y": 40, "tile": 5, "rotation": 45, "pivot": [0.5, 0.5] },
                { "id": "hero", "x": 120, "y": 32, "sprite": "ship", "flipX": true, "scale": 2 },
                { "id": "exit", "x": 240, "y": 0, "collider": [0, 0, 16, 256] } ] },
            { "name": "sky", "actors": [] } ] }
        """;

    [Fact]
    public void JsonAndBinaryRoundTrip()
    {
        LevelDefinition level = LevelFormat.ParseJson(Sample, "stage.level");
        Assert.Equal(2, level.Layers.Length);
        LevelActor[] actors = level.Layers[0].Actors;
        Assert.Equal(LevelVisual.TileRegion, actors[0].Visual);
        Assert.Equal(new LevelCollider(0, 0, 256, 8), actors[0].Collider);
        Assert.Equal(45f, actors[1].Rotation);
        Assert.Equal((2f, 2f, true), (actors[2].ScaleX, actors[2].ScaleY, actors[2].FlipX));
        Assert.Equal(LevelVisual.None, actors[3].Visual);
        Assert.Null(level.Layers[1].Tilemap);

        string json = LevelFormat.FormatJson(level);
        Assert.Equal(json, LevelFormat.FormatJson(LevelFormat.ParseJson(json, "again.level")));
        Assert.Equal(json, LevelFormat.FormatJson(LevelFormat.Decode(LevelFormat.Encode(level), "stage")));
    }

    [Fact]
    public void StreamHasTheDocumentedLayout()
    {
        LevelStream stream = LevelFormat.ToStream(LevelFormat.ParseJson(Sample, "stage.level"));
        Assert.Equal(LevelFormat.Version, stream.Data[0]);
        Assert.Equal(2, stream.Data[1]);
        Assert.Equal(2 + 5 + (4 * LevelFormat.ActorStride) + 5, stream.Data.Length);
        Assert.Equal("walls", stream.Strings[stream.Data[2]]);
    }

    [Theory]
    [InlineData("""{ "layers": [] }""", "non-empty")]
    [InlineData("""{ "layers": [ { "name": "a", "width": 0 } ] }""", "screens")]
    [InlineData("""{ "layers": [ { "name": "a" }, { "name": "a" } ] }""", "unique")]
    [InlineData("""{ "layers": [ { "name": "a", "tilemap": "t", "actors": [ { "id": "x" }, { "id": "x" } ] } ] }""", "unique in the level")]
    [InlineData("""{ "layers": [ { "name": "a", "tilemap": "t", "actors": [ { "id": "x", "tile": 1, "sprite": "s" } ] } ] }""", "at most one")]
    [InlineData("""{ "layers": [ { "name": "a", "actors": [ { "id": "x", "tile": 1 } ] } ] }""", "name a tilemap")]
    [InlineData("""{ "layers": [ { "name": "a", "tilemap": "t", "actors": [ { "id": "x", "region": [30, 0, 4, 1] } ] } ] }""", "inside the 32")]
    [InlineData("""{ "layers": [ { "name": "a", "actors": [ { "id": "x", "collider": [0, 0, 0, 5] } ] } ] }""", "at least 1")]
    [InlineData("""{ "layers": [ { "name": "a", "actors": [ { "id": "x", "pivot": [1] } ] } ] }""", "array of 2")]
    public void InvalidLevelsAreRejected(string json, string expected)
    {
        var e = Assert.Throws<ProjectException>(() => LevelFormat.ParseJson(json, "bad.level"));
        Assert.Contains(expected, e.Message);
    }
}

public class LevelRuntimeTests
{
    private const string LevelJson = """
        { "layers": [
            { "name": "back", "tilemap": "art", "actors": [
                { "id": "one", "x": 0, "y": 0, "tile": 1 },
                { "id": "pair", "x": 16, "y": 0, "region": [0, 0, 2, 1], "collider": [0, 0, 16, 8] },
                { "id": "hero", "x": 40, "y": 0, "sprite": "hero" },
                { "id": "flipped", "x": 60, "y": 0, "tile": 0, "flipX": true },
                { "id": "trigger", "x": 100, "y": 100, "collider": [2, 3, 10, 10] } ] },
            { "name": "front", "tilemap": "art", "actors": [
                { "id": "cover", "x": 0, "y": 0, "tile": 0 } ] },
            { "name": "wide", "tilemap": "art", "width": 2, "actors": [
                { "id": "far", "x": 300, "y": 0, "tile": 1 } ] } ] }
        """;

    private static RuntimeSession Start(string initialize)
    {
        using var dir = new TempDirectory();
        dir.Write(ProjectConfig.FileName, """{ "name": "Lvl", "gameId": "dev.test.level", "directories": { "code": "code", "tilemaps": "tilemaps", "sprites": "sprites", "levels": "levels" } }""");
        File.WriteAllBytes(Path.Combine(dir.Path, "tilemaps", "art.png").EnsureDirectory(), TileArt.Png());
        dir.Write("sprites/hero.sprite", """{ "tilemap": "art", "width": 2, "tiles": [1, 0] }""");
        dir.Write("levels/stage.level", LevelJson);
        dir.Write("code/Game.cs", $$"""
            using ZTX16;
            public sealed class Game : ICartridge
            {
                public void Initialize()
                {
                    Level level = Level.Load("stage");
                    {{initialize}}
                }

                public void Tick() { }

                public void Render() { }
            }
            """);

        CartridgeContent cartridge = ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory);
        var session = new RuntimeSession(VmProgram.Load(cartridge.Executable), 48000, new ResourceStore(cartridge.Resources));
        session.Initialize();
        return session;
    }

    [Fact]
    public void RasterizesActorsInOrderWithTheirVisuals()
    {
        RuntimeSession session = Start("""level.FindLayer("back")!.Rasterize(0);""");
        var layer = session.Graphics.Layers[0];
        Assert.Equal(4, layer.GetPixel(0, 0));      // tile 1
        Assert.Equal(7, layer.GetPixel(16, 0));     // region: tile 0 with its marker
        Assert.Equal(4, layer.GetPixel(24, 0));     //         then tile 1
        Assert.Equal(4, layer.GetPixel(40, 0));     // sprite tiles [1, 0]
        Assert.Equal(7, layer.GetPixel(48, 0));
        Assert.Equal(3, layer.GetPixel(60, 0));     // flipped tile 0: marker moves right
        Assert.Equal(7, layer.GetPixel(67, 0));
        Assert.Equal(0, layer.GetPixel(100, 100));  // collider-only actor draws nothing
    }

    [Fact]
    public void LayersComposeIntoOneGraphicsLayerWithOffsets()
    {
        RuntimeSession session = Start("""
            level.GetLayer(0).Rasterize(0);
            level.GetLayer(1).Rasterize(0);
            level.GetLayer(1).Rasterize(2, 30, 20);
            """);
        Assert.Equal(7, session.Graphics.Layers[0].GetPixel(0, 0));   // "cover" drawn over "one"
        Assert.Equal(7, session.Graphics.Layers[2].GetPixel(30, 20));
        Assert.Equal(0, session.Graphics.Layers[2].GetPixel(0, 0));
        Assert.Empty(session.Machine.Warnings);
    }

    [Fact]
    public void ActorsAreRuntimeReferences()
    {
        RuntimeSession session = Start("""
            Actor trigger = level.FindActor("trigger")!;
            Graphics.SetPixel(1, 0, 0, trigger.Collider!.X == 102 ? 1 : 0);
            Graphics.SetPixel(1, 1, 0, level.Colliders.Count);                      // 2
            Graphics.SetPixel(1, 2, 0, level.GetLayer(0).Colliders.Count);          // 2
            Graphics.SetPixel(1, 3, 0, level.FindActor("nobody") == null ? 1 : 0);
            trigger.SetPosition(150, 100);
            Graphics.SetPixel(1, 4, 0, trigger.Collider.X == 152 && trigger.Collider.Y == 103 ? 1 : 0);
            trigger.Collider = null;
            Graphics.SetPixel(1, 5, 0, level.Colliders.Count);                      // 1

            Actor one = level.FindActor("one")!;
            one.SetPosition(8, 8);
            one.SetTile(0);
            level.FindActor("pair")!.Visible = false;
            level.FindActor("hero")!.ClearVisual();
            level.GetLayer(0).Rasterize(0);

            Level fresh = Level.Load("stage");
            Graphics.SetPixel(1, 6, 0, fresh.FindActor("one")!.X == 0 ? 1 : 0);   // the resource is unchanged
            Graphics.SetPixel(1, 7, 0, fresh.FindActor("trigger")!.Collider!.X == 102 ? 1 : 0);
            """);
        var results = session.Graphics.Layers[1];
        Assert.Equal([1, 2, 2, 1, 1, 1, 1, 1], Enumerable.Range(0, 8).Select(x => results.GetPixel(x, 0)));

        var layer = session.Graphics.Layers[0];
        Assert.Equal(7, layer.GetPixel(8, 8));      // moved and changed to tile 0
        Assert.Equal(0, layer.GetPixel(0, 0));
        Assert.Equal(0, layer.GetPixel(16, 0));     // hidden
        Assert.Equal(0, layer.GetPixel(40, 0));     // visual cleared
    }

    [Fact]
    public void OversizedLayerIsClippedWithOneWarning()
    {
        RuntimeSession session = Start("""
            level.FindLayer("wide")!.Rasterize(3);
            level.FindLayer("wide")!.Rasterize(3);
            """);
        string warning = Assert.Single(session.Machine.Warnings);
        Assert.Contains("'wide'", warning);
        Assert.Contains("clipped", warning);
    }

    [Fact]
    public void FitsAfterTheGraphicsLayerIsResized()
    {
        RuntimeSession session = Start("""
            Graphics.ResizeLayer(3, 2, 1);
            level.FindLayer("wide")!.Rasterize(3);
            """);
        Assert.Empty(session.Machine.Warnings);
        Assert.Equal(4, session.Graphics.Layers[3].GetPixel(300, 0));
    }

    [Fact]
    public void BuildRejectsMissingReferences()
    {
        using var dir = new TempDirectory();
        dir.Write(ProjectConfig.FileName, """{ "name": "Lvl", "gameId": "dev.test.level", "directories": { "code": "code", "tilemaps": "tilemaps", "levels": "levels" } }""");
        File.WriteAllBytes(Path.Combine(dir.Path, "tilemaps", "art.png").EnsureDirectory(), TileArt.Png());
        dir.Write("code/Game.cs", "using ZTX16; public sealed class Game : ICartridge { public void Initialize() { } public void Tick() { } public void Render() { } }");
        dir.Write("levels/stage.level", """{ "layers": [ { "name": "a", "tilemap": "art", "actors": [ { "id": "x", "sprite": "ghost" } ] } ] }""");
        var e = Assert.Throws<ProjectException>(() => ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory));
        Assert.Contains("'ghost'", e.Message);

        dir.Write("levels/stage.level", """{ "layers": [ { "name": "a", "tilemap": "nope" } ] }""");
        e = Assert.Throws<ProjectException>(() => ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory));
        Assert.Contains("'nope'", e.Message);
    }
}
