using ZTX16;
using ZTX16.Collections;

namespace HelloSquare;

/// <summary>
/// Sample cartridge: a ship sprite (from the "art" tilemap) with an animated engine flame, moved with the arrow keys,
/// WASD, the D-pad or the left stick, rotated to face its direction, over a scrolling two-screen background, leaving a
/// trail kept in a Queue, with text in the built-in font. The "stage" Level supplies brick walls that stop the ship
/// (collision) and gems it collects. Sound uses the low-level audio hardware: a chime per gem (Synth square), a thud
/// on walls (Synth noise), an engine hum while moving (Wavetable with LFO vibrato through a low-pass filter) and a
/// fanfare Sample for the last gem, with a little global reverb. Space toggles the background layer, P cycles
/// through the Palette resources, C shows the colliders, M mutes, Escape exits.
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

    // Audio channels.
    private const int ChimeChannel = 0;
    private const int ThudChannel = 1;
    private const int FanfareChannel = 6;
    private const int HumChannel = 7;
    private bool muted;
    private bool humming;
    private int chimeTicks;

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

        SetUpAudio();
    }

    private static void SetUpAudio()
    {
        Audio.SetMasterVolume(0.8f);

        // Gem chime: a short square blip, the envelope only on volume.
        Audio.SetWaveform(ChimeChannel, Waveform.Square);
        Audio.SetVolume(ChimeChannel, 0.25f);
        Audio.SetAdsr(ChimeChannel, 0f, 0.25f, 0f, 0.05f);

        // Wall thud: low noise with a fast decay, slightly to the left.
        Audio.SetWaveform(ThudChannel, Waveform.Noise);
        Audio.SetFrequency(ThudChannel, 1200f);
        Audio.SetVolume(ThudChannel, 0.35f);
        Audio.SetAdsr(ThudChannel, 0f, 0.08f, 0f, 0f);

        // Engine hum: a wavetable, LFO 1 adds vibrato, Filter 1 is an insert low-pass on its channel.
        if (Resources.Exists(ResourceType.Wavetable, "hum"))
        {
            Audio.UseWavetable(HumChannel, "hum");
            Audio.SetFrequency(HumChannel, 55f);
            Audio.SetVolume(HumChannel, 0.5f);
            Audio.SetAdsr(HumChannel, 0.15f, 0f, 1f, 0.3f);
            Audio.SetLfo(0, LfoWaveform.Triangle, 6f);
            Audio.RouteLfo(0, HumChannel, ModulationDestination.Pitch, 0.4f);
            Audio.SetFilter(0, FilterMode.LowPass, 500f, 0.3f);
            Audio.AssignProcessor(ProcessorUnit.Filter1, HumChannel);
        }

        // Fanfare: a one-shot Sample.
        if (Resources.Exists(ResourceType.Sample, "fanfare"))
        {
            Audio.UseSample(FanfareChannel, "fanfare");
            Audio.SetVolume(FanfareChannel, 0.7f);
            Audio.SetAdsr(FanfareChannel, 0f, 0f, 1f, 0.2f);
        }

        // A little reverb on everything.
        Audio.SetEffect(0, EffectType.Reverb, 0.6f, 0.5f, 0.2f);
        Audio.AssignProcessor(ProcessorUnit.Fx1, Audio.Global);
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

        if (Input.WasKeyPressed(Key.M))
        {
            muted = !muted;
            Audio.SetMasterVolume(muted ? 0f : 0.8f);
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
        bool wasTouching = touching;
        touching = hitX.Hit || hitY.Hit;
        if (touching && !wasTouching)
        {
            Audio.NoteOn(ThudChannel);
        }

        bool moving = dx != 0 || dy != 0;
        if (moving != humming)
        {
            humming = moving;
            if (moving)
            {
                Audio.NoteOn(HumChannel);
            }
            else
            {
                Audio.NoteOff(HumChannel);
            }
        }

        engine?.Update();
        CollectGems();

        // The chime's second, higher note follows four ticks after the first.
        if (chimeTicks > 0 && --chimeTicks == 0)
        {
            Audio.SetFrequency(ChimeChannel, 1318.5f);
            Audio.NoteOn(ChimeChannel);
        }

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
        Text.Draw(Hud, "P palette C colliders M mute", 16, 6, 4, "");
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
                Audio.SetFrequency(ChimeChannel, 987.8f);
                Audio.NoteOn(ChimeChannel);
                chimeTicks = 4;
                if (gemsCollected == gems.ActorCount)
                {
                    Audio.NoteOn(FanfareChannel);
                }
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
