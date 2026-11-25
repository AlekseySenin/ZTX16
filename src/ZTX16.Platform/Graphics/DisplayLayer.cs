namespace ZTX16.Graphics;

/// <summary>
/// A Display Layer: the 256×256 window that exposes a region of its corresponding
/// Graphics Layer on the logical display. Display Layer N always shows Graphics Layer N.
/// </summary>
/// <remarks>
/// The window origin is the lower-left pixel of the visible region, in Graphics Layer
/// coordinates. If the window extends past the layer edge, sampling wraps around,
/// so any origin value is valid.
/// </remarks>
public sealed class DisplayLayer
{
    internal DisplayLayer(GraphicsLayer source)
    {
        Source = source;
    }

    /// <summary>Layer index, identical to the index of <see cref="Source"/>.</summary>
    public int Index => Source.Index;

    /// <summary>The Graphics Layer this Display Layer exposes.</summary>
    public GraphicsLayer Source { get; }

    /// <summary>
    /// Whether this layer takes part in final composition. Disabling a layer
    /// preserves its Graphics Layer contents.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>X of the visible window's lower-left corner in Graphics Layer pixels.</summary>
    public int OriginX { get; set; }

    /// <summary>Y of the visible window's lower-left corner in Graphics Layer pixels.</summary>
    public int OriginY { get; set; }

    /// <summary>Sets both origin coordinates.</summary>
    public void SetOrigin(int x, int y)
    {
        OriginX = x;
        OriginY = y;
    }
}
