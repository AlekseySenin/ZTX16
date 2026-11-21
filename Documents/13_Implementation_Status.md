# ZTX16 — Implementation Status and Decision Policy

## Closed architecture

The following are conceptually closed for the initial implementation:

- 256×256 logical graphics resolution;
- pixel-perfect graphics;
- lower-left coordinate origin;
- 512-color global color space / 32-entry palettes;
- eight physical graphics layers;
- raster-buffer physical layers with no persistent Tilemap binding;
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
- DeterministicRandom model.

## Implementation-defined

The following should be selected during Cloud Code implementation rather than treated as architectural blockers:

- exact C# member names where not already established;
- concrete interfaces/classes for Level visuals;
- exact Resource registry implementation;
- project config syntax/schema;
- file extensions;
- resource serialization formats;
- cartridge binary layout;
- executable representation and language/runtime ABI;
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
