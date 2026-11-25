using ZTX16.Core;

namespace ZTX16;

/// <summary>An Animation resource loaded into memory: frames of sprite tile swaps.</summary>
public sealed class Animation
{
    private readonly int[] _durations;
    private readonly int[][] _spriteTiles;
    private readonly int[][] _tileIds;

    private Animation(int[] durations, int[][] spriteTiles, int[][] tileIds)
    {
        _durations = durations;
        _spriteTiles = spriteTiles;
        _tileIds = tileIds;
    }

    /// <summary>Number of frames.</summary>
    public int FrameCount => _durations.Length;

    /// <summary>Loads the Animation resource <paramref name="name"/>.</summary>
    public static Animation Load(string name)
    {
        int count = Animations.FrameCount(name);
        int[] durations = new int[count];
        int[][] spriteTiles = new int[count][];
        int[][] tileIds = new int[count][];
        for (int f = 0; f < count; f++)
        {
            durations[f] = Animations.Duration(name, f);
            int swaps = Animations.SwapCount(name, f);
            spriteTiles[f] = new int[swaps];
            tileIds[f] = new int[swaps];
            for (int s = 0; s < swaps; s++)
            {
                spriteTiles[f][s] = Animations.SwapIndex(name, f, s);
                tileIds[f][s] = Animations.SwapTile(name, f, s);
            }
        }

        return new Animation(durations, spriteTiles, tileIds);
    }

    /// <summary>Duration of frame <paramref name="frame"/> in ticks.</summary>
    public int FrameDuration(int frame) => _durations[frame];

    /// <summary>Sets the sprite tiles described by <paramref name="frame"/>.</summary>
    internal void Apply(int frame, Sprite sprite)
    {
        int[] indices = _spriteTiles[frame];
        int[] tiles = _tileIds[frame];
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= sprite.TileCount)
            {
                Intrinsics.Fail("Animation: a frame sets a sprite tile the sprite does not have");
            }

            sprite.SetTileAt(indices[i], tiles[i]);
        }
    }
}

/// <summary>How a playback behaves at the end of the last frame.</summary>
public enum PlaybackMode
{
    /// <summary>Restart from the first frame.</summary>
    Loop,

    /// <summary>Stop on the last frame, keeping its sprite state.</summary>
    OneShot,
}

/// <summary>
/// Runtime state that plays an <see cref="Animation"/> on its own <see cref="Sprite"/>: current frame, frame
/// time, play speed and mode. It changes the sprite's tiles; it never draws — the game decides when and where
/// the sprite is rendered. Call <see cref="Update"/> once per tick.
/// </summary>
/// <remarks>
/// Instead of callbacks, <see cref="Started"/>, <see cref="Looped"/> and <see cref="Finished"/> report what
/// happened during the most recent <see cref="Update"/>.
/// </remarks>
public sealed class AnimationPlayback
{
    private float _playSpeed = 1f;
    private bool _startPending;

    /// <summary>Creates a stopped playback.</summary>
    public AnimationPlayback(Animation animation, Sprite sprite, PlaybackMode mode = PlaybackMode.Loop)
    {
        Animation = animation;
        Sprite = sprite;
        Mode = mode;
    }

    /// <summary>The animation being played.</summary>
    public Animation Animation { get; }

    /// <summary>The sprite this playback modifies.</summary>
    public Sprite Sprite { get; }

    /// <summary>Loop or one-shot.</summary>
    public PlaybackMode Mode { get; set; }

    /// <summary>Frames advance by this many ticks per update (1 = normal, 0.5 = half speed, 0 = frozen).</summary>
    public float PlaySpeed
    {
        get => _playSpeed;
        set
        {
            if (value < 0f)
            {
                Intrinsics.Fail("AnimationPlayback: PlaySpeed cannot be negative");
            }

            _playSpeed = value;
        }
    }

    /// <summary>Current frame index.</summary>
    public int Frame { get; private set; }

    /// <summary>Ticks elapsed in the current frame.</summary>
    public float FrameTime { get; private set; }

    /// <summary>True while the playback advances.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>True during the update that followed <see cref="Play"/>.</summary>
    public bool Started { get; private set; }

    /// <summary>True during the update in which a loop wrapped to the first frame.</summary>
    public bool Looped { get; private set; }

    /// <summary>True during the update in which a one-shot playback reached its end.</summary>
    public bool Finished { get; private set; }

    /// <summary>Starts from the first frame and applies it to the sprite.</summary>
    public void Play()
    {
        Frame = 0;
        FrameTime = 0f;
        IsPlaying = true;
        _startPending = true;
        Animation.Apply(0, Sprite);
    }

    /// <summary>Stops advancing; the sprite keeps its current tiles.</summary>
    public void Pause() => IsPlaying = false;

    /// <summary>Continues after <see cref="Pause"/>.</summary>
    public void Resume() => IsPlaying = true;

    /// <summary>Advances time by one tick × <see cref="PlaySpeed"/>, changing frames as needed.</summary>
    public void Update()
    {
        Started = _startPending;
        _startPending = false;
        Looped = false;
        Finished = false;
        if (!IsPlaying)
        {
            return;
        }

        FrameTime += _playSpeed;
        while (FrameTime >= Animation.FrameDuration(Frame))
        {
            FrameTime -= Animation.FrameDuration(Frame);
            if (Frame + 1 < Animation.FrameCount)
            {
                Frame++;
            }
            else if (Mode == PlaybackMode.Loop)
            {
                Frame = 0;
                Looped = true;
            }
            else
            {
                IsPlaying = false;
                Finished = true;
                FrameTime = Animation.FrameDuration(Frame);
                return;
            }

            Animation.Apply(Frame, Sprite);
        }
    }
}
