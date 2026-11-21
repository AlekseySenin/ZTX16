# ZTX16 — Animation API

## Animation resource

Animation is a reusable resource containing Frames.

Animation does not know about a Sprite Instance or an Animator.

Animation does not contain playback speed or loop mode.

## Frame

Each Frame contains:

- Duration;
- TileSwaps.

A TileSwap identifies:

- Sprite tile index;
- replacement Tilemap Tile ID.

Each Frame describes the complete desired Tile-ID state of the Sprite, not a delta from the previous frame.

## AnimationPlayback

AnimationPlayback is runtime state.

It:

- references an Animation;
- owns/operates on its own Sprite Instance;
- tracks current frame and frame time;
- owns PlaySpeed;
- owns OneShot/Loop mode;
- may expose start/completion callbacks/actions;
- modifies its own Sprite Instance;
- does not render.

The external caller decides when and where the resulting Sprite is rendered.

Many independent playbacks may use the same Animation resource.

## Completion

OneShot stops on the final Frame and retains the final Sprite state.

Loop restarts according to playback state.

Exact update method and time representation are implementation details.
