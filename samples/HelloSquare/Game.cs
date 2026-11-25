using ZTX16;

namespace HelloSquare;

/// <summary>
/// First C# cartridge: a square moved with the arrow keys, WASD, the D-pad or the left
/// stick, over a scrolling two-screen background. Space toggles the background layer,
/// Escape exits.
/// </summary>
public sealed class Game : ICartridge
{
    private const int Size = 16;
    private const int Speed = 2;
    private const int Background = 0;
    private const int Markers = 1;
    private const int Sprites = 2;

    private int x = 120;
    private int y = 120;
    private int scroll;

    public void Initialize()
    {
        Graphics.ResizeLayer(Background, 2, 1);
        int width = Graphics.LayerWidth(Background);
        for (int cellY = 0; cellY < Graphics.ScreenSize / 32; cellY++)
        {
            for (int cellX = 0; cellX < width / 32; cellX++)
            {
                int color = ((cellX + cellY) & 1) == 0 ? 18 : 26;
                Graphics.FillRect(Background, cellX * 32, cellY * 32, 32, 32, color);
            }
        }

        // Orientation markers: red at (0,0), blue at the upper-right.
        Graphics.FillRect(Markers, 0, 0, 24, 4, 7);
        Graphics.FillRect(Markers, 0, 0, 4, 24, 7);
        Graphics.FillRect(Markers, 232, 252, 24, 4, 19);
        Graphics.FillRect(Markers, 252, 232, 4, 24, 19);
    }

    public void Tick()
    {
        if (Input.WasKeyPressed(Key.Escape))
        {
            Machine.Exit();
        }

        if (Input.WasKeyPressed(Key.Space))
        {
            Graphics.SetDisplayEnabled(Background, !Graphics.IsDisplayEnabled(Background));
        }

        x = Clamp(x + Horizontal() * Speed, 0, Graphics.ScreenSize - Size);
        y = Clamp(y + Vertical() * Speed, 0, Graphics.ScreenSize - Size);

        scroll++;
        Graphics.SetDisplayOrigin(Background, scroll / 2, 0);
    }

    public void Render()
    {
        Graphics.Clear(Sprites);
        Graphics.FillRect(Sprites, x, y, Size, Size, 11);
        Graphics.FillRect(Sprites, x + 4, y + 4, Size - 8, Size - 8, 22);
    }

    private static int Horizontal()
    {
        int dx = 0;
        if (Input.IsKeyDown(Key.Right) || Input.IsKeyDown(Key.D)) dx++;
        if (Input.IsKeyDown(Key.Left) || Input.IsKeyDown(Key.A)) dx--;
        if (Input.IsGamepadConnected(0))
        {
            float stick = Input.GetGamepadAxis(0, GamepadAxis.LeftStickX);
            if (Input.IsGamepadButtonDown(0, GamepadButton.DPadRight) || stick > 0.3f) dx++;
            if (Input.IsGamepadButtonDown(0, GamepadButton.DPadLeft) || stick < -0.3f) dx--;
        }

        return Sign(dx);
    }

    private static int Vertical()
    {
        int dy = 0;
        if (Input.IsKeyDown(Key.Up) || Input.IsKeyDown(Key.W)) dy++;
        if (Input.IsKeyDown(Key.Down) || Input.IsKeyDown(Key.S)) dy--;
        if (Input.IsGamepadConnected(0))
        {
            float stick = Input.GetGamepadAxis(0, GamepadAxis.LeftStickY);
            if (Input.IsGamepadButtonDown(0, GamepadButton.DPadUp) || stick > 0.3f) dy++;
            if (Input.IsGamepadButtonDown(0, GamepadButton.DPadDown) || stick < -0.3f) dy--;
        }

        return Sign(dy);
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private static int Sign(int value) => value > 0 ? 1 : value < 0 ? -1 : 0;
}
