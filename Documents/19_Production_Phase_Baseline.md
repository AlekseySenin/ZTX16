# ZTX16 — Production Phase Baseline

## Purpose

This document is the handoff baseline for implementation work.

The goal is to distinguish architectural decisions already approved from implementation details that should be resolved while building the first production-quality prototype.

## Approved foundation

### Platform

- ZTX16 is a fantasy computer/platform, not a game engine.
- Windows and Linux are the first host targets.
- Host implementation is C#/.NET.
- SDL3 is the host integration layer for the MVP.
- OpenGL is the initial graphics presentation backend.
- SDL3 Audio is the initial audio device backend.
- SDL3 supplies host input devices to the ZTX16 Input Model.

### Display

- Logical display: 256×256 pixels.
- Pixel-perfect rendering.
- Integer display scaling in MVP.
- Windowed and fullscreen modes.

### Runtime

- Fixed 60 Hz simulation.
- No DeltaTime.
- No catch-up ticks.
- Tick order: Input → Game Logic → Audio Update → Render.
- Cartridge is agnostic to host/system events.
- Machine.Suspend() is a request controlled by OS/runtime policy.

### Cartridge execution

- Cartridge uses a proprietary/platform-defined executable format.
- Cartridge executable is not a .NET Assembly format.
- A small ZTX16 VM executes Cartridge code.
- C# is the first/reference source language and toolchain target.
- Runtime is language-independent at the executable/platform boundary.
- Entry point is a platform-defined interface with Initialize, Tick and Render.
- Deinitialize is optional/controlled shutdown behavior.

### Sandbox

- Cartridge has no direct host OS API access.
- Cartridge has no arbitrary host filesystem access.
- Persistent data uses ZTX16 Storage.
- External assets are imported by authoring tools into ZTX16 formats.
- Networking is future capability, not MVP.
- VM/runtime form the primary host capability boundary.

### Graphics architecture

- Eight Graphics Layers.
- Eight Display Layers, fixed 1:1 mapping to Graphics Layers.
- Graphics Layer is a persistent raster buffer and may be larger than 256×256.
- Graphics Layer dimensions are expressed as whole 256×256 screen units.
- Changing a Graphics Layer size creates a new cleared buffer.
- A Graphics Layer can receive rasterization from multiple Level Layers sequentially.
- Rasterization may use an offset.
- Rasterization is clipped to the Graphics Layer bounds.
- If a source Level Layer exceeds the target Graphics Layer, the excess is clipped and a warning is produced.
- Level Layer dimensions are set in the Level Editor and are multiples of 256×256.
- Display Layer takes a 256×256 region from its corresponding Graphics Layer.

## Explicitly deferred

The following must not be invented prematurely:

- exact ZTX16 VM instruction set;
- exact bytecode binary layout;
- executable header/table format;
- exact compiler implementation;
- exact C# language subset supported initially;
- exact VM memory limits;
- exact host graphics resource strategy;
- exact SDL3 interop implementation;
- exact audio buffer/callback implementation;
- exact input event translation code;
- exact security hardening mechanisms;
- exact Cartridge binary packing/compression;
- exact resource serialization layout;
- exact API method signatures where not already implemented.

## Recommended implementation order

1. Host shell and SDL3 integration.
2. 256×256 display and integer scaling.
3. Basic Graphics runtime and layer presentation.
4. Audio device backend and minimal audio output.
5. Input backend and platform Input Model.
6. ZTX16 VM skeleton and executable container.
7. Cartridge lifecycle and entry point.
8. Minimal compiler/toolchain path from C# to ZTX16 executable.
9. Resource loading and Project/Cartridge build pipeline.
10. Core graphics/audio/input APIs.
11. Editors running inside the ZTX16 environment.
12. Broader resources, Level, Collision and higher-level systems.

This order may change when implementation dependencies demonstrate a better sequence, but changes should preserve the approved platform contracts.

## Implementation principle

When an implementation detail is not explicitly fixed by the architecture documents, choose the smallest robust solution that preserves the platform contract.

Do not introduce new public concepts merely to solve an internal implementation problem.
