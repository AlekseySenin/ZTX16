# ZTX16 — Implementation Status and Decision Policy

## Closed architecture

The following are conceptually closed for the initial implementation:

- 256×256 logical graphics resolution;
- pixel-perfect graphics;
- lower-left coordinate origin;
- 512-color global color space / 32-entry palettes;
- eight physical graphics layers;
- raster-buffer physical layers with no persistent Tilemap binding;
- Graphics Layer sizes in whole 256×256 screen units;
- eight Display Layers mapped 1:1 onto Graphics Layers;
- integer runtime coordinates, default lower-left Pivot;
- 8×8 Tiles and 32×32 Tilemaps;
- Sprite composition model;
- per-draw PixelOffset and Transform;
- bitmap Font model;
- 256-code ZTX16 Character Set;
- Animation / AnimationPlayback separation;
- 60 Hz runtime tick and no catch-up;
- Machine API scope;
- suspend semantics;
- eight physical audio channels;
- low-level audio modulation architecture;
- high-level audio allocation architecture;
- Sequence / Song model;
- device-oriented Input model;
- rectangle-only base Collision;
- explicit Collider sets;
- Level / logical Level Layer / Actor model;
- Project Config-controlled directory mapping;
- Project ↔ Cartridge conceptual relationship;
- built-in editor set and editor session model;
- Resource Type + Resource Name public addressing;
- Storage namespace model;
- DeterministicRandom model;
- host stack: C#/.NET, SDL3, OpenGL presentation, SDL3 Audio (`16_Host_and_Backend.md`);
- platform-defined executable format run by the ZTX16 VM; not a .NET Assembly (`17_Cartridge_Execution_and_VM.md`);
- VM compilation path, stack-based VM, managed heap with GC, syscall-table API binding (`17`);
- cartridge lifecycle: Initialize / Tick / Render, optional Deinitialize;
- cartridge sandbox and privileged authoring tools (`18_Sandbox_and_Security.md`).

## Implementation-defined

The following should be selected during Cloud Code implementation rather than treated as architectural blockers:

- exact C# member names where not already established;
- concrete interfaces/classes for Level visuals;
- exact Resource registry implementation;
- project config syntax/schema;
- file extensions;
- resource serialization formats;
- cartridge binary layout;
- VM instruction set, bytecode encoding and executable metadata tables;
- GC algorithm details and VM memory limits;
- compression;
- manifest encoding;
- memory loading strategy;
- validation details;
- exact event delegate signatures;
- exact error UI;
- editor window/panel layouts;
- exact audio numeric ranges where not already specified;
- exact Sequence event payloads;
- exact Song event payloads;
- future Save State format.

## Obsolete concepts

Do not resurrect these from older documents:

- physical Graphics Layer with persistent Tilemap binding;
- Viewport buffer with `Layer.Draw(x, y)` transfers (replaced by Display Layers);
- layers smaller than the display that repeat across it;
- center as the default Pivot; draw position as lower-left of the transformed bounds;
- Cartridge executable as a .NET Assembly;
- `System.Random` as cartridge-side randomness;
- top-left ZTX16 coordinate origin;
- static 256×256 Collision Map;
- Collision Group + Mask model;
- collision tied to Graphics Layers;
- Player abstraction in Input;
- cartridge-visible suspend/resume system events;
- separate public Runtime API duplicating Machine;
- Audio Bus/Mixer Group hierarchy;
- Voice Priority system;
- built-in polyphonic Sequence;
- separate Project/Cartridge Manager editor.

## Documentation maintenance

When implementation makes a decision that changes the public contract, update the relevant module document first or immediately alongside the code change.

Do not leave contradictory old documents in the active implementation documentation tree.
