using ZTX16.Graphics;

namespace ZTX16.Tests;

public class ColorTests
{
    [Fact]
    public void PacksComponentsAsRgb333()
    {
        var color = new Color(5, 2, 7);
        Assert.Equal((5 << 6) | (2 << 3) | 7, color.Value);
        Assert.Equal(color, Color.FromValue(color.Value));
    }

    [Theory]
    [InlineData(8, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, 9)]
    public void RejectsComponentsOutsideRange(int r, int g, int b)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Color(r, g, b));
    }

    [Fact]
    public void ExpandsExtremesToFullIntensity()
    {
        Assert.Equal(0xFF000000u, new Color(0, 0, 0).ToArgb32());
        Assert.Equal(0xFFFFFFFFu, new Color(7, 7, 7).ToArgb32());
    }
}

public class PaletteTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(32)]
    public void InvalidIndexIsAnError(int index)
    {
        var palette = new Palette();
        Assert.Throws<ArgumentOutOfRangeException>(() => palette.GetColor(index));
        Assert.Throws<ArgumentOutOfRangeException>(() => palette.SetColor(index, default));
    }

    [Fact]
    public void RequiresExactly32Colors()
    {
        Assert.Throws<ArgumentException>(() => new Palette(new Color[31]));
    }
}

public class GraphicsLayerTests
{
    [Fact]
    public void UsesLowerLeftOriginStorage()
    {
        var graphics = new GraphicsSystem();
        GraphicsLayer layer = graphics.Layers[0];

        layer.SetPixel(0, 0, 7);
        Assert.Equal(7, layer.Pixels[0]);
        Assert.Equal(7, layer.GetPixel(0, 0));
    }

    [Fact]
    public void ResizeProducesClearedBufferOfScreenMultiples()
    {
        GraphicsLayer layer = new GraphicsSystem().Layers[3];
        layer.Fill(5);

        layer.Resize(2, 3);

        Assert.Equal(512, layer.Width);
        Assert.Equal(768, layer.Height);
        Assert.Equal(0, layer.GetPixel(511, 767));
    }

    [Fact]
    public void ResizeToSameSizeStillClears()
    {
        GraphicsLayer layer = new GraphicsSystem().Layers[0];
        layer.Fill(4);
        layer.Resize(1, 1);
        Assert.Equal(0, layer.GetPixel(10, 10));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(GraphicsLayer.MaxScreensPerAxis + 1, 1)]
    public void ResizeRejectsInvalidSizes(int w, int h)
    {
        GraphicsLayer layer = new GraphicsSystem().Layers[0];
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.Resize(w, h));
    }

    [Fact]
    public void PixelAccessOutsideLayerIsAnError()
    {
        GraphicsLayer layer = new GraphicsSystem().Layers[0];
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.SetPixel(256, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.GetPixel(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.SetPixel(0, 0, 32));
    }

    [Fact]
    public void FillRectangleClipsToLayer()
    {
        GraphicsLayer layer = new GraphicsSystem().Layers[0];
        layer.FillRectangle(-10, 250, 20, 20, 9);

        Assert.Equal(9, layer.GetPixel(0, 250));
        Assert.Equal(9, layer.GetPixel(9, 255));
        Assert.Equal(0, layer.GetPixel(10, 250));
        Assert.Equal(0, layer.GetPixel(0, 249));
    }
}

public class CompositionTests
{
    private static byte[] Compose(GraphicsSystem graphics)
    {
        byte[] frame = new byte[256 * 256];
        graphics.Compose(frame);
        return frame;
    }

    private static int At(byte[] frame, int x, int y) => frame[(y * 256) + x];

    [Fact]
    public void HighestNonTransparentLayerWins()
    {
        var graphics = new GraphicsSystem();
        graphics.Layers[0].Fill(1);
        graphics.Layers[5].SetPixel(3, 4, 5);
        graphics.Layers[2].SetPixel(3, 4, 2);

        byte[] frame = Compose(graphics);

        Assert.Equal(5, At(frame, 3, 4));
        Assert.Equal(1, At(frame, 0, 0));
    }

    [Fact]
    public void AllTransparentGivesBackdropIndex()
    {
        byte[] frame = Compose(new GraphicsSystem());
        Assert.All(frame, value => Assert.Equal(0, value));
    }

    [Fact]
    public void DisabledLayerIsSkippedButKeepsContents()
    {
        var graphics = new GraphicsSystem();
        graphics.Layers[0].Fill(1);
        graphics.Layers[7].Fill(7);
        graphics.DisplayLayers[7].Enabled = false;

        Assert.Equal(1, At(Compose(graphics), 100, 100));
        Assert.Equal(7, graphics.Layers[7].GetPixel(100, 100));
    }

    [Fact]
    public void DisplayOriginSelectsRegionOfLargerLayer()
    {
        var graphics = new GraphicsSystem();
        GraphicsLayer layer = graphics.Layers[1];
        layer.Resize(2, 2);
        layer.SetPixel(300, 260, 9);
        graphics.DisplayLayers[1].SetOrigin(256, 256);

        Assert.Equal(9, At(Compose(graphics), 44, 4));
    }

    [Fact]
    public void DisplayWindowWrapsAroundLayerEdges()
    {
        var graphics = new GraphicsSystem();
        GraphicsLayer layer = graphics.Layers[0];
        layer.SetPixel(0, 0, 3);
        graphics.DisplayLayers[0].SetOrigin(-10, -20);

        Assert.Equal(3, At(Compose(graphics), 10, 20));
    }

    [Fact]
    public void ArgbConversionFlipsToTopDownAndUsesBackdropColor()
    {
        var graphics = new GraphicsSystem();
        graphics.Palette.SetColor(0, new Color(0, 0, 7));
        graphics.Palette.SetColor(5, new Color(7, 0, 0));
        graphics.Layers[0].SetPixel(0, 0, 5);

        byte[] frame = Compose(graphics);
        uint[] argb = new uint[256 * 256];
        graphics.ConvertToArgb(frame, argb);

        Assert.Equal(0xFFFF0000u, argb[255 * 256]); // bottom-left in host order is the last row
        Assert.Equal(0xFF0000FFu, argb[0]);
    }
}
