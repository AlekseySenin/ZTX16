# ZTX16 — Overall Architecture

## 1. Platform identity

ZTX16 is a fantasy computer/platform, not a general-purpose game engine.

The platform defines a constrained but coherent set of hardware-like capabilities and foundational APIs. Higher-level systems and libraries may build game-engine functionality on top of them.

The official editors are applications running inside ZTX16, not privileged architectural components.

## 2. Core separation

The architecture separates:

- immutable cartridge content;
- mutable runtime state;
- low-level platform subsystems;
- higher-level resource systems;
- editors and other applications.

The dependency direction is generally upward:

**Machine/Runtime → platform subsystems → higher-level systems → editors/game frameworks**

Higher-level systems may use lower-level systems. Lower-level systems must not acquire knowledge of higher-level concepts.

## 3. Runtime tick

The nominal simulation rate is 60 Hz.

Each tick executes in this order:

1. Input
2. Game Logic
3. Audio Update
4. Render

There is no DeltaTime model. One simulation tick represents 1/60 second of logical game time.

If execution overruns the nominal tick duration, the runtime does not perform catch-up ticks. The simulation slows.

Audio Update means updating/scheduling audio state and commands; it does not mean generating audio samples at 60 Hz.

## 4. Runtime lifecycle

Conceptual lifecycle:

Load → Validate → Initialize → Execute → Deinitialize → Return to OS

The OS/runtime owns lifecycle authority. A cartridge may request Exit, but cannot force termination semantics.

Unhandled failures terminate execution; normal Deinitialize must not be relied upon after a crash.

## 5. Suspend

Suspend preserves runtime state in memory and stops cartridge ticks, input processing, audio/game-state advancement by cartridge ticks, and rendering.

The cartridge is deliberately **agnostic to system events** such as suspend/resume, focus changes, device connection, or system-menu transitions.

`Machine.Suspend()` is a request. The OS/runtime may honor it, ignore it, or suspend the cartridge independently.

Resume is controlled by the OS/runtime. After resume the cartridge simply continues from the next tick and does not know how much wall-clock time passed.

Save State is a separate future serialization feature.

## 6. Coordinate system

All ZTX16 spatial coordinates use:

- `(0,0)` = lower-left
- +X = right
- +Y = up

This applies to graphics, levels, collision, sprites, actors, colliders, tiles, tilemaps, pivots, and editor-authored ZTX16 data.

An editor may use another internal UI coordinate system, but must convert at its ZTX16 data boundary.

## 7. Runtime state vs resources

Resources are immutable cartridge data.

Runtime objects are mutable state derived from resources.

Examples:

- Animation → AnimationPlayback
- Song → Song Playback
- Level → Level runtime state / Actor Runtime References
- Instrument → Voice
- Project resource → packaged cartridge resource

A runtime object must not silently mutate the resource from which it was created.

## 8. Platform subsystems

### Graphics

256×256 logical viewport, eight persistent physical raster layers, 512-color global color space, 32-entry runtime palettes, 8×8 tiles, 32×32 tilemaps.

### Audio

Eight physical channels: six synth and two Sample/Wavetable. Shared LFO, Filter and FX resources. High-level allocation is separate from the hardware model.

### Input

Devices, not players. Digital state/transitions, analog values/change events, physical keyboard input separated from text input, hot-swappable devices.

### Collision

Independent geometry system. Base v1 uses rectangle colliders and explicit collider sets. No physics engine and no graphics coupling.

### Level

High-level game-space resource consisting of logical visual layers and actors. Logical layers are independent of physical Graphics Layers.

### Machine

Small platform-facing API for time, capabilities/profile, exit and suspend.

### Storage

Persistent writable storage isolated by current Game ID namespace.

### Random

`ZTX16.DeterministicRandom` provides the platform deterministic PRNG; .NET `System.Random` remains ordinary .NET randomness.

## 9. API layering rule

Do not add convenience functionality to a lower-level subsystem merely because a higher-level system currently needs it.

Prefer:

- low-level API models the platform primitive;
- high-level API provides convenience and policy.

Example: physical Audio Channels are fixed hardware resources; voice allocation and channel stealing belong to the high-level Audio layer.
