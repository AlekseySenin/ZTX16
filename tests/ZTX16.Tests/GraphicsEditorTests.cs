using System.Buffers.Binary;
using System.IO.Compression;
using ZTX16.Cartridge;

namespace ZTX16.Tests;

public class PngImportTests
{
    // A minimal 8-bit RGB (colour type 2) PNG, filter 0 on every row.
    internal static byte[] RgbPng(int width, int height, Func<int, int, (byte R, byte G, byte B)> color)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = color(x, y);
                raw.WriteByte(r);
                raw.WriteByte(g);
                raw.WriteByte(b);
            }
        }

        using var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
        {
            zl.Write(raw.ToArray());
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream s, string type, byte[] data)
        {
            var head = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
            System.Text.Encoding.ASCII.GetBytes(type, head.AsSpan(4));
            s.Write(head);
            s.Write(data);
            s.Write(new byte[4]);           // CRC is not checked by the importer
        }
    }

    internal static byte[] Palette8()
    {
        string[] lines = File.ReadAllLines(Path.Combine(CartridgeCompiler.RepositoryRoot, "samples", "HelloSquare", "palettes", "default.pal"))
            .Where(l => l.Length > 0 && char.IsDigit(l[0])).ToArray();
        var palette = new byte[96];
        for (int i = 0; i < 32; i++)
        {
            int[] rgb = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3).Select(int.Parse).ToArray();
            for (int c = 0; c < 3; c++)
            {
                palette[(i * 3) + c] = (byte)(rgb[c] * 255 / 7);
            }
        }

        return palette;
    }

    [Fact]
    public void TruecolorPngMapsToTheNearestPaletteColours()
    {
        // Left half pure white, right half almost-red; 4×2 pixels.
        byte[] png = RgbPng(4, 2, (x, y) => x < 2 ? ((byte)255, (byte)255, (byte)255) : ((byte)250, (byte)10, (byte)5));
        byte[] indices = PngImport.ToIndices(png, Palette8(), out int width, out int height);
        Assert.Equal((4, 2), (width, height));
        Assert.Equal(5, indices[0]);        // white
        Assert.Equal(7, indices[3]);        // red
    }
}

public class GraphicsEditorTests
{
    private const int Zoom = 5;               // a 2×2-tile region fits the main screen at 5×
    private const int MainY = 165;

    private static EnvironmentDriver Start(byte[]? pixels = null)
    {
        var env = new EnvironmentDriver();
        env.Type("new Art");
        WritePng(env, "/Art/tilemaps/art.png", pixels ?? new byte[65536]);
        env.Type("edit /Art/tilemaps/art.png");
        Assert.Equal("graphics", env.Active.Name);
        return env;
    }

    private static void WritePng(EnvironmentDriver env, string path, byte[] pixels)
    {
        byte[] data = new byte[65536 + 96];
        pixels.CopyTo(data, 0);
        PngImportTests.Palette8().CopyTo(data, 65536);
        Assert.Equal("", env.Workspace.WriteBytes(path, TilemapFormat.ToPng(data, "art")));
    }

    private static byte[] ReadPixels(EnvironmentDriver env, string path) =>
        TilemapFormat.Pixels(TilemapFormat.FromPng(env.Workspace.ReadBytes(path)!, path), path);

    private static int Pixel(byte[] pixels, int x, int y) => pixels[(y * 256) + x];

    // Screen position of a region pixel in the main screen (region = tiles (0,0)–(1,1) after opening).
    private static (int X, int Y) At(int x, int y) => ((x * Zoom) + 2, MainY + (y * Zoom) + 2);

    private static void Save(EnvironmentDriver env) => env.Press(Key.S, ctrl: true);

    [Fact]
    public void BrushPaintsAndSaveWritesThePng()
    {
        using EnvironmentDriver env = Start();
        var (x, y) = At(3, 4);
        env.Click(x, y);                                    // primary colour 5
        env.Click(At(6, 1).X, At(6, 1).Y, MouseButton.Right);   // secondary colour 0 (no change)
        Save(env);
        byte[] saved = ReadPixels(env, "/Art/tilemaps/art.png");
        Assert.Equal(5, Pixel(saved, 3, 4));
        Assert.Equal(0, Pixel(saved, 6, 1));
    }

    [Fact]
    public void StrokesFollowTheMouseWithoutGaps()
    {
        using EnvironmentDriver env = Start();
        env.Drag(At(1, 2).X, At(1, 2).Y, At(12, 2).X, At(12, 2).Y, steps: 2);
        Save(env);
        byte[] saved = ReadPixels(env, "/Art/tilemaps/art.png");
        Assert.All(Enumerable.Range(1, 12), x => Assert.Equal(5, Pixel(saved, x, 2)));
    }

    [Fact]
    public void FillStaysInTheRegionAndUndoRestores()
    {
        using EnvironmentDriver env = Start();
        env.Press(Key.F);
        env.Click(At(0, 0).X, At(0, 0).Y);
        Save(env);
        byte[] filled = ReadPixels(env, "/Art/tilemaps/art.png");
        Assert.Equal(5, Pixel(filled, 15, 15));
        Assert.Equal(0, Pixel(filled, 16, 0));              // outside the 16×16 region

        env.Press(Key.Z, ctrl: true);
        Save(env);
        Assert.Equal(0, Pixel(ReadPixels(env, "/Art/tilemaps/art.png"), 15, 15));
    }

    [Fact]
    public void PipettePicksTheColourToPaintWith()
    {
        var pixels = new byte[65536];
        pixels[(1 * 256) + 1] = 11;
        using EnvironmentDriver env = Start(pixels);
        env.Press(Key.I);
        env.Click(At(1, 1).X, At(1, 1).Y);
        env.Press(Key.B);
        env.Click(At(5, 5).X, At(5, 5).Y);
        Save(env);
        Assert.Equal(11, Pixel(ReadPixels(env, "/Art/tilemaps/art.png"), 5, 5));
    }

    [Fact]
    public void ClickingTheTilemapSelectsATile()
    {
        using EnvironmentDriver env = Start();
        env.Click(93 + (5 * 8) + 2, 5 + (3 * 8) + 2);       // tile (5, 3) in the tilemap view
        env.Click(2, MainY + 2);                            // a single tile shows at 10×: its pixel (0, 0)
        Save(env);
        Assert.Equal(5, Pixel(ReadPixels(env, "/Art/tilemaps/art.png"), 40, 24));
    }

    [Fact]
    public void CopyFlipAndPasteIntoTheTilemap()
    {
        var pixels = new byte[65536];
        pixels[0] = 7;                                      // pixel (0, 0) of tile 0
        using EnvironmentDriver env = Start(pixels);
        env.Press(Key.C, ctrl: true);                       // copies the 16×16 region
        env.Press(Key.V, ctrl: true);
        env.Press(Key.H);                                   // flip the floating paste
        env.Click(93 + 32, 5);                              // place at (32, 0)
        env.Press(Key.Escape);
        Save(env);
        byte[] saved = ReadPixels(env, "/Art/tilemaps/art.png");
        Assert.Equal(7, Pixel(saved, 32 + 15, 0));
        Assert.Equal(0, Pixel(saved, 32, 0));
    }

    [Fact]
    public void PaletteColourIsEditedAndSaved()
    {
        using EnvironmentDriver env = Start();
        env.Click(76, 133 + 20);                            // MENU
        env.Press(Key.Enter);                               // EDIT COLOR (of the primary colour, 5 = white)
        env.Press(Key.Left);                                // R 7 → 6
        env.Press(Key.Enter);
        env.Click(76, 133 + 20);
        env.Press(Key.Down);
        env.Press(Key.Down);
        env.Press(Key.Enter);                               // SAVE PALETTE (the project's palette)
        string palette = env.Workspace.ReadText("/Art/palettes/default.pal")!;
        Assert.Contains("6 7 7    # 5", palette);
    }

    [Fact]
    public void ImportPlacesAPngAndExportWritesOne()
    {
        using var host = new TempDirectory();
        string source = Path.Combine(host.Path, "in.png");
        File.WriteAllBytes(source, PngImportTests.RgbPng(3, 3, (_, _) => ((byte)255, (byte)255, (byte)255)));
        using EnvironmentDriver env = Start();

        env.Dialogs.Answer = source;
        env.Click(83 + 5, 250);                             // IMPORT
        env.Tick(2);
        env.Click(93 + 64, 5 + 64);                         // place the 3×3 image at (64, 64)
        env.Press(Key.Escape);

        string target = Path.Combine(host.Path, "out.png");
        env.Dialogs.Answer = target;
        env.Click(114 + 5, 250);                            // EXPORT
        env.Tick(2);
        byte[] exported = TilemapFormat.Pixels(TilemapFormat.FromPng(File.ReadAllBytes(target), "out"), "out");
        Assert.Equal(5, Pixel(exported, 64, 64));
        Assert.Equal(5, Pixel(exported, 66, 66));
        Assert.Equal(0, Pixel(exported, 67, 64));
    }

    [Fact]
    public void OpeningAnotherFileAsksToSaveChanges()
    {
        using EnvironmentDriver env = Start();
        WritePng(env, "/Art/tilemaps/other.png", new byte[65536]);
        env.Click(At(0, 0).X, At(0, 0).Y);              // a change
        env.Press(Key.Escape);                              // back to the Console
        env.Type("edit /Art/tilemaps/other.png");
        env.Text("n");                                      // do not save
        Assert.Equal(0, Pixel(ReadPixels(env, "/Art/tilemaps/art.png"), 0, 0));

        env.Click(At(0, 0).X, At(0, 0).Y);              // change other.png
        env.Press(Key.Escape);
        env.Type("edit /Art/tilemaps/art.png");
        env.Text("y");                                      // save it first
        Assert.Equal(5, Pixel(ReadPixels(env, "/Art/tilemaps/other.png"), 0, 0));
    }
}
