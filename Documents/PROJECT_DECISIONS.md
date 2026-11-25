# ZTX16 — Authoritative Project Decisions

This file records architectural decisions explicitly approved during design discussion. It is not a substitute for subsystem specifications; subsystem documents contain the detailed model.

## Runtime

- Fixed 60 Hz simulation.
- No DeltaTime.
- No catch-up ticks.
- Tick order: Input → Game Logic → Audio Update → Render.
- One Tick → one Audio Update → one Render.
- Cartridge lifecycle requires Initialize, Tick and Render.
- Deinitialize is optional/controlled shutdown behavior.
- Cartridge is agnostic to suspend/resume/focus/device/system events.

## Coordinates and transforms

- `(0,0)` is lower-left.
- +X is right; +Y is up.
- Runtime spatial coordinates are integer-valued.
- Position is object origin.
- Default Pivot is lower-left.
- Scale and Rotation operate around Pivot.
- Transform order: Flip → Scale → Rotation → pixel-perfect rasterization.

## Graphics

- Logical display is 256×256.
- Eight Graphics Layers.
- Eight Display Layers, fixed 1:1 mapping to Graphics Layers.
- Graphics Layer is a large persistent raster buffer.
- Graphics Layer dimensions are whole multiples of 256×256.
- Resizing/recreating a Graphics Layer produces a new cleared buffer.
- Multiple Level Layers can be rasterized sequentially into one Graphics Layer.
- Rasterization supports an offset and clips to Graphics Layer bounds.
- Oversized source Level Layer produces a warning and is clipped.
- Level Layer dimensions are multiples of 256×256 and are authored in Level Editor.
- Display Layer exposes a 256×256 region of its corresponding Graphics Layer.

## Host

- MVP host targets Windows and Linux.
- Host implementation uses C#/.NET.
- SDL3 is the host integration layer.
- OpenGL is the initial graphics presentation backend.
- SDL3 Audio is the initial audio device backend.
- SDL3 supplies host input devices to the ZTX16 Input Model.
- Logical display uses integer scaling in MVP and supports windowed/fullscreen modes.

## Cartridge execution

- Cartridge executable uses a proprietary/platform-defined ZTX16 executable format.
- A small ZTX16 VM executes the executable.
- C# is the first/reference source language/toolchain, not the runtime executable format.
- Runtime execution is language-independent at the platform boundary.

## Sandbox

- Cartridge has no direct Host OS API access.
- Cartridge has no arbitrary host filesystem access.
- Persistent data uses ZTX16 Storage.
- External formats are imported by authoring tools into ZTX16 resources.
- Network is future capability, not MVP.
- VM/runtime form the host capability boundary.
