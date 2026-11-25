using ZTX16.Graphics;
using ZTX16.Input;
using ZTX16.Runtime;

namespace ZTX16.Host.Diagnostics;

/// <summary>
/// Built-in host diagnostic used to verify the platform path end to end: layers,
/// composition, display origin wrapping, coordinate orientation, input and audio.
/// </summary>
/// <remarks>
/// Layout:
/// <list type="bullet">
/// <item>Layer 0 — a 2×2-screen checkerboard; its Display Layer origin scrolls slowly (wrap test).</item>
/// <item>Layer 1 — orientation markers: red at the lower-left (0,0), blue at the upper-right.</item>
/// <item>Layer 2 — a square moved by arrows/WASD/D-pad/left stick; its height sets the tone pitch.</item>
/// <item>Layer 3 — input indicators along the top edge.</item>
/// <item>Layer 7 — mouse crosshair.</item>
/// </list>
/// Space toggles the test tone; Escape exits; 1–8 toggles the corresponding Display Layer.
/// </remarks>
internal sealed class DiagnosticProgram : IRuntimeProgram
{
    private const int SquareSize = 16;
    private const int Speed = 2;

    private RuntimeSession _session = null!;
    private int _squareX = 120;
    private int _squareY = 120;

    public void Initialize(RuntimeSession session)
    {
        _session = session;
        GraphicsSystem graphics = session.Graphics;

        GraphicsLayer background = graphics.Layers[0];
        background.Resize(2, 2);
        for (int cellY = 0; cellY < background.Height / 32; cellY++)
        {
            for (int cellX = 0; cellX < background.Width / 32; cellX++)
            {
                int color = ((cellX + cellY) & 1) == 0 ? 2 : 26;
                background.FillRectangle(cellX * 32, cellY * 32, 32, 32, color);
            }
        }

        // Palette strip across the second screen row so wrapping is visible.
        for (int i = 1; i < Palette.Size; i++)
        {
            background.FillRectangle(i * 8, 300, 8, 24, i);
        }

        GraphicsLayer markers = graphics.Layers[1];
        markers.FillRectangle(0, 0, 24, 4, 7);
        markers.FillRectangle(0, 0, 4, 24, 7);
        markers.FillRectangle(232, 252, 24, 4, 19);
        markers.FillRectangle(252, 232, 4, 24, 19);
    }

    public void Tick()
    {
        InputSystem input = _session.Input;
        Keyboard keyboard = input.Keyboard;

        if (keyboard.WasPressed(Key.Escape))
        {
            _session.Machine.Exit();
        }

        if (keyboard.WasPressed(Key.Space))
        {
            _session.Audio.DiagnosticTone.Enabled = !_session.Audio.DiagnosticTone.Enabled;
        }

        for (int i = 0; i < GraphicsSystem.LayerCount; i++)
        {
            if (keyboard.WasPressed(Key.Digit1 + i))
            {
                DisplayLayer display = _session.Graphics.DisplayLayers[i];
                display.Enabled = !display.Enabled;
            }
        }

        (int dx, int dy) = ReadDirection(input);
        _squareX = Math.Clamp(_squareX + (dx * Speed), 0, GraphicsSystem.DisplayWidth - SquareSize);
        _squareY = Math.Clamp(_squareY + (dy * Speed), 0, GraphicsSystem.DisplayHeight - SquareSize);

        // Pitch rises with height: 220 Hz at the bottom to 880 Hz at the top.
        float t = _squareY / (float)(GraphicsSystem.DisplayHeight - SquareSize);
        _session.Audio.DiagnosticTone.Frequency = 220f * MathF.Pow(4f, t);

        // Slow diagonal scroll of the background window, one pixel every other tick.
        if (_session.Machine.TickCount % 2 == 0)
        {
            DisplayLayer backgroundDisplay = _session.Graphics.DisplayLayers[0];
            backgroundDisplay.SetOrigin(backgroundDisplay.OriginX + 1, backgroundDisplay.OriginY + 1);
        }
    }

    public void Render()
    {
        GraphicsSystem graphics = _session.Graphics;
        InputSystem input = _session.Input;

        GraphicsLayer square = graphics.Layers[2];
        square.Clear();
        square.FillRectangle(_squareX, _squareY, SquareSize, SquareSize, 11);
        square.FillRectangle(_squareX + 4, _squareY + 4, SquareSize - 8, SquareSize - 8, 9);

        GraphicsLayer indicators = graphics.Layers[3];
        indicators.Clear();
        int x = 8;
        DrawIndicator(indicators, ref x, input.Keyboard.IsDown(Key.Space), 14);
        DrawIndicator(indicators, ref x, input.Mouse[MouseButton.Left].IsDown, 17);
        DrawIndicator(indicators, ref x, input.Mouse[MouseButton.Middle].IsDown, 17);
        DrawIndicator(indicators, ref x, input.Mouse[MouseButton.Right].IsDown, 17);
        x += 8;
        foreach (Gamepad pad in input.Gamepads)
        {
            DrawIndicator(indicators, ref x, pad.IsConnected, pad[GamepadButton.Action1].IsDown ? 11 : 15);
        }

        x += 8;
        DrawIndicator(indicators, ref x, _session.Audio.DiagnosticTone.Enabled, 22);

        // Throughput bar: full width (48 px) at 60 ticks/s.
        int bar = (int)Math.Clamp(_session.Machine.TicksPerSecond / 60.0 * 48, 0, 48);
        indicators.FillRectangle(200, 244, 48, 4, 1);
        indicators.FillRectangle(200, 244, bar, 4, 14);

        GraphicsLayer cursor = graphics.Layers[7];
        cursor.Clear();
        if (input.Mouse.IsInsideDisplay)
        {
            cursor.FillRectangle(input.Mouse.X - 4, input.Mouse.Y, 9, 1, 5);
            cursor.FillRectangle(input.Mouse.X, input.Mouse.Y - 4, 1, 9, 5);
        }
    }

    public void Deinitialize()
    {
        _session.Audio.DiagnosticTone.Enabled = false;
    }

    private static (int Dx, int Dy) ReadDirection(InputSystem input)
    {
        Keyboard k = input.Keyboard;
        int dx = (k.IsDown(Key.Right) || k.IsDown(Key.D) ? 1 : 0) - (k.IsDown(Key.Left) || k.IsDown(Key.A) ? 1 : 0);
        int dy = (k.IsDown(Key.Up) || k.IsDown(Key.W) ? 1 : 0) - (k.IsDown(Key.Down) || k.IsDown(Key.S) ? 1 : 0);

        foreach (Gamepad pad in input.Gamepads)
        {
            if (!pad.IsConnected)
            {
                continue;
            }

            // Dead zone is game-side policy; this diagnostic uses 0.3.
            const float deadZone = 0.3f;
            float sx = pad.GetAxis(GamepadAxis.LeftStickX);
            float sy = pad.GetAxis(GamepadAxis.LeftStickY);
            dx += (pad[GamepadButton.DPadRight].IsDown || sx > deadZone ? 1 : 0)
                - (pad[GamepadButton.DPadLeft].IsDown || sx < -deadZone ? 1 : 0);
            dy += (pad[GamepadButton.DPadUp].IsDown || sy > deadZone ? 1 : 0)
                - (pad[GamepadButton.DPadDown].IsDown || sy < -deadZone ? 1 : 0);
        }

        return (Math.Sign(dx), Math.Sign(dy));
    }

    private static void DrawIndicator(GraphicsLayer layer, ref int x, bool active, int activeColor)
    {
        layer.FillRectangle(x, 244, 8, 8, 1);
        layer.FillRectangle(x + 1, 245, 6, 6, active ? activeColor : 3);
        x += 10;
    }
}
