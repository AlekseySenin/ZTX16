using System.Buffers.Binary;
using System.IO.Compression;
using ZTX16.Cartridge;
using ZTX16.Platform.Execution;
using ZTX16.Platform.Graphics;
using ZTX16.Platform.Runtime;
using ZTX16.Toolchain;
using ZTX16.VM;

namespace ZTX16.Tests;

public class PngCodecTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryFilterTypeDecodes(int filter)
    {
        var random = new Random(filter + 7);
        byte[] indices = new byte[37 * 21];
        random.NextBytes(indices);
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] %= 32;
        }

        var image = new IndexedImage(37, 21, indices, new byte[96]);
        IndexedImage decoded = PngCodec.Decode(PngCodec.Encode(image, filter), "test.png");

        Assert.Equal(37, decoded.Width);
        Assert.Equal(21, decoded.Height);
        Assert.Equal(indices, decoded.Indices);
    }

    [Fact]
    public void FourBitImagesDecode()
    {
        // 3×2 pixels, 4 bits per pixel: rows "1 2 3" and "15 0 9" (each row padded to 2 bytes).
        byte[] png = HandmadePng(3, 2, bitDepth: 4, [0x00, 0x12, 0x30, 0x00, 0xF0, 0x90]);
        IndexedImage image = PngCodec.Decode(png, "small.png");
        Assert.Equal(new byte[] { 1, 2, 3, 15, 0, 9 }, image.Indices);
    }

    [Fact]
    public void RgbImagesAreRejectedWithAHint()
    {
        byte[] png = HandmadePng(1, 1, bitDepth: 8, [0, 1, 2, 3], colorType: 2);
        var e = Assert.Throws<ProjectException>(() => PngCodec.Decode(png, "rgb.png"));
        Assert.Contains("indexed", e.Message);
    }

    // Builds a PNG from raw (already filtered) scanlines.
    private static byte[] HandmadePng(int width, int height, int bitDepth, byte[] raw, int colorType = 3)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        Chunk(output, "IHDR", header);
        Chunk(output, "PLTE", new byte[48]);
        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(raw);
            }

            Chunk(output, "IDAT", compressed.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(System.Text.Encoding.ASCII.GetBytes(type));
        output.Write(data);
        output.Write(new byte[4]); // CRC is not checked by the decoder
    }
}

internal static class TileArt
{
    /// <summary>
    /// A 256×256 tilemap image (PNG row order) where tile 0 (lower-left) is color 3 with a color-7 marker at its
    /// lower-left pixel, tile 1 is color 4, and tile 2 holds an 'L' glyph in color 1.
    /// </summary>
    public static IndexedImage Image()
    {
        byte[] indices = new byte[256 * 256];
        void Set(int x, int y, byte color) => indices[((255 - y) * 256) + x] = color; // (x, y) in ZTX16 coordinates

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                Set(x, y, 3);
                Set(8 + x, y, 4);
            }
        }

        Set(0, 0, 7);
        for (int y = 1; y < 7; y++)
        {
            Set(16 + 1, y, 1); // vertical bar of the 'L'
        }

        for (int x = 1; x < 6; x++)
        {
            Set(16 + x, 1, 1); // foot of the 'L'
        }

        byte[] palette = new byte[96];
        for (int i = 0; i < 32; i++)
        {
            palette[i * 3] = (byte)(i * 8);
        }

        return new IndexedImage(256, 256, indices, palette);
    }

    public static byte[] Png() => PngCodec.Encode(Image());

    public static byte[] Pixels() => TilemapFormat.Pixels(TilemapFormat.FromPng(Png(), "art.png"), "art");
}

public class TilemapFormatTests
{
    [Fact]
    public void TileZeroIsTheLowerLeftTileOfTheImage()
    {
        byte[] pixels = TileArt.Pixels();
        Assert.Equal(7, pixels[0]);                 // marker at (0, 0)
        Assert.Equal(3, pixels[1]);
        Assert.Equal(4, pixels[8]);                 // tile 1 is to the right
        Assert.Equal(0, pixels[8 * 256]);           // tile 32 (above tile 0) is empty
    }

    [Fact]
    public void SmallImagesSitAtTheLowerLeft()
    {
        byte[] indices = new byte[16 * 8];
        indices[(7 * 16) + 0] = 5;                   // PNG bottom-left pixel
        byte[] data = TilemapFormat.FromPng(PngCodec.Encode(new IndexedImage(16, 8, indices, new byte[96])), "small.png");
        Assert.Equal(5, TilemapFormat.Pixels(data, "small")[0]);
    }

    [Fact]
    public void ExtractedPngMatchesTheSource()
    {
        byte[] data = TilemapFormat.FromPng(TileArt.Png(), "art.png");
        IndexedImage back = PngCodec.Decode(TilemapFormat.ToPng(data, "art"), "back.png");
        Assert.Equal(TileArt.Image().Indices, back.Indices);
        Assert.Equal(TileArt.Image().Palette, back.Palette);
    }

    [Fact]
    public void RejectsIndicesAbove31AndOddSizes()
    {
        byte[] big = new byte[8 * 8];
        big[3] = 40;
        var e = Assert.Throws<ProjectException>(() => TilemapFormat.FromPng(PngCodec.Encode(new IndexedImage(8, 8, big, new byte[96])), "big.png"));
        Assert.Contains("index 40", e.Message);

        Assert.Throws<ProjectException>(() => TilemapFormat.FromPng(PngCodec.Encode(new IndexedImage(10, 8, new byte[80], new byte[96])), "odd.png"));
    }
}

public class RasterizerTests
{
    // 4×2 source: value = 1 + u + 4v, so every pixel is identifiable.
    private static readonly PixelSource Source = (u, v) => 1 + u + (4 * v);

    private static GraphicsLayer Draw(int x, int y, DrawTransform t)
    {
        GraphicsLayer layer = new GraphicsSystem().Layers[0];
        Rasterizer.Draw(layer, 4, 2, Source, x, y, t);
        return layer;
    }

    [Fact]
    public void IdentityCopiesWithLowerLeftAtPosition()
    {
        GraphicsLayer layer = Draw(10, 20, DrawTransform.Identity);
        Assert.Equal(1, layer.GetPixel(10, 20));
        Assert.Equal(4, layer.GetPixel(13, 20));
        Assert.Equal(5, layer.GetPixel(10, 21));
        Assert.Equal(0, layer.GetPixel(14, 20));
    }

    [Fact]
    public void PivotLandsOnThePosition()
    {
        GraphicsLayer layer = Draw(10, 20, DrawTransform.Identity with { PivotX = 0.5f, PivotY = 0.5f });
        Assert.Equal(1, layer.GetPixel(8, 19));     // lower-left pixel moved by half the size
        Assert.Equal(8, layer.GetPixel(11, 20));    // upper-right pixel
    }

    [Fact]
    public void FlipsMirrorTheSource()
    {
        GraphicsLayer x = Draw(0, 0, DrawTransform.Identity with { FlipX = true });
        Assert.Equal(4, x.GetPixel(0, 0));
        GraphicsLayer y = Draw(0, 0, DrawTransform.Identity with { FlipY = true });
        Assert.Equal(5, y.GetPixel(0, 0));
    }

    [Fact]
    public void ScaleEnlargesPixels()
    {
        GraphicsLayer layer = Draw(0, 0, DrawTransform.Identity with { ScaleX = 2f, ScaleY = 3f });
        Assert.Equal(1, layer.GetPixel(1, 2));
        Assert.Equal(2, layer.GetPixel(2, 0));
        Assert.Equal(5, layer.GetPixel(0, 3));
        Assert.Equal(0, layer.GetPixel(8, 0));
    }

    [Fact]
    public void RotationIsCounterClockwiseAroundThePivot()
    {
        // 90° CCW around the lower-left pivot: source pixel (u, v) lands on (x - v - 1, y + u).
        GraphicsLayer layer = Draw(20, 20, DrawTransform.Identity with { Rotation = 90f });
        Assert.Equal(1, layer.GetPixel(19, 20));
        Assert.Equal(4, layer.GetPixel(19, 23));
        Assert.Equal(5, layer.GetPixel(18, 20));
    }

    [Fact]
    public void DrawingIsClippedToTheLayer()
    {
        GraphicsLayer layer = Draw(254, 255, DrawTransform.Identity with { Rotation = 45f, ScaleX = 3f, ScaleY = 3f });
        Assert.Equal(1, layer.GetPixel(254, 255));
    }

    [Fact]
    public void PixelOffsetShiftsInsideEachTileCyclically()
    {
        byte[] tilemap = TileArt.Pixels();
        PixelSource shifted = TileSources.Region(tilemap, 0, 0, offsetX: 1, offsetY: 0);
        Assert.Equal(7, shifted(1, 0));             // the marker moved one pixel right
        Assert.Equal(3, shifted(0, 0));             // and the tile's last column wrapped to the front
        PixelSource full = TileSources.Region(tilemap, 0, 0, offsetX: 8, offsetY: 0);
        Assert.Equal(7, full(0, 0));                // an offset of 8 changes nothing
    }
}

public class TextRendererTests
{
    private static GraphicsLayer Layer() => new GraphicsSystem().Layers[0];

    [Fact]
    public void SystemFontDrawsGlyphsAtTheLowerLeftOfTheLine()
    {
        GraphicsLayer layer = Layer();
        TextRenderer.Draw(layer, RuntimeFont.System, "A", 10, 20, 5, 0, 0, false);

        // 'A' top row is 0x0C: pixels 2 and 3 at the glyph's top (y + 7); the bottom row is empty.
        Assert.Equal(5, layer.GetPixel(12, 27));
        Assert.Equal(5, layer.GetPixel(13, 27));
        Assert.Equal(0, layer.GetPixel(11, 27));
        Assert.Equal(0, layer.GetPixel(12, 20));
    }

    [Fact]
    public void MeasuresAndWrapsWords()
    {
        Assert.Equal(40, TextRenderer.Measure(RuntimeFont.System, "Hello"));
        Assert.Equal(48, TextRenderer.Measure(RuntimeFont.System, "ab\nlonger"));
        Assert.Equal(3, TextRenderer.CountLines(RuntimeFont.System, "aaa bbb ccc", 40, wordWrap: true));
        Assert.Equal(2, TextRenderer.CountLines(RuntimeFont.System, "abcdefghij", 40, wordWrap: true)); // long word split
    }

    [Fact]
    public void ColorCommandChangesColorMidLine()
    {
        GraphicsLayer layer = Layer();
        TextRenderer.Draw(layer, RuntimeFont.System, "A\u0001\u0009A", 0, 0, 5, 0, 0, false);
        Assert.Equal(5, layer.GetPixel(2, 7));
        Assert.Equal(9, layer.GetPixel(8 + 2, 7));
    }

    [Fact]
    public void BoxStartsAtTheTopAndStopsAtTheBottom()
    {
        GraphicsLayer layer = Layer();
        // Box 0..100 × 0..20 with 9-px lines: lines at y = 11 and y = 2 fit; the third (y = -7) does not.
        TextRenderer.Draw(layer, RuntimeFont.System, "A\nA\nA", 0, 0, 5, 100, 20, false);
        Assert.Equal(5, layer.GetPixel(2, 11 + 7));
        Assert.Equal(5, layer.GetPixel(2, 2 + 7));
        Assert.Equal(0, layer.GetPixel(2, 0));
    }
}

public class SpriteAndFontFormatTests
{
    [Fact]
    public void SpriteJsonRoundTrips()
    {
        SpriteDefinition sprite = SpriteFormat.ParseJson("""{ "tilemap": "hero", "width": 2, "tiles": [0, 1, 32, 33] }""", "hero.sprite");
        Assert.Equal(2, sprite.Height);
        SpriteDefinition again = SpriteFormat.ParseJson(SpriteFormat.FormatJson(sprite), "again.sprite");
        Assert.Equal(sprite.Tiles, again.Tiles);
        Assert.Equal(sprite.Tiles, SpriteFormat.Decode(SpriteFormat.Encode(sprite), "hero").Tiles);
    }

    [Theory]
    [InlineData("""{ "tilemap": "t", "width": 3, "tiles": [0, 1] }""", "multiple")]
    [InlineData("""{ "tilemap": "t", "width": 1, "tiles": [2000] }""", "out of range")]
    [InlineData("""{ "width": 1, "tiles": [0] }""", "'tilemap'")]
    public void InvalidSpritesAreRejected(string json, string expected)
    {
        var e = Assert.Throws<ProjectException>(() => SpriteFormat.ParseJson(json, "bad.sprite"));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void FontRangesWrapDownTheSheetAndRoundTrip()
    {
        const string json = """
            {
              "tilemap": "font", "lineHeight": 10, "advance": 6,
              "ranges": [ { "first": 32, "last": 96, "tile": 992 } ],
              "characters": [ { "char": "i", "tile": 5, "advance": 3 }, { "code": 200, "tile": 6 } ]
            }
            """;
        FontDefinition font = FontFormat.ParseJson(json, "test.font");
        Assert.Equal(992, font.GlyphTiles[32]);         // top-left of the sheet
        Assert.Equal(992 + 31, font.GlyphTiles[63]);
        Assert.Equal(960, font.GlyphTiles[64]);         // next glyph row, below
        Assert.Equal(5, font.GlyphTiles['i']);
        Assert.Equal(3, font.Advances['i']);
        Assert.Equal(6, font.Advances['A']);
        Assert.Equal(-1, font.GlyphTiles[10]);

        FontDefinition again = FontFormat.ParseJson(FontFormat.FormatJson(font), "again.font");
        Assert.Equal(font.GlyphTiles, again.GlyphTiles);
        Assert.Equal(font.Advances, again.Advances);
        Assert.Equal(font.GlyphTiles, FontFormat.Decode(FontFormat.Encode(font), "f").GlyphTiles);
    }

    [Fact]
    public void FontGlyphsMustFitTheTilemap()
    {
        var e = Assert.Throws<ProjectException>(() =>
            FontFormat.ParseJson("""{ "tilemap": "f", "glyphHeight": 2, "characters": [ { "code": 65, "tile": 1000 } ] }""", "tall.font"));
        Assert.Contains("does not fit", e.Message);
    }
}

public class GraphicsCartridgeTests
{
    private const string Game = """
        using ZTX16;

        public sealed class Game : ICartridge
        {
            private Sprite hero = null!;

            public void Initialize()
            {
                hero = Sprite.Load("hero");
                hero.SetTile(1, 0, 0);                                     // replace the right tile at runtime
            }

            public void Tick() { }

            public void Render()
            {
                Graphics.ClearLayers();
                Graphics.DrawTiles(0, "art", 0, 0, 2, 1, 10, 10);           // tiles 0..1
                Tiles.Draw(1, "art", 0, 0, 1, 1, 50, 10, Transform.Identity.Flipped(true, false));
                hero.Draw(2, 100, 10, Transform.Identity.Scaled(2f, 2f));
                Text.Draw(3, "L", 150, 10, 9, "letters");                   // custom font: 'L' is tile 2
                Text.Draw(4, "Hi " + Text.Width("Hi", "").ToString(), 0, 100, 5, "");
            }
        }
        """;

    [Fact]
    public void TilemapSpriteAndFontResourcesWorkEndToEnd()
    {
        using var dir = new TempDirectory();
        dir.Write(ProjectConfig.FileName, """{ "name": "Art", "gameId": "dev.test.art", "directories": { "code": "code", "tilemaps": "tilemaps", "sprites": "sprites", "fonts": "fonts" } }""");
        dir.Write("code/Game.cs", Game);
        File.WriteAllBytes(Path.Combine(dir.Path, "tilemaps", "art.png").EnsureDirectory(), TileArt.Png());
        dir.Write("sprites/hero.sprite", """{ "tilemap": "art", "width": 2, "tiles": [1, 1] }""");
        dir.Write("fonts/letters.font", """{ "tilemap": "art", "characters": [ { "char": "L", "tile": 2 } ] }""");

        CartridgeContent cartridge = ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory);
        var session = new RuntimeSession(VmProgram.Load(cartridge.Executable), 48000, new ResourceStore(cartridge.Resources));
        session.Initialize();
        session.RunTick(TimeSpan.Zero);
        GraphicsSystem g = session.Graphics;

        Assert.Equal(7, g.Layers[0].GetPixel(10, 10));       // tile 0 marker at its lower-left
        Assert.Equal(4, g.Layers[0].GetPixel(18, 10));       // tile 1
        Assert.Equal(7, g.Layers[1].GetPixel(57, 10));       // flipped: marker at the lower-right
        Assert.Equal(4, g.Layers[2].GetPixel(100, 10));      // sprite left tile = 1 (color 4), scaled ×2
        Assert.Equal(7, g.Layers[2].GetPixel(116, 10));      // right tile replaced by tile 0; marker 2×2
        Assert.Equal(7, g.Layers[2].GetPixel(117, 11));
        Assert.Equal(9, g.Layers[3].GetPixel(151, 11));      // the 'L' glyph's corner, in the text color
        Assert.Equal(5, g.Layers[4].GetPixel(8 + 2, 100 + 7)); // "Hi 16": the dot of 'i' (top glyph row 0x0C)

        // Extraction writes the tilemap back as a PNG with the same pixels.
        using var extracted = new TempDirectory();
        Directory.Delete(extracted.Path);
        ProjectExtractor.Extract(cartridge, extracted.Path);
        IndexedImage png = PngCodec.Decode(File.ReadAllBytes(Path.Combine(extracted.Path, "tilemaps", "art.png")), "art.png");
        Assert.Equal(TileArt.Image().Indices, png.Indices);
        Assert.Equal(cartridge.Executable, ProjectBuilder.Build(extracted.Path, ProjectFixtures.CoreDirectory).Executable);
    }

    [Fact]
    public void MissingTilemapReferenceFailsTheBuild()
    {
        using var dir = new TempDirectory();
        dir.Write(ProjectConfig.FileName, """{ "name": "Art", "gameId": "dev.test.art", "directories": { "code": "code", "sprites": "sprites" } }""");
        dir.Write("code/Game.cs", "using ZTX16; public sealed class Game : ICartridge { public void Initialize() { } public void Tick() { } public void Render() { } }");
        dir.Write("sprites/hero.sprite", """{ "tilemap": "nowhere", "width": 1, "tiles": [0] }""");
        var e = Assert.Throws<ProjectException>(() => ProjectBuilder.Build(dir.Path, ProjectFixtures.CoreDirectory));
        Assert.Contains("'nowhere'", e.Message);
    }
}

internal static class PathExtensions
{
    public static string EnsureDirectory(this string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
