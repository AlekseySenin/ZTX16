using ZTX16;
using ZTX16.Collections;

namespace HelloSquare;

/// <summary>
/// Sample cartridge: a ship sprite (from the "art" tilemap) with an animated engine flame, moved with the arrow keys,
/// WASD, the D-pad or the left stick, rotated to face its direction, over a scrolling two-screen background, leaving a
/// trail kept in a Queue, with text in the built-in font. The "stage" Level supplies brick walls that stop the ship
/// (collision) and gems it collects. Space toggles the background layer, P cycles through the Palette resources,
/// C shows the colliders, Escape exits.
/// </summary>
public sealed class Game : ICartridge
{
    private const int Size = 16;
    private const int Speed = 2;
    private const int Background = 0;
    private const int Markers = 1;
    private const int Sprites = 2;
    private const int Hud = 3;

    private int x = 120;
    private int y = 120;
    private int scroll;
    private const int TrailLength = 12;
    private readonly Queue<int> trail = new();
    private int palette;
    private Sprite? ship;
    private float heading;
    private AnimationPlayback? engine;
    private ColliderSet walls = new();
    private LevelLayer? gems;
    private int gemsCollected;
    private readonly List<Collider> touched = new();
    private readonly Collider shipCollider = new(120, 120, Size, Size);
    private bool showColliders;
    private bool touching;

    public void Initialize()
    {
        if (Resources.Exists(ResourceType.Sprite, "ship"))
        {
            ship = Sprite.Load("ship");
            if (Resources.Exists(ResourceType.Animation, "engine"))
            {
                engine = new AnimationPlayback(Animation.Load("engine"), ship);
                engine.Play();
            }
        }

        if (Resources.Exists(ResourceType.Palette, "default"))
        {
            Graphics.UsePalette("default");
        }

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

        // The level's walls are drawn once into the Markers layer; their actors' colliders stop the ship.
        // The gems are redrawn every frame because they disappear when collected.
        if (Resources.Exists(ResourceType.Level, "stage"))
        {
            Level level = Level.Load("stage");
            LevelLayer wallLayer = level.FindLayer("walls")!;
            wallLayer.Rasterize(Markers);
            walls = wallLayer.Colliders;
            gems = level.FindLayer("gems");
        }
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

        int palettes = Resources.Count(ResourceType.Palette);
        if (Input.WasKeyPressed(Key.P) && palettes > 0)
        {
            palette = (palette + 1) % palettes;
            Graphics.UsePalette(Resources.Name(ResourceType.Palette, palette));
        }

        if (Input.WasKeyPressed(Key.C))
        {
            showColliders = !showColliders;
        }

        int dx = Horizontal(), dy = Vertical();
        int targetX = Clamp(x + dx * Speed, 0, Graphics.ScreenSize - Size);
        int targetY = Clamp(y + dy * Speed, 0, Graphics.ScreenSize - Size);

        // Collision only answers the question; the game decides what to do. Moving one axis at a time and stopping
        // at the safe position lets the ship slide along a wall.
        CollisionHit hitX = Collision.Move(shipCollider, targetX, y, walls);
        x = hitX.SafeX;
        shipCollider.SetPosition(x, y);
        CollisionHit hitY = Collision.Move(shipCollider, x, targetY, walls);
        y = hitY.SafeY;
        shipCollider.SetPosition(x, y);
        touching = hitX.Hit || hitY.Hit;
        engine?.Update();
        CollectGems();

        // The ship points up at 0°; rotation is counter-clockwise.
        if (dx != 0 || dy != 0)
        {
            heading = dy > 0 ? (dx < 0 ? 45f : dx > 0 ? 315f : 0f)
                : dy < 0 ? (dx < 0 ? 135f : dx > 0 ? 225f : 180f)
                : (dx < 0 ? 90f : 270f);
        }

        scroll++;
        Graphics.SetDisplayOrigin(Background, scroll / 2, 0);

        // Remember the last positions (every fourth tick) for the trail.
        if (scroll % 4 == 0)
        {
            trail.Enqueue(y * Graphics.ScreenSize + x);
            if (trail.Count > TrailLength)
            {
                trail.Dequeue();
            }
        }
    }

    public void Render()
    {
        Graphics.Clear(Sprites);
        int age = 0;
        for (int i = 0; i < trail.Count; i++)
        {
            int packed = trail.Dequeue();
            trail.Enqueue(packed);
            int size = 2 + age / 2;
            Graphics.FillRect(Sprites, packed % Graphics.ScreenSize + (Size - size) / 2, packed / Graphics.ScreenSize + (Size - size) / 2, size, size, 12);
            age++;
        }

        gems?.Rasterize(Sprites);
        if (ship is null)
        {
            Graphics.FillRect(Sprites, x, y, Size, Size, 11);
        }
        else
        {
            ship.Draw(Sprites, x + Size / 2, y + Size / 2, Transform.Identity.Centered().Rotated(heading));
        }

        Graphics.Clear(Hud);
        if (showColliders)
        {
            CollisionDebug.Draw(Hud, walls, 15);
            if (gems is not null)
            {
                CollisionDebug.Draw(Hud, gems.Colliders, 22);
            }

            CollisionDebug.Draw(Hud, shipCollider, touching ? 7 : 17);
        }

        Text.Draw(Hud, "HELLO " + TextStyle.Color(11) + "ZTX16", 8, 242, 5, "");
        if (gems is not null)
        {
            string count = gemsCollected == gems.ActorCount ? TextStyle.Color(15) + "ALL GEMS!" : "GEMS " + gemsCollected.ToString() + "/" + gems.ActorCount.ToString();
            Text.Draw(Hud, count, 104, 242, 17, "");
        }


        Text.Draw(Hud, "T+" + (Machine.TickCount / 60).ToString(), 200, 242, 4, "");
        Text.Draw(Hud, "P palette  C colliders", 32, 6, 4, "");
    }

    // A gem the ship overlaps disappears: its actor is hidden and loses its collider.
    private void CollectGems()
    {
        if (gems is null)
        {
            return;
        }

        touched.Clear();
        Collision.Overlapping(shipCollider, gems.Colliders, touched);
        for (int i = 0; i < gems.ActorCount; i++)
        {
            Actor gem = gems.GetActor(i);
            if (gem.Collider is not null && touched.Contains(gem.Collider))
            {
                gem.Visible = false;
                gem.Collider = null;
                gemsCollected++;
            }
        }
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
